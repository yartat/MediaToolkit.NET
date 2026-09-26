#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg.Filtering;
using MediaToolkitNet.FFmpeg.Native;
using MediaToolkitNet.FFmpeg.Transcoding;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg.Reading;

/// <summary>How <see cref="FFmpegMediaReader"/> decodes.</summary>
public sealed record MediaReaderOptions
{
    /// <summary>
    /// Pixel layout to deliver video in. Nothing keeps what the decoder produces
    /// when the toolkit can describe it, and converts to
    /// <see cref="PixelFormat.Yuv420P"/> when it cannot, as with ten-bit video.
    /// </summary>
    public PixelFormat? VideoPixelFormat { get; init; }

    /// <summary>Width to scale video to; -1 with a height keeps the aspect ratio.</summary>
    public int? VideoWidth { get; init; }

    /// <summary>Height to scale video to; -1 with a width keeps the aspect ratio.</summary>
    public int? VideoHeight { get; init; }

    /// <summary>Sample layout to deliver audio in. Nothing keeps the decoder's.</summary>
    public SampleFormat? AudioSampleFormat { get; init; }

    /// <summary>Sample rate to deliver audio at. Nothing keeps the decoder's.</summary>
    public int? AudioSampleRate { get; init; }

    /// <summary>Channel count to deliver audio in. Nothing keeps the decoder's.</summary>
    public int? AudioChannels { get; init; }

    /// <summary>
    /// How far ahead of the current position <c>TryReadAt</c> decodes forward
    /// rather than seeking. Seeking costs a decode from the previous key frame,
    /// so for short hops reading on is cheaper.
    /// </summary>
    public TimeSpan SeekThreshold { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Decoder threads per stream; 0 uses as many as there are processors, up to 16.</summary>
    public int DecoderThreads { get; init; }

    /// <summary>Demuxer options, passed through as they are.</summary>
    public IReadOnlyDictionary<string, string>? DemuxerOptions { get; init; }
}

/// <summary>
/// Reads a media file stream by stream, the way FFMediaToolkit's
/// <c>MediaFile</c> does: pick a stream, read its frames in order or jump to a
/// time, and get subtitles as text.
/// </summary>
/// <remarks>
/// <para>
/// Frames are borrowed, as everywhere in this library: the handler receives a
/// view of the decoder's memory that is valid until it returns. Copy what has
/// to be kept.
/// </para>
/// <para>
/// Each stream reader opens the file on its own the first time it reads, so
/// streams can be read at different positions, and seeking one leaves the others
/// where they were. A stream that is never read costs nothing.
/// </para>
/// </remarks>
public sealed class FFmpegMediaReader : IDisposable
{
    private readonly List<MediaStreamReader> _readers = [];
    private bool _disposed;

    private FFmpegMediaReader(string path, MediaInfo info, TimeSpan origin, MediaReaderOptions options)
    {
        Path = path;
        Info = info;
        foreach (var stream in info.Streams)
        {
            MediaStreamReader? reader = stream.Kind switch
            {
                MediaStreamKind.Video => new VideoStreamReader(path, stream, origin, options),
                MediaStreamKind.Audio => new AudioStreamReader(path, stream, origin, options),
                MediaStreamKind.Subtitle => new SubtitleStreamReader(path, stream, origin, options),
                _ => null,
            };

            if (reader is not null)
            {
                _readers.Add(reader);
            }
        }
    }

    /// <summary>The file being read.</summary>
    public string Path { get; }

    /// <summary>What the file contains.</summary>
    public MediaInfo Info { get; }

    /// <summary>Every video stream, cover art included.</summary>
    public IReadOnlyList<VideoStreamReader> VideoStreams => [.. _readers.OfType<VideoStreamReader>()];

    /// <summary>Every audio stream.</summary>
    public IReadOnlyList<AudioStreamReader> AudioStreams => [.. _readers.OfType<AudioStreamReader>()];

