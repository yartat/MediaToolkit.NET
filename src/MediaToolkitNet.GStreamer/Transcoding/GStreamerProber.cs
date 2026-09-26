#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer.Transcoding;

/// <summary>
/// Reads what a file contains with <c>GstDiscoverer</c>, which prerolls the
/// file through <c>uridecodebin</c> and reports what it found.
/// </summary>
/// <remarks>
/// <para>
/// Streams are numbered from the discoverer's stream number, which follows the
/// order the demuxer exposes them in. That is the container order, the same
/// numbering the FFmpeg and mpv probers report and the order in which
/// <c>parsebin</c> names its pads, which is what lets
/// <see cref="GStreamerTranscoder"/> address a stream by index.
/// </para>
/// <para>
/// GStreamer describes codecs with caps rather than names, so
/// <see cref="MediaStreamInfo.CodecName"/> is FFmpeg's name where there is a
/// mapping and the caps name otherwise. Languages come back as ISO 639-2
/// codes, as the other probers report them. The discoverer does not report
/// whether a stream is marked default or forced, so those are always false.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerProber : IMediaProber
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Tags that describe the codec or the container rather than the content.</summary>
    private static readonly HashSet<string> TechnicalTags = new(StringComparer.Ordinal)
    {
        "container-format", "video-codec", "audio-codec", "subtitle-codec", "codec",
        "container-specific-track-id", "language-code", "extended-comment",
    };

    /// <inheritdoc />
    public string Backend => GstConstants.BackendName;

    /// <inheritdoc />
    public MediaInfo Probe(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        GstPbutils.EnsureLoaded();
        GstLayout.Require();

        void* error = null;
        var discoverer = GstPbutils.gst_discoverer_new((ulong)(Timeout.Ticks * GstConstants.NanosecondsPerTick), &error);
        if (discoverer is null)
        {
            throw new MediaToolkitNetException(GstConstants.BackendName, $"gst_discoverer_new failed: {Gst.TakeError(error)}", 0);
        }

        try
        {
            var target = ToUri(uri);
            Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var utf8 = new Utf8Scoped(target, scratch);
            var info = GstPbutils.gst_discoverer_discover_uri(discoverer, utf8.Pointer, &error);
            if (info is null)
            {
                throw new MediaToolkitNetException(GstConstants.BackendName, $"could not read {uri}: {Gst.TakeError(error)}", 0);
            }

            try
            {
                if (error is not null)
                {
                    // A result with an error attached is one the discoverer gave up on.
                    var result = GstPbutils.gst_discoverer_info_get_result(info);
                    throw new MediaToolkitNetException(GstConstants.BackendName, $"could not read {uri}: {Gst.TakeError(error)}", result);
                }

                return Describe(uri, info);
            }
            finally
            {
                Gst.g_object_unref(info);
            }
        }
        finally
        {
            Gst.g_object_unref(discoverer);
        }
    }

    /// <summary>A file path turned into the URI the discoverer and <c>urisourcebin</c> take; a URI is returned as it is.</summary>
    internal static string ToUri(string pathOrUri)
    {
        if (pathOrUri.Contains("://", StringComparison.Ordinal))
        {
            return pathOrUri;
        }

        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(Path.GetFullPath(pathOrUri), scratch);
        void* error = null;
        var uri = Gst.gst_filename_to_uri(utf8.Pointer, &error);
        if (uri is null)
        {
            throw new MediaToolkitNetException(GstConstants.BackendName, $"{pathOrUri} is not a usable path: {Gst.TakeError(error)}", 0);
        }

        return Gst.TakeString(uri);
    }

    private static MediaInfo Describe(string uri, void* info)
    {
        var duration = Time((long)GstPbutils.gst_discoverer_info_get_duration(info));

        var top = GstPbutils.gst_discoverer_info_get_stream_info(info);
        var containerName = string.Empty;
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (top is not null)
        {
            try
            {
                containerName = CapsName(top) ?? string.Empty;
                if (Utf8.ToManagedOrEmpty(GstPbutils.gst_discoverer_stream_info_get_stream_type_nick(top)) == "container")
                {
                    foreach (var (key, value) in StringTags(GstPbutils.gst_discoverer_container_info_get_tags(top)))
                    {
                        if (!TechnicalTags.Contains(key))
                        {
                            metadata[key] = value;
                        }
                    }
                }
            }
            finally
            {
                Gst.g_object_unref(top);
            }
        }

        var streams = new List<MediaStreamInfo>();
        var list = GstPbutils.gst_discoverer_info_get_stream_list(info);
        try
        {
            var position = 0;
            for (var node = list; node is not null; node = GstLayout.NextListNode(node))
            {
                if (Stream(GstLayout.DataOfListNode(node), position++, containerName) is { } stream)
                {
                    streams.Add(stream);
                }
            }
        }
        finally
        {
            if (list is not null)
            {
                GstPbutils.gst_discoverer_stream_info_list_free(list);
            }
        }

        return new MediaInfo(uri, containerName, duration, [.. streams.OrderBy(s => s.Index)])
        {
            Backend = GstConstants.BackendName,
            Container = ContainerOf(containerName, uri),
            Metadata = metadata,
            Chapters = Chapters(GstPbutils.gst_discoverer_info_get_toc(info), duration),
        };
    }

    private static MediaStreamInfo? Stream(void* info, int position, string containerName)
    {
        var kind = Utf8.ToManagedOrEmpty(GstPbutils.gst_discoverer_stream_info_get_stream_type_nick(info)) switch
        {
            "video" => MediaStreamKind.Video,
            "audio" => MediaStreamKind.Audio,
            "subtitles" => MediaStreamKind.Subtitle,
            _ => (MediaStreamKind?)null,
        };

        if (kind is null)
        {
            return null;
        }

        var (codecName, codec) = CodecOf(info, containerName);
        var number = GstPbutils.gst_discoverer_stream_info_get_stream_number(info);
        var tags = StringTags(GstPbutils.gst_discoverer_stream_info_get_tags(info));

        var stream = new MediaStreamInfo(number > 0 ? number - 1 : position, kind.Value, codecName)
        {
            Codec = codec,
            Title = tags.GetValueOrDefault("title"),
        };

        return kind switch
        {
            MediaStreamKind.Video => stream with
            {
                IsAttachedPicture = GstPbutils.gst_discoverer_video_info_is_image(info) != 0,
                BitRate = GstPbutils.gst_discoverer_video_info_get_bitrate(info),
                Video = new VideoFormat(
                    (int)GstPbutils.gst_discoverer_video_info_get_width(info),
                    (int)GstPbutils.gst_discoverer_video_info_get_height(info),
                    PixelFormat.Unknown,
                    GstPbutils.gst_discoverer_video_info_get_framerate_num(info) is var num and > 0
                        ? new Rational((int)num, (int)Math.Max(1u, GstPbutils.gst_discoverer_video_info_get_framerate_denom(info)))
                        : Rational.Zero),
            },
            MediaStreamKind.Audio => stream with
            {
                Language = Language(GstPbutils.gst_discoverer_audio_info_get_language(info)),
                BitRate = GstPbutils.gst_discoverer_audio_info_get_bitrate(info),
                Audio = new AudioFormat(
                    (int)GstPbutils.gst_discoverer_audio_info_get_sample_rate(info),
                    (int)GstPbutils.gst_discoverer_audio_info_get_channels(info),
                    SampleFormat.Unknown),
            },
            _ => stream with
            {
                Language = Language(GstPbutils.gst_discoverer_subtitle_info_get_language(info)),
                IsTextSubtitle = codec?.IsTextSubtitle() ?? !codecName.StartsWith("subpicture/", StringComparison.Ordinal),
            },
        };
    }

    /// <summary>The codec a stream's caps describe, as FFmpeg names it where there is a mapping.</summary>
    private static (string Name, MediaCodec? Codec) CodecOf(void* info, string containerName)
    {
        var caps = GstPbutils.gst_discoverer_stream_info_get_caps(info);
        if (caps is null)
        {
            return (string.Empty, null);
        }

        try
        {
            var structure = Gst.gst_caps_get_structure(caps, 0);
            if (structure is null)
            {
                return (string.Empty, null);
            }

            var name = Utf8.ToManagedOrEmpty(Gst.gst_structure_get_name(structure));
            var mapped = name switch
            {
                "video/x-h264" => "h264",
                "video/x-h265" => "hevc",
                "video/x-vp8" => "vp8",
                "video/x-vp9" => "vp9",
                "video/x-av1" => "av1",
                "image/jpeg" => "mjpeg",
                "audio/mpeg" => MpegAudio(structure),
                "audio/x-opus" => "opus",
                "audio/x-flac" => "flac",
                "audio/x-vorbis" => "vorbis",
                "audio/x-ac3" or "audio/ac3" => "ac3",
                "audio/x-dts" => "dts",
                "audio/x-true-hd" => "truehd",
                "audio/x-raw" => RawAudio(structure),

                // The same caps come out of an SRT track and an MP4 timed-text
                // track; the container is what tells them apart.
                "text/x-raw" => containerName == "video/quicktime" ? "mov_text" : "subrip",
                "application/x-ass" or "application/x-ssa" => "ass",
                "application/x-subtitle" => "subrip",
                "application/x-subtitle-vtt" => "webvtt",
                "subpicture/x-dvd" => "dvd_subtitle",
                "subpicture/x-pgs" => "hdmv_pgs_subtitle",
                _ => null,
            };

            return mapped is null ? (name, null) : (mapped, MediaFormats.CodecFromName(mapped));
        }
        finally
        {
            Gst.gst_mini_object_unref(caps);
        }
    }

    private static string? MpegAudio(void* structure)
    {
        var version = IntField(structure, "mpegversion");
        if (version is 2 or 4)
        {
            return "aac";
        }

        return IntField(structure, "layer") switch
        {
            3 => "mp3",
            2 => "mp2",
            _ => null,
        };
    }

    private static string? RawAudio(void* structure)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var field = new Utf8Scoped("format", scratch);
        return Utf8.ToManagedOrEmpty(Gst.gst_structure_get_string(structure, field.Pointer)) switch
        {
            "U8" => "pcm_u8",
            "S16LE" => "pcm_s16le",
            "S24LE" => "pcm_s24le",
            "S32LE" => "pcm_s32le",
            _ => null,
        };
    }

    private static int? IntField(void* structure, string name)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var field = new Utf8Scoped(name, scratch);
        int value;
        return Gst.gst_structure_get_int(structure, field.Pointer, &value) != 0 ? value : null;
    }

    private static string? CapsName(void* info)
    {
        var caps = GstPbutils.gst_discoverer_stream_info_get_caps(info);
        if (caps is null)
        {
            return null;
        }

        try
        {
            var structure = Gst.gst_caps_get_structure(caps, 0);
            return structure is null ? null : Utf8.ToManagedOrEmpty(Gst.gst_structure_get_name(structure));
        }
        finally
        {
            Gst.gst_mini_object_unref(caps);
        }
    }

    /// <summary>
    /// The string-valued tags of a list. Tags of other types, such as dates and
    /// bit rates, are skipped: reading them as strings is a GLib critical.
    /// </summary>
    internal static Dictionary<string, string> StringTags(void* list)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (list is null)
        {
            return result;
        }

        var count = Gst.gst_tag_list_n_tags(list);
        for (var i = 0; i < count; i++)
        {
            var name = Gst.gst_tag_list_nth_tag_name(list, (uint)i);
            if (name is null || Gst.gst_tag_get_type(name) != GstTypes.String)
            {
                continue;
            }

            byte* value = null;
            if (Gst.gst_tag_list_get_string(list, name, &value) != 0)
            {
                result[Utf8.ToManagedOrEmpty(name)] = Gst.TakeString(value);
            }
        }

        return result;
    }

    /// <summary>Chapters from a table of contents, however deep its editions nest them.</summary>
    private static List<MediaChapter> Chapters(void* toc, TimeSpan duration)
    {
        var found = new List<(TimeSpan Start, TimeSpan? End, string? Title)>();
        if (toc is not null)
        {
            Collect(Gst.gst_toc_get_entries(toc), found);
        }

        found.Sort((a, b) => a.Start.CompareTo(b.Start));

        // An entry without a stop time ends where the next one starts.
        return
        [
            .. found.Select((c, i) => new MediaChapter(
                c.Start,
                c.End ?? (i + 1 < found.Count ? found[i + 1].Start : duration),
                c.Title)),
        ];
    }

    private static void Collect(void* entries, List<(TimeSpan Start, TimeSpan? End, string? Title)> found)
    {
        for (var node = entries; node is not null; node = GstLayout.NextListNode(node))
        {
            var entry = GstLayout.DataOfListNode(node);
            if (Gst.gst_toc_entry_get_entry_type(entry) == GstTypes.TocEntryChapter)
            {
                long start;
                long stop;
                if (Gst.gst_toc_entry_get_start_stop_times(entry, &start, &stop) != 0 && start >= 0)
                {
                    found.Add((
                        Time(start),
                        stop > start ? Time(stop) : null,
                        StringTags(Gst.gst_toc_entry_get_tags(entry)).GetValueOrDefault("title")));
                }
            }

            Collect(Gst.gst_toc_entry_get_sub_entries(entry), found);
        }
    }

    /// <summary>
    /// GStreamer's two-letter code turned into the ISO 639-2 code the other
    /// probers report. Matroska's default, <c>und</c>, is no language at all,
    /// which is how FFmpeg reports it.
    /// </summary>
    private static string? Language(byte* code)
    {
        if (code is null)
        {
            return null;
        }

        var bibliographic = GstPbutils.gst_tag_get_language_code_iso_639_2B(code);
        var language = bibliographic is not null ? Utf8.ToManagedOrEmpty(bibliographic) : Utf8.ToManagedOrEmpty(code);
        return language is "" or "und" ? null : language;
    }

    private static MediaContainer? ContainerOf(string capsName, string uri) => capsName switch
    {
        "video/x-matroska" or "audio/x-matroska" => MediaContainer.Matroska,
        "video/webm" or "audio/webm" => MediaContainer.WebM,
        "video/quicktime" => MediaFormats.ContainerFromPath(uri) is MediaContainer.Mov ? MediaContainer.Mov : MediaContainer.Mp4,
        "video/mpegts" => MediaContainer.MpegTs,
        "application/ogg" or "audio/ogg" or "video/ogg" => MediaContainer.Ogg,
        "video/x-msvideo" => MediaContainer.Avi,
        "audio/x-wav" => MediaContainer.Wav,
        "audio/x-flac" => MediaContainer.Flac,
        "application/x-id3" => MediaContainer.Mp3,
        _ => null,
    };

    private static TimeSpan Time(long nanoseconds) =>
        nanoseconds <= 0 || (ulong)nanoseconds == GstConstants.ClockTimeNone
            ? TimeSpan.Zero
            : new TimeSpan(nanoseconds / GstConstants.NanosecondsPerTick);
}
