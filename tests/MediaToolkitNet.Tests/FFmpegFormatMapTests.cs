using FluentAssertions;
using FluentAssertions.Execution;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.FFmpeg;
using Xunit;

namespace MediaToolkitNet.Tests;

/// <summary>
/// The names and strings handed to FFmpeg. Nothing here loads a library.
/// </summary>
public class FFmpegFormatMapTests
{
    [Theory]
    [InlineData(MediaCodec.Aac, "aac", "libfdk_aac")]
    [InlineData(MediaCodec.Ac3, "ac3", "ac3_fixed")]
    [InlineData(MediaCodec.Dts, "dca")]
    [InlineData(MediaCodec.Flac, "flac")]
    [InlineData(MediaCodec.Opus, "libopus", "opus")]
    [InlineData(MediaCodec.Pcm, "pcm_s16le")]
    [InlineData(MediaCodec.PcmU8, "pcm_u8")]
    [InlineData(MediaCodec.PcmS16, "pcm_s16le")]
    [InlineData(MediaCodec.PcmS24, "pcm_s24le")]
    [InlineData(MediaCodec.PcmS32, "pcm_s32le")]
    [InlineData(MediaCodec.Mjpeg, "mjpeg")]
    [InlineData(MediaCodec.TrueHd, "truehd")]
    [InlineData(MediaCodec.Mp2, "mp2", "libtwolame", "mp2fixed")]
    [InlineData(MediaCodec.Mp3, "libmp3lame", "libshine", "mp3_mf")]
    [InlineData(MediaCodec.Vorbis, "libvorbis", "vorbis")]
    [InlineData(MediaCodec.RealAudio, "real_144")]
    public void EachCodecNamesTheEncodersThatCanWriteIt(MediaCodec codec, params string[] expected) =>
        FFmpegFormatMap.EncoderNames(codec).Should().Equal(expected);

    [Theory]
    [InlineData("dca")]
    [InlineData("truehd")]
    [InlineData("mlp")]
    [InlineData("opus")]
    [InlineData("vorbis")]
    public void TheEncodersFFmpegMarksExperimentalAreOpenedAsSuch(string encoder) =>
        FFmpegFormatMap.IsExperimental(encoder).Should().BeTrue();

    [Theory]
    [InlineData("libopus")]
    [InlineData("libvorbis")]
    [InlineData("aac")]
    [InlineData("ac3")]
    [InlineData("flac")]
    [InlineData("libmp3lame")]
    [InlineData("real_144")]
    public void TheOnesItDoesNotAreNot(string encoder) =>
        FFmpegFormatMap.IsExperimental(encoder).Should().BeFalse();

    [Fact]
    public void AWrapperIsNotExperimentalJustBecauseTheNativeEncoderIs()
    {
        // FFmpeg ships both, and only the native one is marked.
        FFmpegFormatMap.IsExperimental("libopus").Should().NotBe(FFmpegFormatMap.IsExperimental("opus"));
        FFmpegFormatMap.IsExperimental("libvorbis").Should().NotBe(FFmpegFormatMap.IsExperimental("vorbis"));
    }

    [Fact]
    public void TheFirstEncoderNamedForACodecIsTheOneToPrefer()
    {
        using var _ = new AssertionScope();

        // The wrappers come first where FFmpeg has both, because the native
        // encoders of those two are experimental and worse.
        FFmpegFormatMap.EncoderNames(MediaCodec.Vorbis)[0].Should().Be("libvorbis");
        FFmpegFormatMap.EncoderNames(MediaCodec.Opus)[0].Should().Be("libopus");
        FFmpegFormatMap.EncoderNames(MediaCodec.Mp3)[0].Should().Be("libmp3lame");
    }

    [Fact]
    public void TheDefaultCodecNamesNoEncoderOfItsOwn() =>
        FFmpegFormatMap.EncoderNames(MediaCodec.Default).Should().BeEmpty();

