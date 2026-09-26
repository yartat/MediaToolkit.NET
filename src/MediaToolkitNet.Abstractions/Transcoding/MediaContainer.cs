#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions.Recording;

namespace MediaToolkitNet.Abstractions.Transcoding;

/// <summary>A container format.</summary>
public enum MediaContainer
{
    /// <summary>In a request, take the container from the output file's extension.</summary>
    Auto = 0,

    /// <summary>MPEG-4 Part 14.</summary>
    Mp4,

    /// <summary>QuickTime.</summary>
    Mov,

    /// <summary>Matroska, which takes almost any codec, subtitles and attachments included.</summary>
    Matroska,

    /// <summary>WebM, the Matroska subset for VP8, VP9, AV1, Vorbis, Opus and WebVTT.</summary>
    WebM,

    /// <summary>MPEG transport stream.</summary>
    MpegTs,

    /// <summary>Ogg.</summary>
    Ogg,

    /// <summary>Audio Video Interleave.</summary>
    Avi,

    /// <summary>WAVE audio.</summary>
    Wav,

    /// <summary>A bare FLAC stream.</summary>
    Flac,

    /// <summary>A bare MPEG audio layer III stream.</summary>
    Mp3,
}

/// <summary>Helpers for <see cref="MediaContainer"/> and <see cref="MediaCodec"/> that every backend shares.</summary>
public static class MediaFormats
{
    /// <summary>The container a file extension names, or <see langword="null"/> when it names none this library knows.</summary>
    public static MediaContainer? ContainerFromPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" or ".m4a" => MediaContainer.Mp4,
            ".mov" => MediaContainer.Mov,
            ".mkv" or ".mka" or ".mks" => MediaContainer.Matroska,
            ".webm" => MediaContainer.WebM,
            ".ts" or ".m2ts" or ".mts" => MediaContainer.MpegTs,
            ".ogg" or ".ogv" or ".oga" or ".opus" => MediaContainer.Ogg,
            ".avi" => MediaContainer.Avi,
            ".wav" => MediaContainer.Wav,
            ".flac" => MediaContainer.Flac,
            ".mp3" => MediaContainer.Mp3,
            _ => null,
        };
    }

    /// <summary>
    /// The codec a name refers to. Names are FFmpeg's, which mpv reports as well;
    /// GStreamer describes codecs with caps and maps them itself.
    /// </summary>
    /// <returns>Returns the codec, or <see langword="null"/> for one this library has no name for.</returns>
    public static MediaCodec? CodecFromName(string name) => name switch
    {
        "h264" => MediaCodec.H264,
        "hevc" or "h265" => MediaCodec.Hevc,
        "vp9" => MediaCodec.Vp9,
        "av1" => MediaCodec.Av1,
        "mjpeg" => MediaCodec.Mjpeg,
        "aac" => MediaCodec.Aac,
        "opus" => MediaCodec.Opus,
        "flac" => MediaCodec.Flac,
        "ac3" => MediaCodec.Ac3,
        "dts" => MediaCodec.Dts,
        "truehd" => MediaCodec.TrueHd,
        "mp2" => MediaCodec.Mp2,
        "mp3" => MediaCodec.Mp3,
        "vorbis" => MediaCodec.Vorbis,
        "ra_144" => MediaCodec.RealAudio,
        "pcm_u8" => MediaCodec.PcmU8,
        "pcm_s16le" => MediaCodec.PcmS16,
        "pcm_s24le" => MediaCodec.PcmS24,
        "pcm_s32le" => MediaCodec.PcmS32,
        "subrip" or "srt" => MediaCodec.SubRip,
        "ass" or "ssa" => MediaCodec.Ass,
        "webvtt" => MediaCodec.WebVtt,
        "mov_text" => MediaCodec.MovText,
        "dvd_subtitle" => MediaCodec.DvdSubtitle,
        "hdmv_pgs_subtitle" => MediaCodec.Pgs,
        _ => null,
    };

    /// <summary>True for the subtitle codecs, text and bitmap alike.</summary>
    public static bool IsSubtitle(this MediaCodec codec) =>
        codec is MediaCodec.SubRip or MediaCodec.Ass or MediaCodec.WebVtt or MediaCodec.MovText
            or MediaCodec.DvdSubtitle or MediaCodec.Pgs;

    /// <summary>True for the subtitle codecs that carry text and so can be produced by conversion.</summary>
    public static bool IsTextSubtitle(this MediaCodec codec) =>
        codec is MediaCodec.SubRip or MediaCodec.Ass or MediaCodec.WebVtt or MediaCodec.MovText;
}
