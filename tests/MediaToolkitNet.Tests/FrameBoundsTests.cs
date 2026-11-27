#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using FluentAssertions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using Xunit;

namespace MediaToolkitNet.Tests;

/// <summary>A frame must lie inside the buffer it came from.</summary>
public class FrameBoundsTests
{
    private const nint Start = 0x10000;

    private static VideoFormat Format(PixelFormat pixel, int width = 64, int height = 48) => new(width, height, pixel, Rational.Zero);

    [Fact]
    public void ContiguousI420ThatFillsTheBufferFits()
    {
        var format = Format(PixelFormat.Yuv420P);
        nint[] planes = [Start, Start + (64 * 48), Start + (64 * 48) + (32 * 24)];
        int[] strides = [64, 32, 32];

        FrameBounds.FitsIn(format, planes, strides, 3, Start, 64 * 48 * 3 / 2).Should().BeTrue();
    }

    [Fact]
    public void ABufferOneByteShortDoesNotFit()
    {
        var format = Format(PixelFormat.Yuv420P);
        nint[] planes = [Start, Start + (64 * 48), Start + (64 * 48) + (32 * 24)];
        int[] strides = [64, 32, 32];

        FrameBounds.FitsIn(format, planes, strides, 3, Start, (64 * 48 * 3 / 2) - 1).Should().BeFalse();
    }

    [Fact]
    public void ABottomUpPlaneThatStartsAtTheLastRowFits()
    {
        var format = Format(PixelFormat.Bgra32);
        var stride = 64 * 4;
        nint[] planes = [Start + (stride * 47)];
        int[] strides = [-stride];

        FrameBounds.FitsIn(format, planes, strides, 1, Start, (long)stride * 48).Should().BeTrue();
        FrameBounds.FitsIn(format, planes, strides, 1, Start, ((long)stride * 48) - 1).Should().BeFalse();
    }

    [Fact]
    public void APlaneBeforeTheStartOfTheBufferDoesNotFit()
    {
        var format = Format(PixelFormat.Bgra32);
        var stride = 64 * 4;
        nint[] planes = [Start - 16];
        int[] strides = [stride];

        FrameBounds.FitsIn(format, planes, strides, 1, Start, (long)stride * 48).Should().BeFalse();
    }
}
