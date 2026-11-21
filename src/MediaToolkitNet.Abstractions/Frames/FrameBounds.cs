#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions.Formats;

namespace MediaToolkitNet.Abstractions.Frames;

/// <summary>
/// Checks that the planes a producer is about to hand out lie inside the
/// buffer they were taken from. <see cref="VideoFrame.GetPlane"/> trusts the
/// geometry, so a buffer shorter than its format describes would be read past
/// its end.
/// </summary>
public static class FrameBounds
{
    /// <summary>True when every plane, rows and padding included, lies inside the buffer.</summary>
    /// <param name="format">The frame's geometry.</param>
    /// <param name="planes">Pointer to the first row of each plane.</param>
    /// <param name="strides">Row pitch of each plane; negative for a bottom-up plane.</param>
    /// <param name="planeCount">Number of valid entries.</param>
    /// <param name="start">Address of the first byte of the buffer.</param>
    /// <param name="length">Size of the buffer in bytes.</param>
    public static bool FitsIn(
        VideoFormat format, ReadOnlySpan<nint> planes, ReadOnlySpan<int> strides, int planeCount, nint start, long length)
    {
        for (var plane = 0; plane < planeCount; plane++)
        {
            // A compressed format has no row geometry to check.
            if (format.PixelFormat.MinimumStride(format.Width, plane) == 0)
            {
                continue;
            }

            var rows = format.PixelFormat.PlaneHeight(format.Height, plane);
            var pitch = Math.Abs((long)strides[plane]);
            var first = (long)planes[plane] - start;

            // A bottom-up plane's pointer is its top row, the last one in memory.
            var low = strides[plane] < 0 ? first - (pitch * (rows - 1)) : first;
            var high = strides[plane] < 0 ? first + pitch : first + (pitch * rows);
            if (low < 0 || high > length)
            {
                return false;
            }
        }

        return true;
    }
}
