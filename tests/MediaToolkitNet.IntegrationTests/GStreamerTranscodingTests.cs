#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using FluentAssertions;
using FluentAssertions.Execution;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.FFmpeg.Reading;
using MediaToolkitNet.GStreamer;
using MediaToolkitNet.GStreamer.Transcoding;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// Runs the GStreamer transcoder on files the FFmpeg recorder wrote and reads
/// the results back with the FFmpeg prober and reader, so neither backend
/// checks its own output.
/// </summary>
/// <remarks>
/// The codecs are MJPEG and FLAC, whose GStreamer encoders, <c>jpegenc</c> and
/// <c>flacenc</c>, are in gst-plugins-good; the burn-in needs
/// <c>textoverlay</c> from gst-plugins-base.
/// </remarks>
[SupportedOSPlatform("linux")]
public class GStreamerTranscodingTests
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(60);

    [GStreamerFact]
    public void TheProberNumbersAndNamesStreamsAsFFmpegDoes()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");

        var gstreamer = new GStreamerProber().Probe(input);
        var ffmpeg = new FFmpegProber().Probe(input);

        using var _ = new AssertionScope();
        gstreamer.Streams.Select(s => (s.Index, s.Kind, s.Codec, s.Language))
            .Should().Equal(ffmpeg.Streams.Select(s => (s.Index, s.Kind, s.Codec, s.Language)));
        gstreamer.Container.Should().Be(MediaContainer.Matroska);
        gstreamer.Duration.Should().BeCloseTo(ffmpeg.Duration, Tolerance);
        gstreamer.StreamAt(0)!.Video!.Value.Width.Should().Be(320);
        gstreamer.StreamAt(1)!.Audio!.Value.SampleRate.Should().Be(48000);
    }

    [GStreamerFact]
    public async Task ReencodedStreamsHaveTheSizeAndFormatAskedFor()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var mkv = media.PathTo("out.mkv");

        await new GStreamerTranscoder().RunAsync(new TranscodeRequest(mkv)
        {
            Inputs = [input],
            Streams =
            [
                OutputStream.Video(StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings(MediaCodec.Mjpeg) { Width = 160 }),
                OutputStream.Audio(StreamSource.First(MediaStreamKind.Audio), new AudioOutputSettings(MediaCodec.Flac) { SampleRate = 22050, Channels = 1 }),
            ],
        });

        var info = new FFmpegProber().Probe(mkv);

        using var _ = new AssertionScope();
        info.Streams.Select(s => s.Codec).Should().Equal(MediaCodec.Mjpeg, MediaCodec.Flac);
        var video = info.OfKind(MediaStreamKind.Video).Single().Video!.Value;
        (video.Width, video.Height).Should().Be((160, 120), "the height follows the width, keeping the aspect ratio");
        var audio = info.OfKind(MediaStreamKind.Audio).Single().Audio!.Value;
        (audio.SampleRate, audio.Channels).Should().Be((22050, 1));
        info.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(2), Tolerance);
    }

    [GStreamerFact]
    public async Task SideSpeakersLandWhereFFmpegPutsThem()
    {
        // FFmpeg's side-speaker bits sit one below GStreamer's positions, so a
        // mask passed through unchanged asks flacenc for a layout it refuses.
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var mka = media.PathTo("side.mka");

        await new GStreamerTranscoder().RunAsync(new TranscodeRequest(mka)
        {
            Inputs = [input],
            Streams = [OutputStream.Audio(StreamSource.First(MediaStreamKind.Audio), new AudioOutputSettings(MediaCodec.Flac)
            {
                ChannelMask = ChannelLayout.SevenPoint1,
                SampleFormat = SampleFormat.S32,
            })],
        });

        var audio = new FFmpegProber().Probe(mka).OfKind(MediaStreamKind.Audio).Single().Audio!.Value;
        (audio.Channels, audio.EffectiveChannelMask).Should().Be((8, ChannelLayout.SevenPoint1));
    }

    [GStreamerTheory]
    [InlineData(MediaCodec.Dts, 6, ChannelLayout.FivePoint1Side)]
    [InlineData(MediaCodec.PcmS24, 2, ChannelLayout.Stereo)]
    [InlineData(MediaCodec.RealAudio, 1, ChannelLayout.Mono)]
    public async Task TheLibavAndRawCodecsAreWritten(MediaCodec codec, int channels, ulong layout)
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var mka = media.PathTo("out.mka");

        await new GStreamerTranscoder().RunAsync(new TranscodeRequest(mka)
        {
            Inputs = [input],
            Streams = [OutputStream.Audio(StreamSource.First(MediaStreamKind.Audio), new AudioOutputSettings(codec)
            {
                SampleRate = codec == MediaCodec.RealAudio ? 8000 : 48000,
                ChannelMask = layout,
            })],
        });

        var stream = new FFmpegProber().Probe(mka).OfKind(MediaStreamKind.Audio).Single();
        using var _ = new AssertionScope();
        stream.Codec.Should().Be(codec);
        stream.Audio!.Value.Channels.Should().Be(channels);
    }

    [GStreamerFact]
    public async Task LameKeepsTheRateAtALowBitRate()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var mka = media.PathTo("low.mka");

        // Left to itself, lame halves 48 kHz at 64 kbit/s of stereo.
        await new GStreamerTranscoder().RunAsync(new TranscodeRequest(mka)
        {
            Inputs = [input],
            Streams = [OutputStream.Audio(StreamSource.First(MediaStreamKind.Audio), new AudioOutputSettings(MediaCodec.Mp3)
            {
                SampleRate = 48000,
                Channels = 2,
                BitrateBitsPerSecond = 64000,
            })],
        });

        new FFmpegProber().Probe(mka).OfKind(MediaStreamKind.Audio).Single().Audio!.Value.SampleRate.Should().Be(48000);
    }

    [GStreamerFact]
    public void TrueHdIsRefusedForWantOfAMuxer()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");

        var issues = new GStreamerTranscoder().Validate(new TranscodeRequest(media.PathTo("out.mka"))
        {
            Inputs = [input],
            Streams = [OutputStream.Audio(StreamSource.First(MediaStreamKind.Audio), new AudioOutputSettings(MediaCodec.TrueHd))],
        });

        issues.Should().Contain(i => i.Severity == TranscodeIssueSeverity.Error && i.Message.Contains("no GStreamer muxer"));
    }

    [GStreamerFact]
    public async Task ATrimSeeksToTheStartAndStopsAtTheEnd()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var mkv = media.PathTo("trimmed.mkv");

        await new GStreamerTranscoder().RunAsync(new TranscodeRequest(mkv)
        {
            Inputs = [input],
            Start = TimeSpan.FromSeconds(0.5),
            End = TimeSpan.FromSeconds(1.5),
            Streams =
            [
                OutputStream.Video(StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings(MediaCodec.Mjpeg)),
                OutputStream.Audio(StreamSource.First(MediaStreamKind.Audio), new AudioOutputSettings(MediaCodec.Flac)),
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

        using var _ = new AssertionScope();
        first.Should().BeCloseTo(TimeSpan.Zero, Tolerance);
        frames.Should().BeInRange(24, 26, "one second at twenty-five frames per second");
        reader.Info.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(1), Tolerance);
    }

    [GStreamerFact]
    public async Task BurnedInSubtitlesShowOnlyWhileTheirCueDoes()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var mkv = media.PathTo("burned.mkv");

        await new GStreamerTranscoder().RunAsync(new TranscodeRequest(mkv)
        {
            Inputs = [input],
            Streams =
            [
                OutputStream.Video(StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings(MediaCodec.Mjpeg)),
                OutputStream.BurnIn(StreamSource.First(MediaStreamKind.Subtitle), StreamSource.First(MediaStreamKind.Video)),
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

    [GStreamerFact]
    public void CopyingSubtitlesIsRefusedWithTheReason()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");

        var issues = new GStreamerTranscoder().Validate(TranscodeRequest.Remux(input, media.PathTo("out.mkv")));

        issues.Should().Contain(i => i.Severity == TranscodeIssueSeverity.Error
            && i.Message.Contains("Pango markup") && i.Message.Contains("FFmpeg"));
    }

    [GStreamerFact]
    public async Task RequestTagsReplaceTheInputsTags()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var mov = media.PathTo("tagged.mov");

        await new GStreamerTranscoder().RunAsync(new TranscodeRequest(mov)
        {
            Inputs = [input],
            Streams = [OutputStream.Copy(StreamSource.First(MediaStreamKind.Video))],
            Metadata = new Dictionary<string, string> { ["title"] = "Renamed", ["comment"] = "from the test" },
        });

        var tags = new FFmpegProber().Probe(mov).Metadata;
        tags["title"].Should().Be("Renamed");
        tags["comment"].Should().Be("from the test");
    }

    [GStreamerFact]
    public async Task ACodecTheMuxerDoesNotTakeIsRefusedAndWritesNothing()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var mp4 = media.PathTo("refused.mp4");

        // mp4mux takes no JPEG, where FFmpeg's MP4 muxer does.
        var act = () => new GStreamerTranscoder().RunAsync(new TranscodeRequest(mp4)
        {
            Inputs = [input],
            Streams = [OutputStream.Copy(StreamSource.First(MediaStreamKind.Video))],
        });

        (await act.Should().ThrowAsync<TranscodeRejectedException>())
            .Which.Issues.Should().Contain(i => i.Message.Contains("could not link") && i.Message.Contains("Mp4"));
        File.Exists(mp4).Should().BeFalse();
    }

    [GStreamerFact]
    public async Task ACancelledJobLeavesNoFileBehind()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var mkv = media.PathTo("cancelled.mkv");
        using var cancellation = new CancellationTokenSource();

        var act = () => new GStreamerTranscoder().RunAsync(
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

/// <summary>A fact that is skipped unless GStreamer and FFmpeg are both usable.</summary>
public sealed class GStreamerFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="GStreamerFactAttribute"/> class.</summary>
    public GStreamerFactAttribute()
    {
        Skip = !OperatingSystem.IsLinux() ? "The GStreamer backend runs on Linux only."
            : !GStreamerBackend.Instance.IsAvailable ? "GStreamer is not usable here."
            : FFmpegEnvironment.SkipReason;
    }
}

/// <summary>A theory that is skipped unless GStreamer and FFmpeg are both usable.</summary>
public sealed class GStreamerTheoryAttribute : TheoryAttribute
{
    /// <summary>Initializes a new instance of the <see cref="GStreamerTheoryAttribute"/> class.</summary>
    public GStreamerTheoryAttribute()
    {
        Skip = new GStreamerFactAttribute().Skip;
    }
}