    /// <summary>Every subtitle stream.</summary>
    public IReadOnlyList<SubtitleStreamReader> SubtitleStreams => [.. _readers.OfType<SubtitleStreamReader>()];

    /// <summary>The first video stream that is not a still picture, or <see langword="null"/>.</summary>
    public VideoStreamReader? Video => VideoStreams.FirstOrDefault(r => !r.Info.IsAttachedPicture);

    /// <summary>The first audio stream, or <see langword="null"/>.</summary>
    public AudioStreamReader? Audio => AudioStreams.FirstOrDefault();

    /// <summary>Opens a file and reads what it contains; no stream is decoded yet.</summary>
    /// <exception cref="MediaToolkitNetException">The file could not be opened.</exception>
    public static FFmpegMediaReader Open(string path, MediaReaderOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new MediaReaderOptions();

        using var demuxer = FFmpegDemuxer.Open(path, options: options.DemuxerOptions);
        unsafe
        {
            var origin = TimeSpan.FromTicks(AbiLayout.StartTimeOfInput(demuxer.Handle) * 10);
            return new FFmpegMediaReader(path, FFmpegProber.Describe(demuxer, path), origin, options);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var reader in _readers)
        {
            reader.Dispose();
        }
    }
}

/// <summary>What the stream readers share: their own demuxer, opened on first use.</summary>
public abstract unsafe class MediaStreamReader : IDisposable
{
    private readonly string _path;
    private FFmpegDemuxer? _demuxer;
    private AVPacketNative* _packet;
    private bool _endOfFile;

    private protected MediaStreamReader(string path, MediaStreamInfo info, TimeSpan origin, MediaReaderOptions options)
    {
        _path = path;
        Info = info;
        Origin = origin;
        Options = options;
    }

    /// <summary>The stream this reads.</summary>
    public MediaStreamInfo Info { get; }

    /// <summary>
    /// The time of the last frame or cue delivered, measured from the start of
    /// the file, or <see langword="null"/> before the first.
    /// </summary>
    public TimeSpan? Position { get; protected set; }

    private protected TimeSpan Origin { get; }

    private protected MediaReaderOptions Options { get; }

    private protected FFmpegDemuxer Demuxer => _demuxer ?? throw new InvalidOperationException("The stream is not open.");

    private protected bool Disposed { get; private set; }

    private protected int Threads => Options.DecoderThreads > 0 ? Options.DecoderThreads : Math.Min(Environment.ProcessorCount, 16);

    /// <summary>Goes back to the start of the stream.</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (_demuxer is not null)
        {
            SeekTo(TimeSpan.Zero);
        }

        Position = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;
        Close();
        if (_packet is not null)
        {
            fixed (AVPacketNative** packet = &_packet)
            {
                AV.av_packet_free(packet);
            }
        }

        _demuxer?.Dispose();
        _demuxer = null;
    }

    /// <summary>Opens the file for this stream, telling the demuxer to skip every other stream.</summary>
    private protected void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (_demuxer is not null)
        {
            return;
        }

        _demuxer = FFmpegDemuxer.Open(_path, options: Options.DemuxerOptions);
        for (var i = 0; i < _demuxer.Streams.Count; i++)
        {
            if (i != Info.Index)
            {
                AbiLayout.DiscardStream(_demuxer.StreamHandle(i));
            }
        }

