#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.GStreamer;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// One recorder writing to several outputs at once. Each output is read back by FFmpeg, so a branch that got nothing, or only
/// part of the recording, shows as a count or a duration that is wrong.
/// </summary>
[SupportedOSPlatform("linux")]
public unsafe class GStreamerMultiOutputTests
{
    private static readonly VideoFormat Video = new(320, 240, PixelFormat.Bgra32, new Rational(25, 1));
    private static readonly AudioFormat Audio = new(44100, 1, SampleFormat.S16);

    [GStreamerFact]
    public void OneEncodingGoesIntoSeveralFilesThatEachHoldTheWholeRecording()
    {
        using var media = new TestMedia();
        var mp4 = media.PathTo("a.mp4");
        var mkv = media.PathTo("b.mkv");
        var playlist = media.PathTo("c.m3u8");

        using (var recorder = new GStreamerRecorder(mp4))
        {
            recorder.AddOutput(mkv);
            recorder.AddOutput(playlist);
            recorder.Outputs.Should().Equal(mp4, mkv, playlist);
            var video = recorder.AddVideoStream(new VideoEncodingSettings(Video, MediaCodec.H264, 0, 25));
            var audio = recorder.AddAudioStream(new AudioEncodingSettings(Audio, MediaCodec.Aac));
            recorder.Encoders.Should().HaveCount(2, "one encoder for each stream, however many outputs");
            recorder.Start();

            // Everything of one stream before the other, which is the awkward order for the muxers behind a tee.
            WriteVideo(recorder, video, seconds: 2);
            WriteAudio(recorder, audio, seconds: 2);
            recorder.Stop();
        }

        foreach (var file in new[] { mp4, mkv })
        {
            var info = new FFmpegProber().Probe(file);
            info.OfKind(MediaStreamKind.Video).Single().Codec.Should().Be(MediaCodec.H264, file);
            info.OfKind(MediaStreamKind.Audio).Single().Codec.Should().Be(MediaCodec.Aac, file);
            info.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(150), file);
            FfmpegProgram.Count(file, string.Empty).Should().Be(50, file);
        }

