using System.Runtime.Versioning;
using MediaToolkitNet.Core.Formats;
using MediaToolkitNet.Core.Recording;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>Translates between the toolkit format enums and Media Foundation subtype GUIDs.</summary>
[SupportedOSPlatform("windows")]
public static class MediaFoundationFormatMap
{
    /// <summary>Maps a Media Foundation video subtype onto a toolkit pixel format.</summary>
    public static PixelFormat ToPixelFormat(Guid subtype)
    {
        if (subtype == WinGuids.VideoNv12)
        {
            return PixelFormat.Nv12;
        }

        if (subtype == WinGuids.VideoYuy2)
        {
            return PixelFormat.Yuyv422;
        }

        if (subtype == WinGuids.VideoUyvy)
        {
            return PixelFormat.Uyvy422;
        }

        if (subtype == WinGuids.VideoI420 || subtype == WinGuids.VideoIyuv)
        {
            return PixelFormat.Yuv420P;
        }

        if (subtype == WinGuids.VideoMjpg)
        {
            return PixelFormat.Mjpeg;
        }

        // Media Foundation calls these RGB, but the bytes are in BGR order.
        if (subtype == WinGuids.VideoRgb24)
        {
            return PixelFormat.Bgr24;
        }

        return subtype == WinGuids.VideoRgb32 || subtype == WinGuids.VideoArgb32
            ? PixelFormat.Bgra32
            : PixelFormat.Unknown;
    }

    /// <summary>Maps a toolkit pixel format onto a Media Foundation video subtype.</summary>
    public static Guid ToSubtype(PixelFormat format) => format switch
    {
        PixelFormat.Nv12 => WinGuids.VideoNv12,
        PixelFormat.Yuyv422 => WinGuids.VideoYuy2,
        PixelFormat.Uyvy422 => WinGuids.VideoUyvy,
        PixelFormat.Yuv420P => WinGuids.VideoI420,
        PixelFormat.Mjpeg => WinGuids.VideoMjpg,
        PixelFormat.Bgr24 => WinGuids.VideoRgb24,
        PixelFormat.Bgra32 => WinGuids.VideoRgb32,
        _ => Guid.Empty,
    };

    /// <summary>Maps a Media Foundation audio subtype and bit depth onto a toolkit sample format.</summary>
    public static SampleFormat ToSampleFormat(Guid subtype, uint bitsPerSample)
    {
        if (subtype == WinGuids.AudioFloat)
        {
            return bitsPerSample == 64 ? SampleFormat.F64 : SampleFormat.F32;
        }

        if (subtype != WinGuids.AudioPcm)
        {
            return SampleFormat.Unknown;
        }

        return bitsPerSample switch
        {
            8 => SampleFormat.U8,
            16 => SampleFormat.S16,
            32 => SampleFormat.S32,
            _ => SampleFormat.Unknown,
        };
    }

    /// <summary>Maps a toolkit sample format onto a Media Foundation audio subtype.</summary>
    public static Guid ToAudioSubtype(SampleFormat format) =>
        format.IsFloat() ? WinGuids.AudioFloat : WinGuids.AudioPcm;

    /// <summary>Maps a codec onto the Media Foundation subtype the sink writer expects.</summary>
    public static Guid ToEncoderSubtype(MediaCodec codec) => codec switch
    {
        MediaCodec.Default or MediaCodec.H264 => WinGuids.VideoH264,
        MediaCodec.Hevc => WinGuids.VideoHevc,
        MediaCodec.Mjpeg => WinGuids.VideoMjpg,
        MediaCodec.Aac => WinGuids.AudioAac,
        MediaCodec.Pcm => WinGuids.AudioPcm,
        _ => Guid.Empty,
    };
}
