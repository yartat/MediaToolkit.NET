using FluentAssertions;
using FluentAssertions.Execution;
using MediaToolkitNet.Abstractions.Formats;
using Xunit;

namespace MediaToolkitNet.Tests;

/// <summary>
/// What an audio format states, and what it leaves to the backend.
/// </summary>
public class AudioFormatTests
{
    [Fact]
    public void AFormatThatStatesNoLayoutFallsBackToTheConventionalOne()
    {
        var format = new AudioFormat(48000, 6, SampleFormat.S16);

        using var _ = new AssertionScope();

        format.ChannelMask.Should().Be(ChannelLayout.Unspecified);
        format.EffectiveChannelMask.Should().Be(ChannelLayout.FivePoint1Side);
    }

    [Fact]
    public void AFormatThatStatesALayoutKeepsIt()
    {
        var format = new AudioFormat(48000, 6, SampleFormat.S16, ChannelLayout.FivePoint1Back);

        format.EffectiveChannelMask.Should().Be(ChannelLayout.FivePoint1Back);
    }

    [Fact]
    public void TheLayoutSurvivesAWithExpression()
    {
        var format = new AudioFormat(48000, 4, SampleFormat.S16, ChannelLayout.Quad);

        var converted = format with { SampleFormat = SampleFormat.F32Planar };

        using var _ = new AssertionScope();

        converted.EffectiveChannelMask.Should().Be(ChannelLayout.Quad);
        converted.SampleFormat.Should().Be(SampleFormat.F32Planar);
    }

    [Fact]
    public void TwoFormatsThatDifferOnlyInLayoutAreNotEqual()
    {
        var back = new AudioFormat(48000, 6, SampleFormat.S16, ChannelLayout.FivePoint1Back);
        var side = new AudioFormat(48000, 6, SampleFormat.S16, ChannelLayout.FivePoint1Side);

        back.Should().NotBe(side);
    }

    [Fact]
    public void TheCommonDefaultIsUnchanged()
    {
        var format = AudioFormat.Cd48Stereo;

        using var _ = new AssertionScope();

        format.SampleRate.Should().Be(48000);
        format.Channels.Should().Be(2);
        format.SampleFormat.Should().Be(SampleFormat.S16);
        format.EffectiveChannelMask.Should().Be(ChannelLayout.Stereo);
    }

    [Theory]
    [InlineData(SampleFormat.U8, 1)]
    [InlineData(SampleFormat.S16, 2)]
    [InlineData(SampleFormat.S32, 4)]
    [InlineData(SampleFormat.F32, 4)]
    [InlineData(SampleFormat.F64, 8)]
    public void BlockAlignCountsEverySampleOfEveryChannel(SampleFormat sampleFormat, int bytesPerSample)
    {
        var format = new AudioFormat(48000, 6, sampleFormat);

        format.BlockAlign.Should().Be(bytesPerSample * 6);
    }

    [Fact]
    public void AnUnspecifiedLayoutStaysUnspecifiedForASizeWithNoConvention()
    {
        var format = new AudioFormat(48000, 12, SampleFormat.S16);

        format.EffectiveChannelMask.Should().Be(ChannelLayout.Unspecified);
    }
}
