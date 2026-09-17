using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg.Native;

/// <summary>
/// Every direct struct field access this binding performs, in one place.
/// </summary>
/// <remarks>
/// <para>
/// FFmpeg exposes most of its state through public structs whose layout changes
/// between major releases, so the list below is deliberately as short as
/// possible. Everything that FFmpeg offers an accessor or an AVOption for is
/// read that way instead: codec parameters travel through
/// <c>avcodec_parameters_to_context</c>, bitrate and GOP size are set with
/// <c>av_opt_set_int</c>, and the encoder frame size is read with
/// <c>av_opt_get_int</c>.
/// </para>
/// <para>
/// The offsets are for FFmpeg 7.x on a 64-bit target.
/// <see cref="Validate"/> checks them against the values FFmpeg documents for a
/// freshly allocated object, so a mismatched build fails with a clear message
/// instead of corrupting memory.
/// </para>
/// </remarks>
public static unsafe class AbiLayout
{
    /// <summary>Offset of <c>AVFormatContext::pb</c>.</summary>
    public const int FormatContextPb = 32;

    /// <summary>Offset of <c>AVFormatContext::nb_streams</c>.</summary>
    public const int FormatContextNbStreams = 44;

    /// <summary>Offset of <c>AVFormatContext::streams</c>.</summary>
    public const int FormatContextStreams = 48;

    /// <summary>Offset of <c>AVStream::index</c>.</summary>
    public const int StreamIndex = 8;

    /// <summary>Offset of <c>AVStream::time_base</c>.</summary>
    public const int StreamTimeBase = 24;

    /// <summary>Offset of <c>AVStream::start_time</c>.</summary>
    public const int StreamStartTime = 32;

    /// <summary>Offset of <c>AVStream::duration</c>.</summary>
    public const int StreamDuration = 40;

    /// <summary>Offset of <c>AVStream::avg_frame_rate</c>.</summary>
    public const int StreamAvgFrameRate = 80;

    /// <summary>Offset of <c>AVStream::codecpar</c>.</summary>
    public const int StreamCodecPar = 208;

    /// <summary>Offset of <c>AVCodecParameters::codec_type</c>.</summary>
    public const int CodecParametersCodecType = 0;

    /// <summary>Offset of <c>AVCodecParameters::codec_id</c>.</summary>
    public const int CodecParametersCodecId = 4;

    /// <summary>Offset of <c>AVCodec::type</c>.</summary>
    public const int CodecType = 16;

    /// <summary>Offset of <c>AVCodec::id</c>.</summary>
    public const int CodecId = 20;

    /// <summary>
    /// Offset of <c>AVCodecParameters::format</c>, discovered at startup.
    /// See <see cref="ProbeCodecParameters"/>.
    /// </summary>
    public static int CodecParametersFormat { get; private set; } = -1;

    /// <summary>Offset of <c>AVCodecParameters::bit_rate</c>, discovered at startup.</summary>
    public static int CodecParametersBitRate { get; private set; } = -1;

    /// <summary>Offset of <c>AVCodecParameters::width</c>, discovered at startup.</summary>
    public static int CodecParametersWidth { get; private set; } = -1;

    /// <summary>Offset of <c>AVCodecParameters::height</c>, discovered at startup.</summary>
    public static int CodecParametersHeight { get; private set; } = -1;

    /// <summary>True when <see cref="ProbeCodecParameters"/> located every offset it looks for.</summary>
    public static bool CodecParametersLayoutVerified { get; private set; }

    /// <summary>Offset of <c>AVFrame::sample_rate</c>, discovered at startup.</summary>
    public static int FrameSampleRate { get; private set; } = -1;

    /// <summary>Offset of <c>AVFrame::ch_layout</c>, discovered at startup.</summary>
    public static int FrameChannelLayout { get; private set; } = -1;

    /// <summary>
    /// True when the audio fields of AVFrame were located and a write round-trip
    /// through <c>av_frame_get_buffer</c> confirmed them. Audio encoding is
    /// refused when this is false.
    /// </summary>
    public static bool AudioFrameLayoutVerified { get; private set; }

    /// <summary>Offset of <c>AVCodecContext::codec_type</c>. Read only by <see cref="Validate"/>.</summary>
    public const int CodecContextCodecType = 12;

    /// <summary>Offset of <c>AVCodecContext::time_base</c>.</summary>
    public const int CodecContextTimeBase = 100;

    /// <summary>Offset of <c>AVCodecContext::pix_fmt</c>. Read only by <see cref="Validate"/>.</summary>
    public const int CodecContextPixFmt = 136;

