#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using FluentAssertions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using Xunit;

namespace MediaToolkitNet.Tests;

/// <summary>
/// How a request is resolved against what its inputs contain. None of this
/// depends on a backend, which is the point: every transcoder starts from the
/// same plan.
/// </summary>
public class TranscodePlanTests
{
    // Shaped like the integration fixture: H.264, two AAC tracks, two SRT tracks.
    private static readonly MediaInfo Movie = new(
        "movie.mkv",
        "matroska,webm",
        TimeSpan.FromSeconds(5),
        [
            new MediaStreamInfo(0, MediaStreamKind.Video, "h264") { Codec = MediaCodec.H264 },
            new MediaStreamInfo(1, MediaStreamKind.Audio, "aac") { Language = "eng", Title = "Main", IsDefault = true },
            new MediaStreamInfo(2, MediaStreamKind.Audio, "aac") { Language = "rus" },
            new MediaStreamInfo(3, MediaStreamKind.Subtitle, "subrip") { Language = "eng", IsTextSubtitle = true },
            new MediaStreamInfo(4, MediaStreamKind.Subtitle, "subrip") { Language = "rus", IsForced = true, IsTextSubtitle = true },
        ]);

    private static readonly MediaInfo WithCoverAndBitmaps = new(
        "album.mkv",
        "matroska,webm",
        TimeSpan.FromSeconds(5),
        [
            new MediaStreamInfo(0, MediaStreamKind.Video, "mjpeg") { IsAttachedPicture = true },
            new MediaStreamInfo(1, MediaStreamKind.Video, "h264"),
            new MediaStreamInfo(2, MediaStreamKind.Subtitle, "hdmv_pgs_subtitle"),
            new MediaStreamInfo(3, MediaStreamKind.Attachment, "ttf"),
        ]);

    private static readonly MediaInfo ExternalSrt = new(
        "movie.en.srt", "srt", TimeSpan.FromSeconds(5),
        [new MediaStreamInfo(0, MediaStreamKind.Subtitle, "subrip") { IsTextSubtitle = true }]);

    private static TranscodePlan Plan(TranscodeRequest request, params MediaInfo[] inputs) =>
        TranscodePlan.Resolve(request, inputs);

    private static TranscodeRequest Request(params OutputStream[] streams) =>
        new("out.mkv") { Inputs = ["movie.mkv"], Streams = streams };

    [Fact]
    public void ARequestNamingNoStreamsCopiesEveryStreamInOrder()
    {
        var plan = Plan(TranscodeRequest.Remux("movie.mkv", "out.mp4"), Movie);

        plan.HasErrors.Should().BeFalse();
        plan.Streams.Select(s => s.Source.Index).Should().Equal(0, 1, 2, 3, 4);
        plan.Streams.Should().OnlyContain(s => s.Action == PlannedAction.Copy);
        plan.Streams.Select(s => s.OutputIndex).Should().Equal(0, 1, 2, 3, 4);
    }

    [Fact]
    public void ARemuxSaysWhichStreamsItLeavesBehind()
    {
        var plan = Plan(TranscodeRequest.Remux("album.mkv", "out.mkv") with { Inputs = ["album.mkv"] }, WithCoverAndBitmaps);

        plan.HasErrors.Should().BeFalse();
        plan.Issues.Should().ContainSingle(i => i.Severity == TranscodeIssueSeverity.Warning && i.Message.Contains("ttf"));
        plan.Streams.Select(s => s.Source.Index).Should().Equal(0, 1, 2);
    }

    [Fact]
    public void TheFirstVideoIsThePictureNotTheCoverArt()
    {
        var plan = Plan(
            new TranscodeRequest("out.mkv") { Inputs = ["album.mkv"], Streams = [OutputStream.Copy(StreamSource.First(MediaStreamKind.Video))] },
            WithCoverAndBitmaps);

        plan.Streams.Should().ContainSingle().Which.Source.Index.Should().Be(1);
    }

    [Fact]
    public void NthCountsWithinItsKind()
    {
        var plan = Plan(Request(OutputStream.Copy(StreamSource.Nth(MediaStreamKind.Audio, 1))), Movie);

        plan.Streams.Should().ContainSingle().Which.Source.Language.Should().Be("rus");
    }

    [Fact]
    public void ALanguageIsMatchedWithoutRegardToCase()
    {
        var plan = Plan(Request(OutputStream.Copy(StreamSource.First(MediaStreamKind.Subtitle, "RUS"))), Movie);

        plan.Streams.Should().ContainSingle().Which.Source.Index.Should().Be(4);
    }

    [Fact]
    public void DropTakesStreamsOutOfAnAllSelector()
    {
        var plan = Plan(
            Request(
                OutputStream.Copy(StreamSource.All(MediaStreamKind.Audio)),
                OutputStream.Drop(StreamSource.At(2))),
            Movie);

        plan.HasErrors.Should().BeFalse();
        plan.Streams.Should().ContainSingle().Which.Source.Index.Should().Be(1);
    }