        _packet = (AVPacketNative*)FFmpegError.CheckAlloc(AV.av_packet_alloc(), "av_packet_alloc");
        OpenCore();
    }

    /// <summary>Opens the decoder once the demuxer is there.</summary>
    private protected abstract void OpenCore();

    /// <summary>Frees the decoder and whatever converts its output.</summary>
    private protected abstract void Close();

    /// <summary>Drops what the decoder and converters hold, after a seek.</summary>
    private protected abstract void Discard();

    /// <summary>The next packet of this stream, or null at the end of the file; the reader owns it until the next call.</summary>
    private protected AVPacketNative* NextPacket()
    {
        while (!_endOfFile)
        {
            AV.av_packet_unref(_packet);
            if (!Demuxer.ReadPacket(_packet))
            {
                _endOfFile = true;
                break;
            }

            if (_packet->StreamIndex == Info.Index)
            {
                return _packet;
            }
        }

        return null;
    }

    /// <summary>Seeks the demuxer to the key frame at or before a time, and drops what the decoder holds.</summary>
    private protected void SeekTo(TimeSpan time)
    {
        Demuxer.Seek(Origin + time);
        _endOfFile = false;
        Discard();
    }

    /// <summary>A stream timestamp as a time from the start of the file.</summary>
    private protected TimeSpan TimeOf(long timestamp, AVRationalNative timeBase) =>
        FFmpegStreamFormats.ToTimeSpan(timestamp, timeBase) - Origin;
}

/// <summary>Reads decoded frames of one stream, converted as the options ask.</summary>
public abstract unsafe class FrameStreamReader : MediaStreamReader
{
    private FFmpegDecoder? _decoder;
    private FFmpegFilterGraph? _graph;
    private AVFrameHead* _decoded;
    private AVFrameHead* _converted;
    private AVFrameHead* _pending;
    private AVFrameHead* _candidate;
    private bool _hasPending;
    private bool _decoderDrained;
    private bool _graphBuilt;

    private protected FrameStreamReader(string path, MediaStreamInfo info, TimeSpan origin, MediaReaderOptions options)
        : base(path, info, origin, options)
    {
    }

    /// <summary>The time base of the frames the reader hands out.</summary>
    private protected AVRationalNative FrameTimeBase => _graph?.OutputTimeBase ?? AbiLayout.TimeBaseOf(Demuxer.StreamHandle(Info.Index));

    /// <summary>The graph, when the frames are converted.</summary>
    private protected FFmpegFilterGraph? Graph => _graph;

    private protected FFmpegDecoder Decoder => _decoder!;

    /// <summary>
    /// Reads the next frame and hands it to <paramref name="deliver"/>. False at
    /// the end of the stream.
    /// </summary>
    private protected bool ReadNext(Action<nint> deliver)
    {
        EnsureOpen();
        if (!NextFrame())
        {
            return false;
        }

        try
        {
            deliver((nint)_converted);
        }
        finally
        {
            AV.av_frame_unref(_converted);
        }

        return true;
    }

    /// <summary>
    /// Reads the frame showing at <paramref name="time"/>: the last one that
    /// starts at or before it. Short hops forward decode on; anything else seeks.
    /// </summary>
    private protected bool ReadAt(TimeSpan time, Func<nint, TimeSpan> endOf, Action<nint> deliver)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(time, TimeSpan.Zero);
        EnsureOpen();

        var ahead = Position is { } position ? time - position : TimeSpan.MinValue;
        if (ahead < TimeSpan.Zero || ahead > Options.SeekThreshold)
        {
            SeekTo(time);
        }

        // The candidate is the best frame so far; a frame that starts after the
        // time ends the search and waits in the stash for the next read.
        var found = false;
        var covered = false;
        while (NextFrame())
        {
            var start = TimeOf(_converted->Pts, FrameTimeBase);
            if (found && start > time)
            {
                AV.av_frame_move_ref(_pending, _converted);
                _hasPending = true;
                break;
            }

            AV.av_frame_unref(_candidate);
            AV.av_frame_move_ref(_candidate, _converted);
            found = true;

            // This one covers the time, or is the first frame and the time is before it.
            if (start >= time || endOf((nint)_candidate) > time)
            {
                covered = true;
                break;
            }
        }

        // Running out of frames before reaching the time means it is past the end.
        if (!found || (!covered && !_hasPending))
        {
            AV.av_frame_unref(_candidate);
            return false;
        }

        try
        {
            deliver((nint)_candidate);
        }
        finally
        {
            AV.av_frame_unref(_candidate);
        }

