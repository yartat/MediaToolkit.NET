#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.FFmpeg.Native;

namespace MediaToolkitNet.FFmpeg;

/// <summary>
/// What a stream group describes. The numbers are FFmpeg's and only ever grow, so a group of a kind
/// this binding does not know is reported as its number, not refused.
/// </summary>
public enum FFmpegStreamGroupType
{
    /// <summary>No parameters.</summary>
    None = 0,

    /// <summary>An IAMF audio element.</summary>
    IamfAudioElement = 1,

    /// <summary>An IAMF mix presentation.</summary>
    IamfMixPresentation = 2,

    /// <summary>A grid of tiles that make one picture (HEIF, AVIF).</summary>
    TileGrid = 3,

    /// <summary>A base layer and an LCEVC enhancement layer (FFmpeg 8.0, named LCEVC before 8.1).</summary>
    LayeredVideo = 4,

    /// <summary>A track reference (FFmpeg 8.1).</summary>
    TrackReference = 5,

    /// <summary>A Dolby Vision base layer and its enhancement layer (FFmpeg 8.1).</summary>
    DolbyVision = 6,

    /// <summary>A picture and its HDR gain map (FFmpeg 9.1).</summary>
    GainMap = 7,

    /// <summary>A picture and its alpha plane (FFmpeg 9.1).</summary>
    Alpha = 8,
}

/// <summary>Where one tile of a <see cref="FFmpegTileGrid"/> goes.</summary>
/// <param name="StreamInGroup">Which stream of the group holds the tile, counting from 0.</param>
/// <param name="X">Horizontal offset of the tile's top left pixel on the canvas.</param>
/// <param name="Y">Vertical offset of the tile's top left pixel on the canvas.</param>
public readonly record struct FFmpegTile(int StreamInGroup, int X, int Y);

/// <summary>How the tiles of a picture are laid out.</summary>
/// <param name="CodedWidth">Width of the canvas the tiles are placed on.</param>
/// <param name="CodedHeight">Height of the canvas the tiles are placed on.</param>
/// <param name="Width">Width of the picture to show, after cropping the canvas.</param>
/// <param name="Height">Height of the picture to show, after cropping the canvas.</param>
/// <param name="HorizontalOffset">Left edge of the picture on the canvas.</param>
/// <param name="VerticalOffset">Top edge of the picture on the canvas.</param>
/// <param name="Background">The canvas colour as R, G, B, A.</param>
/// <param name="Tiles">The tiles, in the order they are drawn.</param>
public sealed record FFmpegTileGrid(
    int CodedWidth,
    int CodedHeight,
    int Width,
    int Height,
    int HorizontalOffset,
    int VerticalOffset,
    byte[] Background,
    IReadOnlyList<FFmpegTile> Tiles);

/// <summary>The size of a layered video as it is shown.</summary>
/// <param name="EnhancementStream">Which stream of the group is the enhancement layer, counting from 0.</param>
/// <param name="Width">Width to show.</param>
/// <param name="Height">Height to show.</param>
public readonly record struct FFmpegLayeredVideo(int EnhancementStream, int Width, int Height);

/// <summary>
/// A set of streams a container says belong together (<c>AVStreamGroup</c>): the tiles of one picture, the
/// substreams of an IAMF audio element, a picture and its gain map.
/// </summary>
/// <param name="Index">Position in the file's list of groups.</param>
/// <param name="Id">The group's id in the container.</param>
/// <param name="Type">What the group describes.</param>
/// <param name="TypeValue">The raw <c>AVStreamGroupParamsType</c>, for a kind <paramref name="Type"/> does not name.</param>
/// <param name="StreamIndices">Container stream indices of the members, as <see cref="FFmpegDemuxer.Streams"/> numbers them.</param>
/// <param name="TileGrid">The layout, for a <see cref="FFmpegStreamGroupType.TileGrid"/> whose parameters read sensibly.</param>
/// <param name="LayeredVideo">The sizes, for a <see cref="FFmpegStreamGroupType.LayeredVideo"/>.</param>
public sealed record FFmpegStreamGroup(
    int Index,
    long Id,
    FFmpegStreamGroupType Type,
    int TypeValue,
    IReadOnlyList<int> StreamIndices,
    FFmpegTileGrid? TileGrid,
    FFmpegLayeredVideo? LayeredVideo);