    /// <summary>
    /// True when the encoder-side offsets passed <see cref="Validate"/>. Encoding
    /// is refused when this is false.
    /// </summary>
    public static bool EncoderLayoutVerified { get; private set; }

    // ------------------------------------------------------------- accessors

    /// <summary>Reads <c>AVFormatContext::nb_streams</c>.</summary>
    public static int NbStreams(void* formatContext) => *(int*)((byte*)formatContext + FormatContextNbStreams);

    /// <summary>Reads <c>AVFormatContext::streams[index]</c>.</summary>
    public static void* Stream(void* formatContext, int index) =>
        (*(void***)((byte*)formatContext + FormatContextStreams))[index];

    /// <summary>Writes <c>AVFormatContext::pb</c>.</summary>
    public static void SetPb(void* formatContext, void* pb) => *(void**)((byte*)formatContext + FormatContextPb) = pb;

    /// <summary>Reads <c>AVFormatContext::pb</c>.</summary>
    public static void* GetPb(void* formatContext) => *(void**)((byte*)formatContext + FormatContextPb);

    /// <summary>Reads <c>AVStream::index</c>.</summary>
    public static int StreamIndexOf(void* stream) => *(int*)((byte*)stream + StreamIndex);

    /// <summary>Reads <c>AVStream::time_base</c>.</summary>
    public static AVRationalNative TimeBaseOf(void* stream) => *(AVRationalNative*)((byte*)stream + StreamTimeBase);

    /// <summary>Writes <c>AVStream::time_base</c>.</summary>
    public static void SetTimeBase(void* stream, AVRationalNative value) =>
        *(AVRationalNative*)((byte*)stream + StreamTimeBase) = value;

    /// <summary>Reads <c>AVStream::duration</c>, in stream time base units.</summary>
    public static long DurationOf(void* stream) => *(long*)((byte*)stream + StreamDuration);

    /// <summary>Reads <c>AVStream::start_time</c>, in stream time base units.</summary>
    public static long StartTimeOf(void* stream) => *(long*)((byte*)stream + StreamStartTime);

    /// <summary>Reads <c>AVStream::avg_frame_rate</c>.</summary>
    public static AVRationalNative AvgFrameRateOf(void* stream) =>
        *(AVRationalNative*)((byte*)stream + StreamAvgFrameRate);

    /// <summary>Reads <c>AVStream::codecpar</c>.</summary>
    public static void* CodecParametersOf(void* stream) => *(void**)((byte*)stream + StreamCodecPar);

    /// <summary>Reads <c>AVCodecParameters::codec_type</c>.</summary>
    public static AVMediaType CodecTypeOf(void* codecParameters) =>
        (AVMediaType)(*(int*)((byte*)codecParameters + CodecParametersCodecType));

    /// <summary>Reads <c>AVCodecParameters::codec_id</c>.</summary>
    public static int CodecIdOf(void* codecParameters) =>
        *(int*)((byte*)codecParameters + CodecParametersCodecId);

    /// <summary>Reads <c>AVCodec::id</c> from a codec descriptor.</summary>
    public static int IdOfCodec(void* codec) => *(int*)((byte*)codec + CodecId);

    /// <summary>Reads <c>AVCodec::type</c> from a codec descriptor.</summary>
    public static AVMediaType TypeOfCodec(void* codec) => (AVMediaType)(*(int*)((byte*)codec + CodecType));

    /// <summary>
    /// Fills the parts of an <c>AVCodecParameters</c> that this binding uses to
    /// configure an encoder. Everything else is left at its default and filled
    /// in by <c>avcodec_parameters_from_context</c> once the encoder is open.
    /// </summary>
    /// <param name="parameters">Target allocated by <c>avcodec_parameters_alloc</c>.</param>
    /// <param name="mediaType">Video or audio.</param>
    /// <param name="codecId">Value read from the chosen <c>AVCodec</c>.</param>
    /// <param name="format">An AVPixelFormat for video or an AVSampleFormat for audio.</param>
    /// <param name="bitRate">Target bit rate, or 0 to leave it to the encoder.</param>
    /// <param name="width">Frame width; ignored for audio.</param>
    /// <param name="height">Frame height; ignored for audio.</param>
    public static void FillCodecParameters(
        void* parameters,
        AVMediaType mediaType,
        int codecId,
        int format,
        long bitRate,
        int width,
        int height)
    {
        RequireCodecParametersLayout();

        var raw = (byte*)parameters;
        *(int*)(raw + CodecParametersCodecType) = (int)mediaType;
        *(int*)(raw + CodecParametersCodecId) = codecId;
        *(int*)(raw + CodecParametersFormat) = format;
        *(long*)(raw + CodecParametersBitRate) = bitRate;

        if (mediaType == AVMediaType.Video)
        {
            *(int*)(raw + CodecParametersWidth) = width;
            *(int*)(raw + CodecParametersHeight) = height;
        }
    }