    [Fact]
    public void AStreamBothDroppedAndNamedIsAnError()
    {
        var plan = Plan(Request(OutputStream.Copy(StreamSource.At(2)), OutputStream.Drop(StreamSource.At(2))), Movie);

        plan.HasErrors.Should().BeTrue();
        plan.Issues.Should().Contain(i => i.Message.Contains("both dropped and asked for"));
    }

    [Fact]
    public void OneInputStreamCanFeedSeveralOutputs()
    {
        var plan = Plan(
            Request(
                OutputStream.Copy(StreamSource.At(1)),
                OutputStream.Audio(StreamSource.At(1), new AudioOutputSettings(MediaCodec.Opus))),
            Movie);

        plan.Streams.Select(s => (s.Source.Index, s.Action)).Should().Equal(
            (1, PlannedAction.Copy),
            (1, PlannedAction.EncodeAudio));
    }

    [Fact]
    public void EncodingAudioSettingsOnAVideoStreamIsAnError()
    {
        var plan = Plan(Request(OutputStream.Audio(StreamSource.At(0), new AudioOutputSettings())), Movie);

        plan.HasErrors.Should().BeTrue();
        plan.Issues.Should().Contain(i => i.Message.Contains("audio encoding does not apply"));
    }

    [Fact]
    public void BitmapSubtitlesCannotBeConvertedToText()
    {
        var plan = Plan(
            new TranscodeRequest("out.mkv")
            {
                Inputs = ["album.mkv"],
                Streams = [OutputStream.Subtitles(StreamSource.First(MediaStreamKind.Subtitle), MediaCodec.SubRip)],
            },
            WithCoverAndBitmaps);

        plan.HasErrors.Should().BeTrue();
        plan.Issues.Should().Contain(i => i.Message.Contains("bitmap subtitles"));
    }

    [Fact]
    public void ConvertingToABitmapFormatIsRefusedWhenTheRequestIsBuilt()
    {
        var act = () => OutputStream.Subtitles(StreamSource.At(3), MediaCodec.Pgs);

        act.Should().Throw<ArgumentException>().WithMessage("*not a text subtitle format*");
    }

    [Fact]
    public void BurningIntoACopiedVideoIsAnError()
    {
        var plan = Plan(
            Request(
                OutputStream.Copy(StreamSource.First(MediaStreamKind.Video)),
                OutputStream.BurnIn(StreamSource.At(3), StreamSource.First(MediaStreamKind.Video))),
            Movie);

        plan.HasErrors.Should().BeTrue();
        plan.Issues.Should().Contain(i => i.Message.Contains("needs it re-encoded, but the request copies it"));
    }

    [Fact]
    public void BurningIntoAnEncodedVideoAttachesTheSubtitlesToIt()
    {
        var plan = Plan(
            Request(
                OutputStream.Video(StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings()),
                OutputStream.Copy(StreamSource.First(MediaStreamKind.Audio)),
                OutputStream.BurnIn(StreamSource.First(MediaStreamKind.Subtitle, "rus"), StreamSource.First(MediaStreamKind.Video))),
            Movie);

        plan.HasErrors.Should().BeFalse();
        plan.Streams.Should().HaveCount(2, "burning in adds no stream of its own");
        plan.Streams[0].BurnIn.Should().ContainSingle().Which.Subtitles.Index.Should().Be(4);
    }

    [Fact]
    public void SubtitlesCanComeFromAnotherInput()
    {
        var plan = Plan(
            new TranscodeRequest("out.mkv")
            {
                Inputs = ["movie.mkv", "movie.en.srt"],
                Streams =
                [
                    OutputStream.Copy(StreamSource.First(MediaStreamKind.Video)),
                    OutputStream.Copy(StreamSource.First(MediaStreamKind.Subtitle, input: 1)) with { Language = "eng" },
                ],
            },
            Movie,
            ExternalSrt);

        plan.HasErrors.Should().BeFalse();
        plan.Streams[1].Input.Should().Be(1);
        plan.Streams[1].Language.Should().Be("eng");
    }

    [Fact]
    public void ANamedStreamThatIsNotThereIsAnErrorButAnEmptyAllIsNot()
    {
        var missing = Plan(Request(OutputStream.Copy(StreamSource.At(9))), Movie);
        missing.HasErrors.Should().BeTrue();

        var empty = Plan(
            Request(OutputStream.Copy(StreamSource.First(MediaStreamKind.Video)), OutputStream.Copy(StreamSource.All(MediaStreamKind.Data))),
            Movie);
        empty.HasErrors.Should().BeFalse();
        empty.Issues.Should().ContainSingle(i => i.Severity == TranscodeIssueSeverity.Warning);
    }