        return true;
    }

    /// <summary>The filter chain the options ask for, or null when frames go out as decoded.</summary>
    private protected abstract string? ConversionChain(AVFrameHead* first);

    /// <summary>Builds the graph for a chain, from the first decoded frame.</summary>
    private protected abstract FFmpegFilterGraph BuildGraph(AVFrameHead* first, string chain);

    private protected override void OpenCore()
    {
        _decoder = FFmpegDecoder.Open(Demuxer, Info.Index, Threads);
        _decoded = (AVFrameHead*)FFmpegError.CheckAlloc(AV.av_frame_alloc(), "av_frame_alloc");
        _converted = (AVFrameHead*)FFmpegError.CheckAlloc(AV.av_frame_alloc(), "av_frame_alloc");
        _pending = (AVFrameHead*)FFmpegError.CheckAlloc(AV.av_frame_alloc(), "av_frame_alloc");
        _candidate = (AVFrameHead*)FFmpegError.CheckAlloc(AV.av_frame_alloc(), "av_frame_alloc");
    }

    private protected override void Close()
    {
        _graph?.Dispose();
        _graph = null;
        _decoder?.Dispose();
        _decoder = null;
        FreeFrame(ref _decoded);
        FreeFrame(ref _converted);
        FreeFrame(ref _pending);
        FreeFrame(ref _candidate);
    }

    private protected override void Discard()
    {
        _decoder?.Flush();
        _decoderDrained = false;
        _hasPending = false;
        if (_pending is not null)
        {
            AV.av_frame_unref(_pending);
        }

        // A graph remembers timestamps and holds frames; after a seek it starts over.
        _graph?.Dispose();
        _graph = null;
        _graphBuilt = false;
    }

    private static void FreeFrame(ref AVFrameHead* frame)
    {
        if (frame is not null)
        {
            fixed (AVFrameHead** local = &frame)
            {
                AV.av_frame_free(local);
            }
        }
    }

    /// <summary>The next converted frame into <c>_converted</c>, from the stash, the graph or the decoder.</summary>
    private bool NextFrame()
    {
        if (_hasPending)
        {
            _hasPending = false;
            AV.av_frame_unref(_converted);
            AV.av_frame_move_ref(_converted, _pending);
            return true;
        }

        while (true)
        {
            if (_graph is not null && _graph.TryReceive(_converted))
            {
                return true;
            }

            if (_decoderDrained)
            {
                return false;
            }

            if (!Decode())
            {
                // The decoder is empty: flush the graph once, then take what it had.
                _decoderDrained = true;
                if (_graph is not null)
                {
                    _graph.TryPush(null);
                    continue;
                }

                return false;
            }

            if (!_graphBuilt)
            {
                _graphBuilt = true;
                if (ConversionChain(_decoded) is { } chain)
                {
                    _graph = BuildGraph(_decoded, chain);
                }
            }

            if (_graph is null)
            {
                AV.av_frame_unref(_converted);
                AV.av_frame_move_ref(_converted, _decoded);
                return true;
            }

            _graph.TryPush(_decoded);
            AV.av_frame_unref(_decoded);
        }
    }

    /// <summary>One decoded frame into <c>_decoded</c>, feeding packets as needed; false once the decoder is drained.</summary>
    private bool Decode()
    {
        while (true)
        {
            switch (Decoder.ReceiveFrame(_decoded))
            {
                case FFmpegDecoder.ReceiveResult.Frame:
                    return true;
                case FFmpegDecoder.ReceiveResult.EndOfStream:
                    return false;
            }

            var packet = NextPacket();
            Decoder.SendPacket(packet);
        }
    }

}

/// <summary>Reads the frames of one video stream.</summary>
public sealed unsafe class VideoStreamReader : FrameStreamReader
{
    internal VideoStreamReader(string path, MediaStreamInfo info, TimeSpan origin, MediaReaderOptions options)
        : base(path, info, origin, options)
    {
    }