    /// <summary>Writes <c>AVCodecContext::time_base</c>.</summary>
    public static void SetCodecTimeBase(void* codecContext, AVRationalNative value)
    {
        RequireEncoderLayout();
        *(AVRationalNative*)((byte*)codecContext + CodecContextTimeBase) = value;
    }

    // ------------------------------------------------------------ validation

    /// <summary>
    /// Verifies the mapped offsets against the documented defaults of a freshly
    /// allocated AVFrame, AVPacket and AVCodecContext.
    /// </summary>
    /// <exception cref="MediaBackendUnavailableException">A required offset does not match.</exception>
    internal static void Validate()
    {
        ValidateFrame();
        ValidatePacket();
        CodecParametersLayoutVerified = ProbeCodecParameters();
        EncoderLayoutVerified = CodecParametersLayoutVerified && ValidateCodecContext();
        AudioFrameLayoutVerified = ProbeAudioFrameFields() && ConfirmAudioFrameFields();
    }

    /// <summary>
    /// Prepares an AVFrame to receive raw audio for an encoder.
    /// </summary>
    /// <remarks>
    /// FFmpeg validates the frame properties against the encoder context, so
    /// <c>sample_rate</c> and <c>ch_layout</c> have to be set even though they
    /// live past the stable head of the struct. Their offsets are discovered by
    /// <see cref="ProbeAudioFrameFields"/> rather than hard-coded.
    /// </remarks>
    public static void PrepareAudioFrame(AVFrameHead* frame, int sampleRate, int channels, int sampleFormat, int samples)
    {
        if (!AudioFrameLayoutVerified)
        {
            throw Fail("the AVFrame audio field offsets could not be determined, so audio encoding is unavailable.");
        }

        frame->Format = sampleFormat;
        frame->NbSamples = samples;
        *(int*)((byte*)frame + FrameSampleRate) = sampleRate;
        *(AVChannelLayoutNative*)((byte*)frame + FrameChannelLayout) = AVChannelLayoutNative.Default(channels);
    }

    /// <summary>
    /// Discovers the <c>AVCodecParameters</c> field offsets at runtime instead of
    /// hard-coding them.
    /// </summary>
    /// <remarks>
    /// A freshly allocated <c>AVCodecParameters</c> has <c>profile</c> and
    /// <c>level</c> both set to FF_PROFILE_UNKNOWN / FF_LEVEL_UNKNOWN, which is
    /// -99. No other field defaults to that value, so a pair of adjacent -99
    /// integers pins the position of <c>profile</c>, and every other field this
    /// binding writes sits at a fixed distance from it:
    /// <c>level</c> = P+4, <c>width</c> = P+8, <c>height</c> = P+12, and
    /// <c>bit_rate</c> = P-16 regardless of whether the compiler inserted
    /// padding before it. <c>format</c> is then either P-20 or P-24, and it is
    /// the one holding -1.
    /// </remarks>
    private static bool ProbeCodecParameters()
    {
        const int unknownProfile = -99;
        const int searchLimit = 256;

        var parameters = AV.avcodec_parameters_alloc();
        if (parameters is null)
        {
            return false;
        }

        try
        {
            var raw = (byte*)parameters;
            for (var offset = 32; offset + 16 <= searchLimit; offset += 4)
            {
                if (*(int*)(raw + offset) != unknownProfile || *(int*)(raw + offset + 4) != unknownProfile)
                {
                    continue;
                }

                var format = *(int*)(raw + offset - 20) == -1 ? offset - 20
                    : *(int*)(raw + offset - 24) == -1 ? offset - 24
                    : -1;

                if (format < 8)
                {
                    continue;
                }

                // width and height are zero on a fresh allocation; use that as a
                // final cross-check before trusting the offsets.
                if (*(int*)(raw + offset + 8) != 0 || *(int*)(raw + offset + 12) != 0)
                {
                    continue;
                }

                CodecParametersFormat = format;
                CodecParametersBitRate = offset - 16;
                CodecParametersWidth = offset + 8;
                CodecParametersHeight = offset + 12;
                return true;
            }

            return false;
        }
        finally
        {
            var local = parameters;
            AV.avcodec_parameters_free(&local);
        }
    }

