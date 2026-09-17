using FluentAssertions;
using FluentAssertions.Execution;
using MediaToolkitNet.FFmpeg.Native;
using Xunit;

namespace MediaToolkitNet.Tests;

/// <summary>
/// The struct offsets that do not depend on which FFmpeg is installed.
/// </summary>
/// <remarks>
/// <para>
/// These numbers were read with <c>offsetof</c> against the headers of n7.1.5,
/// n8.1.2 and n9.0.1, and they are the same in all three; they have held since
/// FFmpeg 6. Restating them here means an edit to <see cref="AbiLayout"/> has to
/// be a deliberate one.
/// </para>
/// <para>
/// This is a statement of intent, not proof: the integration tests are what
/// check the offsets against a library. They run only where FFmpeg is installed,
/// and this runs everywhere.
/// </para>
/// </remarks>
public class AbiLayoutConstantsTests
{
    [Fact]
    public void TheFormatContextOffsetsAreTheOnesTheHeadersGive()
    {
        using var _ = new AssertionScope();

        AbiLayout.FormatContextPb.Should().Be(32);
        AbiLayout.FormatContextNbStreams.Should().Be(44);
        AbiLayout.FormatContextStreams.Should().Be(48);
    }

    [Fact]
    public void TheStreamOffsetsAreTheOnesTheHeadersGive()
    {
        using var _ = new AssertionScope();

        AbiLayout.StreamIndex.Should().Be(8);
        AbiLayout.StreamCodecPar.Should().Be(16, "codecpar follows priv_data, not the whole struct");
        AbiLayout.StreamTimeBase.Should().Be(32);
        AbiLayout.StreamStartTime.Should().Be(40);
        AbiLayout.StreamDuration.Should().Be(48);
        AbiLayout.StreamAvgFrameRate.Should().Be(88);
    }

    [Fact]
    public void TheCodecOffsetsAreTheOnesTheHeadersGive()
    {
        using var _ = new AssertionScope();

        AbiLayout.CodecParametersCodecType.Should().Be(0);
        AbiLayout.CodecParametersCodecId.Should().Be(4);
        AbiLayout.CodecType.Should().Be(16);
        AbiLayout.CodecId.Should().Be(20);
        AbiLayout.CodecContextCodecType.Should().Be(12);
    }

    [Fact]
    public void TheStreamFieldsThatFollowOneAnotherAreSpacedAsTheirTypesRequire()
    {
        using var _ = new AssertionScope();

        // time_base is an AVRational, then two int64 in a row.
        (AbiLayout.StreamStartTime - AbiLayout.StreamTimeBase).Should().Be(8);
        (AbiLayout.StreamDuration - AbiLayout.StreamStartTime).Should().Be(8);
    }

    [Fact]
    public void EveryOffsetTheSeriesDecidesIsListedForEverySeries()
    {
        // A series that forgot one would leave the layout pointing at whatever the
        // previous load left behind, which is the one case worse than a wrong
        // constant.
        using var _ = new AssertionScope();

        foreach (var generation in FFmpegGeneration.Known)
        {
            generation.CodecContextTimeBase.Should().BePositive($"{generation} states a time_base offset");
            generation.CodecContextPixFmt.Should().BePositive($"{generation} states a pix_fmt offset");
        }
    }
}