        File.ReadAllText(playlist).Should().Contain("#EXT-X-ENDLIST");
        FfmpegProgram.Count(playlist, string.Empty).Should().Be(50, "every frame is in a segment of the playlist too");
        Directory.GetFiles(media.PathTo(string.Empty)).Should().OnlyContain(f => !Path.GetFileName(f).Contains("partial"), "nothing is left half written");
    }

    [GStreamerFact]
    public void ASrtOutputNobodyListensToDoesNotHoldTheFileBack()
    {
        using var media = new TestMedia();
        var file = media.PathTo("kept.mp4");
        var clock = Stopwatch.StartNew();

        using (var recorder = new GStreamerRecorder(file))
        {
            recorder.AddOutput($"srt://127.0.0.1:{FreePort()}?mode=listener");
            var video = recorder.AddVideoStream(new VideoEncodingSettings(Video, MediaCodec.H264, 0, 25));
            recorder.Start();
            WriteVideo(recorder, video, seconds: 2);
            recorder.Stop();
        }

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "the recording does not wait for a receiver that never comes");
        FfmpegProgram.Count(file, string.Empty).Should().Be(50);
    }

    [GStreamerFact]
    public void AFileAndAnSrtReceiverGetTheSameRecording()
    {
        using var media = new TestMedia();
        var file = media.PathTo("both.mp4");
        var received = media.PathTo("received.ts");
        var port = FreePort();

        using (var recorder = new GStreamerRecorder(file))
        {
            recorder.AddOutput($"srt://127.0.0.1:{port}?mode=listener");
            var video = recorder.AddVideoStream(new VideoEncodingSettings(Video, MediaCodec.H264, 0, 25));
            recorder.Start();

            using var receiver = GStreamerPipeline.Parse($"srtsrc uri=srt://127.0.0.1:{port}?mode=caller ! filesink name=out");
            receiver.FindElement("out")!.Set("location", received);
            receiver.FindElement("out")!.Set("buffer-mode", "unbuffered");
            receiver.Play();
            Thread.Sleep(500);

            WriteVideo(recorder, video, seconds: 2);
            recorder.Stop();
            var last = -1L;
            for (var i = 0; i < 50; i++)
            {
                Thread.Sleep(100);
                var size = File.Exists(received) ? new FileInfo(received).Length : 0;
                if (size > 0 && size == last)
                {
                    break;
                }

                last = size;
            }
        }

        FfmpegProgram.Count(file, string.Empty).Should().Be(50);
        FfmpegProgram.Count(received, string.Empty).Should().Be(50, "the receiver was there from the start");
    }

    [GStreamerFact]
    public void AStartThatFailsLeavesNoFileOfAnyOutput()
    {
        using var media = new TestMedia();
        var good = media.PathTo("good.mp4");
        var bad = media.PathTo(Path.Combine("no-such-directory", "bad.mkv"));

        using var recorder = new GStreamerRecorder(good);
        recorder.AddOutput(bad);
        recorder.AddVideoStream(new VideoEncodingSettings(Video, MediaCodec.H264));
        var act = () => recorder.Start();

        act.Should().Throw<MediaToolkitNetException>();
        recorder.IsRecording.Should().BeFalse();
        Directory.GetFiles(media.PathTo(string.Empty)).Should().BeEmpty("neither the good output nor a partial one stays");
    }

    [GStreamerFact]
    public void AnUnstoppedRecordingLeavesNoOutput()
    {
        using var media = new TestMedia();
        using (var recorder = new GStreamerRecorder(media.PathTo("x.mp4")))
        {
            recorder.AddOutput(media.PathTo("y.mkv"));
            var video = recorder.AddVideoStream(new VideoEncodingSettings(Video, MediaCodec.H264));
            recorder.Start();
            WriteVideo(recorder, video, seconds: 1);
        }

        Directory.GetFiles(media.PathTo(string.Empty)).Should().BeEmpty();
    }

    [GStreamerFact]
    public void OutputsThatCannotBeWrittenTogetherAreRefusedByName()
    {
        using var media = new TestMedia();
        using var recorder = new GStreamerRecorder(media.PathTo("a.mp4"));
        var twice = () => recorder.AddOutput(media.PathTo("a.mp4"));
        twice.Should().Throw<ArgumentException>("the same file cannot be written twice");

        recorder.AddOutput(media.PathTo("sound.wav"));
        recorder.AddVideoStream(new VideoEncodingSettings(Video, MediaCodec.H264));
        recorder.AddAudioStream(new AudioEncodingSettings(Audio, MediaCodec.Aac));
        var start = () => recorder.Start();
        start.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain(".wav");

        using var other = new GStreamerRecorder(media.PathTo("b.mp4"));
        other.AddOutput("gopher://example.invalid/x");
        other.AddVideoStream(new VideoEncodingSettings(Video, MediaCodec.H264));
        var unknown = () => other.Start();
        unknown.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("gopher");
        other.Dispose();

        using var running = new GStreamerRecorder(media.PathTo("c.mp4"));
        running.AddVideoStream(new VideoEncodingSettings(Video, MediaCodec.H264));
        running.Start();
        var late = () => running.AddOutput(media.PathTo("d.mp4"));
        late.Should().Throw<InvalidOperationException>("the outputs are fixed once it records");
    }

    private static void WriteVideo(GStreamerRecorder recorder, int stream, int seconds)
    {
        var data = new byte[Video.Width * Video.Height * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = 30;
            data[i + 1] = 100;
            data[i + 2] = 200;
            data[i + 3] = 255;
        }

        fixed (byte* p = data)
        {
            for (var i = 0; i < seconds * 25; i++)
            {
                recorder.WriteVideo(stream, new VideoFrame(Video, TimeSpan.FromMilliseconds(40 * i), p, Video.Width * 4));
            }
        }
    }

    private static void WriteAudio(GStreamerRecorder recorder, int stream, int seconds)
    {
        var block = new short[441];
        fixed (short* p = block)
        {
            for (var i = 0; i < seconds * 100; i++)
            {
                for (var n = 0; n < block.Length; n++)
                {
                    block[n] = (short)(Math.Sin(2 * Math.PI * 440 * ((i * 441L) + n) / 44100) * 8000);
                }

                recorder.WriteAudio(stream, new AudioFrame(Audio, TimeSpan.FromMilliseconds(10 * i), block.Length, (byte*)p));
            }
        }
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
