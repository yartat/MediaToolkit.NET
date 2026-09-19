using FluentAssertions;
using FluentAssertions.Execution;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.FFmpeg.Native;
using Xunit;
using Xunit.Abstractions;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// Encodes with every codec the recorder names and reads the result back.
/// </summary>
/// <remarks>
/// Reading back through this library's own demuxer rather than an outside tool
/// keeps the test to one dependency, and puts the AVStream offsets under load
/// from both directions at once.
/// </remarks>
public unsafe class RecordingTests(ITestOutputHelper output)
{
    [FFmpegTheory]
    [InlineData(MediaCodec.Ac3, 6, 48000, ChannelLayout.FivePoint1Back, 448_000, "ac3")]
    [InlineData(MediaCodec.Ac3, 4, 48000, ChannelLayout.Quad, 192_000, "ac3")]
    [InlineData(MediaCodec.Ac3, 1, 48000, ChannelLayout.Mono, 96_000, "ac3")]
    [InlineData(MediaCodec.Dts, 6, 48000, ChannelLayout.FivePoint1Side, 768_000, "dts")]
    [InlineData(MediaCodec.Aac, 2, 44100, ChannelLayout.Stereo, 128_000, "aac")]
    [InlineData(MediaCodec.PcmU8, 1, 8000, ChannelLayout.Mono, 0, "pcm_u8")]
    [InlineData(MediaCodec.PcmS16, 2, 44100, ChannelLayout.Stereo, 0, "pcm_s16le")]
    [InlineData(MediaCodec.PcmS24, 8, 96000, ChannelLayout.SevenPoint1, 0, "pcm_s24le")]
    [InlineData(MediaCodec.PcmS32, 2, 48000, ChannelLayout.Stereo, 0, "pcm_s32le")]
    [InlineData(MediaCodec.Mp2, 2, 48000, ChannelLayout.Stereo, 192_000, "mp2")]
    [InlineData(MediaCodec.Mp3, 2, 44100, ChannelLayout.Stereo, 192_000, "mp3")]
    [InlineData(MediaCodec.Mp3, 1, 22050, ChannelLayout.Mono, 64_000, "mp3")]
    [InlineData(MediaCodec.Opus, 6, 48000, ChannelLayout.FivePoint1Back, 256_000, "opus")]
    [InlineData(MediaCodec.Flac, 8, 96000, ChannelLayout.SevenPoint1, 0, "flac")]
    [InlineData(MediaCodec.TrueHd, 6, 48000, ChannelLayout.FivePoint1Side, 0, "truehd")]
    [InlineData(MediaCodec.RealAudio, 1, 8000, ChannelLayout.Mono, 0, "ra_144")]
    public void EveryAudioCodecWritesAMatroskaFileThatReadsBackAsItself(
        MediaCodec codec,
        int channels,
        int sampleRate,
        ulong layout,
        int bitrate,
        string expectedName)
    {
        using var media = new TestMedia();
        var format = new AudioFormat(sampleRate, channels, SampleFormat.S16, layout);

        var path = media.WriteAudio(
            $"{codec}.mka".ToLowerInvariant(),
            format,
            new AudioEncodingSettings(default, codec, bitrate));

        using var demuxer = FFmpegDemuxer.Open(path);
        var stream = demuxer.Streams.Should().ContainSingle().Subject;

        output.WriteLine($"{codec}: {stream.CodecName}, {new FileInfo(path).Length:N0} bytes");

        using var _ = new AssertionScope();

        stream.MediaType.Should().Be(AVMediaType.Audio);
        stream.CodecName.Should().Be(expectedName);
        new FileInfo(path).Length.Should().BeGreaterThan(1024, "a second of audio is not empty");
    }

    [FFmpegFact]
    public void TheExperimentalDtsEncoderIsOpenedAsExperimental()
    {
        // Nothing in the caller's code says "strict -2"; asking for DTS is enough.
        // The rate is one the DCA encoder accepts for a pair of channels: it has a
        // table of its own and refuses anything below 384 kbps for stereo.
        using var media = new TestMedia();
        var format = new AudioFormat(48000, 2, SampleFormat.S16, ChannelLayout.Stereo);

        var write = () => media.WriteAudio(
            "dts.mka", format, new AudioEncodingSettings(default, MediaCodec.Dts, 384_000));

        write.Should().NotThrow();
    }

