#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

namespace MediaToolkitNet.Mpv;

/// <summary>What a track of mpv's <c>track-list</c> carries.</summary>
public enum MpvTrackType
{
    /// <summary>A type this binding does not name; see <see cref="MpvTrack.Raw"/>.</summary>
    Unknown,

    /// <summary>Pictures.</summary>
    Video,

    /// <summary>Sound.</summary>
    Audio,

    /// <summary>Subtitles.</summary>
    Subtitle,
}

/// <summary>One entry of mpv's <c>track-list</c>.</summary>
/// <param name="Id">The track's id, which is what <c>aid</c>, <c>vid</c> and <c>sid</c> take. It is not the container index.</param>
/// <param name="Type">What the track carries.</param>
/// <param name="Title">The track's title, when it has one.</param>
/// <param name="Language">ISO 639 language, when the container states one.</param>
/// <param name="Codec">The codec as mpv names it.</param>
/// <param name="IsSelected">Whether mpv is playing this track now.</param>
/// <param name="IsDefault">Whether the container marks it as the default.</param>
/// <param name="IsForced">Whether the container marks it as forced.</param>
/// <param name="IsExternal">Whether it comes from a separate file.</param>
/// <param name="IsAlbumArt">Whether it is cover art, not video.</param>
/// <param name="FfIndex">The FFmpeg stream index, when mpv knows it (not for every track since mpv 0.42).</param>
/// <param name="Width">Width of a video track as the demuxer reports it.</param>
/// <param name="Height">Height of a video track as the demuxer reports it.</param>
/// <param name="Channels">Channel count of an audio track.</param>
/// <param name="SampleRate">Sample rate of an audio track.</param>
/// <param name="Bitrate">Bit rate in bits per second, when the demuxer reports it.</param>
/// <param name="Raw">The whole node, for the keys not named here.</param>
public sealed record MpvTrack(
    long Id,
    MpvTrackType Type,
    string? Title,
    string? Language,
    string? Codec,
    bool IsSelected,
    bool IsDefault,
    bool IsForced,
    bool IsExternal,
    bool IsAlbumArt,
    long? FfIndex,
    long? Width,
    long? Height,
    long? Channels,
    long? SampleRate,
    long? Bitrate,
    MpvNode Raw)
{
    internal static MpvTrack From(MpvNode node) => new(
        node["id"]?.AsInt64() ?? 0,
        node["type"]?.AsString() switch
        {
            "video" => MpvTrackType.Video,
            "audio" => MpvTrackType.Audio,
            "sub" => MpvTrackType.Subtitle,
            _ => MpvTrackType.Unknown,
        },
        node["title"]?.AsString(),
        node["lang"]?.AsString(),
        node["codec"]?.AsString(),
        node["selected"]?.AsFlag() ?? false,
        node["default"]?.AsFlag() ?? false,
        node["forced"]?.AsFlag() ?? false,
        node["external"]?.AsFlag() ?? false,
        node["albumart"]?.AsFlag() ?? false,
        node["ff-index"]?.AsInt64(),
        node["demux-w"]?.AsInt64(),
        node["demux-h"]?.AsInt64(),
        node["demux-channel-count"]?.AsInt64(),
        node["demux-samplerate"]?.AsInt64(),
        node["demux-bitrate"]?.AsInt64(),
        node);
}

/// <summary>One entry of mpv's <c>chapter-list</c>.</summary>
/// <param name="Title">The chapter's title.</param>
/// <param name="Start">Where the chapter begins.</param>
public sealed record MpvChapter(string Title, TimeSpan Start);
