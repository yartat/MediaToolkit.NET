#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions.Formats;

namespace MediaToolkitNet.Abstractions.Transcoding;

/// <summary>
/// A video filter every transcoding backend can apply. Each backend translates
/// these into its own terms: a libavfilter chain for FFmpeg and mpv, elements for
/// GStreamer.
/// </summary>
public abstract record VideoFilter
{
    private protected VideoFilter()
    {
    }

    /// <summary>Resizes. -1 for one side keeps the aspect ratio.</summary>
    /// <param name="Width">Width in pixels, or -1.</param>
    /// <param name="Height">Height in pixels, or -1.</param>
    public sealed record Scale(int Width, int Height) : VideoFilter;

    /// <summary>Cuts a rectangle out of the picture.</summary>
    /// <param name="Width">Width of the rectangle.</param>
    /// <param name="Height">Height of the rectangle.</param>
    /// <param name="X">Left edge.</param>
    /// <param name="Y">Top edge.</param>
    public sealed record Crop(int Width, int Height, int X, int Y) : VideoFilter;

    /// <summary>Changes the frame rate by duplicating or dropping frames.</summary>
    /// <param name="Rate">The rate to reach.</param>
    public sealed record FrameRate(Rational Rate) : VideoFilter;

    /// <summary>Rotates clockwise by a multiple of 90 degrees.</summary>
    /// <param name="Degrees">90, 180 or 270.</param>
    public sealed record Rotate(int Degrees) : VideoFilter
    {
        /// <summary>The rotation, checked.</summary>
        public int Degrees { get; } = Degrees is 90 or 180 or 270
            ? Degrees
            : throw new ArgumentOutOfRangeException(nameof(Degrees), Degrees, "Rotation must be 90, 180 or 270 degrees.");
    }

    /// <summary>Mirrors the picture.</summary>
    /// <param name="Horizontal">Left to right.</param>
    /// <param name="Vertical">Top to bottom.</param>
    public sealed record Flip(bool Horizontal, bool Vertical) : VideoFilter;

    /// <summary>Turns interlaced fields into progressive frames.</summary>
    public sealed record Deinterlace : VideoFilter;

    /// <summary>
    /// A filter in one backend's own syntax, which the others refuse: a
    /// libavfilter chain for <c>ffmpeg</c> and <c>mpv</c>, a <c>gst-launch</c>
    /// fragment for <c>gstreamer</c>.
    /// </summary>
    /// <param name="Backend">The backend this is written for.</param>
    /// <param name="Description">The filter itself.</param>
    public sealed record Custom(string Backend, string Description) : VideoFilter;
}

/// <summary>An audio filter every transcoding backend can apply.</summary>
public abstract record AudioFilter
{
    private protected AudioFilter()
    {
    }

    /// <summary>Scales the level.</summary>
    /// <param name="Gain">Linear gain: 0.5 halves the amplitude, 2 doubles it.</param>
    public sealed record Volume(double Gain) : AudioFilter;

    /// <summary>A filter in one backend's own syntax, which the others refuse.</summary>
    /// <param name="Backend">The backend this is written for.</param>
    /// <param name="Description">The filter itself.</param>
    public sealed record Custom(string Backend, string Description) : AudioFilter;
}