    /// <summary>
    /// Locates <c>AVFrame::sample_rate</c> and <c>AVFrame::ch_layout</c> without
    /// writing anything.
    /// </summary>
    /// <remarks>
    /// A PCM decoder is opened with a deliberately unusual sample rate and
    /// channel count, then fed one packet. FFmpeg stamps those values onto the
    /// frame it returns, so scanning the frame past its stable head for the
    /// marker rate and for a native-order channel layout with the marker channel
    /// count reveals both offsets.
    /// </remarks>
    private static bool ProbeAudioFrameFields()
    {
        const int markerRate = 12_345;
        const int markerChannels = 3;
        const int headSize = 160;
        const int scanLimit = 1024;

        Span<byte> scratch = stackalloc byte[32];
        using var codecName = new Utf8Scoped("pcm_s16le", scratch);
        var codec = AV.avcodec_find_decoder_by_name(codecName.Pointer);
        if (codec is null)
        {
            return false;
        }

        var context = AV.avcodec_alloc_context3(codec);
        if (context is null)
        {
            return false;
        }

        var packet = AV.av_packet_alloc();
        var frame = AV.av_frame_alloc();
        var payload = stackalloc byte[markerChannels * 2 * 16];

        try
        {
            if (packet is null || frame is null)
            {
                return false;
            }

            AV.SetOption(context, "ar", markerRate);
            AV.SetOption(context, "ac", markerChannels);
            if (AV.avcodec_open2(context, codec, null) < 0)
            {
                return false;
            }

            packet->Data = payload;
            packet->Size = markerChannels * 2 * 16;
            if (AV.avcodec_send_packet(context, packet) < 0 || AV.avcodec_receive_frame(context, frame) < 0)
            {
                return false;
            }

            var raw = (byte*)frame;
            var sampleRateOffset = -1;
            for (var offset = headSize; offset + 4 <= scanLimit; offset += 4)
            {
                if (*(int*)(raw + offset) == markerRate)
                {
                    sampleRateOffset = offset;
                    break;
                }
            }

            if (sampleRateOffset < 0)
            {
                return false;
            }

            // AVChannelLayout starts with AV_CHANNEL_ORDER_NATIVE (1) followed by
            // the channel count, and is 8-byte aligned because of the union that
            // follows.
            for (var offset = sampleRateOffset + 4; offset + 16 <= scanLimit; offset += 8)
            {
                var aligned = (offset + 7) & ~7;
                if (aligned + 16 > scanLimit)
                {
                    break;
                }

                if (*(int*)(raw + aligned) == 1 && *(int*)(raw + aligned + 4) == markerChannels)
                {
                    FrameSampleRate = sampleRateOffset;
                    FrameChannelLayout = aligned;
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (frame is not null)
            {
                var localFrame = frame;
                AV.av_frame_free(&localFrame);
            }

            if (packet is not null)
            {
                // The payload is stack memory that the packet never owned.
                packet->Data = null;
                packet->Size = 0;
                var localPacket = packet;
                AV.av_packet_free(&localPacket);
            }

            var localContext = context;
            AV.avcodec_free_context(&localContext);
        }
    }

    /// <summary>
    /// Writes into the offsets found by <see cref="ProbeAudioFrameFields"/> and
    /// checks that <c>av_frame_get_buffer</c> allocates exactly the expected
    /// amount, which confirms FFmpeg read the values back from where they were put.
    /// </summary>
    private static bool ConfirmAudioFrameFields()
    {
        if (FrameSampleRate < 0 || FrameChannelLayout < 0)
        {
            return false;
        }

        const int samples = 1024;
        const int channels = 2;
        var frame = AV.av_frame_alloc();
        if (frame is null)
        {
            return false;
        }

        try
        {
            frame->Format = (int)AVSampleFormat.FltP;
            frame->NbSamples = samples;
            *(int*)((byte*)frame + FrameSampleRate) = 48_000;
            *(AVChannelLayoutNative*)((byte*)frame + FrameChannelLayout) = AVChannelLayoutNative.Default(channels);

            if (AV.av_frame_get_buffer(frame, 0) < 0)
            {
                return false;
            }

            // Planar float: one plane per channel, four bytes per sample.
            return frame->Linesize[0] == samples * 4 && frame->Data[1] != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            var local = frame;
            AV.av_frame_free(&local);
        }
    }

    private static void RequireCodecParametersLayout()
    {
        if (!CodecParametersLayoutVerified)
        {
            throw Fail("the AVCodecParameters layout could not be determined.");
        }
    }

    private static void ValidateFrame()
    {
        var frame = AV.av_frame_alloc();
        if (frame is null)
        {
            throw Fail("av_frame_alloc returned NULL.");
        }

        try
        {
            // av_frame_alloc leaves the frame unreferenced: format is -1, the
            // geometry is zero and both timestamps are AV_NOPTS_VALUE.
            if (frame->Format != -1 || frame->Width != 0 || frame->Height != 0 || frame->NbSamples != 0)
            {
                throw Fail("the AVFrame layout does not match (format/width/height/nb_samples).");
            }

            if (frame->Pts != AVConstants.NoPtsValue || frame->PktDts != AVConstants.NoPtsValue)
            {
                throw Fail("the AVFrame layout does not match (pts/pkt_dts).");
            }
        }
        finally
        {
            var local = frame;
            AV.av_frame_free(&local);
        }
    }

    private static void ValidatePacket()
    {
        var packet = AV.av_packet_alloc();
        if (packet is null)
        {
            throw Fail("av_packet_alloc returned NULL.");
        }

        try
        {
            // av_packet_alloc initialises through av_packet_unref: null payload,
            // AV_NOPTS_VALUE timestamps and pos = -1.
            if (packet->Data is not null || packet->Size != 0 || packet->Pos != -1)
            {
                throw Fail("the AVPacket layout does not match (data/size/pos).");
            }

            if (packet->Pts != AVConstants.NoPtsValue || packet->Dts != AVConstants.NoPtsValue)
            {
                throw Fail("the AVPacket layout does not match (pts/dts).");
            }
        }
        finally
        {
            var local = packet;
            AV.av_packet_free(&local);
        }
    }

    private static bool ValidateCodecContext()
    {
        // Any video encoder will do; try a few so the check still runs on
        // minimal builds.
        Span<byte> scratch = stackalloc byte[64];
        foreach (var name in (string[])["mjpeg", "mpeg4", "rawvideo", "png", "ffv1"])
        {
            using var utf8 = new Utf8Scoped(name, scratch);
            var codec = AV.avcodec_find_encoder_by_name(utf8.Pointer);
            if (codec is null)
            {
                continue;
            }

            var context = AV.avcodec_alloc_context3(codec);
            if (context is null)
            {
                continue;
            }

            try
            {
                var codecType = *(int*)((byte*)context + CodecContextCodecType);
                var timeBase = *(AVRationalNative*)((byte*)context + CodecContextTimeBase);
                var pixFmt = *(int*)((byte*)context + CodecContextPixFmt);

                // avcodec_alloc_context3 sets codec_type from the codec,
                // time_base to 0/1 and pix_fmt to AV_PIX_FMT_NONE.
                return codecType == (int)AVMediaType.Video
                       && timeBase is { Num: 0, Den: 1 }
                       && pixFmt == -1;
            }
            finally
            {
                var local = context;
                AV.avcodec_free_context(&local);
            }
        }

        return false;
    }

    /// <summary>
    /// Verifies the AVStream and AVFormatContext offsets against an open input.
    /// Called once per demuxer, which is cheap and catches a wrong build early.
    /// </summary>
    internal static void ValidateOpenInput(void* formatContext)
    {
        var count = NbStreams(formatContext);
        if (count is < 0 or > 4096)
        {
            throw Fail($"the AVFormatContext layout does not match: nb_streams = {count}.");
        }

        for (var i = 0; i < count; i++)
        {
            var stream = Stream(formatContext, i);
            if (stream is null || StreamIndexOf(stream) != i)
            {
                throw Fail("the AVStream layout does not match: index differs from its position in the streams array.");
            }

            var parameters = CodecParametersOf(stream);
            if (parameters is null)
            {
                throw Fail("the AVStream layout does not match: codecpar is NULL.");
            }

            var type = CodecTypeOf(parameters);
            if (type is < AVMediaType.Unknown or > AVMediaType.Attachment)
            {
                throw Fail($"the AVStream layout does not match: codecpar->codec_type = {(int)type}.");
            }

            var timeBase = TimeBaseOf(stream);
            if (timeBase.Den <= 0 || timeBase.Num <= 0)
            {
                throw Fail($"the AVStream layout does not match: time_base = {timeBase.Num}/{timeBase.Den}.");
            }
        }
    }

    private static void RequireEncoderLayout()
    {
        if (!EncoderLayoutVerified)
        {
            throw Fail("the AVCodecContext layout was not confirmed, so encoding is unavailable on this FFmpeg build.");
        }
    }

    private static MediaBackendUnavailableException Fail(string reason) =>
        new(FFmpegLibraries.BackendName,
            $"{reason} FFmpeg 7.x is expected; adjust the offsets in AbiLayout for your build.");
}
