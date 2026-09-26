#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Globalization;
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
        // libopenh264 is the H.264 encoder an LGPL build has, and the _mf ones
        // go through whatever Media Foundation has registered; both come after
        // the hardware encoders, which are faster and better where they open.
        Abstractions.Recording.MediaCodec.H264 => ["libx264", "h264_nvenc", "h264_qsv", "h264_videotoolbox", "h264_amf", "libopenh264", "h264_mf"],
        Abstractions.Recording.MediaCodec.Hevc => ["libx265", "hevc_nvenc", "hevc_qsv", "hevc_videotoolbox", "hevc_amf", "hevc_mf"],
        Abstractions.Recording.MediaCodec.Vp9 => ["libvpx-vp9"],
        Abstractions.Recording.MediaCodec.Av1 => ["libsvtav1", "librav1e", "libaom-av1"],
        Abstractions.Recording.MediaCodec.Mjpeg => ["mjpeg"],
        Abstractions.Recording.MediaCodec.Aac => ["aac", "libfdk_aac"],
        Abstractions.Recording.MediaCodec.Opus => ["libopus", "opus"],
        Abstractions.Recording.MediaCodec.Flac => ["flac"],
        Abstractions.Recording.MediaCodec.Ac3 => ["ac3", "ac3_fixed"],
        Abstractions.Recording.MediaCodec.Dts => ["dca"],
        Abstractions.Recording.MediaCodec.TrueHd => ["truehd"],
        Abstractions.Recording.MediaCodec.Mp2 => ["mp2", "libtwolame", "mp2fixed"],
        Abstractions.Recording.MediaCodec.Mp3 => ["libmp3lame", "libshine", "mp3_mf"],
        Abstractions.Recording.MediaCodec.Vorbis => ["libvorbis", "vorbis"],
        Abstractions.Recording.MediaCodec.RealAudio => ["real_144"],
        Abstractions.Recording.MediaCodec.Pcm or Abstractions.Recording.MediaCodec.PcmS16 => ["pcm_s16le"],
        Abstractions.Recording.MediaCodec.PcmU8 => ["pcm_u8"],
        Abstractions.Recording.MediaCodec.PcmS24 => ["pcm_s24le"],
        Abstractions.Recording.MediaCodec.PcmS32 => ["pcm_s32le"],
        Abstractions.Recording.MediaCodec.SubRip => ["srt", "subrip"],
        Abstractions.Recording.MediaCodec.Ass => ["ass", "ssa"],
        Abstractions.Recording.MediaCodec.WebVtt => ["webvtt"],
        Abstractions.Recording.MediaCodec.MovText => ["mov_text"],
        Abstractions.Recording.MediaCodec.DvdSubtitle => ["dvdsub"],

        // FFmpeg decodes PGS but has no encoder for it, so it can only be copied.
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
    /// Encoders FFmpeg marks experimental, which will not open unless the
    /// compliance level is lowered to <see cref="AVConstants.ComplianceExperimental"/>.
    /// </summary>
    /// <param name="encoderName">The encoder that was chosen.</param>
    /// <returns>Returns whether it has to be opened as experimental.</returns>
    /// <remarks>
    /// This is a property of the encoder rather than of the codec: FFmpeg ships a
    /// native <c>opus</c> and <c>vorbis</c> encoder that are experimental beside
    /// the libopus and libvorbis wrappers that are not, and picking one of those
    /// is something the caller asked for by naming the codec.
    /// </remarks>
    public static bool IsExperimental(string encoderName) =>
        encoderName is "dca" or "truehd" or "mlp" or "opus" or "vorbis" or "sonic" or "sonicls" or "s302m";

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

    /// <summary>The libavformat muxer for a container, or <see langword="null"/> to guess from the file name.</summary>
    public static string? MuxerName(Abstractions.Transcoding.MediaContainer? container) => container switch
    {
        Abstractions.Transcoding.MediaContainer.Mp4 => "mp4",
        Abstractions.Transcoding.MediaContainer.Mov => "mov",
        Abstractions.Transcoding.MediaContainer.Matroska => "matroska",
        Abstractions.Transcoding.MediaContainer.WebM => "webm",
        Abstractions.Transcoding.MediaContainer.MpegTs => "mpegts",
        Abstractions.Transcoding.MediaContainer.Ogg => "ogg",
        Abstractions.Transcoding.MediaContainer.Avi => "avi",
        Abstractions.Transcoding.MediaContainer.Wav => "wav",
        Abstractions.Transcoding.MediaContainer.Flac => "flac",
        Abstractions.Transcoding.MediaContainer.Mp3 => "mp3",
        _ => null,
    };

    /// <summary>
    /// The encoder's private options for speed and quality, which only
    /// <c>avcodec_open2</c> can set, or mpv's <c>--ovcopts</c>. The caller's own
    /// options go last and win.
    /// </summary>
    public static Dictionary<string, string> VideoEncoderOptions(string encoder, Abstractions.Transcoding.VideoOutputSettings settings)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (settings.Speed is { } speed)
        {
            switch (encoder)
            {
                case "libx264" or "libx265":
                    options["preset"] = speed.ToString().ToLowerInvariant();
                    break;
                case "libsvtav1":
                    options["preset"] = (12 - ((int)speed * 10 / 8)).ToString(CultureInfo.InvariantCulture);
                    break;
                case "libvpx-vp9" or "libaom-av1":
                    options["cpu-used"] = (8 - (int)speed).ToString(CultureInfo.InvariantCulture);
                    break;
                case var nvenc when nvenc.EndsWith("_nvenc", StringComparison.Ordinal):
                    options["preset"] = $"p{7 - ((int)speed * 6 / 8)}";
                    break;
            }
        }

        if (settings.Quality is { } quality && UsesConstantQuality(encoder))
        {
            var value = Transcoding.FilterChains.Number(quality);
            if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
            {
                options["cq"] = value;
            }
            else
            {
                options["crf"] = value;
                if (encoder == "libvpx-vp9")
                {
                    // libvpx only honours crf as constant quality with the rate cap removed.
                    options["b"] = "0";
                }
            }
        }

        if (settings.Options is not null)
        {
            foreach (var (key, value) in settings.Options)
            {
                options[key] = value;
            }
        }

        return options;
    }

    /// <summary>
    /// True for the encoders whose constant-quality setting is a private option
    /// (<c>crf</c> or <c>cq</c>) rather than <c>global_quality</c>.
    /// </summary>
    public static bool UsesConstantQuality(string encoder) =>
        encoder is "libx264" or "libx265" or "libsvtav1" or "libaom-av1" or "libvpx-vp9"
        || encoder.EndsWith("_nvenc", StringComparison.Ordinal);
}