    [FFmpegFact]
    public void AnEncoderCanBeNamedOutrightWithItsOwnSettings()
    {
        using var media = new TestMedia();
        var format = new AudioFormat(48000, 2, SampleFormat.S16, ChannelLayout.Stereo);

        var path = media.WriteAudio(
            "named.mka",
            format,
            new AudioEncodingSettings(default, MediaCodec.Default, 192_000)
            {
                EncoderName = "ac3_fixed",
                Options = new Dictionary<string, string> { ["center_mixlev"] = "0.594" },
            });

        using var demuxer = FFmpegDemuxer.Open(path);

        demuxer.Streams.Should().ContainSingle().Which.CodecName.Should().Be("ac3");
    }

    [FFmpegFact]
    public void AnEncoderThatThisBuildDoesNotCarryIsNamedInTheFailure()
    {
        using var media = new TestMedia();
        var format = new AudioFormat(48000, 2, SampleFormat.S16, ChannelLayout.Stereo);

        var write = () => media.WriteAudio(
            "missing.mka", format, new AudioEncodingSettings(default) { EncoderName = "no_such_encoder" });

        write.Should().Throw<MediaToolkitNetException>().WithMessage("*no_such_encoder*");
    }

    [FFmpegFact]
    public void AVariableBitrateStreamEncodesAndDiffersFromAFixedOne()
    {
        using var media = new TestMedia();
        var format = new AudioFormat(44100, 2, SampleFormat.S16, ChannelLayout.Stereo);

        var fixedRate = media.WriteAudio(
            "cbr.mka", format, new AudioEncodingSettings(default, MediaCodec.Aac, 96_000));
        var variable = media.WriteAudio(
            "vbr.mka", format, new AudioEncodingSettings(default, MediaCodec.Aac) { Quality = 2.0 });

        var fixedSize = new FileInfo(fixedRate).Length;
        var variableSize = new FileInfo(variable).Length;
        output.WriteLine($"constant {fixedSize:N0} bytes, variable {variableSize:N0} bytes");

        using var _ = new AssertionScope();

        variableSize.Should().BeGreaterThan(0);
        variableSize.Should().NotBe(fixedSize, "a quality and a rate do not produce the same bitstream");
    }

    [FFmpegFact]
    public void ALayoutThatNamesTheWrongNumberOfSpeakersIsRefusedBeforeAnythingIsEncoded()
    {
        using var media = new TestMedia();

        // Six channels described by a two speaker mask. Left to FFmpeg this
        // surfaces from inside swresample, several layers from the mistake.
        var format = new AudioFormat(48000, 6, SampleFormat.S16, ChannelLayout.Stereo);

        var write = () => media.WriteAudio(
            "mismatch.mka", format, new AudioEncodingSettings(default, MediaCodec.Ac3, 448_000));

        write.Should().Throw<ArgumentException>().WithMessage("*2 speakers*6 channels*");
    }

    [FFmpegFact]
    public void ALayoutTheEncoderDoesNotSupportIsReportedWithTheLayout()
    {
        using var media = new TestMedia();

        // The DTS encoder takes the side arrangement of five point one and not the
        // back one. Both are six channels, so only the mask tells them apart.
        var format = new AudioFormat(48000, 6, SampleFormat.S16, ChannelLayout.FivePoint1Back);

        var write = () => media.WriteAudio(
            "unsupported.mka", format, new AudioEncodingSettings(default, MediaCodec.Dts, 768_000));

        write.Should().Throw<MediaToolkitNetException>().WithMessage("*0x3f*");
    }

    [FFmpegFact]
    public void AVideoStreamWritesAndReadsBackAtTheRateItWasGiven()
    {
        using var media = new TestMedia();
        var path = media.WriteVideo("video.mkv");

        using var demuxer = FFmpegDemuxer.Open(path);
        var stream = demuxer.Streams.Should().ContainSingle().Subject;

        using var _ = new AssertionScope();

        stream.MediaType.Should().Be(AVMediaType.Video);
        stream.CodecName.Should().Be("mjpeg");
        stream.AverageFrameRate.Numerator.Should().Be(25);
        stream.AverageFrameRate.Denominator.Should().Be(1);
    }

