using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.FFmpeg.Native;

namespace MediaToolkitNet.FFmpeg;

/// <summary>Translates between the toolkit format enums and FFmpeg's.</summary>
public static class FFmpegFormatMap
{
    /// <summary>Maps a toolkit pixel format onto <c>AVPixelFormat</c>.</summary>
    public static AVPixelFormat ToAV(PixelFormat format) => format switch
    {
        PixelFormat.Yuv420P => AVPixelFormat.Yuv420P,
        PixelFormat.Yuv422P => AVPixelFormat.Yuv422P,
        PixelFormat.Yuv444P => AVPixelFormat.Yuv444P,
        PixelFormat.Nv12 => AVPixelFormat.Nv12,
        PixelFormat.Yuyv422 => AVPixelFormat.Yuyv422,
        PixelFormat.Uyvy422 => AVPixelFormat.Uyvy422,
        PixelFormat.Rgb24 => AVPixelFormat.Rgb24,
        PixelFormat.Bgr24 => AVPixelFormat.Bgr24,
        PixelFormat.Rgba32 => AVPixelFormat.Rgba,
        PixelFormat.Bgra32 => AVPixelFormat.Bgra,

        // MJPEG is a compressed payload, not a raw layout; swscale never sees it.
        _ => AVPixelFormat.None,
    };

    /// <summary>Maps an <c>AVPixelFormat</c> onto a toolkit pixel format.</summary>
    public static PixelFormat FromAV(AVPixelFormat format) => format switch
    {
        AVPixelFormat.Yuv420P or AVPixelFormat.Yuvj420P => PixelFormat.Yuv420P,
        AVPixelFormat.Yuv422P => PixelFormat.Yuv422P,
        AVPixelFormat.Yuv444P => PixelFormat.Yuv444P,
        AVPixelFormat.Nv12 => PixelFormat.Nv12,
        AVPixelFormat.Yuyv422 => PixelFormat.Yuyv422,
        AVPixelFormat.Uyvy422 => PixelFormat.Uyvy422,
        AVPixelFormat.Rgb24 => PixelFormat.Rgb24,
        AVPixelFormat.Bgr24 => PixelFormat.Bgr24,
        AVPixelFormat.Rgba => PixelFormat.Rgba32,
        AVPixelFormat.Bgra => PixelFormat.Bgra32,
        _ => PixelFormat.Unknown,
    };

    /// <summary>Maps a toolkit sample format onto <c>AVSampleFormat</c>.</summary>
    public static AVSampleFormat ToAV(SampleFormat format) => format switch
    {
        SampleFormat.U8 => AVSampleFormat.U8,
        SampleFormat.S16 => AVSampleFormat.S16,
        SampleFormat.S32 => AVSampleFormat.S32,
        SampleFormat.F32 => AVSampleFormat.Flt,
        SampleFormat.F64 => AVSampleFormat.Dbl,
        SampleFormat.U8Planar => AVSampleFormat.U8P,
        SampleFormat.S16Planar => AVSampleFormat.S16P,
        SampleFormat.S32Planar => AVSampleFormat.S32P,
        SampleFormat.F32Planar => AVSampleFormat.FltP,
        SampleFormat.F64Planar => AVSampleFormat.DblP,
        _ => AVSampleFormat.None,
    };

    /// <summary>Maps an <c>AVSampleFormat</c> onto a toolkit sample format.</summary>
    public static SampleFormat FromAV(AVSampleFormat format) => format switch
    {
        AVSampleFormat.U8 => SampleFormat.U8,
        AVSampleFormat.S16 => SampleFormat.S16,
        AVSampleFormat.S32 => SampleFormat.S32,
        AVSampleFormat.Flt => SampleFormat.F32,
        AVSampleFormat.Dbl => SampleFormat.F64,
        AVSampleFormat.U8P => SampleFormat.U8Planar,
        AVSampleFormat.S16P => SampleFormat.S16Planar,
        AVSampleFormat.S32P => SampleFormat.S32Planar,
        AVSampleFormat.FltP => SampleFormat.F32Planar,
        AVSampleFormat.DblP => SampleFormat.F64Planar,
        _ => SampleFormat.Unknown,
    };

    /// <summary>
    /// Default encoder name for a codec. Native FFmpeg encoders are preferred
    /// over external libraries so that a minimal build still works.
    /// </summary>
    public static string[] EncoderNames(Abstractions.Recording.MediaCodec codec) => codec switch
    {
        Abstractions.Recording.MediaCodec.H264 => ["libx264", "h264_nvenc", "h264_qsv", "h264_videotoolbox", "h264_amf"],
        Abstractions.Recording.MediaCodec.Hevc => ["libx265", "hevc_nvenc", "hevc_qsv", "hevc_videotoolbox"],
        Abstractions.Recording.MediaCodec.Vp9 => ["libvpx-vp9"],
        Abstractions.Recording.MediaCodec.Av1 => ["libsvtav1", "librav1e", "libaom-av1"],
        Abstractions.Recording.MediaCodec.Mjpeg => ["mjpeg"],
        Abstractions.Recording.MediaCodec.Aac => ["aac", "libfdk_aac"],
        Abstractions.Recording.MediaCodec.Opus => ["libopus", "opus"],
        Abstractions.Recording.MediaCodec.Flac => ["flac"],
        Abstractions.Recording.MediaCodec.Ac3 => ["ac3", "ac3_fixed"],
        Abstractions.Recording.MediaCodec.Dts => ["dca"],
        Abstractions.Recording.MediaCodec.Pcm or Abstractions.Recording.MediaCodec.PcmS16 => ["pcm_s16le"],
        Abstractions.Recording.MediaCodec.PcmU8 => ["pcm_u8"],
        Abstractions.Recording.MediaCodec.PcmS24 => ["pcm_s24le"],
        Abstractions.Recording.MediaCodec.PcmS32 => ["pcm_s32le"],
        _ => [],
    };

    /// <summary>
    /// Containers that require the encoder to emit extradata in the container
    /// header rather than in-band.
    /// </summary>
    public static bool NeedsGlobalHeader(string outputPath) =>
        Path.GetExtension(outputPath).ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" or ".m4a" or ".mov" => true,

            // Matroska writes audio and subtitles under their own extensions,
            // and all three are the same muxer with the same header.
            ".mkv" or ".mka" or ".mks" or ".webm" => true,
            _ => false,
        };

    /// <summary>
    /// Describes a channel layout the way the <c>ch_layout</c> option parses it.
    /// </summary>
    /// <param name="channels">Number of channels.</param>
    /// <param name="mask">The speaker mask, or zero when none was asked for.</param>
    /// <returns>Returns the description.</returns>
    /// <remarks>
    /// A mask goes as hexadecimal rather than as a name: <c>5.1</c> and
    /// <c>5.1(side)</c> are both six channels and the name table decides which
    /// of the two a string means, while a mask says it outright.
    /// </remarks>
    public static string ChannelLayoutDescription(int channels, ulong mask) =>
        mask != 0 ? $"0x{mask:x}" : $"{channels}c";
}
