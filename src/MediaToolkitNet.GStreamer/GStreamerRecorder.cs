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
/// <see cref="AddOutput"/> adds further outputs, a file, an HLS playlist or an SRT stream, which the same encoding goes into: the
/// frames are encoded once and split with a <c>tee</c>, each output having a muxer of its own.
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

    private readonly List<Target> _targets = [];
    private readonly List<Entry> _streams = [];
    private readonly List<GStreamerSource> _sources = [];
    private readonly List<string> _warnings = [];
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
        _targets.Add(Target.For(outputPath));
    }

    /// <summary>The outputs being written: the one given to the constructor first, then those added.</summary>
    public IReadOnlyList<string> Outputs => [.. _targets.Select(t => t.Address)];

    /// <summary>
    /// Adds an output beside the first: a file, a path ending in <c>.m3u8</c> for HLS, or <c>srt://</c>. The same encoded streams
    /// go into each. Call it before <see cref="Start"/>.
    /// </summary>
    /// <remarks>
    /// An added file is whole even when <see cref="SegmentDuration"/> cuts the first one; an added HLS playlist takes that duration
    /// as the length of its segments. An added SRT output does not wait for a receiver: what is sent while nobody is connected is
    /// lost, so that it never holds the other outputs back. The queues in front of the outputs have no limit, so frames pushed
    /// faster than an SRT output sends them are held in memory.
    /// </remarks>
    /// <exception cref="ArgumentException">The output is already being written.</exception>
    /// <exception cref="InvalidOperationException">The recording has started.</exception>
    public void AddOutput(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        EnsureNotStarted();
        var target = Target.For(outputPath);
        if (_targets.Any(t => string.Equals(t.Address, target.Address, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"{outputPath} is already an output of this recording.", nameof(outputPath));
        }

        _targets.Add(target);
    }

    /// <inheritdoc />
    public string Backend => GstConstants.BackendName;

    /// <inheritdoc />
    public bool IsRecording => _pipeline is not null;

    /// <inheritdoc />
    public TimeSpan Elapsed => _elapsed;

    /// <summary>
    /// When set, the recording is cut into files of about this length, each starting on a key frame. Set it before
    /// <see cref="Start"/>; the files are <c>name_00000.ext</c> beside the output, which itself is not written. For an
    /// HLS output (<c>.m3u8</c>) it is the target length of a segment, two seconds when left unset.
    /// </summary>
    public TimeSpan? SegmentDuration { get; set; }

    /// <summary>
    /// For an SRT output, how long <see cref="Stop"/> keeps the connection open after the last frame was sent, so that the
    /// receiver gets what is still on its way. One second by default; zero closes at once and loses the last few frames.
    /// </summary>
    public TimeSpan DrainTime { get; set; } = TimeSpan.FromSeconds(1);

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

        for (var k = 0; k < _targets.Count; k++)
        {
            Check(_targets[k], first: k == 0);
        }

        var properties = new List<(string Element, string Property, string Value)>();
        var description = Describe(properties);
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
            DiscardPartials();
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

            // An SRT sender that closes its socket at once loses the last frames it has not yet delivered: a plain gst-launch
            // of the same stream loses three of fifty. Waiting lets the link finish the delivery.
            if (_targets.Any(t => t.Kind == OutputKind.Srt) && DrainTime > TimeSpan.Zero)
            {
                Thread.Sleep(DrainTime);
            }

            // The muxers finish their files when the pipeline reaches NULL, so the outputs are renamed only after that.
            pipeline.Stop();
            pipeline.Dispose();
            _pipeline = null;
            foreach (var target in _targets)
            {
                target.Partial?.Commit();
            }
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

    // What an output cannot be written as, with its name.
    private void Check(Target target, bool first)
    {
        if (target.Kind == OutputKind.Unknown)
        {
            throw new MediaToolkitNetException(
                Backend, $"{target.Address.Split("://")[0]}:// is not a stream this recorder writes; it writes srt:// and an .m3u8 playlist, and files by extension", 0);
        }

        if (first && target.Kind == OutputKind.Srt && SegmentDuration is not null)
        {
            throw new MediaToolkitNetException(Backend, "a stream over SRT is not cut into segments", 0);
        }

        if (target.Muxer is null && target.Container is not null && _streams.Count > 1)
        {
            throw new MediaToolkitNetException(Backend, $"{Path.GetExtension(target.Address)} holds one stream and {_streams.Count} were added ({target.Address})", 0);
        }

        if (target.Muxer == "wavenc" && _streams.Count > 1)
        {
            throw new MediaToolkitNetException(Backend, $"{Path.GetExtension(target.Address)} holds one stream and {_streams.Count} were added ({target.Address})", 0);
        }

        if (target.Kind == OutputKind.File && target.Container is null)
        {
            throw new MediaToolkitNetException(Backend, $"the extension {Path.GetExtension(target.Address)} names no container this library writes", 0);
        }

        if (first && SegmentDuration is not null && target.Muxer is null)
        {
            throw new MediaToolkitNetException(Backend, "a recording in segments needs a container with a muxer", 0);
        }
    }

    // The sinks first, one for each output, then one chain per stream. With several outputs a chain ends in a tee and each output
    // takes a branch of it. The branch queues have no limit: behind a tee, two muxers with limited queues can in principle each wait
    // for a stream that the full queue of the other holds back, and a paced output would hold the others to its pace. The deadlock
    // was not reproduced here (limited queues passed with ten seconds of one stream ahead of the other), so this is a precaution.
    private string Describe(List<(string Element, string Property, string Value)> properties)
    {
        var builder = new StringBuilder();
        var several = _targets.Count > 1;
        for (var k = 0; k < _targets.Count; k++)
        {
            DescribeSink(_targets[k], k, several, builder, properties);
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

            string? parser = null;
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

                parser = GStreamerCodecs.EncodedParser(encoder);
            }

            if (several)
            {
                builder.Append($" ! tee name=t{i}");
            }

            for (var k = 0; k < _targets.Count; k++)
            {
                var target = _targets[k];
                if (several)
                {
                    builder.Append($"  t{i}. ! queue max-size-buffers=0 max-size-bytes=0 max-size-time=0");
                }

                // Each output parses for itself: MP4 and MPEG-TS want the same H.264 in different forms.
                if (parser is not null)
                {
                    builder.Append(" ! ").Append(parser);
                }

                if (target.Muxer is not { } muxer)
                {
                    builder.Append($" ! filesink name=out{k}");
                    continue;
                }

                var split = k == 0 && target.Kind == OutputKind.File && SegmentDuration is not null;
                var pad = target.Kind == OutputKind.Hls ? (stream.IsVideo ? "video" : "audio")
                    : split ? stream.IsVideo ? "video" : $"audio_{audio}"
                    : GStreamerGraph.MuxerPad(muxer, stream.IsVideo, stream.IsVideo ? video : audio, i);
                if (!several)
                {
                    builder.Append(" ! queue");
                }

                builder.Append($" ! mux{k}.{pad}");
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

    private void DescribeSink(Target target, int k, bool several, StringBuilder builder, List<(string Element, string Property, string Value)> properties)
    {
        if (builder.Length > 0)
        {
            builder.Append("  ");
        }

        var mux = $"mux{k}";
        var sink = $"out{k}";
        if (target.Kind == OutputKind.Hls)
        {
            // hlssink2 muxes into MPEG-TS segments itself and writes the playlist, which is complete (ENDLIST) once the
            // end of the stream reached it. Nothing is deleted and the playlist keeps every segment, as a recording does.
            var seconds = Math.Max(1, (int)Math.Ceiling((SegmentDuration ?? TimeSpan.FromSeconds(2)).TotalSeconds));
            var directory = Path.GetDirectoryName(target.Address)!;
            builder.Append($"hlssink2 name={mux}");
            properties.Add((mux, "location", Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(target.Address)}_%05d.ts")));
            properties.Add((mux, "playlist-location", target.Address));
            properties.Add((mux, "target-duration", seconds.ToString(CultureInfo.InvariantCulture)));
            properties.Add((mux, "max-files", "0"));
            properties.Add((mux, "playlist-length", "0"));
        }
        else if (target.Kind == OutputKind.Srt)
        {
            builder.Append($"mpegtsmux name={mux} ! srtsink name={sink}");
            properties.Add((sink, "uri", target.Address));

            // Beside other outputs it must not wait for a receiver, or none of them would be written until one came.
            if (several)
            {
                properties.Add((sink, "wait-for-connection", "false"));
            }
        }
        else if (k == 0 && SegmentDuration is { } length)
        {
            var directory = Path.GetDirectoryName(target.Address)!;
            var pattern = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(target.Address)}_%05d{Path.GetExtension(target.Address)}");
            builder.Append($"splitmuxsink name={mux}");
            properties.Add((mux, "location", pattern));
            properties.Add((mux, "muxer-factory", target.Muxer!));
            properties.Add((mux, "max-size-time", ((ulong)length.Ticks * (ulong)GstConstants.NanosecondsPerTick).ToString(CultureInfo.InvariantCulture)));
        }
        else
        {
            target.Partial = PartialOutput.Begin(target.Address);
            properties.Add((sink, "location", target.Partial.Path));
            if (target.Muxer is { } muxer)
            {
                builder.Append($"{muxer} name={mux} ! filesink name={sink}");
            }
        }
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
        DiscardPartials();
    }

    private void DiscardPartials()
    {
        foreach (var target in _targets)
        {
            target.Partial?.Dispose();
            target.Partial = null;
        }
    }

    private enum OutputKind
    {
        File,
        Hls,
        Srt,
        Unknown,
    }

    // One place the recording is written to.
    private sealed class Target
    {
        private Target(string address, OutputKind kind, MediaContainer? container)
        {
            Address = address;
            Kind = kind;
            Container = container;
            Muxer = kind switch
            {
                OutputKind.Hls => "hlssink2",
                OutputKind.Srt => "mpegtsmux",
                OutputKind.File => GStreamerGraph.MuxerFor(container),
                _ => null,
            };
        }

        public string Address { get; }

        public OutputKind Kind { get; }

        public MediaContainer? Container { get; }

        public string? Muxer { get; }

        public PartialOutput? Partial { get; set; }

        public static Target For(string output)
        {
            var isAddress = output.Contains("://", StringComparison.Ordinal);
            var address = isAddress ? output : Path.GetFullPath(output);
            var kind = isAddress
                ? output.StartsWith("srt://", StringComparison.OrdinalIgnoreCase) ? OutputKind.Srt : OutputKind.Unknown
                : address.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? OutputKind.Hls : OutputKind.File;
            return new Target(address, kind, kind == OutputKind.File ? MediaFormats.ContainerFromPath(address) : null);
        }
    }

    private sealed record Entry(
        bool IsVideo,
        VideoFormat Video,
        AudioFormat Audio,
        string? Encoder,
        List<(string Name, string Value)> Properties,
        string? PcmFormat);
}
