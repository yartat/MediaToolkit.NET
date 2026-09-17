using FluentAssertions;
using FluentAssertions.Execution;
using MediaToolkitNet.FFmpeg.Native;
using Xunit;

namespace MediaToolkitNet.Tests;

/// <summary>
/// The table that says which struct offsets belong to which FFmpeg release.
/// </summary>
/// <remarks>
/// The expected values here were read with <c>offsetof</c> against the headers
/// of n7.1.5, n8.1.2 and n9.0.1 on a 64-bit target. The integration tests check
/// the same numbers against whichever FFmpeg is actually installed; these check
/// that the table itself has not been edited into something inconsistent.
/// </remarks>
public class FFmpegGenerationTests
{
    [Theory]
    [InlineData(7, 59, 61, 61, 8, 5, 84, 140)]
    [InlineData(8, 60, 62, 62, 9, 6, 84, 136)]
    [InlineData(9, 61, 63, 63, 10, 7, 84, 136)]
    public void EachKnownSeriesCarriesTheVersionsAndOffsetsItsHeadersGive(
        int release,
        int avUtil,
        int avCodec,
        int avFormat,
        int swScale,
        int swResample,
        int timeBase,
        int pixFmt)
    {
        var generation = FFmpegGeneration.Known.Should().ContainSingle(x => x.Release == release).Subject;

        using var _ = new AssertionScope();

        generation.AvUtil.Should().Be(avUtil);
        generation.AvCodec.Should().Be(avCodec);
        generation.AvFormat.Should().Be(avFormat);
        generation.AvDevice.Should().Be(avFormat, "libavdevice carries libavformat's major version");
        generation.SwScale.Should().Be(swScale);
        generation.SwResample.Should().Be(swResample);
        generation.CodecContextTimeBase.Should().Be(timeBase);
        generation.CodecContextPixFmt.Should().Be(pixFmt);
    }

    [Fact]
    public void TheSeriesAreListedNewestFirst() =>
        FFmpegGeneration.Known.Select(x => x.Release).Should().BeInDescendingOrder();

    [Fact]
    public void NoTwoSeriesShareALibraryVersion()
    {
        using var _ = new AssertionScope();

        FFmpegGeneration.Known.Select(x => x.AvUtil).Should().OnlyHaveUniqueItems();
        FFmpegGeneration.Known.Select(x => x.AvCodec).Should().OnlyHaveUniqueItems();
        FFmpegGeneration.Known.Select(x => x.AvFormat).Should().OnlyHaveUniqueItems();
        FFmpegGeneration.Known.Select(x => x.SwScale).Should().OnlyHaveUniqueItems();
        FFmpegGeneration.Known.Select(x => x.SwResample).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryKnownSeriesFindsItself()
    {
        foreach (var generation in FFmpegGeneration.Known)
        {
            FFmpegGeneration.Find(generation.AvUtil, generation.AvCodec, generation.AvFormat)
                .Should().Be(generation);
        }
    }

    [Theory]
    [InlineData(58, 60, 60)]
    [InlineData(62, 64, 64)]
    [InlineData(0, 0, 0)]
    public void ASeriesThatIsNotInTheTableIsNotFound(int avUtil, int avCodec, int avFormat) =>
        FFmpegGeneration.Find(avUtil, avCodec, avFormat).Should().BeNull();

    [Fact]
    public void LibrariesFromTwoSeriesAreNotASeries()
    {
        // avutil from FFmpeg 9 beside avcodec from FFmpeg 7 is not FFmpeg 9 and
        // not FFmpeg 7, and reading either with the other's offsets is how a
        // process corrupts its own heap.
        FFmpegGeneration.Find(avUtil: 61, avCodec: 61, avFormat: 61).Should().BeNull();
        FFmpegGeneration.Find(avUtil: 59, avCodec: 63, avFormat: 63).Should().BeNull();
    }

    [Fact]
    public void ASeriesSaysWhichReleaseItIs() =>
        FFmpegGeneration.Known[0].ToString().Should().Be("FFmpeg 9.x");
}
