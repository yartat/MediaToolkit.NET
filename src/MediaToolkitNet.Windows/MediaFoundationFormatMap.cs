#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using System.Text;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Recording;
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

    /// <summary>
    /// Names a subtype the way the other backends name codecs, and finds this library's <see cref="MediaCodec"/> for it
    /// when it has one. An unfamiliar subtype is named by its four character code.
    /// </summary>
    public static (MediaCodec? Codec, string Name) Describe(Guid subtype, uint bitsPerSample)
    {
        if (subtype == WinGuids.VideoH264)
        {
            return (MediaCodec.H264, "h264");
        }

        if (subtype == WinGuids.VideoHevc || subtype == WinGuids.FromFourCc("HEV1"))
        {
            return (MediaCodec.Hevc, "hevc");
        }

        if (subtype == WinGuids.VideoMjpg)
        {
            return (MediaCodec.Mjpeg, "mjpeg");
        }

        if (subtype == WinGuids.FromFourCc("VP90"))
        {
            return (MediaCodec.Vp9, "vp9");
        }

        if (subtype == WinGuids.FromFourCc("AV01"))
        {
            return (MediaCodec.Av1, "av1");
        }

        if (subtype == WinGuids.AudioAac)
        {
            return (MediaCodec.Aac, "aac");
        }

        if (subtype == WinGuids.AudioMp3)
        {
            return (MediaCodec.Mp3, "mp3");
        }

        if (subtype == WinGuids.FromWaveFormatTag(0x2000))
        {
            return (MediaCodec.Ac3, "ac3");
        }

        if (subtype == WinGuids.AudioFloat)
        {
            return (null, bitsPerSample == 64 ? "pcm_f64le" : "pcm_f32le");
        }

        if (subtype == WinGuids.AudioPcm)
        {
            return bitsPerSample switch
            {
                8 => (MediaCodec.PcmU8, "pcm_u8"),
                16 => (MediaCodec.PcmS16, "pcm_s16le"),
                24 => (MediaCodec.PcmS24, "pcm_s24le"),
                32 => (MediaCodec.PcmS32, "pcm_s32le"),
                _ => (MediaCodec.Pcm, "pcm"),
            };
        }

        // The common subtypes are a four character code in the first field and a fixed tail.
        var bytes = subtype.ToByteArray();
        var isBase = bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0x00 && bytes[7] == 0x10 && bytes[8] == 0x80;
        if (isBase && bytes[..4].All(b => b is >= 0x20 and < 0x7F))
        {
            return (null, Encoding.ASCII.GetString(bytes, 0, 4).Trim().ToLowerInvariant());
        }

        return (null, subtype.ToString());
    }

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
