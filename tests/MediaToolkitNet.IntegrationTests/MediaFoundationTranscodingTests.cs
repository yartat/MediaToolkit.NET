#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using FluentAssertions;
using FluentAssertions.Execution;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.Windows;
using MediaToolkitNet.Windows.Native;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>A fact that runs on Windows with Media Foundation, and with FFmpeg to make and check the files.</summary>
public sealed class MediaFoundationFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="MediaFoundationFactAttribute"/> class.</summary>
    public MediaFoundationFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || !MfApi.TryStartup())
        {
            Skip = "Media Foundation is Windows only.";
        }
        else if (FFmpegEnvironment.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// The Media Foundation prober and transcoder. Their output is read back with the FFmpeg prober, so what
/// they claim to have written is checked by something that did not write it, and every setting is given a
/// value the input does not already have.
/// </summary>
[SupportedOSPlatform("windows")]
public class MediaFoundationTranscodingTests
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(250);

    [MediaFoundationFact]
    public void TheProberReadsTheStreamsOfAnMp4()
    {
        using var media = new TestMedia();
        var path = MakeSource(media);

        var info = new MediaFoundationProber().Probe(path);

        using var scope = new AssertionScope();
        info.Backend.Should().Be("mediafoundation");
        info.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(2), Tolerance);
        var video = info.OfKind(MediaStreamKind.Video).Should().ContainSingle().Subject;
        video.Codec.Should().Be(MediaCodec.H264);
        video.Video!.Value.Width.Should().Be(320);
        video.Video.Value.Height.Should().Be(240);
        video.Video.Value.FrameRate.Value.Should().BeApproximately(25, 0.01);
        var audio = info.OfKind(MediaStreamKind.Audio).Should().ContainSingle().Subject;
        audio.Codec.Should().Be(MediaCodec.Aac);
        audio.Audio!.Value.SampleRate.Should().Be(48000);
        audio.Audio.Value.Channels.Should().Be(2);
    }

    [MediaFoundationFact]
    public void AMissingFileIsReportedNotInvented()
    {
        using var media = new TestMedia();

        var act = () => new MediaFoundationProber().Probe(media.PathTo("nothing.mp4"));

        act.Should().Throw<MediaToolkitNetException>();
    }

    [MediaFoundationTheory]
    [InlineData(MediaCodec.H264, "h264", true)]
    [InlineData(MediaCodec.H264, "h264", false)]
    [InlineData(MediaCodec.Hevc, "hevc", true)]
    public async Task EverySettingOfARequestReachesTheFile(MediaCodec codec, string expected, bool hardware)
    {
        using var media = new TestMedia();
        var input = MakeSource(media);
        var output = media.PathTo("out.mp4");
        var request = new TranscodeRequest(output)
        {
            Inputs = [input],
            Streams =
            [
                OutputStream.Video(StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings(codec)
                {
                    Width = 160,
                    Height = 120,
                    FrameRate = new Rational(10, 1),
                    BitrateBitsPerSecond = 300_000,
                }),
                OutputStream.Audio(StreamSource.First(MediaStreamKind.Audio), new AudioOutputSettings(MediaCodec.Aac)
                {
                    SampleRate = 44100,
                    Channels = 1,
                    BitrateBitsPerSecond = 96_000,
                }),
            ],
        };

        var result = await new MediaFoundationTranscoder(hardware).RunAsync(request);

        var info = new FFmpegProber().Probe(output);
        using var scope = new AssertionScope();
        result.Output.Should().Be(output);
        info.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(2), Tolerance);
        var video = info.OfKind(MediaStreamKind.Video).Should().ContainSingle().Subject;
        video.CodecName.Should().Be(expected);
        video.Video!.Value.Width.Should().Be(160);
        video.Video.Value.Height.Should().Be(120);
        video.Video.Value.FrameRate.Value.Should().BeApproximately(10, 0.5);
        var audio = info.OfKind(MediaStreamKind.Audio).Should().ContainSingle().Subject;
        audio.CodecName.Should().Be("aac");
        audio.Audio!.Value.SampleRate.Should().Be(44100);
        audio.Audio.Value.Channels.Should().Be(1);
    }

    [MediaFoundationFact]
    public async Task AWidthAloneKeepsTheAspectRatio()
    {
        using var media = new TestMedia();
        var output = media.PathTo("out.mp4");
        var request = new TranscodeRequest(output)
        {
            Inputs = [MakeSource(media)],
            Streams = [OutputStream.Video(StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings { Width = 160, Height = -1 })],
        };

        await new MediaFoundationTranscoder().RunAsync(request);

        var video = new FFmpegProber().Probe(output).OfKind(MediaStreamKind.Video).Single();
        video.Video!.Value.Width.Should().Be(160);
        video.Video.Value.Height.Should().Be(120);
    }

    [MediaFoundationFact]
    public async Task ATrimKeepsOnlyWhatWasAskedFor()
    {
        using var media = new TestMedia();
        var output = media.PathTo("out.mp4");
        var request = new TranscodeRequest(output)
        {
            Inputs = [MakeSource(media)],
            Start = TimeSpan.FromSeconds(0.5),
            End = TimeSpan.FromSeconds(1.5),
            Streams =
            [
                OutputStream.Video(StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings()),
                OutputStream.Audio(StreamSource.First(MediaStreamKind.Audio), new AudioOutputSettings()),
            ],
        };

        var result = await new MediaFoundationTranscoder().RunAsync(request);

        var info = new FFmpegProber().Probe(output);
        info.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(1), Tolerance);
        result.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(1), Tolerance);
    }

    [MediaFoundationFact]
    public async Task TheKeyFrameIntervalIsHonoured()
    {
        using var media = new TestMedia();
        var output = media.PathTo("out.mp4");
        var request = new TranscodeRequest(output)
        {
            Inputs = [MakeSource(media, seconds: 4)],
            Streams = [OutputStream.Video(StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings { KeyFrameInterval = 10 })],
        };

        await new MediaFoundationTranscoder().RunAsync(request);

        // 100 frames at one key frame in ten: ten of them, where the encoder's own default would give one or two.
        var keys = FfmpegProgram.Count(output, "-skip_frame nokey");
        keys.Should().BeInRange(9, 11);
    }

    [MediaFoundationFact]
    public void WhatMediaFoundationCannotDoIsRefusedWithAReason()
    {
        using var media = new TestMedia();
        var input = MakeSource(media);
        var video = StreamSource.First(MediaStreamKind.Video);
        var audio = StreamSource.First(MediaStreamKind.Audio);
        var transcoder = new MediaFoundationTranscoder();

        (string Why, TranscodeRequest Request)[] cases =
        [
            ("copy", new TranscodeRequest("o.mp4") { Inputs = [input], Streams = [OutputStream.Copy(video)] }),
            ("no streams", TranscodeRequest.Remux(input, "o.mp4")),
            ("vp9", Video(input, new VideoOutputSettings(MediaCodec.Vp9))),
            ("encoder name", Video(input, new VideoOutputSettings { EncoderName = "libx264" })),
            ("quality", Video(input, new VideoOutputSettings { Quality = 23 })),
            ("options", Video(input, new VideoOutputSettings { Options = new Dictionary<string, string> { ["a"] = "b" } })),
            ("filters", Video(input, new VideoOutputSettings { Filters = [new VideoFilter.Scale(10, 10)] })),
            ("opus", Audio(input, new AudioOutputSettings(MediaCodec.Opus))),
            ("rate", Audio(input, new AudioOutputSettings { SampleRate = 22050 })),
            ("channels", Audio(input, new AudioOutputSettings { Channels = 3 })),
            ("mkv output", new TranscodeRequest("o.mkv") { Inputs = [input], Streams = [OutputStream.Video(video, new VideoOutputSettings())] }),
            ("two inputs", new TranscodeRequest("o.mp4") { Inputs = [input, input], Streams = [OutputStream.Video(video, new VideoOutputSettings())] }),
            ("metadata", Video(input, new VideoOutputSettings()) with { Metadata = new Dictionary<string, string> { ["title"] = "x" } }),
            ("subtitles", new TranscodeRequest("o.mp4") { Inputs = [input], Streams = [OutputStream.Audio(audio, new AudioOutputSettings()), OutputStream.Subtitles(StreamSource.First(MediaStreamKind.Subtitle), MediaCodec.MovText)] }),
            ("no such stream", new TranscodeRequest("o.mp4") { Inputs = [input], Streams = [OutputStream.Video(StreamSource.Nth(MediaStreamKind.Video, 3), new VideoOutputSettings())] }),
        ];

        using var scope = new AssertionScope();
        foreach (var (why, request) in cases)
        {
            transcoder.Validate(request).Should().Contain(i => i.Severity == TranscodeIssueSeverity.Error, why);
        }
    }

    [MediaFoundationFact]
    public void CopyingEveryStreamOfAKindThatIsNotThereIsPassedOver()
    {
        using var media = new TestMedia();
        var request = Video(MakeSource(media), new VideoOutputSettings()) with
        {
            Streams =
            [
                OutputStream.Video(StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings()),
                OutputStream.Copy(StreamSource.All(MediaStreamKind.Subtitle)),
            ],
        };

        new MediaFoundationTranscoder().Validate(request).Should().NotContain(i => i.Severity == TranscodeIssueSeverity.Error);
    }

    [MediaFoundationFact]
    public void ASettingThatIsOnlyApproximatedIsReported()
    {
        using var media = new TestMedia();
        var request = Audio(MakeSource(media), new AudioOutputSettings { BitrateBitsPerSecond = 100_000, SampleFormat = SampleFormat.F32 });

        var issues = new MediaFoundationTranscoder().Validate(request);

        issues.Should().NotContain(i => i.Severity == TranscodeIssueSeverity.Error);
        issues.Should().Contain(i => i.Message.Contains("bit rate")).And.Contain(i => i.Message.Contains("sample format"));
    }

    [MediaFoundationFact]
    public async Task ARejectedRequestWritesNothingAndLeavesAnExistingOutputAlone()
    {
        using var media = new TestMedia();
        var output = media.PathTo("out.mp4");
        await File.WriteAllTextAsync(output, "precious");
        var request = new TranscodeRequest(output)
        {
            Inputs = [media.PathTo("nothing.mp4")],
            Streams = [OutputStream.Video(StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings())],
        };

        var act = () => new MediaFoundationTranscoder().RunAsync(request);

        await act.Should().ThrowAsync<TranscodeRejectedException>();
        (await File.ReadAllTextAsync(output)).Should().Be("precious");
        Directory.GetFiles(Path.GetDirectoryName(output)!, ".*partial*").Should().BeEmpty();
    }

    [MediaFoundationFact]
    public async Task ACancelledJobLeavesNoFileBehind()
    {
        using var media = new TestMedia();
        var output = media.PathTo("out.mp4");
        using var cancel = new CancellationTokenSource();
        var request = new TranscodeRequest(output)
        {
            Inputs = [MakeSource(media, seconds: 4)],
            Streams = [OutputStream.Video(StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings())],
        };

        var act = () => new MediaFoundationTranscoder().RunAsync(request, new SynchronousProgress(_ => cancel.Cancel()), cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(output).Should().BeFalse();
        Directory.GetFiles(Path.GetDirectoryName(output)!, ".*partial*").Should().BeEmpty();
    }

    [MediaFoundationFact]
    public async Task ProgressReachesTheEnd()
    {
        using var media = new TestMedia();
        var output = media.PathTo("out.mp4");
        var seen = new List<TranscodeProgress>();
        var request = new TranscodeRequest(output)
        {
            Inputs = [MakeSource(media)],
            Streams = [OutputStream.Video(StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings())],
        };

        await new MediaFoundationTranscoder().RunAsync(request, new SynchronousProgress(seen.Add));

        seen.Should().NotBeEmpty();
        seen[^1].Fraction.Should().BeGreaterThan(0.9);
    }

    private static TranscodeRequest Video(string input, VideoOutputSettings settings) => new("o.mp4")
    {
        Inputs = [input],
        Streams = [OutputStream.Video(StreamSource.First(MediaStreamKind.Video), settings)],
    };

    private static TranscodeRequest Audio(string input, AudioOutputSettings settings) => new("o.mp4")
    {
        Inputs = [input],
        Streams = [OutputStream.Audio(StreamSource.First(MediaStreamKind.Audio), settings)],
    };

    // A second of a test pattern and a tone, as Media Foundation reads them: H.264 and AAC in an MP4.
    private static string MakeSource(TestMedia media, int seconds = 2)
    {
        var path = media.PathTo($"source{seconds}.mp4");
        FfmpegProgram.Run(
            $"-f lavfi -i testsrc2=size=320x240:rate=25:duration={seconds} " +
            $"-f lavfi -i sine=frequency=440:duration={seconds}:sample_rate=48000 " +
            $"-c:v libx264 -g 25 -pix_fmt yuv420p -c:a aac -b:a 128k -ac 2 \"{path}\"");
        return path;
    }

    // Progress<T> posts to the thread pool; the tests want the report on the transcoder's own thread, in order.
    private sealed class SynchronousProgress(Action<TranscodeProgress> handler) : IProgress<TranscodeProgress>
    {
        public void Report(TranscodeProgress value) => handler(value);
    }
}

/// <summary>A theory with the same conditions as <see cref="MediaFoundationFactAttribute"/>.</summary>
public sealed class MediaFoundationTheoryAttribute : TheoryAttribute
{
    /// <summary>Initializes a new instance of the <see cref="MediaFoundationTheoryAttribute"/> class.</summary>
    public MediaFoundationTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows() || !MfApi.TryStartup())
        {
            Skip = "Media Foundation is Windows only.";
        }
        else if (FFmpegEnvironment.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}
