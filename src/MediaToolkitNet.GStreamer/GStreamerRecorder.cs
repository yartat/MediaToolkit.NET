#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.GStreamer.Transcoding;

namespace MediaToolkitNet.GStreamer;

/// <summary>
/// Records frames pushed by the caller into a file: each stream enters an <c>appsrc</c>, is converted, encoded and
/// muxed by the elements GStreamer has for the codec, so a hardware encoder such as <c>nvh264enc</c> is a matter of
/// naming it in <see cref="VideoEncodingSettings.EncoderName"/>.
/// </summary>
/// <remarks>
/// <para>
/// The container comes from the extension of the output. A format with no muxer of its own (<c>.flac</c>, <c>.mp3</c>)
/// takes one stream, written straight by its encoder. The pipeline is built by <see cref="Start"/> from the streams
/// added before it, and each frame must be in the format its stream was added with: nothing is converted for the
/// caller, and a frame that differs is refused.
/// </para>
/// <para>
/// The file is written beside the output and takes its place when <see cref="Stop"/> has finished it; a recording that
/// is disposed without a stop leaves the output as it was. With <see cref="SegmentDuration"/> the recording is a
/// series of files, <c>name_00000.ext</c> and on, that are written in place and each play on their own.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class GStreamerRecorder : IMediaRecorder
{
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(60);

    private readonly string _output;
    private readonly MediaContainer? _container;
    private readonly List<Entry> _streams = [];
    private readonly List<GStreamerSource> _sources = [];
    private readonly List<string> _warnings = [];
    private PartialOutput? _partial;
    private GStreamerPipeline? _pipeline;
    private TimeSpan _elapsed;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates a recorder writing to <paramref name="outputPath"/>; the container is inferred from its extension.</summary>
    /// <exception cref="MediaBackendUnavailableException">GStreamer is not usable here.</exception>
    public GStreamerRecorder(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        Gst.EnsureLoaded();
        _output = Path.GetFullPath(outputPath);
        _container = MediaFormats.ContainerFromPath(_output);
    }

    /// <inheritdoc />
    public string Backend => GstConstants.BackendName;

    /// <inheritdoc />
    public bool IsRecording => _pipeline is not null;

    /// <inheritdoc />
    public TimeSpan Elapsed => _elapsed;

    /// <summary>
    /// When set, the recording is cut into files of about this length, each starting on a key frame. Set it before
    /// <see cref="Start"/>; the files are <c>name_00000.ext</c> beside the output, which itself is not written.
    /// </summary>
    public TimeSpan? SegmentDuration { get; set; }

    /// <summary>The encoder element chosen for each stream, in the order the streams were added; PCM has none and shows as <c>raw</c>.</summary>
    public IReadOnlyList<string> Encoders => [.. _streams.Select(s => s.Encoder ?? "raw")];

    /// <summary>What was asked for and is not carried out exactly, such as a setting the chosen encoder does not map.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <inheritdoc />
    public int AddVideoStream(VideoEncodingSettings settings)
    {
        EnsureNotStarted();
        if (!settings.Format.IsValid)
        {
            throw new ArgumentException("No valid video format was supplied.", nameof(settings));
        }

        var output = new VideoOutputSettings(settings.Codec == MediaCodec.Default ? MediaCodec.H264 : settings.Codec)
        {
            BitrateBitsPerSecond = settings.BitrateBitsPerSecond,
            KeyFrameInterval = settings.KeyFrameInterval,
            EncoderName = settings.EncoderName,
            Options = settings.Options,
        };
        var encoder = GStreamerCodecs.VideoEncoder(output);
        if (encoder is null || !GStreamerElements.Exists(encoder))
        {
            throw new MediaToolkitNetException(
                Backend,
                encoder is null
                    ? $"no GStreamer element encodes {output.Codec}; looked for {string.Join(", ", GStreamerCodecs.VideoCandidates(output.Codec).DefaultIfEmpty("none known"))}"
                    : $"the encoder {encoder} is not installed",
                0);
        }

        var issues = new List<TranscodeIssue>();
        var entry = new Entry(true, settings.Format, default, encoder, GStreamerCodecs.VideoEncoderProperties(encoder, output, issues), null);
        _warnings.AddRange(issues.Select(i => i.Message));
        _streams.Add(entry);
        return _streams.Count - 1;
    }

    /// <inheritdoc />
    public int AddAudioStream(AudioEncodingSettings settings)
    {
        EnsureNotStarted();
        var output = new AudioOutputSettings(settings.Codec)
        {
            BitrateBitsPerSecond = settings.BitrateBitsPerSecond,
            Quality = settings.Quality,
            SampleFormat = settings.SampleFormat,
            EncoderName = settings.EncoderName,
            Options = settings.Options,
        };
        var encoder = GStreamerCodecs.AudioEncoder(output);
        var raw = GStreamerCodecs.RawFormatOf(settings.Codec);
        if (encoder is null && raw is null)
        {
            throw new MediaToolkitNetException(
                Backend,
                $"no GStreamer element encodes {settings.Codec}; looked for {string.Join(", ", GStreamerCodecs.AudioCandidates(settings.Codec).DefaultIfEmpty("none known"))}",
                0);
        }

        if (encoder is not null && !GStreamerElements.Exists(encoder))
        {
            throw new MediaToolkitNetException(Backend, $"the encoder {encoder} is not installed", 0);
        }

        var issues = new List<TranscodeIssue>();
        var properties = encoder is null ? [] : GStreamerCodecs.AudioEncoderProperties(encoder, output, issues);
        _warnings.AddRange(issues.Select(i => i.Message));
        _streams.Add(new Entry(false, default, settings.Format, encoder, properties, settings.SampleFormat is { } sample ? GStreamerGraph.SampleFormatName(sample, encoder) : raw));
        return _streams.Count - 1;
    }

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("The recording has already started.");
        }

        if (_streams.Count == 0)
        {
            throw new InvalidOperationException("Add at least one stream before Start.");
        }

        var muxer = GStreamerGraph.MuxerFor(_container);
        if (muxer is null && _streams.Count > 1)
        {
            throw new MediaToolkitNetException(Backend, $"{Path.GetExtension(_output)} holds one stream and {_streams.Count} were added", 0);
        }

        if (_container is null)
        {
            throw new MediaToolkitNetException(Backend, $"the extension {Path.GetExtension(_output)} names no container this library writes", 0);
        }

        if (SegmentDuration is not null && muxer is null)
        {
            throw new MediaToolkitNetException(Backend, "a recording in segments needs a container with a muxer", 0);
        }

        var properties = new List<(string Element, string Property, string Value)>();
        var description = Describe(muxer, properties);
        var pipeline = GStreamerPipeline.Parse(description, strict: true);
        try
        {
            foreach (var (element, property, value) in properties)
            {
                pipeline.FindElement(element)!.Set(property, value);
            }

            for (var i = 0; i < _streams.Count; i++)
            {
                var source = pipeline.GetSource($"src{i}");
                if (_streams[i].IsVideo)
                {
                    source.SetFormat(_streams[i].Video);
                }
                else
                {
                    source.SetFormat(_streams[i].Audio);
                }

                _sources.Add(source);
            }

            pipeline.Play();
        }
        catch
        {
            pipeline.Dispose();
            _sources.Clear();
            _partial?.Dispose();
            _partial = null;
            throw;
        }

        _pipeline = pipeline;
        _started = true;
    }

    /// <inheritdoc />
    public void WriteVideo(int streamIndex, in VideoFrame frame)
    {
        var source = Source(streamIndex, video: true);
        if (!source.PushVideo(frame))
        {
            throw Stopped();
        }

        var rate = _streams[streamIndex].Video.FrameRate;
        var length = rate.Numerator > 0 && rate.Denominator > 0 ? TimeSpan.FromSeconds(rate.Denominator / (double)rate.Numerator) : TimeSpan.Zero;
        _elapsed = Max(_elapsed, frame.Timestamp + length);
    }

    /// <inheritdoc />
    public void WriteAudio(int streamIndex, in AudioFrame frame)
    {
        var source = Source(streamIndex, video: false);
        if (!source.PushAudio(frame))
        {
            throw Stopped();
        }

        _elapsed = Max(_elapsed, frame.Timestamp + frame.Duration);
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_pipeline is not { } pipeline)
        {
            return;
        }

        try
        {
            foreach (var source in _sources)
            {
                source.EndOfStream();
            }

            if (!pipeline.WaitForCompletion(FinishTimeout))
            {
                throw new MediaToolkitNetException(Backend, $"the recording did not finish within {FinishTimeout.TotalSeconds:0} s of its end", 0);
            }

            // The muxer finishes its file when the pipeline reaches NULL, so the output is renamed only after that.
            pipeline.Stop();
            pipeline.Dispose();
            _pipeline = null;
            _partial?.Commit();
        }
        finally
        {
            Release();
        }
    }

    /// <summary>Ends a recording that was not stopped without keeping its file; the output is left as it was.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release();
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private string Describe(string? muxer, List<(string Element, string Property, string Value)> properties)
    {
        var builder = new StringBuilder();
        var segments = SegmentDuration;
        if (segments is { } length)
        {
            var directory = Path.GetDirectoryName(_output)!;
            var pattern = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(_output)}_%05d{Path.GetExtension(_output)}");
            builder.Append("splitmuxsink name=mux");
            properties.Add(("mux", "location", pattern));
            properties.Add(("mux", "muxer-factory", muxer!));
            properties.Add(("mux", "max-size-time", ((ulong)length.Ticks * (ulong)GstConstants.NanosecondsPerTick).ToString(CultureInfo.InvariantCulture)));
        }
        else
        {
            _partial = PartialOutput.Begin(_output);
            properties.Add(("out", "location", _partial.Path));
            if (muxer is not null)
            {
                builder.Append(muxer).Append(" name=mux ! filesink name=out");
            }
        }

        var video = 0;
        var audio = 0;
        for (var i = 0; i < _streams.Count; i++)
        {
            var stream = _streams[i];
            if (builder.Length > 0)
            {
                builder.Append("  ");
            }

            builder.Append($"appsrc name=src{i} ! queue");
            if (stream.IsVideo)
            {
                builder.Append(" ! videoconvert");
            }
            else
            {
                builder.Append(" ! audioconvert ! audioresample");
                var caps = new List<string>();
                if (stream.PcmFormat is { } format)
                {
                    caps.Add($"format={format}");
                    if (stream.Encoder is null)
                    {
                        caps.Add("layout=interleaved");
                    }
                }

                if (caps.Count > 0)
                {
                    builder.Append(" ! audio/x-raw,").Append(string.Join(',', caps));
                }
            }

            if (stream.Encoder is { } encoder)
            {
                builder.Append($" ! {encoder} name=enc{i}");
                foreach (var (name, value) in stream.Properties)
                {
                    properties.Add(($"enc{i}", name, value));
                }

                // lame drops to a lower rate at low bit rates unless the rate after it is pinned.
                if (encoder == "lamemp3enc")
                {
                    builder.Append($" ! audio/mpeg,rate={stream.Audio.SampleRate}");
                }

                if (GStreamerCodecs.EncodedParser(encoder) is { } parser)
                {
                    builder.Append(" ! ").Append(parser);
                }
            }

            if (muxer is null)
            {
                builder.Append(" ! filesink name=out");
            }
            else
            {
                var pad = segments is not null
                    ? stream.IsVideo ? "video" : $"audio_{audio}"
                    : GStreamerGraph.MuxerPad(muxer, stream.IsVideo, stream.IsVideo ? video : audio, i);
                builder.Append(" ! queue ! mux.").Append(pad);
            }

            if (stream.IsVideo)
            {
                video++;
            }
            else
            {
                audio++;
            }
        }

        return builder.ToString();
    }

    private GStreamerSource Source(int streamIndex, bool video)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipeline is null)
        {
            throw new InvalidOperationException("Call Start before writing frames.");
        }

        if (streamIndex < 0 || streamIndex >= _streams.Count || _streams[streamIndex].IsVideo != video)
        {
            throw new ArgumentOutOfRangeException(nameof(streamIndex), streamIndex, $"There is no {(video ? "video" : "audio")} stream with that index.");
        }

        return _sources[streamIndex];
    }

    private MediaToolkitNetException Stopped() =>
        new(Backend, $"the pipeline takes no more data: {_pipeline?.LastError() ?? "it has ended"}", 0);

    private void EnsureNotStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("Streams must be added before Start.");
        }
    }

    // Takes down whatever is still up and deletes a partial file that was not committed.
    private void Release()
    {
        _pipeline?.Dispose();
        _pipeline = null;
        _sources.Clear();
        _partial?.Dispose();
        _partial = null;
    }

    private sealed record Entry(
        bool IsVideo,
        VideoFormat Video,
        AudioFormat Audio,
        string? Encoder,
        List<(string Name, string Value)> Properties,
        string? PcmFormat);
}