    /// <summary>Reads the next frame. False at the end of the stream.</summary>
    /// <param name="handler">Receives the frame, which is valid only for the call.</param>
    public bool TryReadNext(VideoFrameHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return ReadNext(frame => Deliver((AVFrameHead*)frame, handler));
    }

    /// <summary>
    /// Reads the frame on screen at a time: the last one that starts at or
    /// before it. False when the time is past the end.
    /// </summary>
    /// <param name="time">Time from the start of the file.</param>
    /// <param name="handler">Receives the frame, which is valid only for the call.</param>
    public bool TryReadAt(TimeSpan time, VideoFrameHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return ReadAt(time, EndOf, frame => Deliver((AVFrameHead*)frame, handler));
    }

    private protected override string? ConversionChain(AVFrameHead* first)
    {
        var wanted = Options.VideoPixelFormat is { } pinned
            ? FFmpegFormatMap.ToAV(pinned)
            : FFmpegFormatMap.FromAV((AVPixelFormat)first->Format) == PixelFormat.Unknown
                ? AVPixelFormat.Yuv420P
                : (AVPixelFormat)first->Format;

        var parts = new List<string>();
        if (Options.VideoWidth is not null || Options.VideoHeight is not null)
        {
            parts.Add($"scale={Options.VideoWidth ?? -1}:{Options.VideoHeight ?? -1}");
        }

        if ((int)wanted != first->Format)
        {
            parts.Add($"format=pix_fmts={Utf8.ToManagedOrEmpty(AV.av_get_pix_fmt_name((int)wanted))}");
        }

        return parts.Count == 0 ? null : FilterChains.Join(parts);
    }

    private protected override FFmpegFilterGraph BuildGraph(AVFrameHead* first, string chain)
    {
        var stream = Demuxer.StreamHandle(Info.Index);

        // As in the transcoder: stating the colour space and range the decoder
        // reports keeps the first frame from looking like a format change.
        var colorSpace = (int)AV.GetOption(Decoder.Handle, "colorspace", 2);
        var colorRange = (int)AV.GetOption(Decoder.Handle, "color_range", 0);
        return FFmpegFilterGraph.ForVideo(
            first->Width,
            first->Height,
            first->Format,
            AbiLayout.TimeBaseOf(stream),
            AbiLayout.AvgFrameRateOf(stream),
            first->SampleAspectRatio,
            chain,
            colorSpace == 2 ? null : colorSpace,
            colorRange == 0 ? null : colorRange);
    }

    private TimeSpan EndOf(nint frame) =>
        TimeOf(((AVFrameHead*)frame)->Pts, FrameTimeBase) +
        (Info.Video is { FrameRate.Denominator: > 0, FrameRate.Numerator: > 0 } video
            ? TimeSpan.FromSeconds(video.FrameRate.Denominator / (double)video.FrameRate.Numerator)
            : TimeSpan.Zero);

    private void Deliver(AVFrameHead* frame, VideoFrameHandler handler)
    {
        var format = new VideoFormat(
            frame->Width,
            frame->Height,
            FFmpegFormatMap.FromAV((AVPixelFormat)frame->Format),
            Info.Video?.FrameRate ?? default);

        var planeCount = Math.Max(format.PixelFormat.PlaneCount(), 1);
        Span<nint> planes = stackalloc nint[MediaPlanes.MaxPlanes];
        Span<int> strides = stackalloc int[MediaPlanes.MaxPlanes];
        for (var plane = 0; plane < planeCount; plane++)
        {
            planes[plane] = (nint)frame->Data[plane];
            strides[plane] = frame->Linesize[plane];
        }

        var timestamp = TimeOf(frame->Pts, FrameTimeBase);
        Position = timestamp;
        var view = new VideoFrame(format, timestamp, planes, strides, planeCount);
        handler(in view);
    }
}

