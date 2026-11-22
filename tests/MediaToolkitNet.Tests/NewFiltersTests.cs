#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using FluentAssertions;
using MediaToolkitNet.FFmpeg.Filtering;
using MediaToolkitNet.FFmpeg.Transcoding;
using Xunit;

namespace MediaToolkitNet.Tests;

/// <summary>The typed descriptions of the filters FFmpeg 8.x and 9.x added.</summary>
public class NewFiltersTests
{
    [Fact]
    public void ColorDetectNamesWhatItLooksForAndLeavesDefaultsOut()
    {
        FFmpegNewFilters.ColorDetect().Should().Be("colordetect");
        FFmpegNewFilters.ColorDetect(ColorDetectMode.ColorRange).Should().Be("colordetect=mode=color_range");
        FFmpegNewFilters.ColorDetect(ColorDetectMode.ColorRange | ColorDetectMode.AlphaMode, 0.01)
            .Should().Be("colordetect=threshold=0.01");
        FFmpegNewFilters.ColorDetect(ColorDetectMode.AlphaMode).Should().Be("colordetect=mode=alpha_mode");
    }

    [Fact]
    public void ColorDetectRefusesAThresholdTheFilterRefuses()
    {
        var act = () => FFmpegNewFilters.ColorDetect(threshold: 0.1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void LatticePalWritesOnlyWhatDiffersFromTheDefaults()
    {
        FFmpegNewFilters.LatticePal().Should().Be("latticepal");
        FFmpegNewFilters.LatticePal(density: 12, maxColors: 64, alpha: true, LatticeRefine.Batched, LatticeDither.BlueNoise)
            .Should().Be("latticepal=density=12:max_colors=64:alpha=1:refine=batched:dither=blue_noise");
        FFmpegNewFilters.LatticePal(dither: LatticeDither.None).Should().Be("latticepal=dither=none");
    }

    [Theory]
    [InlineData(0, 256)]
    [InlineData(20, 1)]
    [InlineData(20, 257)]
    public void LatticePalRefusesValuesOutsideWhatTheFilterAccepts(int density, int maxColors)
    {
        var act = () => FFmpegNewFilters.LatticePal(density, maxColors);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void WhisperEscapesThePathOfItsModelAndLeavesDefaultsOut()
    {
        const string model = "C:\\models\\ggml 'base': en.bin";

        FFmpegNewFilters.Whisper(model).Should().Be($"whisper=model={FilterChains.EscapeOptionValue(model)}");
        FFmpegNewFilters.Whisper("m.bin", "de", translate: true, queue: TimeSpan.FromSeconds(10), useGpu: false, gpuDevice: 1,
                destination: "out.srt", format: WhisperFormat.Srt, maxLength: 42)
            .Should().Be("whisper=model=m.bin:language=de:translate=1:queue=10:use_gpu=0:gpu_device=1:destination=out.srt:format=srt:max_len=42");
    }

    [Fact]
    public void WhisperNeedsAModel()
    {
        var act = () => FFmpegNewFilters.Whisper(string.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GainMapWritesOnlyWhatWasAskedFor()
    {
        FFmpegNewFilters.GainMap().Should().Be("gainmap");
        FFmpegNewFilters.GainMap(GainMapMode.Luma, GainMapColorSpace.Alternate, -2, 5.5, 2.2)
            .Should().Be("gainmap=mode=luma:colorspace=alternate:min=-2:max=5.5:gamma=2.2");
    }

    [Fact]
    public void GainMapRefusesABoundOutsideTheRange()
    {
        var act = () => FFmpegNewFilters.GainMap(minGain: -33);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
