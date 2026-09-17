using MediaToolkitNet.Core.Formats;
using MediaToolkitNet.FFmpeg.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg;

/// <summary>Description of one stream inside a container.</summary>
/// <param name="Index">Stream index, matching <c>AVStream::index</c>.</param>
/// <param name="MediaType">Video, audio or something else.</param>
/// <param name="CodecId">Raw <c>AVCodecID</c>; treated as an opaque value.</param>
/// <param name="CodecName">Human-readable codec name from <c>avcodec_get_name</c>.</param>
/// <param name="TimeBase">Time base the stream timestamps are expressed in.</param>
/// <param name="AverageFrameRate">Average frame rate, or 0/0 for audio and variable rate streams.</param>
/// <param name="Duration">Stream duration, or <see cref="TimeSpan.Zero"/> when the container does not state one.</param>
public readonly record struct FFmpegStreamInfo(
    int Index,
    AVMediaType MediaType,
    int CodecId,
    string CodecName,
    Rational TimeBase,
    Rational AverageFrameRate,
    TimeSpan Duration);

/// <summary>
/// Thin wrapper over <c>AVFormatContext</c> in demuxing mode: opens an input,
/// exposes its streams and pulls packets out of it.
/// </summary>
public sealed unsafe class FFmpegDemuxer : IDisposable
{
    private void* _context;
    private FFmpegStreamInfo[] _streams = [];
    private bool _disposed;

    private FFmpegDemuxer(void* context)
    {
        _context = context;
    }

    /// <summary>Streams found in the container.</summary>
    public IReadOnlyList<FFmpegStreamInfo> Streams => _streams;

    /// <summary>Raw <c>AVFormatContext</c> pointer, for callers that need to go lower.</summary>
    public void* Handle => _context;

    /// <summary>
    /// Opens a file, a URL or a capture device.
    /// </summary>
    /// <param name="url">Path, URL, or device name when <paramref name="inputFormat"/> is set.</param>
    /// <param name="inputFormat">
    /// Forced demuxer name such as <c>dshow</c>, <c>v4l2</c> or <c>avfoundation</c>.
    /// Requires libavdevice for the device demuxers.
    /// </param>
    /// <param name="options">Demuxer options, passed through as an AVDictionary.</param>
    public static FFmpegDemuxer Open(
        string url,
        string? inputFormat = null,
        IReadOnlyDictionary<string, string>? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        FFmpegLibraries.EnsureLoaded();

        void* format = null;
        if (!string.IsNullOrEmpty(inputFormat))
        {
            Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var name = new Utf8Scoped(inputFormat, scratch);
            format = AV.av_find_input_format(name.Pointer);
            if (format is null)
            {
                throw new Core.MediaToolkitNetException(
                    FFmpegLibraries.BackendName, $"demuxer \"{inputFormat}\" not found", AVConstants.ErrorInvalid);
            }
        }

        void* dictionary = null;
        if (options is not null)
        {
            foreach (var (key, value) in options)
            {
                AV.DictionarySet(&dictionary, key, value);
            }
        }

        void* context = null;
        try
        {
            Span<byte> urlScratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var urlUtf8 = new Utf8Scoped(url, urlScratch);
            FFmpegError.Check(AV.avformat_open_input(&context, urlUtf8.Pointer, format, &dictionary), $"avformat_open_input({url})");
            FFmpegError.Check(AV.avformat_find_stream_info(context, null), "avformat_find_stream_info");
        }
        catch
        {
            if (context is not null)
            {
                AV.avformat_close_input(&context);
            }

            throw;
        }
        finally
        {
            if (dictionary is not null)
            {
                AV.av_dict_free(&dictionary);
            }
        }

        AbiLayout.ValidateOpenInput(context);

        var demuxer = new FFmpegDemuxer(context);
        demuxer.ReadStreamInfo();
        return demuxer;
    }

    private void ReadStreamInfo()
    {
        var count = AbiLayout.NbStreams(_context);
        var result = new FFmpegStreamInfo[count];
        for (var i = 0; i < count; i++)
        {
            var stream = AbiLayout.Stream(_context, i);
            var parameters = AbiLayout.CodecParametersOf(stream);
            var codecId = AbiLayout.CodecIdOf(parameters);
            var timeBase = AbiLayout.TimeBaseOf(stream).ToRational();
            var rawDuration = AbiLayout.DurationOf(stream);

            result[i] = new FFmpegStreamInfo(
                i,
                AbiLayout.CodecTypeOf(parameters),
                codecId,
                Interop.Utf8.ToManagedOrEmpty(AV.avcodec_get_name(codecId)),
                timeBase,
                AbiLayout.AvgFrameRateOf(stream).ToRational(),
                rawDuration == AVConstants.NoPtsValue || timeBase.Denominator == 0
                    ? TimeSpan.Zero
                    : TimeSpan.FromSeconds(rawDuration * timeBase.Value));
        }

        _streams = result;
    }

    /// <summary>Longest stream duration, or <see cref="TimeSpan.Zero"/> when nothing reports one.</summary>
    public TimeSpan Duration
    {
        get
        {
            var longest = TimeSpan.Zero;
            foreach (var stream in _streams)
            {
                if (stream.Duration > longest)
                {
                    longest = stream.Duration;
                }
            }

            return longest;
        }
    }

    /// <summary>Returns the first stream of the given type, or <see langword="null"/>.</summary>
    public FFmpegStreamInfo? FindStream(AVMediaType type)
    {
        foreach (var stream in _streams)
        {
            if (stream.MediaType == type)
            {
                return stream;
            }
        }

        return null;
    }

    /// <summary>Raw <c>AVStream</c> pointer for the given index.</summary>
    public void* StreamHandle(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _streams.Length);
        return AbiLayout.Stream(_context, index);
    }

    /// <summary>Raw <c>AVCodecParameters</c> pointer for the given stream.</summary>
    public void* CodecParameters(int index) => AbiLayout.CodecParametersOf(StreamHandle(index));

    /// <summary>
    /// Reads the next packet into <paramref name="packet"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a packet was read, <see langword="false"/> at
    /// end of stream. The caller owns the packet reference and must call
    /// <c>av_packet_unref</c>.
    /// </returns>
    public bool ReadPacket(AVPacketNative* packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = AV.av_read_frame(_context, packet);
        if (result == AVConstants.ErrorEof)
        {
            return false;
        }

        FFmpegError.Check(result, "av_read_frame");
        return true;
    }

    /// <summary>Seeks the container so that the next packets are at or before <paramref name="position"/>.</summary>
    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var timestamp = (long)(position.TotalSeconds * AVConstants.TimeBase);
        FFmpegError.Check(
            AV.av_seek_frame(_context, -1, timestamp, AVConstants.SeekFlagBackward),
            $"av_seek_frame({position})");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_context is not null)
        {
            fixed (void** context = &_context)
            {
                AV.avformat_close_input(context);
            }
        }
    }
}
