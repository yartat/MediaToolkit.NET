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
using MediaToolkitNet.Mpv;
using MediaToolkitNet.Mpv.Transcoding;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// Runs the mpv prober and transcoder through libmpv on files the FFmpeg
/// recorder wrote, and reads the results back with the FFmpeg prober and
/// reader.
/// </summary>
/// <remarks>
/// mpv encodes through libavcodec, so MJPEG and FLAC are available wherever
/// libmpv was built with encoding support.
/// </remarks>
public class MpvTranscodingTests
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(60);

    [MpvFact]
    public void TheProberNumbersAndNamesStreamsAsFFmpegDoes()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");

        var mpv = new MpvProber().Probe(input);
        var ffmpeg = new FFmpegProber().Probe(input);

        using var _ = new AssertionScope();
        mpv.Streams.Select(s => (s.Index, s.Kind, s.Codec, s.Language, s.IsDefault, s.IsForced))
            .Should().Equal(ffmpeg.Streams.Select(s => (s.Index, s.Kind, s.Codec, s.Language, s.IsDefault, s.IsForced)));
        mpv.Container.Should().Be(MediaContainer.Matroska);
        mpv.Duration.Should().BeCloseTo(ffmpeg.Duration, Tolerance);
    }

    [MpvFact]
    public void AFileOfSubtitlesAloneIsProbedAsOneSubtitleStream()
    {
        using var media = new TestMedia();
        var srt = media.WriteSrt("cues.srt");

        var info = new MpvProber().Probe(srt);

        var stream = info.Streams.Should().ContainSingle().Subject;
        using var _ = new AssertionScope();
        stream.Kind.Should().Be(MediaStreamKind.Subtitle);
        stream.Codec.Should().Be(MediaCodec.SubRip);
        stream.IsTextSubtitle.Should().BeTrue();
        stream.Title.Should().BeNull("mpv's title for an external track is its file name, which the file does not state");
    }

    [MpvFact]
    public async Task ATrimReencodesOnlyTheWindow()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var mkv = media.PathTo("trimmed.mkv");

        await new MpvTranscoder().RunAsync(new TranscodeRequest(mkv)
        {
            Inputs = [input],
            Start = TimeSpan.FromSeconds(0.5),
            End = TimeSpan.FromSeconds(1.5),
            Streams =
            [
                OutputStream.Video(StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings(MediaCodec.Mjpeg) { Width = 160 }),
                OutputStream.Audio(StreamSource.First(MediaStreamKind.Audio), new AudioOutputSettings(MediaCodec.Flac)),
            ],
        });

        var info = new FFmpegProber().Probe(mkv);
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

        using var _ = new AssertionScope();
        info.Streams.Select(s => s.Codec).Should().BeEquivalentTo([MediaCodec.Mjpeg, MediaCodec.Flac]);
        var video = info.OfKind(MediaStreamKind.Video).Single().Video!.Value;
        (video.Width, video.Height).Should().Be((160, 120));
        first.Should().BeCloseTo(TimeSpan.Zero, Tolerance);
        frames.Should().BeInRange(24, 26, "one second at twenty-five frames per second");
        info.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(1), Tolerance);
    }

    [MpvFact]
    public async Task SubtitlesFromAnotherFileAreBurnedInWhileTheirCueShows()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var srt = media.WriteSrt("cues.srt");
        var mkv = media.PathTo("burned.mkv");

        await new MpvTranscoder().RunAsync(new TranscodeRequest(mkv)
        {
            Inputs = [input, srt],
            Streams =
            [
                OutputStream.Video(StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings(MediaCodec.Mjpeg)),
                OutputStream.BurnIn(StreamSource.First(MediaStreamKind.Subtitle, input: 1), StreamSource.First(MediaStreamKind.Video)),
            ],
        });

        // The source frames are flat, so any spread of luma in the bottom of
        // the picture is the text.
        using var reader = FFmpegMediaReader.Open(mkv, new MediaReaderOptions { VideoPixelFormat = PixelFormat.Yuv420P });
        int Spread(double seconds)
        {
            var spread = -1;
            reader.Video!.TryReadAt(TimeSpan.FromSeconds(seconds), (in VideoFrame frame) =>
            {
                var plane = frame.GetPlane(0);
                var stride = frame.Stride(0);
                int low = 255, high = 0;
                for (var y = frame.Format.Height * 4 / 5; y < frame.Format.Height; y++)
                {
                    foreach (var value in plane.Slice(y * stride, frame.Format.Width))
                    {
                        low = Math.Min(low, value);
                        high = Math.Max(high, value);
                    }
                }

                spread = high - low;
            }).Should().BeTrue();
            return spread;
        }

        using var _ = new AssertionScope();
        Spread(0.5).Should().BeGreaterThan(100, "the first cue runs from 0.2 to 0.8 s");
        Spread(0.9).Should().BeLessThan(30, "no cue is on screen at 0.9 s");
        Spread(1.3).Should().BeGreaterThan(100, "the second cue runs from 1.0 to 1.6 s");
        Spread(1.8).Should().BeLessThan(30, "no cue is on screen at 1.8 s");
    }

    [MpvFact]
    public void CopyingIsRefusedWithTheReason()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");

        var issues = new MpvTranscoder().Validate(TranscodeRequest.Remux(input, media.PathTo("out.mkv")));

        issues.Should().Contain(i => i.Severity == TranscodeIssueSeverity.Error && i.Message.Contains("cannot copy"));
    }

    [MpvFact]
    public async Task ACancelledJobLeavesNoFileBehind()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var mkv = media.PathTo("cancelled.mkv");
        using var cancellation = new CancellationTokenSource();

        var act = () => new MpvTranscoder().RunAsync(
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

    private sealed class CancelOnFirstReport(CancellationTokenSource source) : IProgress<TranscodeProgress>
    {
        public void Report(TranscodeProgress value) => source.Cancel();
    }
}

/// <summary>A fact that is skipped unless libmpv and FFmpeg are both usable.</summary>
public sealed class MpvFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="MpvFactAttribute"/> class.</summary>
    public MpvFactAttribute() =>
        Skip = !MpvBackend.Instance.IsAvailable ? "libmpv is not usable here." : FFmpegEnvironment.SkipReason;
}
