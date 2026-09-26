#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using FluentAssertions;
using FluentAssertions.Execution;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.FFmpeg.Reading;
using MediaToolkitNet.FFmpeg.Transcoding;
using Xunit;
using Xunit.Abstractions;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// Transcodes files this library wrote and reads the results back through
/// this library's prober and reader, so every stage checks the one before it.
/// </summary>
/// <remarks>
/// The codecs are MJPEG, AAC and FLAC, whose encoders are part of every FFmpeg
/// build, so nothing here depends on how FFmpeg was configured beyond the
/// subtitles filter, which only the burn-in test needs.
/// </remarks>
public class TranscodingTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(60);

    [FFmpegFact]
    public void SubtitlesWrittenByTheRecorderReadBackAsTheSameCues()
    {
        using var media = new TestMedia();
        var path = media.WriteMovie("movie.mkv");

        using var reader = FFmpegMediaReader.Open(path);
        var cues = reader.SubtitleStreams.Should().ContainSingle().Subject.ReadAll().ToList();

        cues.Select(c => c.Text).Should().Equal(TestMedia.MovieCues.Select(c => c.Text));
        AssertTimed(cues, TestMedia.MovieCues, TimeSpan.Zero);
        reader.Info.OfKind(MediaStreamKind.Subtitle).Single().Language.Should().Be("eng");
    }

    [FFmpegFact]
    public async Task ARemuxKeepsEveryStreamAndItsLanguage()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var output1 = media.PathTo("copy.mkv");

        var result = await new FFmpegTranscoder().RunAsync(TranscodeRequest.Remux(input, output1));

        var before = new FFmpegProber().Probe(input);
        var after = new FFmpegProber().Probe(output1);
        output.WriteLine($"{result.Duration} in {result.Elapsed}");

        using var _ = new AssertionScope();
        after.Streams.Select(s => (s.Kind, s.CodecName, s.Language))
            .Should().Equal(before.Streams.Select(s => (s.Kind, s.CodecName, s.Language)));
        after.Duration.Should().BeCloseTo(before.Duration, Tolerance);
    }

    [FFmpegFact]
    public void SubRipCannotBeCopiedIntoMp4AndTheRefusalSaysWhatToDo()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");

        var issues = new FFmpegTranscoder().Validate(TranscodeRequest.Remux(input, media.PathTo("out.mp4")));

        issues.Should().Contain(i => i.Severity == TranscodeIssueSeverity.Error
            && i.Message.Contains("cannot hold subrip") && i.Message.Contains("MovText"));
    }

    [FFmpegFact]
    public async Task SubRipConvertedToMovTextReadsBackAsTheSameText()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var mp4 = media.PathTo("out.mp4");

        await new FFmpegTranscoder().RunAsync(new TranscodeRequest(mp4)
        {
            Inputs = [input],
            Streams =
            [
                OutputStream.Copy(StreamSource.First(MediaStreamKind.Video)),
                OutputStream.Copy(StreamSource.First(MediaStreamKind.Audio)),
                OutputStream.Subtitles(StreamSource.All(MediaStreamKind.Subtitle), MediaCodec.MovText),
            ],
        });

        using var reader = FFmpegMediaReader.Open(mp4);
        var subtitles = reader.SubtitleStreams.Should().ContainSingle().Subject;
        subtitles.Info.CodecName.Should().Be("mov_text");
        subtitles.Info.Language.Should().Be("eng");

        var cues = subtitles.ReadAll().ToList();
        cues.Select(c => c.Text).Should().Equal(TestMedia.MovieCues.Select(c => c.Text));
    }

    [FFmpegTheory]
    [InlineData(MediaCodec.SubRip, "subrip")]
    [InlineData(MediaCodec.Ass, "ass")]
    [InlineData(MediaCodec.WebVtt, "webvtt")]
    public async Task SubtitlesFromAnotherFileAreConvertedIntoTheOutput(MediaCodec codec, string expected)
    {
        using var media = new TestMedia();
        var movie = media.WriteMovie("movie.mkv");
        var srt = media.WriteSrt("extra.srt");
        var mkv = media.PathTo("with-extra.mkv");

        await new FFmpegTranscoder().RunAsync(new TranscodeRequest(mkv)
        {
            Inputs = [movie, srt],
            Streams =
            [
                OutputStream.Copy(StreamSource.First(MediaStreamKind.Video)),
                OutputStream.Subtitles(StreamSource.First(MediaStreamKind.Subtitle, input: 1), codec) with { Language = "fra" },
            ],
        });

        using var reader = FFmpegMediaReader.Open(mkv);
        var subtitles = reader.SubtitleStreams.Should().ContainSingle().Subject;
        subtitles.Info.CodecName.Should().Be(expected);
        subtitles.Info.Language.Should().Be("fra");
        subtitles.ReadAll().Select(c => c.Text).Should().Equal(TestMedia.MovieCues.Select(c => c.Text));
    }

    [FFmpegFact]
    public async Task ReencodedVideoHasTheSizeAndRateAskedFor()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var mkv = media.PathTo("small.mkv");

        await new FFmpegTranscoder().RunAsync(new TranscodeRequest(mkv)
        {
            Inputs = [input],
            Streams =
            [
                OutputStream.Video(
                    StreamSource.First(MediaStreamKind.Video),
                    new VideoOutputSettings(MediaCodec.Mjpeg) { Width = 160, Height = 120, FrameRate = new Rational(10, 1) }),
            ],
        });

        using var reader = FFmpegMediaReader.Open(mkv);
        var frames = 0;
        var size = (0, 0);
        while (reader.Video!.TryReadNext((in VideoFrame frame) => size = (frame.Format.Width, frame.Format.Height)))
        {
            frames++;
        }

        size.Should().Be((160, 120));
        frames.Should().BeInRange(19, 21, "two seconds at ten frames per second");
    }

    [FFmpegFact]
    public async Task ReencodedAudioIsResampledAndMixedDown()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var mka = media.PathTo("mono.mka");

        await new FFmpegTranscoder().RunAsync(new TranscodeRequest(mka)
        {
            Inputs = [input],
            Streams =
            [
                OutputStream.Audio(
                    StreamSource.First(MediaStreamKind.Audio),
                    new AudioOutputSettings(MediaCodec.Flac) { SampleRate = 22050, Channels = 1 }),
            ],
        });

        using var reader = FFmpegMediaReader.Open(mka);
        var samples = 0L;
        AudioFormat format = default;
        while (reader.Audio!.TryReadNext((in AudioFrame frame) =>
        {
            format = frame.Format;
            samples += frame.SampleCount;
        }))
        {
        }

        using var _ = new AssertionScope();
        format.SampleRate.Should().Be(22050);
        format.Channels.Should().Be(1);
        (samples / 22050d).Should().BeApproximately(2.0, 0.05);
    }

    [FFmpegFact]
    public async Task ATrimKeepsOnlyTheWindowAndMovesItToZero()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var mkv = media.PathTo("trimmed.mkv");

        await new FFmpegTranscoder().RunAsync(new TranscodeRequest(mkv)
        {
            Inputs = [input],
            Start = TimeSpan.FromSeconds(0.5),
            End = TimeSpan.FromSeconds(1.5),
            Streams =
            [
                OutputStream.Video(StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings(MediaCodec.Mjpeg)),
                OutputStream.Audio(StreamSource.First(MediaStreamKind.Audio), new AudioOutputSettings(MediaCodec.Flac)),
                OutputStream.Subtitles(StreamSource.First(MediaStreamKind.Subtitle), MediaCodec.SubRip),
            ],
        });

        using var reader = FFmpegMediaReader.Open(mkv);
        var first = TimeSpan.MinValue;
        var frames = 0;
        while (reader.Video!.TryReadNext((in VideoFrame frame) =>
        {
            if (first == TimeSpan.MinValue)
            {
                first = frame.Timestamp;
            }
        }))
        {
            frames++;
        }

        var cues = reader.SubtitleStreams.Single().ReadAll().ToList();

        // Times are measured from each file's own start, which AAC priming puts a
        // little before zero; so the expectation comes from the input as read.
        using var source = FFmpegMediaReader.Open(input);
        var original = source.SubtitleStreams.Single().ReadAll().ToList();
        var start = TimeSpan.FromSeconds(0.5);
        var end = TimeSpan.FromSeconds(1.5);

        using var _ = new AssertionScope();
        first.Should().BeCloseTo(TimeSpan.Zero, Tolerance);
        frames.Should().BeInRange(24, 26, "one second at twenty-five frames per second");
        reader.Info.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(1), Tolerance);

        // The first cue straddles the start and is cut to begin there, which only
        // works if it is read at all: its packet lies before the point the seek
        // lands on. The second runs past the end and is cut there.
        cues.Select(c => c.Text).Should().Equal(TestMedia.MovieCues.Select(c => c.Text));
        cues[0].Start.Should().BeCloseTo(TimeSpan.Zero, Tolerance);
        cues[0].End.Should().BeCloseTo(original[0].End - start, Tolerance);
        cues[1].Start.Should().BeCloseTo(original[1].Start - start, Tolerance);
        cues[1].End.Should().BeCloseTo(end - start, Tolerance);
    }

    [FFmpegFact]
    public async Task RequestTagsReplaceTheInputsTags()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var mkv = media.PathTo("tagged.mkv");

        await new FFmpegTranscoder().RunAsync(TranscodeRequest.Remux(input, mkv) with
        {
            Metadata = new Dictionary<string, string> { ["title"] = "Renamed", ["comment"] = "from the test" },
        });

        var tags = new FFmpegProber().Probe(mkv).Metadata;
        tags["title"].Should().Be("Renamed");
        tags["comment"].Should().Be("from the test");
    }

    [FFmpegFact]
    public async Task BurningIntoACopiedVideoIsRefusedAndWritesNothing()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var mkv = media.PathTo("refused.mkv");

        var act = () => new FFmpegTranscoder().RunAsync(new TranscodeRequest(mkv)
        {
            Inputs = [input],
            Streams =
            [
                OutputStream.Copy(StreamSource.First(MediaStreamKind.Video)),
                OutputStream.BurnIn(StreamSource.First(MediaStreamKind.Subtitle), StreamSource.First(MediaStreamKind.Video)),
            ],
        });

        (await act.Should().ThrowAsync<TranscodeRejectedException>())
            .Which.Issues.Should().Contain(i => i.Message.Contains("needs it re-encoded"));
        File.Exists(mkv).Should().BeFalse();
    }

    [FFmpegFact]
    public async Task ACancelledJobLeavesNoFileBehind()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var mkv = media.PathTo("cancelled.mkv");
        using var cancellation = new CancellationTokenSource();

        var act = () => new FFmpegTranscoder().RunAsync(
            new TranscodeRequest(mkv)
            {
                Inputs = [input],
                Streams = [OutputStream.Video(StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings(MediaCodec.Mjpeg))],
            },
            new CancelOnFirstReport(cancellation),
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(mkv).Should().BeFalse();
    }

    [FFmpegFact]
    public void TheReaderFindsTheFrameOnScreenAtATimeAndReadsOnFromIt()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        using var reader = FFmpegMediaReader.Open(input);
        var video = reader.Video!;

        var at = TimeSpan.MinValue;
        var next = TimeSpan.MinValue;
        video.TryReadAt(TimeSpan.FromSeconds(1.01), (in VideoFrame frame) => at = frame.Timestamp).Should().BeTrue();
        video.TryReadNext((in VideoFrame frame) => next = frame.Timestamp).Should().BeTrue();

        var back = TimeSpan.MinValue;
        video.TryReadAt(TimeSpan.FromSeconds(0.3), (in VideoFrame frame) => back = frame.Timestamp).Should().BeTrue();

        using var _ = new AssertionScope();
        at.Should().BeCloseTo(TimeSpan.FromSeconds(1.0), TimeSpan.FromMilliseconds(20));
        next.Should().BeCloseTo(TimeSpan.FromSeconds(1.04), TimeSpan.FromMilliseconds(20));
        back.Should().BeCloseTo(TimeSpan.FromSeconds(0.28), TimeSpan.FromMilliseconds(20));
        video.TryReadAt(TimeSpan.FromSeconds(30), (in VideoFrame _) => { }).Should().BeFalse();
    }

    [FFmpegFact]
    public void TheReaderConvertsToThePixelFormatAndSizeAskedFor()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        using var reader = FFmpegMediaReader.Open(
            input,
            new MediaReaderOptions { VideoPixelFormat = PixelFormat.Bgra32, VideoWidth = 64, VideoHeight = 48 });

        VideoFormat format = default;
        var stride = 0;
        reader.Video!.TryReadNext((in VideoFrame frame) =>
        {
            format = frame.Format;
            stride = frame.Stride(0);
        }).Should().BeTrue();

        format.PixelFormat.Should().Be(PixelFormat.Bgra32);
        (format.Width, format.Height).Should().Be((64, 48));
        stride.Should().BeGreaterThanOrEqualTo(64 * 4);
    }

    private static void AssertTimed(IReadOnlyList<SubtitleCue> actual, IReadOnlyList<SubtitleCue> expected, TimeSpan shift)
    {
        actual.Should().HaveSameCount(expected);
        for (var i = 0; i < expected.Count; i++)
        {
            actual[i].Start.Should().BeCloseTo(expected[i].Start - shift, Tolerance);
            actual[i].End.Should().BeCloseTo(expected[i].End - shift, Tolerance);
        }
    }

    private sealed class CancelOnFirstReport(CancellationTokenSource source) : IProgress<TranscodeProgress>
    {
        public void Report(TranscodeProgress value) => source.Cancel();
    }
}
