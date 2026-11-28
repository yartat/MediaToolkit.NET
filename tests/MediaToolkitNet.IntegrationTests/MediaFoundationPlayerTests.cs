#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using FluentAssertions.Execution;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Playback;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Windows;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>A fact that needs Windows with a Media Foundation video encoder, to make the media the player is given.</summary>
public sealed class MediaPlayerFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="MediaPlayerFactAttribute"/> class.</summary>
    public MediaPlayerFactAttribute() =>
        Skip = !OperatingSystem.IsWindows() ? "The Media Foundation player is Windows only."
            : !MediaFoundationRecorder.HasVideoEncoder() ? "There is no video encoder to make the test media with."
            : null;
}

/// <summary>
/// The player on the Media Engine, with a two-second file of one colour and a 440 Hz tone. Frames come back from the engine's
/// frame server; the sound is played by Windows and heard by capturing this process's own output.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public unsafe class MediaFoundationPlayerTests
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(200);
    private static readonly (int R, int G, int B) Colour = (200, 100, 30);

    [MediaPlayerFact]
    public void AnOpenFileIsStoppedAtItsStartWithItsDuration()
    {
        using var media = new TestMedia();
        using var player = new MediaFoundationPlayer { Volume = 0 };

        player.State.Should().Be(PlaybackState.Closed);
        player.Open(Movie(media));

        player.State.Should().Be(PlaybackState.Stopped);
        player.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(2), Tolerance);
        player.Position.Should().BeCloseTo(TimeSpan.Zero, Tolerance);
        player.CanSeek.Should().BeTrue();
        player.Backend.Should().Be("windows");
    }

    [MediaPlayerFact]
    public void PlayingDeliversThePictureInRealTimeAndEnds()
    {
        using var media = new TestMedia();
        using var player = new MediaFoundationPlayer { Volume = 0 };
        var frames = new List<(int Width, int Height, PixelFormat Format, double Seconds, int R, int G, int B)>();
        var states = new List<PlaybackState>();
        var ended = new ManualResetEventSlim();
        player.VideoFrameDecoded += (in VideoFrame f) =>
        {
            var plane = f.GetPlane(0);
            var at = (120 * f.Stride(0)) + (160 * 4);
            lock (frames)
            {
                frames.Add((f.Format.Width, f.Format.Height, f.Format.PixelFormat, f.Timestamp.TotalSeconds, plane[at + 2], plane[at + 1], plane[at]));
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
                ended.Set();
            }
        };
        player.Open(Movie(media));

        var clock = Stopwatch.StartNew();
        player.Play();
        ended.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue("a two-second file ends");
        clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(1700), "it is played, not just decoded");

        using var scope = new AssertionScope();
        lock (frames)
        {
            frames.Count.Should().BeInRange(35, 51, "two seconds at 25 frames a second; a few may be passed over under load");
            frames.Should().OnlyContain(f => f.Width == 320 && f.Height == 240 && f.Format == PixelFormat.Bgra32);
            frames.Select(f => f.Seconds).Should().BeInAscendingOrder();
            var middle = frames[frames.Count / 2];
            middle.R.Should().BeCloseTo(Colour.R, 24);
            middle.G.Should().BeCloseTo(Colour.G, 24);
            middle.B.Should().BeCloseTo(Colour.B, 24);
        }

        lock (states)
        {
            states.Should().Equal(PlaybackState.Stopped, PlaybackState.Playing, PlaybackState.Ended);
        }
    }

    [MediaPlayerFact]
    public void ASeekMovesWhereThePictureStartsAndPauseHoldsIt()
    {
        using var media = new TestMedia();
        using var player = new MediaFoundationPlayer { Volume = 0 };
        var seen = new List<double>();
        player.VideoFrameDecoded += (in VideoFrame f) =>
        {
            lock (seen)
            {
                seen.Add(f.Timestamp.TotalSeconds);
            }
        };
        player.Open(Movie(media));

        player.Seek(TimeSpan.FromSeconds(1.2));
        player.Position.Should().BeCloseTo(TimeSpan.FromSeconds(1.2), Tolerance);
        player.Play();
        WaitUntil(() => Count(seen) >= 3);
        player.Pause();

        player.State.Should().Be(PlaybackState.Paused);
        lock (seen)
        {
            seen.Should().NotBeEmpty().And.OnlyContain(s => s > 1.0, "nothing from before the place seeked to");
        }

        var held = Count(seen);
        Thread.Sleep(400);
        Count(seen).Should().BeInRange(held, held + 1, "a frame may have been on its way when the pause came");

        player.Stop();
        player.State.Should().Be(PlaybackState.Stopped);
        player.Position.Should().BeCloseTo(TimeSpan.Zero, Tolerance);
    }

    [MediaPlayerFact]
    public void AfterTheEndPlayStartsOverAndASeekWaitsPaused()
    {
        using var media = new TestMedia();
        using var player = new MediaFoundationPlayer { Volume = 0 };
        var ends = new SemaphoreSlim(0);
        var first = double.NaN;
        player.VideoFrameDecoded += (in VideoFrame f) =>
        {
            if (double.IsNaN(first))
            {
                first = f.Timestamp.TotalSeconds;
            }
        };
        player.StateChanged += (_, e) =>
        {
            if (e.Current == PlaybackState.Ended)
            {
                ends.Release();
            }
        };
        player.Open(Movie(media));
        player.Play();
        ends.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
        player.Position.Should().BeCloseTo(player.Duration, Tolerance);

        first = double.NaN;
        player.Play();
        WaitUntil(() => !double.IsNaN(first));
        first.Should().BeLessThan(0.5, "playing from the end starts over");
        ends.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue("the second run ends too");

        player.Seek(TimeSpan.FromSeconds(1));
        player.State.Should().Be(PlaybackState.Paused, "positioned and waiting, not running off by itself");
        first = double.NaN;
        player.Play();
        WaitUntil(() => !double.IsNaN(first));
        first.Should().BeGreaterThan(0.8, "playing goes on from the place seeked to");
    }

    [MediaPlayerFact]
    public void TheSoundIsPlayedAtTheVolumeSet()
    {
        using var media = new TestMedia();
        var file = Movie(media, seconds: 4);
        using var listener = new Tone(440);
        using var player = new MediaFoundationPlayer { Volume = 0.25 };
        player.Open(file);

        listener.Amplitude(TimeSpan.FromMilliseconds(500)).Should().BeLessThan(0.001, "nothing plays yet");
        player.Play();
        Thread.Sleep(500);
        var loud = listener.Amplitude(TimeSpan.FromMilliseconds(500));
        player.Volume = 0;
        Thread.Sleep(300);
        var silent = listener.Amplitude(TimeSpan.FromMilliseconds(500));

        loud.Should().BeGreaterThan(0.02, "the 440 Hz tone reaches Windows");
        silent.Should().BeLessThan(loud / 20, "volume 0 silences it");
    }

    [MediaPlayerFact]
    public void VolumeIsKeptWithinZeroAndOne()
    {
        using var player = new MediaFoundationPlayer();

        player.Volume = 7;
        player.Volume.Should().Be(1.0);
        player.Volume = -1;
        player.Volume.Should().Be(0.0);
        player.Volume = 0.5;
        player.Volume.Should().Be(0.5);
    }

    [MediaPlayerFact]
    public void AFileThatIsNotThereIsRefusedWithItsName()
    {
        using var player = new MediaFoundationPlayer();

        var act = () => player.Open(@"C:\nonexistent\mediatoolkitnet-missing.mp4");

        act.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("mediatoolkitnet-missing");
        player.State.Should().NotBe(PlaybackState.Playing);
        var play = () => player.Play();
        play.Should().Throw<InvalidOperationException>("nothing is open");
    }

    [MediaPlayerFact]
    public void ADisposedPlayerLetsGoWhilePlayingAndFromItsOwnHandler()
    {
        using var media = new TestMedia();
        var path = Movie(media);
        for (var i = 0; i < 6; i++)
        {
            var player = new MediaFoundationPlayer { Volume = 0 };
            player.VideoFrameDecoded += (in VideoFrame _) => { };
            player.Open(path);
            player.Play();
            Thread.Sleep(150);
            player.Dispose();
            player.State.Should().Be(PlaybackState.Closed);
        }

        var inside = new MediaFoundationPlayer { Volume = 0 };
        var disposed = new ManualResetEventSlim();
        inside.VideoFrameDecoded += (in VideoFrame _) =>
        {
            inside.Dispose();
            disposed.Set();
        };
        inside.Open(path);
        inside.Play();
        disposed.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        inside.State.Should().Be(PlaybackState.Closed);
    }

    [MediaPlayerFact]
    public void TheBackendMakesAPlayerButIsNotTheDefaultOne()
    {
        WindowsBackend.Instance.Capabilities.Should().HaveFlag(BackendCapabilities.Playback);
        using var player = WindowsBackend.Instance.CreatePlayer();
        player.Should().BeOfType<MediaFoundationPlayer>();
        MediaToolkitNet.MediaToolkitNetBackends.Select(BackendCapabilities.Playback)?.Name.Should().NotBe("windows");
    }

    private static string Movie(TestMedia media, int seconds = 2)
    {
        var path = media.PathTo($"movie{seconds}.mp4");
        var video = new VideoFormat(320, 240, PixelFormat.Bgra32, new Rational(25, 1));
        var audio = new AudioFormat(48000, 2, SampleFormat.S16);
        using var recorder = new MediaFoundationRecorder(path);
        var v = recorder.AddVideoStream(new VideoEncodingSettings(video, MediaCodec.H264, 1_000_000, 25));
        var a = recorder.AddAudioStream(new AudioEncodingSettings(audio, MediaCodec.Aac, 128_000));
        recorder.Start();

        var picture = new byte[320 * 240 * 4];
        for (var i = 0; i < picture.Length; i += 4)
        {
            picture[i] = (byte)Colour.B;
            picture[i + 1] = (byte)Colour.G;
            picture[i + 2] = (byte)Colour.R;
            picture[i + 3] = 255;
        }

        var tone = new short[1920 * 2];
        fixed (byte* p = picture)
        fixed (short* t = tone)
        {
            for (var frame = 0; frame < seconds * 25; frame++)
            {
                for (var s = 0; s < 1920; s++)
                {
                    var value = (short)(Math.Sin(2 * Math.PI * 440 * (((long)frame * 1920) + s) / 48000) * 12000);
                    tone[s * 2] = value;
                    tone[(s * 2) + 1] = value;
                }

                var at = TimeSpan.FromMilliseconds(40 * frame);
                recorder.WriteVideo(v, new VideoFrame(video, at, p, 320 * 4));
                recorder.WriteAudio(a, new AudioFrame(audio, at, 1920, (byte*)t));
            }
        }

        recorder.Stop();
        return path;
    }

    private static int Count<T>(List<T> list)
    {
        lock (list)
        {
            return list.Count;
        }
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && !condition())
        {
            Thread.Sleep(10);
        }
    }

    /// <summary>Captures what this process plays and measures one frequency in it.</summary>
    private sealed class Tone : IDisposable
    {
        private readonly WasapiCapture _capture;
        private readonly List<float> _samples = [];
        private readonly double _frequency;

        public Tone(double frequency)
        {
            _frequency = frequency;
            _capture = WasapiCapture.ForProcess(Environment.ProcessId);
            _capture.FrameCaptured += OnFrame;
            _capture.Start();
        }

        /// <summary>The amplitude of the tone heard during the next <paramref name="span"/>.</summary>
        public double Amplitude(TimeSpan span)
        {
            lock (_samples)
            {
                _samples.Clear();
            }

            Thread.Sleep(span);
            float[] data;
            lock (_samples)
            {
                data = [.. _samples];
            }

            if (data.Length == 0)
            {
                return 0;
            }

            double real = 0, imaginary = 0;
            for (var n = 0; n < data.Length; n++)
            {
                var angle = 2 * Math.PI * _frequency * n / _capture.Format.SampleRate;
                real += data[n] * Math.Cos(angle);
                imaginary += data[n] * Math.Sin(angle);
            }

            return 2 * Math.Sqrt((real * real) + (imaginary * imaginary)) / data.Length;
        }

        public void Dispose() => _capture.Dispose();

        private void OnFrame(in AudioFrame frame)
        {
            var values = MemoryMarshal.Cast<byte, float>(frame.Interleaved);
            lock (_samples)
            {
                for (var i = 0; i < values.Length; i += frame.Format.Channels)
                {
                    _samples.Add(values[i]);
                }
            }
        }
    }
}
