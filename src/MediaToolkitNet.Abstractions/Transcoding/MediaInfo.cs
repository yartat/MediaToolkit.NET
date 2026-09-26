#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Recording;

namespace MediaToolkitNet.Abstractions.Transcoding;

/// <summary>What a stream inside a container carries.</summary>
public enum MediaStreamKind
{
    /// <summary>Pictures.</summary>
    Video,

    /// <summary>Sound.</summary>
    Audio,

    /// <summary>Subtitles, as text or as bitmaps.</summary>
    Subtitle,

    /// <summary>Timed data that is neither of the above, such as timecode or telemetry.</summary>
    Data,

    /// <summary>A file carried alongside the media, such as a font in Matroska.</summary>
    Attachment,
}

/// <summary>One stream of a probed file.</summary>
/// <param name="Index">
/// Position of the stream in the container. The FFmpeg and mpv backends both
/// report the container's own numbering, so an index read with one can be used
/// with the other.
/// </param>
/// <param name="Kind">What the stream carries.</param>
/// <param name="CodecName">The codec as the backend names it, for example <c>h264</c> or <c>subrip</c>.</param>
public sealed record MediaStreamInfo(int Index, MediaStreamKind Kind, string CodecName)
{
    /// <summary>The codec as this library names it, or <see langword="null"/> when it has no name here.</summary>
    public MediaCodec? Codec { get; init; }

    /// <summary>ISO 639 language tag, when the container states one.</summary>
    public string? Language { get; init; }

    /// <summary>Title of the stream, when the container states one.</summary>
    public string? Title { get; init; }

    /// <summary>True when the stream is marked as the default of its kind.</summary>
    public bool IsDefault { get; init; }

    /// <summary>True when the stream is marked as forced, as foreign-dialogue subtitles usually are.</summary>
    public bool IsForced { get; init; }

    /// <summary>
    /// True when the stream is a still picture, such as cover art, rather than
    /// video. Selecting the first or all video streams passes over these.
    /// </summary>
    public bool IsAttachedPicture { get; init; }

    /// <summary>Stream duration, or <see cref="TimeSpan.Zero"/> when the container does not state one.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Bit rate in bits per second, or 0 when unknown.</summary>
    public long BitRate { get; init; }

    /// <summary>Geometry, pixel layout and frame rate, for a video stream.</summary>
    public VideoFormat? Video { get; init; }

    /// <summary>Rate, channels and sample layout, for an audio stream.</summary>
    public AudioFormat? Audio { get; init; }

    /// <summary>
    /// True for text subtitles (SRT, ASS, WebVTT, timed text), false for bitmap
    /// ones (DVD, PGS). Only text can be converted between formats.
    /// </summary>
    public bool IsTextSubtitle { get; init; }

    /// <inheritdoc />
    public override string ToString()
    {
        var detail = Video is { } v ? $" {v}" : Audio is { } a ? $" {a}" : string.Empty;
        var language = Language is null ? string.Empty : $" [{Language}]";
        return $"#{Index} {Kind} {CodecName}{detail}{language}";
    }
}

/// <summary>One chapter of a probed file.</summary>
/// <param name="Start">Where the chapter begins.</param>
/// <param name="End">Where the chapter ends.</param>
/// <param name="Title">The chapter's title, when it has one.</param>
public sealed record MediaChapter(TimeSpan Start, TimeSpan End, string? Title);

/// <summary>What a file contains, as a backend read it.</summary>
/// <param name="Uri">The file or URL that was probed.</param>
/// <param name="ContainerName">The container as the backend names it, for example <c>matroska,webm</c>.</param>
/// <param name="Duration">Length of the longest stream.</param>
/// <param name="Streams">Every stream, in container order.</param>
public sealed record MediaInfo(
    string Uri,
    string ContainerName,
    TimeSpan Duration,
    IReadOnlyList<MediaStreamInfo> Streams)
{
    /// <summary>The backend that read the file.</summary>
    public string Backend { get; init; } = string.Empty;

    /// <summary>The container as this library names it, or <see langword="null"/> when it has no name here.</summary>
    public MediaContainer? Container { get; init; }

    /// <summary>File-level tags such as <c>title</c> and <c>artist</c>.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>Chapters, in order.</summary>
    public IReadOnlyList<MediaChapter> Chapters { get; init; } = [];

    /// <summary>The streams of one kind, in container order.</summary>
    public IEnumerable<MediaStreamInfo> OfKind(MediaStreamKind kind) => Streams.Where(s => s.Kind == kind);

    /// <summary>The stream at a container index, or <see langword="null"/>.</summary>
    public MediaStreamInfo? StreamAt(int index) => Streams.FirstOrDefault(s => s.Index == index);
}

/// <summary>Reads what a file contains without decoding it.</summary>
public interface IMediaProber
{
    /// <summary>Name of the backend implementing this prober.</summary>
    string Backend { get; }

    /// <summary>Reads the container, its streams, tags and chapters.</summary>
    /// <param name="uri">A file path or a URL the backend can open.</param>
    /// <exception cref="MediaToolkitNetException">The file could not be opened or read.</exception>
    MediaInfo Probe(string uri);
}