    [Fact]
    public void EveryCodecOtherThanTheDefaultNamesAtLeastOneEncoder()
    {
        foreach (var codec in Enum.GetValues<MediaCodec>())
        {
            if (codec == MediaCodec.Default)
            {
                continue;
            }

            FFmpegFormatMap.EncoderNames(codec).Should().NotBeEmpty($"{codec} has to be encodable by some name");
        }
    }

    [Theory]
    [InlineData("a.mkv")]
    [InlineData("a.mka")]
    [InlineData("a.mks")]
    [InlineData("a.webm")]
    [InlineData("a.mp4")]
    [InlineData("a.m4a")]
    [InlineData("a.m4v")]
    [InlineData("a.mov")]
    [InlineData("A.MKA")]
    [InlineData(@"C:\a folder\surround.mka")]
    public void ContainersThatCarryTheirExtradataInTheHeaderAskForAGlobalOne(string path) =>
        FFmpegFormatMap.NeedsGlobalHeader(path).Should().BeTrue();

    [Theory]
    [InlineData("a.wav")]
    [InlineData("a.ac3")]
    [InlineData("a.ts")]
    [InlineData("a.flac")]
    [InlineData("a")]
    public void ContainersThatDoNotAreLeftAlone(string path) =>
        FFmpegFormatMap.NeedsGlobalHeader(path).Should().BeFalse();

    [Fact]
    public void MatroskaAudioIsTreatedLikeMatroskaVideo() =>
        FFmpegFormatMap.NeedsGlobalHeader("a.mka")
            .Should().Be(FFmpegFormatMap.NeedsGlobalHeader("a.mkv"), "both are the same muxer");

    [Theory]
    [InlineData(6, ChannelLayout.FivePoint1Back, "0x3f")]
    [InlineData(6, ChannelLayout.FivePoint1Side, "0x60f")]
    [InlineData(4, ChannelLayout.Quad, "0x33")]
    [InlineData(2, ChannelLayout.Stereo, "0x3")]
    public void AStatedLayoutTravelsAsAHexadecimalMask(int channels, ulong mask, string expected) =>
        FFmpegFormatMap.ChannelLayoutDescription(channels, mask).Should().Be(expected);

    [Theory]
    [InlineData(1, "1c")]
    [InlineData(6, "6c")]
    [InlineData(16, "16c")]
    public void AnUnstatedLayoutTravelsAsAChannelCount(int channels, string expected) =>
        FFmpegFormatMap.ChannelLayoutDescription(channels, ChannelLayout.Unspecified).Should().Be(expected);

    [Fact]
    public void TheDescriptionIsNeverTheAmbiguousName()
    {
        // "5.1" means the back arrangement to FFmpeg's name table and the side one
        // to av_channel_layout_default. A mask says which without asking.
        FFmpegFormatMap.ChannelLayoutDescription(6, ChannelLayout.FivePoint1Side)
            .Should().NotBe(FFmpegFormatMap.ChannelLayoutDescription(6, ChannelLayout.FivePoint1Back));
    }

    [Theory]
    [InlineData(PixelFormat.Yuv420P)]
    [InlineData(PixelFormat.Nv12)]
    [InlineData(PixelFormat.Rgb24)]
    [InlineData(PixelFormat.Bgra32)]
    public void PixelFormatsSurviveTheRoundTrip(PixelFormat format) =>
        FFmpegFormatMap.FromAV(FFmpegFormatMap.ToAV(format)).Should().Be(format);

    [Theory]
    [InlineData(SampleFormat.U8)]
    [InlineData(SampleFormat.S16)]
    [InlineData(SampleFormat.S32)]
    [InlineData(SampleFormat.F32)]
    [InlineData(SampleFormat.F64)]
    [InlineData(SampleFormat.S16Planar)]
    [InlineData(SampleFormat.S32Planar)]
    [InlineData(SampleFormat.F32Planar)]
    public void SampleFormatsSurviveTheRoundTrip(SampleFormat format) =>
        FFmpegFormatMap.FromAV(FFmpegFormatMap.ToAV(format)).Should().Be(format);
}