    [Fact]
    public void AnInputThatIsNotInTheRequestIsAnError()
    {
        var plan = Plan(Request(OutputStream.Copy(StreamSource.At(0, input: 3))), Movie);

        plan.Issues.Should().Contain(i => i.Message.Contains("names input 3"));
    }

    [Theory]
    [InlineData(-1, null, "negative")]
    [InlineData(3, 2, "not after the start")]
    [InlineData(6, null, "past the end of the input")]
    public void ATrimThatMakesNoSenseIsAnError(int start, int? end, string message)
    {
        var plan = Plan(
            Request(OutputStream.Copy(StreamSource.At(0))) with
            {
                Start = TimeSpan.FromSeconds(start),
                End = end is { } e ? TimeSpan.FromSeconds(e) : null,
            },
            Movie);

        plan.Issues.Should().Contain(i => i.Severity == TranscodeIssueSeverity.Error && i.Message.Contains(message));
    }

    [Fact]
    public void TheExpectedDurationIsTheTrimmedLength()
    {
        var plan = Plan(Request(OutputStream.Copy(StreamSource.At(0))) with { Start = TimeSpan.FromSeconds(1) }, Movie);
        plan.ExpectedDuration.Should().Be(TimeSpan.FromSeconds(4));

        var bounded = Plan(
            Request(OutputStream.Copy(StreamSource.At(0))) with { Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(3) },
            Movie);
        bounded.ExpectedDuration.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void WritingOverAnInputIsAnError()
    {
        var plan = Plan(TranscodeRequest.Remux("movie.mkv", "movie.mkv"), Movie);

        plan.Issues.Should().Contain(i => i.Message.Contains("overwritten while it is read"));
    }

    [Fact]
    public void ARequestWithNoInputIsAnError() =>
        TranscodePlan.Resolve(new TranscodeRequest("out.mkv"), []).HasErrors.Should().BeTrue();

    [Fact]
    public void StreamTagsComeFromTheInputUnlessTheRequestReplacesThem()
    {
        var plan = Plan(
            Request(
                OutputStream.Copy(StreamSource.At(1)),
                OutputStream.Copy(StreamSource.At(2)) with { Title = "Russian dub", IsDefault = true }),
            Movie);

        plan.Streams[0].Title.Should().Be("Main");
        plan.Streams[0].IsDefault.Should().BeTrue();
        plan.Streams[1].Title.Should().Be("Russian dub");
        plan.Streams[1].Language.Should().Be("rus");
        plan.Streams[1].IsDefault.Should().BeTrue();
    }

    [Theory]
    [InlineData("a.mp4", MediaContainer.Mp4)]
    [InlineData("a.MKV", MediaContainer.Matroska)]
    [InlineData("a.mka", MediaContainer.Matroska)]
    [InlineData("a.webm", MediaContainer.WebM)]
    [InlineData("a.ts", MediaContainer.MpegTs)]
    [InlineData("a.opus", MediaContainer.Ogg)]
    public void AContainerIsNamedByItsExtension(string path, MediaContainer expected) =>
        MediaFormats.ContainerFromPath(path).Should().Be(expected);

    [Fact]
    public void AnUnknownExtensionNamesNoContainer() => MediaFormats.ContainerFromPath("a.xyz").Should().BeNull();

    [Theory]
    [InlineData("subrip", MediaCodec.SubRip)]
    [InlineData("mov_text", MediaCodec.MovText)]
    [InlineData("hdmv_pgs_subtitle", MediaCodec.Pgs)]
    [InlineData("hevc", MediaCodec.Hevc)]
    public void CodecsAreNamedTheWayFFmpegNamesThem(string name, MediaCodec expected) =>
        MediaFormats.CodecFromName(name).Should().Be(expected);

    [Fact]
    public void OnlyTextSubtitleFormatsCountAsText()
    {
        MediaCodec.SubRip.IsTextSubtitle().Should().BeTrue();
        MediaCodec.Pgs.IsTextSubtitle().Should().BeFalse();
        MediaCodec.Pgs.IsSubtitle().Should().BeTrue();
        MediaCodec.Aac.IsSubtitle().Should().BeFalse();
    }

    [Fact]
    public void ARotationThatIsNotAQuarterTurnIsRefused()
    {
        var act = () => new VideoFilter.Rotate(45);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ProgressReportsTheShareDoneAndTheSpeed()
    {
        var progress = new TranscodeProgress(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(5));

        progress.Fraction.Should().Be(0.25);
        progress.Speed.Should().Be(2);
        new TranscodeProgress(TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.Zero).Fraction.Should().Be(double.NaN);
    }

    [Fact]
    public void AnAudioFormatIsCarriedOnTheStream() =>
        new MediaStreamInfo(1, MediaStreamKind.Audio, "aac") { Audio = new AudioFormat(48000, 2, SampleFormat.F32Planar) }
            .ToString().Should().Contain("aac").And.Contain("48000");
}
