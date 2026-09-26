#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Globalization;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.Mpv.Native;

namespace MediaToolkitNet.Mpv.Transcoding;

/// <summary>
/// Reads what a file contains by loading it into a paused, silent mpv and
/// reading its <c>track-list</c>, <c>metadata</c> and <c>chapter-list</c>.
/// </summary>
/// <remarks>
/// <para>
/// Stream indices come from each track's <c>ff-index</c>, which is the
/// container's own numbering, so they match what the FFmpeg prober reports.
/// </para>
/// <para>
/// mpv refuses to play a file that holds nothing but subtitles, such as an
/// <c>.srt</c>, with <c>MPV_ERROR_NOTHING_TO_PLAY</c>. Such a file is probed
/// again as an external subtitle file beside a one-second blank picture from
/// libavfilter, which is how mpv itself would load it, and only its own tracks
/// are reported.
/// </para>
/// </remarks>
public sealed unsafe class MpvProber : IMediaProber
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(15);

    /// <summary>MPV_ERROR_NOTHING_TO_PLAY: the file has no audio or video.</summary>
    private const int NothingToPlay = -16;

    /// <summary>What the subtitles of a subtitle-only file are loaded beside.</summary>
    private const string Placeholder = "av://lavfi:color=c=black:s=16x16:d=1";

    /// <inheritdoc />
    public string Backend => Native.Mpv.BackendName;

    /// <inheritdoc />
    public MediaInfo Probe(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        try
        {
            return Probe(uri, subtitlesOnly: false);
        }
        catch (MediaToolkitNetException ex) when (ex.NativeCode == NothingToPlay)
        {
            return Probe(uri, subtitlesOnly: true);
        }
    }

    private static MediaInfo Probe(string uri, bool subtitlesOnly)
    {
        List<KeyValuePair<string, string>> options =
        [
            new("vo", "null"),
            new("ao", "null"),
            new("pause", "yes"),
            new("idle", "yes"),
            new("terminal", "no"),
            new("load-scripts", "no"),
            new("ytdl", "no"),

            // Only what is in the file itself, not what mpv would find beside it.
            new("sub-auto", "no"),
            new("audio-file-auto", "no"),
            new("cover-art-auto", "no"),
        ];

        if (subtitlesOnly)
        {
            options.Add(new("sub-files-append", uri));
        }

        using var client = MpvClient.Create(options);
        client.Command("loadfile", subtitlesOnly ? Placeholder : uri);
        WaitUntilLoaded(client, uri);

        // The file's own tracks are the internal ones, except when it was
        // loaded as an external subtitle file beside the placeholder.
        var streams = new List<MediaStreamInfo>();
        var count = (int)(client.GetNumber("track-list/count") ?? 0);
        for (var i = 0; i < count; i++)
        {
            if (client.GetFlag($"track-list/{i}/external") == subtitlesOnly && Track(client, i) is { } stream)
            {
                // mpv titles an external track with its file name, which the
                // file itself does not say.
                streams.Add(subtitlesOnly ? stream with { Title = null } : stream);
            }
        }

        if (subtitlesOnly)
        {
            return new MediaInfo(uri, "subtitles", TimeSpan.Zero, [.. streams.OrderBy(s => s.Index)])
            {
                Backend = Native.Mpv.BackendName,
            };
        }

        var duration = TimeSpan.FromSeconds(client.GetNumber("duration") ?? 0);
        var format = client.Get("file-format") ?? string.Empty;
        return new MediaInfo(uri, format, duration, [.. streams.OrderBy(s => s.Index)])
        {
            Backend = Native.Mpv.BackendName,
            Container = ContainerOf(format, uri),
            Metadata = Metadata(client),
            Chapters = Chapters(client, duration),
        };
    }

    private static void WaitUntilLoaded(MpvClient client, string uri)
    {
        var deadline = DateTime.UtcNow + LoadTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var evt = client.WaitEvent(TimeSpan.FromMilliseconds(250));
            if (evt is null)
            {
                continue;
            }

            switch (evt->EventId)
            {
                case MpvEventId.FileLoaded:
                    return;
                case MpvEventId.EndFile:
                    var error = evt->Data is null ? 0 : ((int*)evt->Data)[1];
                    throw new MediaToolkitNetException(
                        Native.Mpv.BackendName, $"mpv could not open {uri}: {Native.Mpv.Describe(error)}", error);
                case MpvEventId.Shutdown:
                    throw new MediaToolkitNetException(Native.Mpv.BackendName, $"mpv shut down while opening {uri}", 0);
            }
        }

        throw new MediaToolkitNetException(Native.Mpv.BackendName, $"mpv did not finish opening {uri} in {LoadTimeout.TotalSeconds} s", 0);
    }

    private static MediaStreamInfo? Track(MpvClient client, int i)
    {
        var prefix = $"track-list/{i}/";
        var kind = client.Get(prefix + "type") switch
        {
            "video" => MediaStreamKind.Video,
            "audio" => MediaStreamKind.Audio,
            "sub" => MediaStreamKind.Subtitle,
            _ => (MediaStreamKind?)null,
        };

        if (kind is null)
        {
            return null;
        }

        var codec = client.Get(prefix + "codec") ?? string.Empty;
        var known = MediaFormats.CodecFromName(codec);
        var index = (int)(client.GetNumber(prefix + "ff-index") ?? i);

        var stream = new MediaStreamInfo(index, kind.Value, codec)
        {
            Codec = known,
            Language = client.Get(prefix + "lang"),
            Title = client.Get(prefix + "title"),
            IsDefault = client.GetFlag(prefix + "default"),
            IsForced = client.GetFlag(prefix + "forced"),
            IsAttachedPicture = client.GetFlag(prefix + "image") || client.GetFlag(prefix + "albumart"),
            IsTextSubtitle = kind == MediaStreamKind.Subtitle && (known?.IsTextSubtitle() ?? codec is "text" or "ssa"),
        };

        return kind switch
        {
            MediaStreamKind.Video => stream with
            {
                Video = new VideoFormat(
                    (int)(client.GetNumber(prefix + "demux-w") ?? 0),
                    (int)(client.GetNumber(prefix + "demux-h") ?? 0),
                    PixelFormat.Unknown,
                    client.GetNumber(prefix + "demux-fps") is { } fps and > 0 ? Rational.FromDouble(fps) : Rational.Zero),
            },
            MediaStreamKind.Audio => stream with
            {
                Audio = new AudioFormat(
                    (int)(client.GetNumber(prefix + "demux-samplerate") ?? 0),
                    (int)(client.GetNumber(prefix + "demux-channel-count") ?? 0),
                    SampleFormat.Unknown),
            },
            _ => stream,
        };
    }

    private static Dictionary<string, string> Metadata(MpvClient client)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var count = (int)(client.GetNumber("metadata/list/count") ?? 0);
        for (var i = 0; i < count; i++)
        {
            if (client.Get($"metadata/list/{i}/key") is { } key)
            {
                result[key] = client.Get($"metadata/list/{i}/value") ?? string.Empty;
            }
        }

        return result;
    }

    private static List<MediaChapter> Chapters(MpvClient client, TimeSpan duration)
    {
        var count = (int)(client.GetNumber("chapter-list/count") ?? 0);
        var starts = new List<(TimeSpan Start, string? Title)>();
        for (var i = 0; i < count; i++)
        {
            starts.Add((
                TimeSpan.FromSeconds(client.GetNumber($"chapter-list/{i}/time") ?? 0),
                client.Get($"chapter-list/{i}/title")));
        }

        // mpv lists where chapters start; each ends where the next begins.
        return
        [
            .. starts.Select((c, i) => new MediaChapter(c.Start, i + 1 < starts.Count ? starts[i + 1].Start : duration, c.Title)),
        ];
    }

    private static MediaContainer? ContainerOf(string format, string uri) => format switch
    {
        "mkv" or "matroska" => MediaFormats.ContainerFromPath(uri) is MediaContainer.WebM ? MediaContainer.WebM : MediaContainer.Matroska,
        _ when format.Contains("mp4", StringComparison.Ordinal) =>
            MediaFormats.ContainerFromPath(uri) is MediaContainer.Mov ? MediaContainer.Mov : MediaContainer.Mp4,
        "mpegts" => MediaContainer.MpegTs,
        "ogg" => MediaContainer.Ogg,
        "avi" => MediaContainer.Avi,
        "wav" => MediaContainer.Wav,
        "flac" => MediaContainer.Flac,
        "mp3" => MediaContainer.Mp3,
        _ => null,
    };
}
