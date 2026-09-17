using FluentAssertions;
using FluentAssertions.Execution;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.FFmpeg.Native;
using Xunit;

namespace MediaToolkitNet.Tests;

/// <summary>
/// What a stream can ask its encoder for.
/// </summary>
public class EncodingSettingsTests
{
    [Fact]
    public void AnAudioStreamAsksForNothingUnusualByDefault()
    {
        var settings = new AudioEncodingSettings(AudioFormat.Cd48Stereo);

        using var _ = new AssertionScope();

        settings.Codec.Should().Be(MediaCodec.Aac);
        settings.BitrateBitsPerSecond.Should().Be(0);
        settings.Quality.Should().BeNull("a stream without a quality is a constant bitrate one");
        settings.EncoderName.Should().BeNull();
        settings.Options.Should().BeNull();
    }

    [Fact]
    public void AnAudioStreamCanNameItsEncoderAndItsSettings()
    {
        var settings = new AudioEncodingSettings(AudioFormat.Cd48Stereo, MediaCodec.Dts)
        {
            EncoderName = "dca",
            Quality = 1.2,
            Options = new Dictionary<string, string> { ["strict"] = "-2" },
        };

        using var _ = new AssertionScope();

        settings.EncoderName.Should().Be("dca");
        settings.Quality.Should().Be(1.2);
        settings.Options!["strict"].Should().Be("-2");
    }

    [Fact]
    public void AVideoStreamCanNameItsEncoderAndItsSettings()
    {
        var format = new VideoFormat(1920, 1080, PixelFormat.Yuv420P, 25);
        var settings = new VideoEncodingSettings(format, MediaCodec.H264, 4_000_000, 50)
        {
            EncoderName = "libx264rgb",
            Options = new Dictionary<string, string> { ["preset"] = "veryfast", ["crf"] = "23" },
        };

        using var _ = new AssertionScope();

        settings.KeyFrameInterval.Should().Be(50);
        settings.EncoderName.Should().Be("libx264rgb");
        settings.Options.Should().HaveCount(2);
    }

    [Fact]
    public void TheFormatCanBeReplacedWithoutLosingTheRest()
    {
        var settings = new AudioEncodingSettings(AudioFormat.Cd48Stereo, MediaCodec.Ac3, 448_000)
        {
            EncoderName = "ac3_fixed",
        };

        var moved = settings with { Format = new AudioFormat(44100, 2, SampleFormat.S16) };

        using var _ = new AssertionScope();

        moved.EncoderName.Should().Be("ac3_fixed");
        moved.BitrateBitsPerSecond.Should().Be(448_000);
        moved.Format.SampleRate.Should().Be(44100);
    }

    [Theory]
    [InlineData(1.0, 118)]
    [InlineData(1.2, 142)]
    [InlineData(2.0, 236)]
    [InlineData(0.4, 47)]
    public void AQualityBecomesTheGlobalQualityFFmpegExpects(double quality, int expected) =>
        ((int)Math.Round(quality * AVConstants.QualityToLambda)).Should().Be(expected);

    [Fact]
    public void TheEncoderFlagsAreTheOnesFFmpegDefines()
    {
        using var _ = new AssertionScope();

        AVConstants.CodecFlagGlobalHeader.Should().Be(0x400000);
        AVConstants.CodecFlagQScale.Should().Be(0x2);
        AVConstants.QualityToLambda.Should().Be(118, "FF_QP2LAMBDA");
        AVConstants.ComplianceExperimental.Should().Be(-2, "FF_COMPLIANCE_EXPERIMENTAL");
    }

    [Fact]
    public void AskingForAQualityAndARateAtOnceKeepsBoth()
    {
        // The recorder prefers the quality; the record does not silently drop the
        // rate, so a caller that set both can still see what they asked for.
        var settings = new AudioEncodingSettings(AudioFormat.Cd48Stereo, MediaCodec.Aac, 128_000) { Quality = 2 };

        settings.BitrateBitsPerSecond.Should().Be(128_000);
        settings.Quality.Should().Be(2);
    }
}
