#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using System.Runtime.Versioning;
using FluentAssertions;
using FluentAssertions.Execution;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Playback;
using MediaToolkitNet.GStreamer;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// The GStreamer player on a one-second file with a picture and a sound, written by the FFmpeg recorder so that the
/// player is not judged on its own output. The player decodes and hands frames back, like FFmpeg's: a test plays in real
/// time, because the sinks wait on the clock.
/// </summary>
[SupportedOSPlatform("linux")]
public class GStreamerPlayerTests
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(150);

    [GStreamerFact]
    public void AnOpenFileIsStoppedAtItsStartWithItsDuration()
    {
        using var media = new TestMedia();
        using var player = new GStreamerPlayer();

        player.State.Should().Be(PlaybackState.Closed);
        player.Open(media.WriteVideoAndAudio("movie.mkv"));

        player.State.Should().Be(PlaybackState.Stopped);
        player.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(1), Tolerance);
        player.Position.Should().Be(TimeSpan.Zero);
        player.CanSeek.Should().BeTrue();
        player.Backend.Should().Be("gstreamer");
    }

    [GStreamerFact]
    public void PlayingDeliversEveryFrameInRealTimeAndEnds()
    {
        using var media = new TestMedia();
        using var player = new GStreamerPlayer();
        var video = new List<(int Width, int Height, PixelFormat Format, double Seconds)>();
        long samples = 0;
        var audioFormat = default(AudioFormat);
        var states = new List<PlaybackState>();
        var ended = new ManualResetEventSlim();
        player.VideoFrameDecoded += (in VideoFrame f) =>
        {
            lock (video)
            {
                video.Add((f.Format.Width, f.Format.Height, f.Format.PixelFormat, f.Timestamp.TotalSeconds));
            }
        };
        player.AudioFrameDecoded += (in AudioFrame f) =>
        {
            Interlocked.Add(ref samples, f.SampleCount);
            audioFormat = f.Format;
        };
        player.StateChanged += (_, e) =>
        {
            lock (states)
            {
                states.Add(e.Current);
            }

            if (e.Current == PlaybackState.Ended)
            {
                ended.Set();
            }
        };
        player.Open(media.WriteVideoAndAudio("movie.mkv"));

        var clock = Stopwatch.StartNew();
        player.Play();
        ended.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("a one-second file ends");
        clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(700), "the sinks keep to the clock rather than decoding as fast as they can");

        using var scope = new AssertionScope();
        lock (video)
        {
            video.Should().HaveCount(25);
            video.Should().OnlyContain(v => v.Width == 320 && v.Height == 240 && v.Format == PixelFormat.Bgra32);
            video.Select(v => v.Seconds).Should().BeInAscendingOrder();
            video[0].Seconds.Should().BeLessThan(0.1);
        }

        // AC-3 codes 1536 samples at a time, so a second of 48 kHz is written as 32 such frames, 49152 samples.
        Interlocked.Read(ref samples).Should().Be(49152, "every sample of the file arrives, none twice and none dropped");
        audioFormat.Channels.Should().Be(2);
        audioFormat.SampleFormat.Should().Be(SampleFormat.S16);
        lock (states)
        {
            states.Should().Equal(PlaybackState.Stopped, PlaybackState.Playing, PlaybackState.Ended);
        }
    }

    [GStreamerFact]
    public void ASeekMovesWhereTheFramesStart()
    {
        using var media = new TestMedia();
        using var player = new GStreamerPlayer();
        var first = double.NaN;
        var count = 0;
        var ended = new ManualResetEventSlim();
        player.VideoFrameDecoded += (in VideoFrame f) =>
        {
            if (double.IsNaN(first))
            {
                first = f.Timestamp.TotalSeconds;
            }

            Interlocked.Increment(ref count);
        };
        player.StateChanged += (_, e) =>
        {
            if (e.Current == PlaybackState.Ended)
            {
                ended.Set();
            }
        };
        player.Open(media.WriteVideoAndAudio("movie.mkv"));

        player.Seek(TimeSpan.FromSeconds(0.6));
        player.Position.Should().BeCloseTo(TimeSpan.FromSeconds(0.6), Tolerance);
        player.Play();
        ended.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        first.Should().BeInRange(0.5, 0.7);
        count.Should().BeInRange(9, 12, "what is left of a second of 25 frames after 0.6 s");
    }

    [GStreamerFact]
    public void AfterTheEndThePositionIsTheEndAndPauseAndStopBehave()
    {
        using var media = new TestMedia();
        using var player = new GStreamerPlayer();
        var ended = new ManualResetEventSlim();
        player.StateChanged += (_, e) =>
        {
            if (e.Current == PlaybackState.Ended)
            {
                ended.Set();
            }
        };
        player.Open(media.WriteVideoAndAudio("movie.mkv"));
        player.Play();
        ended.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        player.State.Should().Be(PlaybackState.Ended);
        player.Position.Should().BeCloseTo(player.Duration, Tolerance, "the position stays where the stream ended");
        player.Position.Should().BeGreaterThan(TimeSpan.FromMilliseconds(800));

        player.Pause();
        player.State.Should().Be(PlaybackState.Ended, "there is nothing playing to pause");

        player.Stop();
        player.State.Should().Be(PlaybackState.Stopped);
        player.Position.Should().BeCloseTo(TimeSpan.Zero, Tolerance, "stopping rewinds, from the end as from anywhere");
    }

    [GStreamerFact]
    public void PlayingAgainAfterTheEndStartsOverAndEndsAgain()
    {
        using var media = new TestMedia();
        using var player = new GStreamerPlayer();
        var firstFrames = new List<double>();
        var frames = firstFrames;
        var states = new List<PlaybackState>();
        var ended = new SemaphoreSlim(0);
        player.VideoFrameDecoded += (in VideoFrame f) =>
        {
            lock (firstFrames)
            {
                frames.Add(f.Timestamp.TotalSeconds);
            }
        };
        player.StateChanged += (_, e) =>
        {
            lock (states)
            {
                states.Add(e.Current);
            }

            if (e.Current == PlaybackState.Ended)
            {
                ended.Release();
            }
        };
        player.Open(media.WriteVideoAndAudio("movie.mkv"));

        player.Play();
        ended.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the first run ends");
        List<double> second;
        lock (firstFrames)
        {
            second = [];
            frames = second;
        }

        player.Play();
        ended.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the second run ends as well");

        using var scope = new AssertionScope();
        lock (firstFrames)
        {
            firstFrames.Should().HaveCount(25);
            second.Should().HaveCount(25, "the whole file plays again, not what was left of it");
            second[0].Should().BeLessThan(0.1, "it starts from the beginning");
            second.Should().BeInAscendingOrder();
        }

        lock (states)
        {
            states.Should().Equal(PlaybackState.Stopped, PlaybackState.Playing, PlaybackState.Ended, PlaybackState.Playing, PlaybackState.Ended);
        }

        player.Position.Should().BeCloseTo(player.Duration, Tolerance);
    }

    [GStreamerFact]
    public void ASeekAfterTheEndWorksAndPlayContinuesFromThere()
    {
        using var media = new TestMedia();
        using var player = new GStreamerPlayer();
        var first = double.NaN;
        var count = 0;
        var ended = new SemaphoreSlim(0);
        player.VideoFrameDecoded += (in VideoFrame f) =>
        {
            if (double.IsNaN(first))
            {
                first = f.Timestamp.TotalSeconds;
            }

            Interlocked.Increment(ref count);
        };
        player.StateChanged += (_, e) =>
        {
            if (e.Current == PlaybackState.Ended)
            {
                ended.Release();
            }
        };
        player.Open(media.WriteVideoAndAudio("movie.mkv"));
        player.Play();
        ended.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        first = double.NaN;
        count = 0;

        player.Seek(TimeSpan.FromSeconds(0.6));
        player.Position.Should().BeCloseTo(TimeSpan.FromSeconds(0.6), Tolerance);
        player.State.Should().Be(PlaybackState.Paused, "the media is positioned and waits, it does not run off by itself");
        Thread.Sleep(300);
        Volatile.Read(ref count).Should().Be(0, "nothing plays until Play is called");
        player.Play();
        ended.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        first.Should().BeInRange(0.5, 0.7, "playing went on from the place seeked to, not from the start");
        count.Should().BeInRange(9, 12);
    }

    [GStreamerFact]
    public void AFaultedPlayerRefusesToPlayOrSeekAndOpeningAgainRecoversIt()
    {
        using var media = new TestMedia();
        var file = media.WriteVideoAndAudio("movie.mkv");
        using var player = new GStreamerPlayer { VideoSink = "identity error-after=3 ! fakesink", AudioSink = "fakesink" };
        var failures = new List<string>();
        var faulted = new ManualResetEventSlim();
        player.Failed += (_, e) =>
        {
            lock (failures)
            {
                failures.Add(e.Message);
            }
        };
        player.StateChanged += (_, e) =>
        {
            if (e.Current == PlaybackState.Faulted)
            {
                faulted.Set();
            }
        };
        player.Open(file);

        player.Play();
        faulted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the sink fails after three buffers");

        player.State.Should().Be(PlaybackState.Faulted);
        lock (failures)
        {
            failures.Should().ContainSingle().Which.Should().Contain("identity");
        }

        var seek = () => player.Seek(TimeSpan.FromSeconds(0.5));
        var play = () => player.Play();
        seek.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("faulted");
        play.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("faulted");
        player.Pause();
        player.Stop();
        player.State.Should().Be(PlaybackState.Faulted, "only opening the media again recovers a player");

        player.VideoSink = null;
        player.AudioSink = null;
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
        player.Open(file);
        player.State.Should().Be(PlaybackState.Stopped);
        player.Play();
        ended.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        frames.Should().Be(25);
    }

    [GStreamerFact]
    public void AStreamWithNoLengthCannotSeekOrStartOverAtItsEnd()
    {
        using var media = new TestMedia();
        var fifo = media.PathTo("live.mkv");
        using (var make = System.Diagnostics.Process.Start("mkfifo", fifo)!)
        {
            make.WaitForExit();
            make.ExitCode.Should().Be(0);
        }

        // A matroska stream written to a pipe has no duration in its header and cannot be seeked.
        using var writer = FfmpegProgram.Start($"-f lavfi -i testsrc=size=160x120:rate=10 -t 2 -c:v mjpeg -f matroska \"{fifo}\"");
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
        player.Open(fifo);

        player.Duration.Should().Be(TimeSpan.Zero);
        player.CanSeek.Should().BeFalse();
        var seek = () => player.Seek(TimeSpan.FromSeconds(1));
        seek.Should().Throw<InvalidOperationException>("a stream with no length cannot seek");

        player.Play();
        ended.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue("the writer closes the pipe after two seconds");
        frames.Should().BeInRange(15, 21);

        player.State.Should().Be(PlaybackState.Ended);
        seek.Should().Throw<InvalidOperationException>("nor can it at the end");
        player.State.Should().Be(PlaybackState.Ended, "a refused seek leaves the state alone");
        var again = () => player.Play();
        again.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("start over");
        player.State.Should().Be(PlaybackState.Ended);
    }

    [GStreamerFact]
    public void PausingHoldsTheFramesAndPlayingGoesOn()
    {
        using var media = new TestMedia();
        using var player = new GStreamerPlayer();
        var count = 0;
        player.VideoFrameDecoded += (in VideoFrame _) => Interlocked.Increment(ref count);
        player.Open(media.WriteVideoAndAudio("movie.mkv"));

        player.Play();
        WaitUntil(() => Volatile.Read(ref count) >= 3);
        player.Pause();
        player.State.Should().Be(PlaybackState.Paused);
        var held = Volatile.Read(ref count);
        Thread.Sleep(400);

        Volatile.Read(ref count).Should().BeInRange(held, held + 1, "a frame may have been on its way when the pause came");
        player.Play();
        WaitUntil(() => Volatile.Read(ref count) >= held + 5);
        Volatile.Read(ref count).Should().BeGreaterThanOrEqualTo(held + 5);
    }

    [GStreamerFact]
    public void StoppingRewindsAndKeepsTheMediaOpen()
    {
        using var media = new TestMedia();
        using var player = new GStreamerPlayer();
        player.Open(media.WriteVideoAndAudio("movie.mkv"));
        player.Play();
        Thread.Sleep(300);

        player.Stop();

        player.State.Should().Be(PlaybackState.Stopped);
        player.Position.Should().BeCloseTo(TimeSpan.Zero, Tolerance);
        var again = () => player.Play();
        again.Should().NotThrow("stopped media plays again");
    }

    [GStreamerFact]
    public void VolumeIsKeptWithinZeroAndOne()
    {
        using var media = new TestMedia();
        using var player = new GStreamerPlayer();
        player.Open(media.WriteVideoAndAudio("movie.mkv"));

        player.Volume = 0.25;
        player.Volume.Should().BeApproximately(0.25, 0.001);
        player.Volume = 7;
        player.Volume.Should().Be(1.0);
        player.Volume = -1;
        player.Volume.Should().Be(0.0);
    }

    [GStreamerFact]
    public void AFileThatIsNotThereIsRefusedWithItsName()
    {
        using var player = new GStreamerPlayer();

        var act = () => player.Open("/nonexistent/mediatoolkitnet-missing.mkv");

        act.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("mediatoolkitnet-missing");
        player.State.Should().NotBe(PlaybackState.Playing);
    }

    [GStreamerFact]
    public void ADisposedPlayerLetsGoWhilePlaying()
    {
        using var media = new TestMedia();
        var path = media.WriteVideoAndAudio("movie.mkv");
        for (var i = 0; i < 8; i++)
        {
            var player = new GStreamerPlayer();
            player.VideoFrameDecoded += (in VideoFrame _) => { };
            player.Open(path);
            player.Play();
            Thread.Sleep(60);
            player.Dispose();
            player.State.Should().Be(PlaybackState.Closed);
        }
    }

    [GStreamerFact]
    public void TheBackendMakesAPlayerButIsNotTheDefaultOne()
    {
        GStreamerBackend.Instance.Capabilities.Should().HaveFlag(BackendCapabilities.Playback);
        using var player = GStreamerBackend.Instance.CreatePlayer();
        player.Should().BeOfType<GStreamerPlayer>();
        MediaToolkitNet.MediaToolkitNetBackends.Select(BackendCapabilities.Playback)?.Name.Should().NotBe("gstreamer");
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !condition())
        {
            Thread.Sleep(10);
        }
    }
}