    [FFmpegTheory]
    [InlineData(SampleFormat.S16, AVSampleFormat.S16)]
    [InlineData(SampleFormat.S32, AVSampleFormat.S32)]
    public unsafe void AFlacStreamIsAsWideAsThePinnedFormat(SampleFormat pinned, AVSampleFormat expected)
    {
        // A lossless encoder takes its width from the format it was opened with —
        // FLAC writes 24 bits out of s32 and 16 out of s16 — so pinning the format
        // is how a caller asks for one rather than the other.
        using var media = new TestMedia();
        var format = new AudioFormat(48000, 2, SampleFormat.S16, ChannelLayout.Stereo);

        var path = media.WriteAudio(
            $"flac-{pinned}.mka".ToLowerInvariant(),
            format,
            new AudioEncodingSettings(default, MediaCodec.Flac) { SampleFormat = pinned });

        using var demuxer = FFmpegDemuxer.Open(path);
        var parameters = demuxer.CodecParameters(0);
        var written = (AVSampleFormat)(*(int*)((byte*)parameters + AbiLayout.CodecParametersFormat));

        output.WriteLine($"FLAC pinned to {pinned} came back as {written}");
        written.Should().Be(expected);
    }

    [FFmpegTheory]
    [InlineData(SampleFormat.S16Planar)]
    [InlineData(SampleFormat.S32Planar)]
    public void TrueHdTakesEitherWidthItSupports(SampleFormat pinned)
    {
        // Unlike FLAC, the width TrueHD was encoded at is not visible in the
        // container: its decoder always reports s32. What is checked here is that
        // the encoder opens at both of the formats it accepts and writes a file.
        using var media = new TestMedia();
        var format = new AudioFormat(48000, 2, SampleFormat.S16, ChannelLayout.Stereo);

        var path = media.WriteAudio(
            $"truehd-{pinned}.mka".ToLowerInvariant(),
            format,
            new AudioEncodingSettings(default, MediaCodec.TrueHd) { SampleFormat = pinned });

        using var demuxer = FFmpegDemuxer.Open(path);

        using var _ = new AssertionScope();

        demuxer.Streams.Should().ContainSingle().Which.CodecName.Should().Be("truehd");
        new FileInfo(path).Length.Should().BeGreaterThan(1024);
    }

    [FFmpegFact]
    public void AnEncoderThatDoesNotTakeThePinnedFormatSaysSo()
    {
        using var media = new TestMedia();
        var format = new AudioFormat(48000, 2, SampleFormat.S16, ChannelLayout.Stereo);

        // pcm_u8 takes u8 and nothing else.
        var write = () => media.WriteAudio(
            "pinned.mka",
            format,
            new AudioEncodingSettings(default, MediaCodec.PcmU8) { SampleFormat = SampleFormat.S32 });

        write.Should().Throw<MediaToolkitNetException>().WithMessage("*S32*");
    }

    [FFmpegFact]
    public void OneSourceFormatReachesEveryEncoderWhateverItAccepts()
    {
        // The caller pushes the same 16-bit samples throughout, and each of these
        // encoders takes something different: u8 for pcm_u8, s32 for pcm_s24le and
        // pcm_s32le, s32p for ac3_fixed, planar float for aac.
        using var media = new TestMedia();
        var format = new AudioFormat(48000, 2, SampleFormat.S16, ChannelLayout.Stereo);

        using var _ = new AssertionScope();

        foreach (var name in (string[])["pcm_u8", "pcm_s24le", "pcm_s32le", "ac3_fixed", "aac"])
        {
            var write = () => media.WriteAudio(
                $"{name}.mka",
                format,
                new AudioEncodingSettings(default, MediaCodec.Default, 256_000) { EncoderName = name });

            write.Should().NotThrow($"the resampler can produce whatever {name} takes");
        }
    }
}
