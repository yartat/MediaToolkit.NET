using MediaToolkitNet.Core.Formats;
using MediaToolkitNet.FFmpeg.Native;

namespace MediaToolkitNet.FFmpeg;

/// <summary>
/// Thin wrapper over a decoding <c>AVCodecContext</c>: packets go in, frames
/// come out. Follows FFmpeg's send/receive protocol without hiding it.
/// </summary>
public sealed unsafe class FFmpegDecoder : IDisposable
{
    private void* _context;
    private bool _disposed;

    private FFmpegDecoder(void* context, AVMediaType mediaType, Rational streamTimeBase)
    {
        _context = context;
        MediaType = mediaType;
        StreamTimeBase = streamTimeBase;
    }

    /// <summary>Video or audio.</summary>
    public AVMediaType MediaType { get; }

    /// <summary>Time base of the stream this decoder reads from.</summary>
    public Rational StreamTimeBase { get; }

    /// <summary>Raw <c>AVCodecContext</c> pointer.</summary>
    public void* Handle => _context;

    /// <summary>
    /// Audio format the decoder produces. Read through AVOptions, so it does not
    /// depend on the struct layout. Meaningless for video decoders.
    /// </summary>
    public AudioFormat AudioFormat { get; private set; }

    /// <summary>
    /// Opens a decoder for the given stream of <paramref name="demuxer"/>.
    /// </summary>
    /// <param name="demuxer">Open input to take codec parameters from.</param>
    /// <param name="streamIndex">Index of the stream to decode.</param>
    /// <param name="threadCount">Decode threads; 0 lets FFmpeg pick.</param>
    public static FFmpegDecoder Open(FFmpegDemuxer demuxer, int streamIndex, int threadCount = 0)
    {
        ArgumentNullException.ThrowIfNull(demuxer);

        var info = demuxer.Streams[streamIndex];
        var parameters = demuxer.CodecParameters(streamIndex);

        var codec = AV.avcodec_find_decoder(info.CodecId);
        if (codec is null)
        {
            throw new Core.MediaToolkitNetException(
                FFmpegLibraries.BackendName,
                $"no decoder found for \"{info.CodecName}\"",
                AVConstants.ErrorDecoderNotFound);
        }

        var context = AV.avcodec_alloc_context3(codec);
        FFmpegError.CheckAlloc(context, "avcodec_alloc_context3");

        try
        {
            FFmpegError.Check(
                AV.avcodec_parameters_to_context(context, parameters), "avcodec_parameters_to_context");

            if (threadCount > 0)
            {
                AV.SetOption(context, "threads", threadCount);
            }

            FFmpegError.Check(AV.avcodec_open2(context, codec, null), $"avcodec_open2({info.CodecName})");
        }
        catch
        {
            AV.avcodec_free_context(&context);
            throw;
        }

        var decoder = new FFmpegDecoder(context, info.MediaType, info.TimeBase);
        if (info.MediaType == AVMediaType.Audio)
        {
            decoder.ReadAudioFormat();
        }

        return decoder;
    }

    private void ReadAudioFormat()
    {
        // "ar" and "ac" are AVCodecContext AVOptions, so reading them avoids
        // depending on where sample_rate and ch_layout sit in the struct.
        var sampleRate = (int)AV.GetOption(_context, "ar");
        var channels = (int)AV.GetOption(_context, "ac");
        AudioFormat = new AudioFormat(sampleRate, channels, SampleFormat.Unknown);
    }

    /// <summary>
    /// Hands a packet to the decoder. Pass <see langword="null"/> to start
    /// draining at end of stream.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the decoder is full and needs
    /// <see cref="ReceiveFrame"/> to be called first.
    /// </returns>
    public bool SendPacket(AVPacketNative* packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = AV.avcodec_send_packet(_context, packet);
        if (result == AVConstants.ErrorAgain)
        {
            return false;
        }

        if (result == AVConstants.ErrorEof)
        {
            return true;
        }

        FFmpegError.Check(result, "avcodec_send_packet");
        return true;
    }

    /// <summary>Outcome of <see cref="ReceiveFrame"/>.</summary>
    public enum ReceiveResult
    {
        /// <summary>A frame was written into the supplied AVFrame.</summary>
        Frame,

        /// <summary>Nothing available yet; send more packets.</summary>
        NeedMoreInput,

        /// <summary>The decoder has been fully drained.</summary>
        EndOfStream,
    }

    /// <summary>Pulls one decoded frame out of the decoder.</summary>
    public ReceiveResult ReceiveFrame(AVFrameHead* frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = AV.avcodec_receive_frame(_context, frame);
        return result switch
        {
            0 => ReceiveResult.Frame,
            AVConstants.ErrorAgain => ReceiveResult.NeedMoreInput,
            AVConstants.ErrorEof => ReceiveResult.EndOfStream,
            _ => throw new Core.MediaToolkitNetException(
                FFmpegLibraries.BackendName,
                $"avcodec_receive_frame: {FFmpegError.Describe(result)}",
                result),
        };
    }

    /// <summary>Drops any buffered state, e.g. after seeking.</summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AV.avcodec_flush_buffers(_context);
    }

    /// <summary>
    /// Builds a <see cref="VideoFormat"/> from a decoded frame. The pixel format
    /// and geometry come from the frame itself, the frame rate from the stream.
    /// </summary>
    public VideoFormat DescribeVideo(AVFrameHead* frame, Rational frameRate) =>
        new(frame->Width, frame->Height, FFmpegFormatMap.FromAV((AVPixelFormat)frame->Format), frameRate);

    /// <summary>Builds an <see cref="AudioFormat"/> from a decoded frame.</summary>
    public AudioFormat DescribeAudio(AVFrameHead* frame) =>
        AudioFormat with { SampleFormat = FFmpegFormatMap.FromAV((AVSampleFormat)frame->Format) };

    /// <summary>Converts a frame timestamp into wall-clock time using the stream time base.</summary>
    public TimeSpan TimestampOf(AVFrameHead* frame)
    {
        var pts = frame->Pts;
        if (pts == AVConstants.NoPtsValue || StreamTimeBase.Denominator == 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(pts * StreamTimeBase.Value);
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
                AV.avcodec_free_context(context);
            }
        }
    }
}