/// <summary>Reads the decoded audio of one stream.</summary>
public sealed unsafe class AudioStreamReader : FrameStreamReader
{
    private AudioFormat _decoded;

    internal AudioStreamReader(string path, MediaStreamInfo info, TimeSpan origin, MediaReaderOptions options)
        : base(path, info, origin, options)
    {
    }

    /// <summary>Reads the next block of audio. False at the end of the stream.</summary>
    /// <param name="handler">Receives the frame, which is valid only for the call.</param>
    public bool TryReadNext(AudioFrameHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return ReadNext(frame => Deliver((AVFrameHead*)frame, handler));
    }

    /// <summary>
    /// Reads the block of audio playing at a time. Blocks are what the decoder
    /// produces, a few tens of milliseconds each, so the block starts at or
    /// shortly before the time rather than exactly on it.
    /// </summary>
    /// <param name="time">Time from the start of the file.</param>
    /// <param name="handler">Receives the frame, which is valid only for the call.</param>
    public bool TryReadAt(TimeSpan time, AudioFrameHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return ReadAt(time, EndOf, frame => Deliver((AVFrameHead*)frame, handler));
    }

    private protected override void OpenCore()
    {
        base.OpenCore();
        var (rate, channels, mask) = FFmpegStreamFormats.ReadAudioOptions(Decoder.Handle);
        _decoded = new AudioFormat(rate, channels, SampleFormat.Unknown, mask);
    }

    private protected override string? ConversionChain(AVFrameHead* first)
    {
        var wantedFormat = Options.AudioSampleFormat is { } pinned
            ? FFmpegFormatMap.ToAV(pinned)
            : FFmpegFormatMap.FromAV((AVSampleFormat)first->Format) == SampleFormat.Unknown
                ? AVSampleFormat.FltP
                : (AVSampleFormat)first->Format;

        if (Options.AudioSampleRate is null && Options.AudioChannels is null && (int)wantedFormat == first->Format)
        {
            return null;
        }

        var parts = new List<string> { $"sample_fmts={Utf8.ToManagedOrEmpty(AV.av_get_sample_fmt_name((int)wantedFormat))}" };
        if (Options.AudioSampleRate is { } rate)
        {
            parts.Add($"sample_rates={rate}");
        }

        if (Options.AudioChannels is { } channels)
        {
            var layout = AVChannelLayoutNative.Of(channels);
            parts.Add($"channel_layouts={FFmpegFilterGraph.DescribeLayout(&layout)}");
        }

        return "aformat=" + string.Join(':', parts);
    }

    private protected override FFmpegFilterGraph BuildGraph(AVFrameHead* first, string chain)
    {
        var layout = AVChannelLayoutNative.Of(_decoded.Channels, _decoded.ChannelMask);
        return FFmpegFilterGraph.ForAudio(
            _decoded.SampleRate,
            first->Format,
            FFmpegFilterGraph.DescribeLayout(&layout),
            AbiLayout.TimeBaseOf(Demuxer.StreamHandle(Info.Index)),
            chain);
    }

    private TimeSpan EndOf(nint frame)
    {
        var raw = (AVFrameHead*)frame;
        var rate = Graph?.OutputAudioFormat.SampleRate ?? _decoded.SampleRate;
        return TimeOf(raw->Pts, FrameTimeBase) + (rate > 0 ? TimeSpan.FromSeconds(raw->NbSamples / (double)rate) : TimeSpan.Zero);
    }

    private void Deliver(AVFrameHead* frame, AudioFrameHandler handler)
    {
        var format = Graph is { } graph
            ? graph.OutputAudioFormat
            : _decoded with { SampleFormat = FFmpegFormatMap.FromAV((AVSampleFormat)frame->Format) };

        var planeCount = format.SampleFormat.IsPlanar() ? Math.Min(format.Channels, MediaPlanes.MaxPlanes) : 1;
        Span<nint> planes = stackalloc nint[MediaPlanes.MaxPlanes];
        for (var plane = 0; plane < planeCount; plane++)
        {
            planes[plane] = (nint)frame->Data[plane];
        }

        var timestamp = TimeOf(frame->Pts, FrameTimeBase);
        Position = timestamp;
        var view = new AudioFrame(format, timestamp, frame->NbSamples, planes, planeCount);
        handler(in view);
    }
}

