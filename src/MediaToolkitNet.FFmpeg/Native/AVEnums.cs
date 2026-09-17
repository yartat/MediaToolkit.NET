namespace MediaToolkitNet.FFmpeg.Native;

/// <summary>Mirrors <c>AVMediaType</c>.</summary>
public enum AVMediaType
{
    /// <summary>AVMEDIA_TYPE_UNKNOWN.</summary>
    Unknown = -1,

    /// <summary>AVMEDIA_TYPE_VIDEO.</summary>
    Video = 0,

    /// <summary>AVMEDIA_TYPE_AUDIO.</summary>
    Audio = 1,

    /// <summary>AVMEDIA_TYPE_DATA.</summary>
    Data = 2,

    /// <summary>AVMEDIA_TYPE_SUBTITLE.</summary>
    Subtitle = 3,

    /// <summary>AVMEDIA_TYPE_ATTACHMENT.</summary>
    Attachment = 4,
}

/// <summary>
/// Mirrors the subset of <c>AVPixelFormat</c> this library maps. The numeric
/// values at the head of that enum have been stable since FFmpeg 0.x.
/// </summary>
public enum AVPixelFormat
{
    /// <summary>AV_PIX_FMT_NONE.</summary>
    None = -1,

    /// <summary>AV_PIX_FMT_YUV420P.</summary>
    Yuv420P = 0,

    /// <summary>AV_PIX_FMT_YUYV422.</summary>
    Yuyv422 = 1,

    /// <summary>AV_PIX_FMT_RGB24.</summary>
    Rgb24 = 2,

    /// <summary>AV_PIX_FMT_BGR24.</summary>
    Bgr24 = 3,

    /// <summary>AV_PIX_FMT_YUV422P.</summary>
    Yuv422P = 4,

    /// <summary>AV_PIX_FMT_YUV444P.</summary>
    Yuv444P = 5,

    /// <summary>AV_PIX_FMT_GRAY8.</summary>
    Gray8 = 8,

    /// <summary>AV_PIX_FMT_UYVY422.</summary>
    Uyvy422 = 15,

    /// <summary>AV_PIX_FMT_NV12.</summary>
    Nv12 = 23,

    /// <summary>AV_PIX_FMT_NV21.</summary>
    Nv21 = 24,

    /// <summary>AV_PIX_FMT_ARGB.</summary>
    Argb = 25,

    /// <summary>AV_PIX_FMT_RGBA.</summary>
    Rgba = 26,

    /// <summary>AV_PIX_FMT_ABGR.</summary>
    Abgr = 27,

    /// <summary>AV_PIX_FMT_BGRA.</summary>
    Bgra = 28,

    /// <summary>AV_PIX_FMT_YUVJ420P.</summary>
    Yuvj420P = 12,
}

/// <summary>Mirrors <c>AVSampleFormat</c>. These values have been stable since FFmpeg 2.x.</summary>
public enum AVSampleFormat
{
    /// <summary>AV_SAMPLE_FMT_NONE.</summary>
    None = -1,

    /// <summary>AV_SAMPLE_FMT_U8.</summary>
    U8 = 0,

    /// <summary>AV_SAMPLE_FMT_S16.</summary>
    S16 = 1,

    /// <summary>AV_SAMPLE_FMT_S32.</summary>
    S32 = 2,

    /// <summary>AV_SAMPLE_FMT_FLT.</summary>
    Flt = 3,

    /// <summary>AV_SAMPLE_FMT_DBL.</summary>
    Dbl = 4,

    /// <summary>AV_SAMPLE_FMT_U8P.</summary>
    U8P = 5,

    /// <summary>AV_SAMPLE_FMT_S16P.</summary>
    S16P = 6,

    /// <summary>AV_SAMPLE_FMT_S32P.</summary>
    S32P = 7,

    /// <summary>AV_SAMPLE_FMT_FLTP.</summary>
    FltP = 8,

    /// <summary>AV_SAMPLE_FMT_DBLP.</summary>
    DblP = 9,

    /// <summary>AV_SAMPLE_FMT_S64.</summary>
    S64 = 10,

    /// <summary>AV_SAMPLE_FMT_S64P.</summary>
    S64P = 11,
}

/// <summary>Constants lifted from the FFmpeg headers that are part of the public contract.</summary>
public static class AVConstants
{
    /// <summary>AV_NOPTS_VALUE.</summary>
    public const long NoPtsValue = long.MinValue;

    /// <summary>AV_NUM_DATA_POINTERS.</summary>
    public const int NumDataPointers = 8;

    /// <summary>AVERROR_EOF, computed as -MKTAG('E','O','F',' ').</summary>
    public const int ErrorEof = -541478725;

    /// <summary>AVERROR_EAGAIN. FFmpeg uses the C errno value, which is 11 on every platform it supports.</summary>
    public const int ErrorAgain = -11;

    /// <summary>AVERROR(ENOMEM).</summary>
    public const int ErrorNoMemory = -12;

    /// <summary>AVERROR(EINVAL).</summary>
    public const int ErrorInvalid = -22;

    /// <summary>AVERROR_DECODER_NOT_FOUND.</summary>
    public const int ErrorDecoderNotFound = -1128613112;

    /// <summary>AV_CODEC_FLAG_GLOBAL_HEADER.</summary>
    public const int CodecFlagGlobalHeader = 1 << 22;

    /// <summary>AV_CODEC_FLAG_QSCALE, which makes the encoder follow <c>global_quality</c> instead of a bitrate.</summary>
    public const int CodecFlagQScale = 1 << 1;

    /// <summary>FF_QP2LAMBDA, the factor between a quality value and <c>global_quality</c>.</summary>
    public const int QualityToLambda = 118;

    /// <summary>FF_COMPLIANCE_EXPERIMENTAL, the compliance level an experimental encoder demands.</summary>
    public const int ComplianceExperimental = -2;

    /// <summary>AVFMT_GLOBALHEADER.</summary>
    public const int FormatGlobalHeader = 0x0040;

    /// <summary>AVFMT_NOFILE.</summary>
    public const int FormatNoFile = 0x0001;

    /// <summary>AVIO_FLAG_WRITE.</summary>
    public const int AvioFlagWrite = 2;

    /// <summary>AV_PKT_FLAG_KEY.</summary>
    public const int PacketFlagKey = 0x0001;

    /// <summary>AVSEEK_FLAG_BACKWARD.</summary>
    public const int SeekFlagBackward = 1;

    /// <summary>AV_TIME_BASE, the microsecond time base FFmpeg uses for container-level timestamps.</summary>
    public const int TimeBase = 1_000_000;

    /// <summary>AV_CH_LAYOUT_MONO.</summary>
    public const ulong ChannelLayoutMono = 0x4;

    /// <summary>AV_CH_LAYOUT_STEREO.</summary>
    public const ulong ChannelLayoutStereo = 0x3;
}
