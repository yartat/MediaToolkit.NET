#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Globalization;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;

namespace MediaToolkitNet.GStreamer.Transcoding;

/// <summary>
/// Which GStreamer element encodes each codec, and how the settings of a
/// request become that element's properties.
/// </summary>
/// <remarks>
/// Encoders disagree on units: <c>x264enc</c> takes its bit rate in kbit/s,
/// <c>vp9enc</c> and <c>opusenc</c> in bit/s. Each mapping below was read off
/// <c>gst-inspect-1.0</c> for the element it names. An element this table does
/// not know still runs, through <see cref="VideoOutputSettings.EncoderName"/>,
/// but only its <see cref="VideoOutputSettings.Options"/> reach it.
/// </remarks>
[SupportedOSPlatform("linux")]
internal static class GStreamerCodecs
{
    /// <summary>Video encoders per codec, in order of preference.</summary>
    public static IReadOnlyList<string> VideoCandidates(MediaCodec codec) => codec switch
    {
        MediaCodec.H264 => ["x264enc", "openh264enc"],
        MediaCodec.Hevc => ["x265enc"],
        MediaCodec.Vp9 => ["vp9enc"],
        MediaCodec.Av1 => ["svtav1enc", "av1enc"],
        MediaCodec.Mjpeg => ["jpegenc"],
        _ => [],
    };

    /// <summary>Audio encoders per codec, in order of preference.</summary>
    public static IReadOnlyList<string> AudioCandidates(MediaCodec codec) => codec switch
    {
        MediaCodec.Aac => ["avenc_aac", "fdkaacenc", "voaacenc"],
        MediaCodec.Opus => ["opusenc"],
        MediaCodec.Flac => ["flacenc"],
        MediaCodec.Mp3 => ["lamemp3enc"],
        MediaCodec.Vorbis => ["vorbisenc"],
        MediaCodec.Ac3 => ["avenc_ac3"],
        // twolame refuses the bit rates Layer II forbids for a channel mode, such
        // as 320 kbit/s mono, where libavcodec's encoder carries on as FFmpeg does.
        MediaCodec.Mp2 => ["avenc_mp2", "twolamemp2enc"],
        MediaCodec.Dts => ["avenc_dca"],
        MediaCodec.RealAudio => ["avenc_real_144"],
        _ => [],
    };

    /// <summary>
    /// The GStreamer sample format that stands for a PCM codec. PCM needs no
    /// encoder: a caps filter with this format is the whole encoding.
    /// </summary>
    public static string? RawFormatOf(MediaCodec codec) => codec switch
    {
        MediaCodec.Pcm or MediaCodec.PcmS16 => "S16LE",
        MediaCodec.PcmU8 => "U8",
        MediaCodec.PcmS24 => "S24LE",
        MediaCodec.PcmS32 => "S32LE",
        _ => null,
    };

    /// <summary>The element that encodes this video, or null when none is installed.</summary>
    public static string? VideoEncoder(VideoOutputSettings settings) =>
        settings.EncoderName ?? VideoCandidates(settings.Codec).FirstOrDefault(GStreamerElements.Exists);

    /// <summary>
    /// The element that encodes this audio, or null when none is installed or,
    /// for PCM, none is needed; <see cref="RawFormatOf"/> tells the two apart.
    /// </summary>
    public static string? AudioEncoder(AudioOutputSettings settings) =>
        settings.EncoderName
        ?? (RawFormatOf(settings.Codec) is not null ? null : AudioCandidates(settings.Codec).FirstOrDefault(GStreamerElements.Exists));

    /// <summary>The parser that follows an encoder, for the muxers that want parsed caps.</summary>
    public static string? EncodedParser(string encoder) => encoder switch
    {
        "x264enc" or "openh264enc" => "h264parse",
        "x265enc" => "h265parse",
        "svtav1enc" or "av1enc" => "av1parse",
        "avenc_aac" or "fdkaacenc" or "voaacenc" => "aacparse",
        "lamemp3enc" or "twolamemp2enc" or "avenc_mp2" => "mpegaudioparse",
        "avenc_ac3" => "ac3parse",
        "avenc_dca" => "dcaparse",
        _ => null,
    };