/// <summary>Reads one subtitle stream as text.</summary>
public sealed unsafe class SubtitleStreamReader : MediaStreamReader
{
    private static readonly AVRationalNative Microseconds = new(1, AVConstants.TimeBase);

    private FFmpegDecoder? _decoder;
    private byte* _subtitle;

    internal SubtitleStreamReader(string path, MediaStreamInfo info, TimeSpan origin, MediaReaderOptions options)
        : base(path, info, origin, options)
    {
    }

    /// <summary>Reads the next cue. False at the end of the stream.</summary>
    /// <exception cref="NotSupportedException">The stream is bitmap subtitles, which have no text.</exception>
    public bool TryReadNext(out SubtitleCue cue)
    {
        if (!Info.IsTextSubtitle)
        {
            throw new NotSupportedException($"{Info} is bitmap subtitles, which have no text to read.");
        }

        EnsureOpen();
        while (NextPacket() is var packet && packet is not null)
        {
            if (Decode(packet) is { } decoded)
            {
                cue = decoded;
                Position = cue.Start;
                return true;
            }
        }

        cue = null!;
        return false;
    }

    /// <summary>Every cue from the current position to the end.</summary>
    public IEnumerable<SubtitleCue> ReadAll()
    {
        while (TryReadNext(out var cue))
        {
            yield return cue;
        }
    }

    private protected override void OpenCore()
    {
        AbiLayout.RequireSubtitleLayout();
        _decoder = FFmpegDecoder.Open(Demuxer, Info.Index);
        _subtitle = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(AbiLayout.SubtitleSize);
    }

    private protected override void Close()
    {
        _decoder?.Dispose();
        _decoder = null;
        if (_subtitle is not null)
        {
            System.Runtime.InteropServices.NativeMemory.Free(_subtitle);
            _subtitle = null;
        }
    }

    private protected override void Discard() => _decoder?.Flush();

    private SubtitleCue? Decode(AVPacketNative* packet)
    {
        new Span<byte>(_subtitle, AbiLayout.SubtitleSize).Clear();
        int got;
        if (AV.avcodec_decode_subtitle2(_decoder!.Handle, _subtitle, &got, packet) < 0 || got == 0)
        {
            return null;
        }

        try
        {
            var (pts, startMs, endMs) = AbiLayout.TimingOfSubtitle(_subtitle);
            if (pts == AVConstants.NoPtsValue)
            {
                pts = AV.av_rescale_q(packet->Pts, AbiLayout.TimeBaseOf(Demuxer.StreamHandle(Info.Index)), Microseconds);
            }

            var texts = new List<string>();
            var events = new List<string>();
            for (var i = 0; i < AbiLayout.RectCountOf(_subtitle); i++)
            {
                var (type, text, ass) = AbiLayout.RectOf(_subtitle, i);
                if (type == AVConstants.SubtitleAss && ass is not null)
                {
                    events.Add(ass);
                    texts.Add(SubtitleCue.TextOfAssEvent(ass));
                }
                else if (type == AVConstants.SubtitleText && text is not null)
                {
                    texts.Add(text);
                }
            }

            if (texts.Count == 0)
            {
                return null;
            }

            var start = TimeOf(pts + (startMs * 1000L), Microseconds);
            var end = TimeOf(pts + (endMs * 1000L), Microseconds);
            return new SubtitleCue(start, end, string.Join('\n', texts))
            {
                Ass = events.Count == 0 ? null : string.Join('\n', events),
            };
        }
        finally
        {
            AV.avsubtitle_free(_subtitle);
        }
    }
}
