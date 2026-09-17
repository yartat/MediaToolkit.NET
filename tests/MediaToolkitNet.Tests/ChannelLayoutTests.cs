using FluentAssertions;
using FluentAssertions.Execution;
using MediaToolkitNet.Abstractions.Formats;
using Xunit;

namespace MediaToolkitNet.Tests;

/// <summary>
/// The speaker masks, which have to be the ones libavutil uses down to the bit.
/// </summary>
/// <remarks>
/// These are the values <c>libavutil/channel_layout.h</c> defines. A wrong one
/// does not fail loudly: the encoder accepts it and writes a file whose speakers
/// are in the wrong places, which is exactly what happened while four channels
/// were described as 3.1.
/// </remarks>
public class ChannelLayoutTests
{
    [Theory]
    [InlineData(ChannelLayout.FrontLeft, 0x1UL)]
    [InlineData(ChannelLayout.FrontRight, 0x2UL)]
    [InlineData(ChannelLayout.FrontCenter, 0x4UL)]
    [InlineData(ChannelLayout.LowFrequency, 0x8UL)]
    [InlineData(ChannelLayout.BackLeft, 0x10UL)]
    [InlineData(ChannelLayout.BackRight, 0x20UL)]
    [InlineData(ChannelLayout.FrontLeftOfCenter, 0x40UL)]
    [InlineData(ChannelLayout.FrontRightOfCenter, 0x80UL)]
    [InlineData(ChannelLayout.BackCenter, 0x100UL)]
    [InlineData(ChannelLayout.SideLeft, 0x200UL)]
    [InlineData(ChannelLayout.SideRight, 0x400UL)]
    public void SpeakerBitsAreTheOnesLibavutilDefines(ulong actual, ulong expected) =>
        actual.Should().Be(expected);

    [Theory]
    [InlineData(ChannelLayout.Mono, 0x4UL)]
    [InlineData(ChannelLayout.Stereo, 0x3UL)]
    [InlineData(ChannelLayout.TwoPoint1, 0xBUL)]
    [InlineData(ChannelLayout.Surround, 0x7UL)]
    [InlineData(ChannelLayout.ThreePoint1, 0xFUL)]
    [InlineData(ChannelLayout.Quad, 0x33UL)]
    [InlineData(ChannelLayout.QuadSide, 0x603UL)]
    [InlineData(ChannelLayout.FourPoint0, 0x107UL)]
    [InlineData(ChannelLayout.FivePoint0Back, 0x37UL)]
    [InlineData(ChannelLayout.FivePoint0Side, 0x607UL)]
    [InlineData(ChannelLayout.FivePoint1Back, 0x3FUL)]
    [InlineData(ChannelLayout.FivePoint1Side, 0x60FUL)]
    [InlineData(ChannelLayout.SixPoint1, 0x70FUL)]
    [InlineData(ChannelLayout.SevenPoint1, 0x63FUL)]
    public void LayoutsAreTheOnesLibavutilDefines(ulong actual, ulong expected) =>
        actual.Should().Be(expected);

    [Theory]
    [InlineData(1, 0x4UL)]
    [InlineData(2, 0x3UL)]
    [InlineData(3, 0x7UL)]
    [InlineData(4, 0x33UL)]
    [InlineData(5, 0x607UL)]
    [InlineData(6, 0x60FUL)]
    [InlineData(7, 0x70FUL)]
    [InlineData(8, 0x63FUL)]
    public void DefaultMatchesTheTableInAvChannelLayoutDefault(int channels, ulong expected) =>
        ChannelLayout.Default(channels).Should().Be(expected);

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(-1)]
    public void DefaultStatesNothingForASizeWithNoConventionalLayout(int channels) =>
        ChannelLayout.Default(channels).Should().Be(ChannelLayout.Unspecified);

    [Fact]
    public void FourChannelsAreQuadAndNotThreePointOne()
    {
        // (1 << 4) - 1 is 0xF, which names a centre speaker and a subwoofer where
        // quad has a back pair. Both are four channels and they are not the same.
        using var _ = new AssertionScope();

        ChannelLayout.Default(4).Should().NotBe(0xFUL);
        ChannelLayout.Default(4).Should().Be(ChannelLayout.Quad);
        ChannelLayout.ThreePoint1.Should().Be(0xFUL);
    }

    [Fact]
    public void EightChannelsAreSevenPointOneAndNotTheFirstEightBits()
    {
        ChannelLayout.Default(8).Should().NotBe(0xFFUL);
        ChannelLayout.Default(8).Should().Be(ChannelLayout.SevenPoint1);
    }

    [Fact]
    public void TheTwoFivePointOneArrangementsAreDifferentAndBothSixChannels()
    {
        using var _ = new AssertionScope();

        ChannelLayout.FivePoint1Back.Should().NotBe(ChannelLayout.FivePoint1Side);
        System.Numerics.BitOperations.PopCount(ChannelLayout.FivePoint1Back).Should().Be(6);
        System.Numerics.BitOperations.PopCount(ChannelLayout.FivePoint1Side).Should().Be(6);
        ChannelLayout.Default(6).Should().Be(
            ChannelLayout.FivePoint1Side, "that is what av_channel_layout_default gives for six");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void EveryDefaultLayoutPlacesExactlyAsManySpeakersAsItHasChannels(int channels) =>
        System.Numerics.BitOperations.PopCount(ChannelLayout.Default(channels)).Should().Be(channels);
}
