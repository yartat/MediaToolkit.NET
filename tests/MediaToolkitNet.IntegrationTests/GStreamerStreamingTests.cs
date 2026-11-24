#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Playback;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.GStreamer;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// The recorder writing to a stream instead of a file: an HLS playlist with its segments, and SRT to a receiver that
/// connects over the loopback. What was sent is read back by something that is not the recorder: ffmpeg for the segments,
/// a plain <c>srtsrc</c> pipeline for the stream.
/// </summary>
[SupportedOSPlatform("linux")]
public unsafe class GStreamerStreamingTests
{
    private static readonly (int R, int G, int B) Colour = (200, 100, 30);

    [GStreamerFact]
    public void AnM3u8OutputIsAPlaylistOfSegmentsThatEndsAndPlaysBack()
    {
        using var media = new TestMedia();
        var playlist = media.PathTo("live.m3u8");
        var video = new VideoFormat(320, 240, PixelFormat.Bgra32, new Rational(10, 1));
        var audio = new AudioFormat(44100, 1, SampleFormat.S16);

        using (var recorder = new GStreamerRecorder(playlist) { SegmentDuration = TimeSpan.FromSeconds(2) })
        {
            var v = recorder.AddVideoStream(new VideoEncodingSettings(video, MediaCodec.H264, 0, 10));
            var a = recorder.AddAudioStream(new AudioEncodingSettings(audio, MediaCodec.Aac));
            recorder.Start();
            WriteFrames(recorder, v, video, seconds: 6);
            WriteTone(recorder, a, audio, seconds: 6);
            recorder.Stop();
        }

        var text = File.ReadAllText(playlist);
        text.Should().Contain("#EXTM3U").And.Contain("#EXT-X-ENDLIST", "the recording is finished, so the playlist says so");
        var durations = Regex.Matches(text, @"#EXTINF:([\d.]+)").Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        durations.Count.Should().BeInRange(3, 4, "six seconds in segments of about two");
        durations.SkipLast(1).Should().OnlyContain(d => d > 1.0 && d < 3.5, "every segment but the last, which holds what is left, is about two seconds");
        durations.Last().Should().BeInRange(0.1, 3.5);
        durations.Sum().Should().BeApproximately(6.0, 0.5);
        Directory.GetFiles(media.PathTo(string.Empty), "live_*.ts").Should().HaveCount(durations.Count);

        FfmpegProgram.Count(playlist, string.Empty).Should().Be(60, "every frame is in some segment");

        using var player = new GStreamerPlayer();
        var frames = 0;
        var ended = new ManualResetEventSlim();
        player.VideoFrameDecoded += (in VideoFrame _) => Interlocked.Increment(ref frames);
        player.StateChanged += (_, e) =>
        {
            if (e.Current == PlaybackState.Ended)
            {
                ended.Set();
            }
        };
        player.Open(playlist);
        player.Play();
        ended.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue("the whole playlist plays to its end");
        frames.Should().Be(60);
    }

    [GStreamerFact]
    public void AnSrtOutputReachesAReceiverThatConnectsToItAndDecodes()
    {
        var port = FreePort();
        var video = new VideoFormat(320, 240, PixelFormat.Bgra32, new Rational(25, 1));
        using var media = new TestMedia();
        var dump = media.PathTo("received.ts");

        using var recorder = new GStreamerRecorder($"srt://127.0.0.1:{port}?mode=listener");
        var v = recorder.AddVideoStream(new VideoEncodingSettings(video, MediaCodec.H264, 0, 25));
        recorder.Start();

        // The receiver is a plain srtsrc writing what arrives; ffmpeg then decodes the file, so nothing of this library
        // judges the stream.
        using (var receiver = GStreamerPipeline.Parse($"srtsrc uri=srt://127.0.0.1:{port}?mode=caller ! filesink name=out"))
        {
            receiver.FindElement("out")!.Set("location", dump);
            receiver.FindElement("out")!.Set("buffer-mode", "unbuffered"); // so that the size on disk is what has arrived
            receiver.Play();
            Thread.Sleep(500);

            WriteFrames(recorder, v, video, seconds: 2, fps: 25);
            recorder.Stop();

            // The stream has no end of its own for the receiver: wait for the file to stop growing.
            var last = -1L;
            var steady = 0;
            for (var i = 0; i < 100 && steady < 6; i++)
            {
                Thread.Sleep(100);
                var size = File.Exists(dump) ? new FileInfo(dump).Length : 0;
                steady = size == last && size > 0 ? steady + 1 : 0;
                last = size;
            }
        }

        // The recorder keeps the link open for DrainTime after the last frame, which is what lets the tail arrive.
        FfmpegProgram.Count(dump, string.Empty).Should().Be(50, "every frame of two seconds at 25 frames a second reaches the receiver");

        using var reader = MediaToolkitNet.FFmpeg.Reading.FFmpegMediaReader.Open(dump, new MediaToolkitNet.FFmpeg.Reading.MediaReaderOptions { VideoPixelFormat = PixelFormat.Bgra32 });
        (int R, int G, int B)? pixel = null;
        var size2 = (0, 0);
        reader.Video!.TryReadNext((in VideoFrame f) =>
        {
            size2 = (f.Format.Width, f.Format.Height);
            var plane = f.GetPlane(0);
            var at = (120 * f.Stride(0)) + (160 * 4);
            pixel = (plane[at + 2], plane[at + 1], plane[at]);
        }).Should().BeTrue();
        size2.Should().Be((320, 240));
        pixel!.Value.R.Should().BeCloseTo(Colour.R, 16);
        pixel.Value.G.Should().BeCloseTo(Colour.G, 16);
        pixel.Value.B.Should().BeCloseTo(Colour.B, 16);
    }

    [GStreamerFact]
    public void AnAddressThatIsNoStreamKindIsRefusedNamingWhatIsKnown()
    {
        using var media = new TestMedia();
        var video = new VideoFormat(320, 240, PixelFormat.Bgra32, new Rational(25, 1));
        using var recorder = new GStreamerRecorder("gopher://example.invalid/stream");
        recorder.AddVideoStream(new VideoEncodingSettings(video, MediaCodec.H264));

        var act = () => recorder.Start();

        act.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("gopher").And.Contain("srt");
    }

    private static void WriteFrames(GStreamerRecorder recorder, int stream, VideoFormat format, int seconds, int fps = 10)
    {
        var data = new byte[format.Width * format.Height * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = (byte)Colour.B;
            data[i + 1] = (byte)Colour.G;
            data[i + 2] = (byte)Colour.R;
            data[i + 3] = 255;
        }

        fixed (byte* p = data)
        {
            for (var i = 0; i < seconds * fps; i++)
            {
                recorder.WriteVideo(stream, new VideoFrame(format, TimeSpan.FromSeconds((double)i / fps), p, format.Width * 4));
            }
        }
    }

    private static void WriteTone(GStreamerRecorder recorder, int stream, AudioFormat format, int seconds)
    {
        var block = new short[441];
        var written = 0L;
        fixed (short* p = block)
        {
            while (written < (long)seconds * format.SampleRate)
            {
                for (var s = 0; s < block.Length; s++)
                {
                    block[s] = (short)(Math.Sin(2 * Math.PI * 440 * (written + s) / format.SampleRate) * 8000);
                }

                recorder.WriteAudio(stream, new AudioFrame(format, TimeSpan.FromSeconds((double)written / format.SampleRate), block.Length, (byte*)p));
                written += block.Length;
            }
        }
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
