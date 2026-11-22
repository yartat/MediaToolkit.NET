#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>
/// Re-encodes a file through Media Foundation: an <c>IMFSourceReader</c> decodes and scales, an
/// <c>IMFSinkWriter</c> encodes and writes the MP4, and the machine's hardware encoders (NVENC, Quick Sync,
/// AMD VCE) are used when it has them.
/// </summary>
/// <remarks>
/// It does less than the FFmpeg backend and says so instead of approximating silently: <see cref="Validate"/> lists
/// every setting it cannot carry out as an error, and every one it carries out only approximately as a warning.
/// What it does is one video stream (H.264 or HEVC) and one audio stream (AAC), from one input into
/// MP4, M4V or M4A, with a size, frame rate, bit rate, key frame interval and a start and an end. Streams are never
/// copied, only re-encoded, and file tags and chapters are not carried over.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class MediaFoundationTranscoder : IMediaTranscoder
{
    // The Media Foundation AAC encoder takes these, in bits per second, and nothing in between.
    private static readonly int[] AacBitrates = [96_000, 128_000, 160_000, 192_000];
    private static readonly int[] AacRates = [44100, 48000];
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    private readonly bool _hardware;

    /// <summary>Creates a transcoder.</summary>
    /// <param name="hardwareEncoders">Whether to let hardware encoders take the job; they are used when present.</param>
    public MediaFoundationTranscoder(bool hardwareEncoders = true) => _hardware = hardwareEncoders;

    /// <inheritdoc />
    public string Backend => MediaFoundationDeviceEnumerator.BackendName;

    /// <inheritdoc />
    public TranscodeCapabilities Capabilities =>
        TranscodeCapabilities.VideoEncode | TranscodeCapabilities.AudioEncode | TranscodeCapabilities.Trim;

    /// <inheritdoc />
    public IReadOnlyList<TranscodeIssue> Validate(TranscodeRequest request) => Resolve(request).Issues;

    /// <inheritdoc />
    public Task<TranscodeResult> RunAsync(
        TranscodeRequest request,
        IProgress<TranscodeProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var plan = Resolve(request);
        if (plan.Issues.Any(i => i.Severity == TranscodeIssueSeverity.Error))
        {
            throw new TranscodeRejectedException(Backend, plan.Issues);
        }

        cancellation.ThrowIfCancellationRequested();
        return Task.Run(() => Execute(request, plan, progress, cancellation), cancellation);
    }

    // ------------------------------------------------------------------ planning

    private sealed record VideoJob(int Source, MediaCodec Codec, int Width, int Height, Rational FrameRate, int Bitrate, int KeyFrames);

    private sealed record AudioJob(int Source, int SampleRate, int Channels, int Bitrate);

    private sealed record Plan(
        IReadOnlyList<TranscodeIssue> Issues,
        MediaInfo? Info,
        VideoJob? Video,
        AudioJob? Audio,
        TimeSpan Start,
        TimeSpan? End);

    private static Plan Resolve(TranscodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var issues = new List<TranscodeIssue>();
        void Error(string message) => issues.Add(new TranscodeIssue(TranscodeIssueSeverity.Error, message));
        void Warn(string message) => issues.Add(new TranscodeIssue(TranscodeIssueSeverity.Warning, message));
        Plan Done(MediaInfo? info = null, VideoJob? video = null, AudioJob? audio = null) =>
            new(issues, info, video, audio, request.Start ?? TimeSpan.Zero, request.End);

        var extension = Path.GetExtension(request.Output).ToLowerInvariant();
        if (extension is not (".mp4" or ".m4v" or ".m4a") || request.Container is not (MediaContainer.Auto or MediaContainer.Mp4))
        {
            Error("Media Foundation writes MP4 here: give the output a .mp4, .m4v or .m4a name");
        }

        if (request.Metadata is not null)
        {
            Error("file tags cannot be written through Media Foundation");
        }

        if (request.ContainerOptions is not null)
        {
            Error("container options are not supported by Media Foundation");
        }

        if (request.Inputs.Count != 1)
        {
            Error(request.Inputs.Count == 0 ? "the request names no input" : "Media Foundation takes one input; combine them first");
            return Done();
        }

        if (request.Streams.Count == 0)
        {
            Error("Media Foundation re-encodes and cannot copy streams: ask for a video and an audio stream with OutputStream.Video and OutputStream.Audio");
            return Done();
        }

        if (request.Start is { } s && request.End is { } e && e <= s)
        {
            Error("the end is not after the start");
        }

        MediaInfo info;
        try
        {
            info = new MediaFoundationProber().Probe(request.Inputs[0]);
        }
        catch (MediaToolkitNetException ex)
        {
            Error($"cannot read {request.Inputs[0]}: {ex.Message}");
            return Done();
        }

        if (request.Start is { } start && info.Duration > TimeSpan.Zero && start >= info.Duration)
        {
            Error("the start is past the end of the input");
        }

        if (request.CopyChapters)
        {
            Warn("chapters and tags of the input are not carried over");
        }

        VideoJob? video = null;
        AudioJob? audio = null;
        foreach (var output in request.Streams)
        {
            if (output.Language is not null || output.Title is not null || output.IsDefault is not null || output.IsForced is not null)
            {
                Warn("stream language, title and flags are not written");
            }

            switch (output)
            {
                case EncodeVideo encode:
                    if (video is not null)
                    {
                        Error("Media Foundation writes one video stream");
                    }
                    else if (Pick(info, encode.Source, MediaStreamKind.Video, Error) is { } source)
                    {
                        video = PlanVideo(source, encode.Settings, Error, Warn);
                    }

                    break;
                case EncodeAudio encode:
                    if (audio is not null)
                    {
                        Error("Media Foundation writes one audio stream");
                    }
                    else if (Pick(info, encode.Source, MediaStreamKind.Audio, Error) is { } source)
                    {
                        audio = PlanAudio(source, encode.Settings, Error, Warn);
                    }

                    break;
                case DropStream:
                    break;
                default:
                    // "Every stream of a kind" that finds none, as for subtitles in a file without any, is passed over.
                    if (output.Source.MatchesAll && Matching(info, output.Source).Count == 0)
                    {
                        break;
                    }

                    Error(output is CopyStream
                        ? $"Media Foundation cannot copy {output.Source}, only re-encode it"
                        : $"Media Foundation cannot carry out {output.GetType().Name} for {output.Source}: it handles video and audio only");
                    break;
            }
        }

        if (video is null && audio is null && !issues.Any(i => i.Severity == TranscodeIssueSeverity.Error))
        {
            Error("the request leaves nothing to write");
        }

        return Done(info, video, audio);
    }

    private static MediaStreamInfo? Pick(MediaInfo info, StreamSource source, MediaStreamKind kind, Action<string> error)
    {
        if (source.Input != 0)
        {
            error($"{source} is from an input Media Foundation does not have");
            return null;
        }

        if (source.Language is not null)
        {
            error($"{source}: Media Foundation does not report stream languages, so none can be selected by one");
            return null;
        }

        if (source.Kind is { } wanted && wanted != kind)
        {
            error($"{source} is not a {kind} stream");
            return null;
        }

        var matches = Matching(info, source).Where(s => s.Kind == kind).ToList();

        if (matches.Count == 0)
        {
            error($"the input has no {source}");
            return null;
        }

        if (source.MatchesAll && matches.Count > 1)
        {
            error($"{source} matches {matches.Count} streams and Media Foundation writes one");
            return null;
        }

        return matches[0];
    }

    // The streams a selector picks, as the other backends read it: by index, or by kind and position.
    private static List<MediaStreamInfo> Matching(MediaInfo info, StreamSource source)
    {
        var matches = source.Index is { } index
            ? info.Streams.Where(s => s.Index == index).ToList()
            : info.Streams.Where(s => s.Kind == source.Kind && !s.IsAttachedPicture).ToList();
        return source.KindIndex is { } n ? matches.Skip(n).Take(1).ToList() : matches;
    }

    private static VideoJob? PlanVideo(MediaStreamInfo source, VideoOutputSettings settings, Action<string> error, Action<string> warn)
    {
        var refused = false;
        void Refuse(string message)
        {
            error(message);
            refused = true;
        }

        if (settings.Codec is not (MediaCodec.Default or MediaCodec.H264 or MediaCodec.Hevc))
        {
            Refuse($"Media Foundation encodes H.264 and HEVC video here, not {settings.Codec}");
        }

        if (settings.EncoderName is not null)
        {
            Refuse("an encoder cannot be chosen by name through Media Foundation");
        }

        if (settings.Options is { Count: > 0 })
        {
            Refuse("encoder options are not supported by Media Foundation");
        }

        if (settings.Filters.Count > 0)
        {
            Refuse("video filters are not supported by Media Foundation: size and frame rate are settings of the stream");
        }

        if (settings.Quality is not null)
        {
            Refuse("constant quality is not supported by Media Foundation: give a bit rate");
        }

        if (settings.Speed is not null)
        {
            warn("encoder speed is ignored: Media Foundation encoders have no such setting");
        }

        if (settings.PixelFormat is not null)
        {
            warn("pixel format is ignored: Media Foundation encodes from NV12");
        }

        var native = source.Video ?? default;
        if (native.Width <= 0 || native.Height <= 0)
        {
            Refuse("the input's picture size is not known");
        }

        if (refused)
        {
            return null;
        }

        var width = settings.Width is > 0 ? settings.Width.Value : -1;
        var height = settings.Height is > 0 ? settings.Height.Value : -1;
        if (width < 0 && height < 0)
        {
            (width, height) = (native.Width, native.Height);
        }
        else if (height < 0)
        {
            height = (int)Math.Round(width * (double)native.Height / native.Width);
        }
        else if (width < 0)
        {
            width = (int)Math.Round(height * (double)native.Width / native.Height);
        }

        if ((width & 1) != 0 || (height & 1) != 0)
        {
            warn($"{width}x{height} has an odd side, which 4:2:0 video cannot have; the picture is written as {width + (width & 1)}x{height + (height & 1)}");
            width += width & 1;
            height += height & 1;
        }

        var rate = settings.FrameRate is { Denominator: > 0, Numerator: > 0 } asked ? asked
            : native.FrameRate is { Denominator: > 0, Numerator: > 0 } own ? own
            : new Rational(30, 1);
        var bitrate = settings.BitrateBitsPerSecond > 0
            ? settings.BitrateBitsPerSecond
            : (int)Math.Clamp(width * (long)height * (long)Math.Max(rate.Value, 1d) / 12, 200_000, 60_000_000);
        var codec = settings.Codec == MediaCodec.Default ? MediaCodec.H264 : settings.Codec;
        return new VideoJob(source.Index, codec, width, height, rate, bitrate, settings.KeyFrameInterval);
    }

    private static AudioJob? PlanAudio(MediaStreamInfo source, AudioOutputSettings settings, Action<string> error, Action<string> warn)
    {
        var refused = false;
        void Refuse(string message)
        {
            error(message);
            refused = true;
        }

        if (settings.Codec is not (MediaCodec.Default or MediaCodec.Aac))
        {
            Refuse($"Media Foundation encodes AAC audio here, not {settings.Codec}");
        }

        if (settings.EncoderName is not null)
        {
            Refuse("an encoder cannot be chosen by name through Media Foundation");
        }

        if (settings.Options is { Count: > 0 })
        {
            Refuse("encoder options are not supported by Media Foundation");
        }

        if (settings.Filters.Count > 0)
        {
            Refuse("audio filters are not supported by Media Foundation");
        }

        if (settings.Quality is not null)
        {
            Refuse("variable bit rate quality is not supported by Media Foundation: give a bit rate");
        }

        if (settings.SampleFormat is not null)
        {
            warn("sample format is ignored: AAC is encoded from 16-bit samples");
        }

        if (settings.ChannelMask is not null)
        {
            warn("the speaker layout is ignored: Media Foundation uses the conventional one for the channel count");
        }

        var native = source.Audio ?? default;
        var channels = settings.Channels ?? native.Channels;
        if (channels is not (1 or 2))
        {
            Refuse($"the Media Foundation AAC encoder takes one or two channels here, not {channels}: ask for Channels = 2");
        }

        int rate;
        if (settings.SampleRate is { } asked)
        {
            rate = asked;
            if (!AacRates.Contains(asked))
            {
                Refuse($"the Media Foundation AAC encoder takes 44100 or 48000 Hz, not {asked}");
            }
        }
        else
        {
            rate = AacRates.Contains(native.SampleRate) ? native.SampleRate : 44100;
            if (rate != native.SampleRate)
            {
                warn($"the input's {native.SampleRate} Hz is not a rate the AAC encoder takes; the audio is written at {rate} Hz");
            }
        }

        if (refused)
        {
            return null;
        }

        var wanted = settings.BitrateBitsPerSecond > 0 ? settings.BitrateBitsPerSecond : (channels == 1 ? 96_000 : 128_000);
        var bitrate = AacBitrates.OrderBy(b => Math.Abs(b - wanted)).First();
        if (bitrate != wanted)
        {
            warn($"the AAC encoder has no bit rate of {wanted}; it is written at {bitrate}");
        }

        return new AudioJob(source.Index, rate, channels, bitrate);
    }

    // ----------------------------------------------------------------- execution

    private sealed class StreamState(uint writerIndex, bool isVideo)
    {
        public uint WriterIndex { get; } = writerIndex;

        public bool IsVideo { get; } = isVideo;

        public bool Done { get; set; }
    }

    private TranscodeResult Execute(
        TranscodeRequest request, Plan plan, IProgress<TranscodeProgress>? progress, CancellationToken cancellation)
    {
        MfApi.Startup();
        var clock = Stopwatch.StartNew();
        using var partial = PartialOutput.Begin(request.Output);
        var written = Transcode(request.Inputs[0], partial.Path, plan, progress, cancellation, clock);

        // The writer is released by now, so the file is closed and can be moved.
        partial.Commit();
        return new TranscodeResult(request.Output, written, clock.Elapsed, plan.Issues.Where(i => i.Severity == TranscodeIssueSeverity.Warning).ToList());
    }

    private TimeSpan Transcode(
        string input, string output, Plan plan, IProgress<TranscodeProgress>? progress, CancellationToken cancellation, Stopwatch clock)
    {
        ComPtr reader = default;
        ComPtr writer = default;
        try
        {
            reader = OpenReader(input, plan.Video is not null);
            writer = OpenWriter(output);
            HResult.ThrowIfFailed(Mf.SetStreamSelection(reader.Pointer, MfApi.AllStreams, selected: false), "IMFSourceReader::SetStreamSelection");

            var states = new Dictionary<uint, StreamState>();
            if (plan.Video is { } video)
            {
                states[(uint)video.Source] = new StreamState(AddVideo(reader.Pointer, writer.Pointer, video), isVideo: true);
            }

            if (plan.Audio is { } audio)
            {
                states[(uint)audio.Source] = new StreamState(AddAudio(reader.Pointer, writer.Pointer, audio), isVideo: false);
            }

            if (plan.Start > TimeSpan.Zero)
            {
                HResult.ThrowIfFailed(Mf.SetCurrentPosition(reader.Pointer, plan.Start.Ticks), "IMFSourceReader::SetCurrentPosition");
            }

            HResult.ThrowIfFailed(Mf.BeginWriting(writer.Pointer), "IMFSinkWriter::BeginWriting");

            var length = (plan.End ?? plan.Info!.Duration) - plan.Start;
            if (length < TimeSpan.Zero)
            {
                length = TimeSpan.Zero;
            }

            var position = Pump(reader.Pointer, writer.Pointer, states, plan, length, progress, cancellation, clock);
            HResult.ThrowIfFailed(Mf.FinalizeWriter(writer.Pointer), "IMFSinkWriter::Finalize");
            progress?.Report(new TranscodeProgress(position, position, clock.Elapsed));
            return position;
        }
        finally
        {
            writer.Dispose();
            reader.Dispose();
        }
    }

    private static TimeSpan Pump(
        void* reader, void* writer, Dictionary<uint, StreamState> states, Plan plan, TimeSpan length,
        IProgress<TranscodeProgress>? progress, CancellationToken cancellation, Stopwatch clock)
    {
        var start = plan.Start.Ticks;
        var end = plan.End?.Ticks ?? long.MaxValue;
        var active = states.Count;
        var position = 0L;
        var lastReport = TimeSpan.Zero;

        while (active > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            HResult.ThrowIfFailed(
                Mf.ReadSample(reader, MfApi.AllStreams, out var index, out var flags, out var time, out var sample), "IMFSourceReader::ReadSample");
            try
            {
                if (!states.TryGetValue(index, out var state) || state.Done)
                {
                    continue;
                }

                if ((flags & MfApi.EndOfStream) != 0)
                {
                    state.Done = true;
                    active--;
                    continue;
                }

                if (sample is null)
                {
                    continue;
                }

                if (time >= end)
                {
                    // Nothing more is wanted from this stream; stop the reader producing it.
                    state.Done = true;
                    active--;
                    Mf.SetStreamSelection(reader, index, selected: false);
                    continue;
                }

                var duration = 0L;
                Mf.GetSampleDuration(sample, out duration);
                if (state.IsVideo ? time < start : time + duration <= start)
                {
                    continue;
                }

                var shifted = Math.Max(0, time - start);
                HResult.ThrowIfFailed(Mf.SetSampleTime(sample, shifted), "IMFSample::SetSampleTime");
                HResult.ThrowIfFailed(Mf.WriteSample(writer, state.WriterIndex, sample), "IMFSinkWriter::WriteSample");
                position = Math.Max(position, shifted + duration);

                if (progress is not null && clock.Elapsed - lastReport >= ProgressInterval)
                {
                    lastReport = clock.Elapsed;
                    progress.Report(new TranscodeProgress(TimeSpan.FromTicks(position), length, clock.Elapsed));
                }
            }
            finally
            {
                if (sample is not null)
                {
                    Com.Release(sample);
                }
            }
        }

        return TimeSpan.FromTicks(position);
    }

    private static ComPtr OpenReader(string input, bool scaleVideo)
    {
        void* attributes;
        HResult.ThrowIfFailed(MfApi.MFCreateAttributes(&attributes, 2), "MFCreateAttributes");
        try
        {
            // Without these the reader decodes to the picture's own size and rate.
            Mf.SetUint32(attributes, WinGuids.EnableAdvancedVideoProcessing, scaleVideo ? 1u : 0u);
            void* reader;
            HResult.ThrowIfFailed(MfApi.MFCreateSourceReaderFromURL(input, attributes, &reader), $"MFCreateSourceReaderFromURL({input})");
            return new ComPtr(reader);
        }
        finally
        {
            Com.Release(attributes);
        }
    }

    private ComPtr OpenWriter(string output)
    {
        void* attributes;
        HResult.ThrowIfFailed(MfApi.MFCreateAttributes(&attributes, 2), "MFCreateAttributes");
        try
        {
            Mf.SetUint32(attributes, WinGuids.EnableHardwareTransforms, _hardware ? 1u : 0u);
            Mf.SetUint32(attributes, WinGuids.DisableThrottling, 1);
            void* writer;
            HResult.ThrowIfFailed(MfApi.MFCreateSinkWriterFromURL(output, null, attributes, &writer), "MFCreateSinkWriterFromURL");
            return new ComPtr(writer);
        }
        finally
        {
            Com.Release(attributes);
        }
    }

    private static uint AddVideo(void* reader, void* writer, VideoJob job)
    {
        // What the reader hands over: raw frames at the size and rate the encoder is to get.
        using var decoded = NewType(WinGuids.MediaTypeVideo, WinGuids.VideoNv12);
        SetVideoShape(decoded.Pointer, job);
        Mf.SetUint32(decoded.Pointer, WinGuids.InterlaceMode, MfApi.InterlaceProgressive);
        HResult.ThrowIfFailed(Mf.SelectAndSet(reader, (uint)job.Source, decoded.Pointer), "IMFSourceReader::SetCurrentMediaType(video)");

        using var encoded = NewType(WinGuids.MediaTypeVideo, MediaFoundationFormatMap.ToEncoderSubtype(job.Codec));
        SetVideoShape(encoded.Pointer, job);
        Mf.SetUint32(encoded.Pointer, WinGuids.AverageBitrate, (uint)job.Bitrate);
        Mf.SetUint32(encoded.Pointer, WinGuids.InterlaceMode, MfApi.InterlaceProgressive);
        HResult.ThrowIfFailed(Mf.AddStream(writer, encoded.Pointer, out var index), "IMFSinkWriter::AddStream(video)");

        // The writer's input is the type the reader now produces, which carries the stride and the rest.
        HResult.ThrowIfFailed(Mf.GetCurrentMediaType(reader, (uint)job.Source, out var current), "IMFSourceReader::GetCurrentMediaType");
        try
        {
            // Encoder settings travel beside the input type: the encoder ignores the output type's own key frame spacing.
            void* parameters = null;
            if (job.KeyFrames > 0)
            {
                HResult.ThrowIfFailed(MfApi.MFCreateAttributes(&parameters, 1), "MFCreateAttributes");
                Mf.SetUint32(parameters, WinGuids.EncoderGopSize, (uint)job.KeyFrames);
            }

            try
            {
                HResult.ThrowIfFailed(Mf.SetInputMediaType(writer, index, current, parameters), "IMFSinkWriter::SetInputMediaType(video)");
            }
            finally
            {
                if (parameters is not null)
                {
                    Com.Release(parameters);
                }
            }
        }
        finally
        {
            Com.Release(current);
        }

        return index;
    }

    private static uint AddAudio(void* reader, void* writer, AudioJob job)
    {
        using var decoded = NewType(WinGuids.MediaTypeAudio, WinGuids.AudioPcm);
        Mf.SetUint32(decoded.Pointer, WinGuids.AudioBitsPerSample, 16);
        Mf.SetUint32(decoded.Pointer, WinGuids.AudioSamplesPerSecond, (uint)job.SampleRate);
        Mf.SetUint32(decoded.Pointer, WinGuids.AudioChannels, (uint)job.Channels);
        Mf.SetUint32(decoded.Pointer, WinGuids.AudioBlockAlignment, (uint)(job.Channels * 2));
        Mf.SetUint32(decoded.Pointer, WinGuids.AudioAverageBytesPerSecond, (uint)(job.SampleRate * job.Channels * 2));
        HResult.ThrowIfFailed(Mf.SelectAndSet(reader, (uint)job.Source, decoded.Pointer), "IMFSourceReader::SetCurrentMediaType(audio)");

        using var encoded = NewType(WinGuids.MediaTypeAudio, WinGuids.AudioAac);
        Mf.SetUint32(encoded.Pointer, WinGuids.AudioBitsPerSample, 16);
        Mf.SetUint32(encoded.Pointer, WinGuids.AudioSamplesPerSecond, (uint)job.SampleRate);
        Mf.SetUint32(encoded.Pointer, WinGuids.AudioChannels, (uint)job.Channels);
        Mf.SetUint32(encoded.Pointer, WinGuids.AudioAverageBytesPerSecond, (uint)(job.Bitrate / 8));
        HResult.ThrowIfFailed(Mf.AddStream(writer, encoded.Pointer, out var index), "IMFSinkWriter::AddStream(audio)");

        HResult.ThrowIfFailed(Mf.GetCurrentMediaType(reader, (uint)job.Source, out var current), "IMFSourceReader::GetCurrentMediaType");
        try
        {
            HResult.ThrowIfFailed(Mf.SetInputMediaType(writer, index, current, null), "IMFSinkWriter::SetInputMediaType(audio)");
        }
        finally
        {
            Com.Release(current);
        }

        return index;
    }

    private static void SetVideoShape(void* type, VideoJob job)
    {
        Mf.SetUint64(type, WinGuids.FrameSize, Mf.Pack((uint)job.Width, (uint)job.Height));
        Mf.SetUint64(type, WinGuids.FrameRate, Mf.Pack((uint)job.FrameRate.Numerator, (uint)job.FrameRate.Denominator));
        Mf.SetUint64(type, WinGuids.PixelAspectRatio, Mf.Pack(1, 1));
    }

    private static ComPtr NewType(Guid major, Guid subtype)
    {
        void* type;
        HResult.ThrowIfFailed(MfApi.MFCreateMediaType(&type), "MFCreateMediaType");
        var owner = new ComPtr(type);
        HResult.ThrowIfFailed(Mf.SetGuid(type, WinGuids.MajorType, major), "SetGUID(MAJOR_TYPE)");
        HResult.ThrowIfFailed(Mf.SetGuid(type, WinGuids.Subtype, subtype), "SetGUID(SUBTYPE)");
        return owner;
    }
}