    /// <summary>The properties that carry video settings to an encoder element, options last.</summary>
    public static List<(string Name, string Value)> VideoEncoderProperties(
        string encoder, VideoOutputSettings settings, List<TranscodeIssue> issues)
    {
        var result = new List<(string Name, string Value)>();
        var speedIndex = settings.Speed is { } speed ? (int)speed : (int?)null;

        switch (encoder)
        {
            case "x264enc":
            case "x265enc":
                if (settings.Quality is { } crf)
                {
                    if (encoder == "x264enc")
                    {
                        result.Add(("pass", "qual"));
                        result.Add(("quantizer", Integer(crf)));
                    }
                    else
                    {
                        result.Add(("option-string", $"crf={Number(crf)}"));
                    }
                }

                AddIf(result, "bitrate", settings.BitrateBitsPerSecond / 1000);
                AddIf(result, "key-int-max", settings.KeyFrameInterval);
                if (settings.Speed is { } preset)
                {
                    result.Add(("speed-preset", preset.ToString().ToLowerInvariant()));
                }

                break;

            case "vp9enc":
            case "av1enc":
                if (settings.Quality is { } cq)
                {
                    result.Add(("end-usage", encoder == "vp9enc" ? "cq" : "q"));
                    result.Add(("cq-level", Integer(cq)));
                }

                // vp9enc takes bit/s, av1enc kbit/s.
                AddIf(result, "target-bitrate", encoder == "vp9enc" ? settings.BitrateBitsPerSecond : settings.BitrateBitsPerSecond / 1000);
                AddIf(result, "keyframe-max-dist", settings.KeyFrameInterval);
                if (speedIndex is { } fast)
                {
                    result.Add(("cpu-used", (8 - fast).ToString(CultureInfo.InvariantCulture)));
                }

                break;

            case "svtav1enc":
                if (settings.Quality is { } svtCrf)
                {
                    result.Add(("crf", Integer(svtCrf)));
                }

                AddIf(result, "target-bitrate", settings.BitrateBitsPerSecond / 1000);
                AddIf(result, "intra-period-length", settings.KeyFrameInterval);
                if (speedIndex is { } svtSpeed)
                {
                    result.Add(("preset", (12 - svtSpeed).ToString(CultureInfo.InvariantCulture)));
                }

                break;

            case "openh264enc":
                AddIf(result, "bitrate", settings.BitrateBitsPerSecond);
                AddIf(result, "gop-size", settings.KeyFrameInterval);
                Unmapped(encoder, settings.Quality is not null, "a constant quality", issues);
                break;

            case "jpegenc":
                // jpegenc has one knob, quality from 0 to 100, higher being better.
                if (settings.Quality is { } jpeg)
                {
                    result.Add(("quality", Integer(jpeg)));
                }

                Unmapped(encoder, settings.BitrateBitsPerSecond > 0, "a bit rate", issues);
                break;

            default:
                Unmapped(encoder, settings.Quality is not null || settings.BitrateBitsPerSecond > 0
                    || settings.KeyFrameInterval > 0 || settings.Speed is not null, "quality, bit rate, key frame interval or speed", issues);
                break;
        }

        AddOptions(result, settings.Options);
        return result;
    }

    /// <summary>The properties that carry audio settings to an encoder element, options last.</summary>
    public static List<(string Name, string Value)> AudioEncoderProperties(
        string encoder, AudioOutputSettings settings, List<TranscodeIssue> issues)
    {
        var result = new List<(string Name, string Value)>();
        switch (encoder)
        {
            case "avenc_aac":
            case "fdkaacenc":
            case "voaacenc":
            case "opusenc":
            case "avenc_ac3":
            case "avenc_mp2":
                AddIf(result, "bitrate", settings.BitrateBitsPerSecond);
                Unmapped(encoder, settings.Quality is not null, "a variable-quality setting", issues);
                break;

            case "avenc_dca":
                // libavcodec still calls its DCA encoder experimental.
                result.Add(("strict", "experimental"));
                AddIf(result, "bitrate", settings.BitrateBitsPerSecond);
                Unmapped(encoder, settings.Quality is not null, "a variable-quality setting", issues);
                break;

            case "avenc_real_144":
                Unmapped(encoder, settings.Quality is not null || settings.BitrateBitsPerSecond > 0, "a bit rate or quality, RealAudio 1.0 having one", issues);
                break;

            case "twolamemp2enc":
                AddIf(result, "bitrate", settings.BitrateBitsPerSecond / 1000);
                Unmapped(encoder, settings.Quality is not null, "a variable-quality setting", issues);
                break;

            case "vorbisenc":
                // -q:a runs from 0 to 10 for libvorbis; vorbisenc takes the same scale divided by ten.
                if (settings.Quality is { } vorbis)
                {
                    result.Add(("quality", Number(vorbis / 10)));
                }

                AddIf(result, "bitrate", settings.BitrateBitsPerSecond);
                break;

            case "lamemp3enc":
                if (settings.Quality is { } lame)
                {
                    result.Add(("target", "quality"));
                    result.Add(("quality", Number(lame)));
                }
                else if (settings.BitrateBitsPerSecond > 0)
                {
                    result.Add(("target", "bitrate"));
                    result.Add(("bitrate", (settings.BitrateBitsPerSecond / 1000).ToString(CultureInfo.InvariantCulture)));
                }

                break;

            case "flacenc":
                Unmapped(encoder, settings.Quality is not null || settings.BitrateBitsPerSecond > 0, "a bit rate or quality, being lossless", issues);
                break;

            default:
                Unmapped(encoder, settings.Quality is not null || settings.BitrateBitsPerSecond > 0, "quality or bit rate", issues);
                break;
        }

        AddOptions(result, settings.Options);
        return result;
    }

    private static void AddIf(List<(string Name, string Value)> target, string name, int value)
    {
        if (value > 0)
        {
            target.Add((name, value.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static void AddOptions(List<(string Name, string Value)> target, IReadOnlyDictionary<string, string>? options)
    {
        foreach (var (name, value) in options ?? new Dictionary<string, string>())
        {
            target.RemoveAll(p => p.Name == name);
            target.Add((name, value));
        }
    }

    private static void Unmapped(string encoder, bool asked, string what, List<TranscodeIssue> issues)
    {
        if (asked)
        {
            issues.Add(new TranscodeIssue(
                TranscodeIssueSeverity.Warning,
                $"{what} is not mapped for the {encoder} element and is ignored; set its properties through Options"));
        }
    }

    private static string Integer(double value) => ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
