#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using FluentAssertions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Playback;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.GStreamer;
using MediaToolkitNet.GStreamer.Native;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>Serialises the tests that use the one display and the one sound server of the machine.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealOutputCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Real output";
}

/// <summary>A fact that needs an X display with <c>ximagesink</c> and the ImageMagick and X utilities that read it back.</summary>
public sealed class DisplayFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="DisplayFactAttribute"/> class.</summary>
    public DisplayFactAttribute()
    {
        Skip = !OperatingSystem.IsLinux() ? "The GStreamer backend runs on Linux only."
            : !GStreamerBackend.Instance.IsAvailable ? "GStreamer is not usable here."
            : FFmpegEnvironment.SkipReason
            ?? (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ? "There is no X display."
            : !GStreamerElements.Exists("ximagesink") ? "There is no ximagesink."
            : !ScreenReader.HasTools ? "xwininfo and import (ImageMagick) are needed to look at the window."
            : null);
    }
}

/// <summary>
/// A fact that needs a PulseAudio server whose output can be recorded back: WSLg's, whose sink is <c>RDPSink</c>, so that
/// what a test plays can be heard by a <c>pulsesrc</c> on its monitor.
/// </summary>
public sealed class SoundFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="SoundFactAttribute"/> class.</summary>
    public SoundFactAttribute()
    {
        Skip = !OperatingSystem.IsLinux() ? "The GStreamer backend runs on Linux only."
            : !GStreamerBackend.Instance.IsAvailable ? "GStreamer is not usable here."
            : FFmpegEnvironment.SkipReason
            ?? (Environment.GetEnvironmentVariable("PULSE_SERVER") is not { } server || !server.Contains("wslg", StringComparison.OrdinalIgnoreCase)
                ? "The WSLg sound server (RDPSink) is not here."
                : !GStreamerElements.Exists("pulsesink") || !GStreamerElements.Exists("pulsesrc") || !GStreamerElements.Exists("level")
                    ? "pulsesink, pulsesrc and level are needed."
                    : null);
    }
}

/// <summary>A fact that needs both the display and the sound server of <see cref="DisplayFactAttribute"/> and <see cref="SoundFactAttribute"/>.</summary>
public sealed class DisplayAndSoundFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="DisplayAndSoundFactAttribute"/> class.</summary>
    public DisplayAndSoundFactAttribute() => Skip = new DisplayFactAttribute().Skip ?? new SoundFactAttribute().Skip;
}

/// <summary>
/// The player with its output going somewhere real: into a window of its own, into a window of the caller's, and into the
/// sound server. What reaches the output is read back from outside the library: the window's pixels by ImageMagick from the
/// X server, and the sound by recording the sink's monitor.
/// </summary>
[Collection(RealOutputCollection.Name)]
[SupportedOSPlatform("linux")]
public unsafe class GStreamerOutputTests
{
    private static readonly (int R, int G, int B) Colour = (200, 100, 30);

    [DisplayFact]
    public void ARenderingVideoSinkDrawsThePictureInAWindowOfItsOwn()
    {
        using var media = new TestMedia();
        var file = SolidVideo(media, seconds: 3);
        using var player = new GStreamerPlayer { VideoSink = "ximagesink", AudioSink = "fakesink" };
        var delivered = 0;
        player.VideoFrameDecoded += (in VideoFrame _) => Interlocked.Increment(ref delivered);
        player.Open(file);

        player.Play();
        var window = ScreenReader.WaitForWindow(320, 240);

        window.Should().NotBeNull("the sink opens a window of the picture's size");

        // The window is mapped a moment before the first frame is drawn in it.
        WaitFor(() => ScreenReader.Pixel(window!, 160, 120).R > 100, TimeSpan.FromSeconds(5));
        var pixel = ScreenReader.Pixel(window!, 160, 120);
        pixel.R.Should().BeCloseTo(Colour.R, 16);
        pixel.G.Should().BeCloseTo(Colour.G, 16);
        pixel.B.Should().BeCloseTo(Colour.B, 16);
        delivered.Should().Be(0, "frames go to the sink, not to the event");
    }

    [DisplayFact]
    public void AWindowOfTheCallersReceivesThePictureInsteadOfANewOne()
    {
        using var media = new TestMedia();
        var file = SolidVideo(media, seconds: 3);
        using var own = new XWindow(200, 150);
        using var player = new GStreamerPlayer { VideoSink = "ximagesink", AudioSink = "fakesink", VideoWindowHandle = own.Id };
        player.Open(file);

        player.Play();
        var drawn = WaitFor(() => ScreenReader.Pixel(own.Id.ToString("x", CultureInfo.InvariantCulture), 100, 75).R > 100, TimeSpan.FromSeconds(5));

        drawn.Should().BeTrue("the picture arrives in the window that was handed over");
        var pixel = ScreenReader.Pixel(own.Id.ToString("x", CultureInfo.InvariantCulture), 100, 75);
        pixel.R.Should().BeCloseTo(Colour.R, 16);
        pixel.G.Should().BeCloseTo(Colour.G, 16);
        pixel.B.Should().BeCloseTo(Colour.B, 16);
        ScreenReader.WaitForWindow(320, 240, TimeSpan.FromSeconds(1)).Should().BeNull("no window of the sink's own is opened");
    }

    [SoundFact]
    public void TheAudioSinkReachesTheSoundServerAtTheVolumeSet()
    {
        using var media = new TestMedia();
        var file = media.WriteVideoAndAudio("tone.mkv");
        using var listener = new SoundListener();

        // The sound server still holds the tail of whatever played last; wait for it to run out.
        var silence = 0.0;
        WaitFor(() => (silence = listener.Loudest(TimeSpan.FromMilliseconds(300))) < -50, TimeSpan.FromSeconds(6));
        silence.Should().BeLessThan(-50, "nothing plays yet");

        using var player = new GStreamerPlayer { AudioSink = "pulsesink", VideoSink = "fakesink" };
        player.Open(file);
        player.Volume = 1.0;
        player.Play();
        Thread.Sleep(150);
        var loud = listener.Loudest(TimeSpan.FromMilliseconds(500));

        player.Volume = 0.1;
        Thread.Sleep(100);
        var quiet = listener.Loudest(TimeSpan.FromMilliseconds(250));

        loud.Should().BeGreaterThan(-20, "a 440 Hz tone at a third of full scale is audible on the monitor");
        quiet.Should().BeLessThan(loud - 10, "a tenth of the volume is twenty decibels down");
    }

    [DisplayAndSoundFact]
    public void PlayingAgainAfterTheEndDrawsAndSoundsAgain()
    {
        using var media = new TestMedia();
        var file = TwoColourMovie(media);
        using var listener = new SoundListener();
        var quiet = 0.0;
        WaitFor(() => (quiet = listener.Loudest(TimeSpan.FromMilliseconds(300))) < -50, TimeSpan.FromSeconds(6));
        quiet.Should().BeLessThan(-50);

        using var player = new GStreamerPlayer { VideoSink = "ximagesink", AudioSink = "pulsesink" };
        var ended = new SemaphoreSlim(0);
        player.StateChanged += (_, e) =>
        {
            if (e.Current == PlaybackState.Ended)
            {
                ended.Release();
            }
        };
        player.Open(file);

        player.Play();
        var window = ScreenReader.WaitForWindow(320, 240);
        window.Should().NotBeNull();
        WaitFor(() => ScreenReader.Pixel(window!, 160, 120).R > 150, TimeSpan.FromSeconds(5)).Should().BeTrue("the first half is the red colour");
        listener.Loudest(TimeSpan.FromMilliseconds(400)).Should().BeGreaterThan(-20, "the first run sounds");
        ended.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        // The last picture stays in the window and the sound dies away.
        ScreenReader.Pixel(window!, 160, 120).B.Should().BeGreaterThan(150, "the second half is the blue colour, and it is what is left");
        var silence = 0.0;
        WaitFor(() => (silence = listener.Loudest(TimeSpan.FromMilliseconds(300))) < -50, TimeSpan.FromSeconds(6));
        silence.Should().BeLessThan(-50, "nothing plays at the end");

        player.Play();
        var again = ScreenReader.WaitForWindow(320, 240);
        again.Should().NotBeNull("the picture is still shown");
        WaitFor(() => ScreenReader.Pixel(again!, 160, 120).R > 150, TimeSpan.FromSeconds(5))
            .Should().BeTrue("playing again starts from the first half: red is drawn over the blue that was left");
        listener.Loudest(TimeSpan.FromMilliseconds(400)).Should().BeGreaterThan(-20, "the second run sounds");
        ended.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the second run ends too");
        player.State.Should().Be(PlaybackState.Ended);
    }

    // Two seconds, red and then blue, over a tone: what is drawn tells which half is on show.
    private static string TwoColourMovie(TestMedia media)
    {
        var path = media.PathTo("two.mkv");
        var video = new VideoFormat(320, 240, PixelFormat.Bgra32, new Rational(25, 1));
        var audio = new AudioFormat(44100, 1, SampleFormat.S16);
        using var recorder = new GStreamerRecorder(path);
        var v = recorder.AddVideoStream(new VideoEncodingSettings(video, MediaCodec.Mjpeg));
        var a = recorder.AddAudioStream(new AudioEncodingSettings(audio, MediaCodec.Flac));
        recorder.Start();

        var picture = new byte[320 * 240 * 4];
        for (var frame = 0; frame < 50; frame++)
        {
            var (r, b) = frame < 25 ? (200, 30) : (30, 200);
            for (var i = 0; i < picture.Length; i += 4)
            {
                picture[i] = (byte)b;
                picture[i + 1] = 100;
                picture[i + 2] = (byte)r;
                picture[i + 3] = 255;
            }

            var tone = new short[1764];
            for (var s = 0; s < tone.Length; s++)
            {
                tone[s] = (short)(Math.Sin(2 * Math.PI * 440 * (((long)frame * tone.Length) + s) / 44100) * 12000);
            }

            fixed (byte* p = picture)
            fixed (short* t = tone)
            {
                recorder.WriteVideo(v, new VideoFrame(video, TimeSpan.FromSeconds(frame / 25d), p, 320 * 4));
                recorder.WriteAudio(a, new AudioFrame(audio, TimeSpan.FromSeconds(frame / 25d), tone.Length, (byte*)t));
            }
        }

        recorder.Stop();
        return path;
    }

    // A second of picture that is one colour, long enough that the window is still there when it is read.
    private static string SolidVideo(TestMedia media, int seconds)
    {
        var path = media.PathTo("solid.mkv");
        var format = new VideoFormat(320, 240, PixelFormat.Bgra32, new Rational(25, 1));
        var data = new byte[320 * 240 * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = (byte)Colour.B;
            data[i + 1] = (byte)Colour.G;
            data[i + 2] = (byte)Colour.R;
            data[i + 3] = 255;
        }

        using var recorder = new GStreamerRecorder(path);
        var stream = recorder.AddVideoStream(new VideoEncodingSettings(format, MediaCodec.Mjpeg));
        recorder.Start();
        fixed (byte* p = data)
        {
            for (var i = 0; i < seconds * 25; i++)
            {
                recorder.WriteVideo(stream, new VideoFrame(format, TimeSpan.FromSeconds(i / 25d), p, 320 * 4));
            }
        }

        recorder.Stop();
        return path;
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return condition();
    }
}

/// <summary>Reads the X server back with <c>xwininfo</c> and ImageMagick's <c>import</c>.</summary>
internal static partial class ScreenReader
{
    public static bool HasTools => Run("xwininfo", "-version") is not null && Run("import", "-version") is not null;

    /// <summary>The id of a window of exactly this size, waited for, or null.</summary>
    public static string? WaitForWindow(int width, int height, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        var pattern = new Regex($@"^\s*(0x[0-9a-f]+) .*\s{width}x{height}\+", RegexOptions.Multiline);
        do
        {
            var tree = Run("xwininfo", "-root -tree");
            if (tree is not null && pattern.Match(tree) is { Success: true } match)
            {
                return match.Groups[1].Value;
            }

            Thread.Sleep(50);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    /// <summary>True when the window is mapped and so can be drawn on and read.</summary>
    public static bool IsViewable(string window) =>
        Run("xwininfo", $"-id 0x{window.Replace("0x", string.Empty)}")?.Contains("IsViewable", StringComparison.Ordinal) == true;

    /// <summary>The colour of one pixel of a window, as the X server holds it; black when the window cannot be read.</summary>
    public static (int R, int G, int B) Pixel(string window, int x, int y)
    {
        var id = window.StartsWith("0x", StringComparison.Ordinal) ? window : "0x" + window;
        var text = Run("import", $"-window {id} -crop 1x1+{x}+{y} -depth 8 txt:-");
        var match = text is null ? null : Regex.Match(text, @"\((\d+),(\d+),(\d+)\)");
        return match is { Success: true }
            ? (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture))
            : (0, 0, 0);
    }

    private static string? Run(string program, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(program, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return null;
            }

            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            _ = error.Result;
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>A plain, mapped X window that the test owns, for a player to draw into.</summary>
internal sealed partial class XWindow : IDisposable
{
    private readonly nint _display;

    public XWindow(int width, int height, uint background = 0)
    {
        _display = XOpenDisplay(0);
        if (_display == 0)
        {
            throw new InvalidOperationException("The X display could not be opened.");
        }

        Id = XCreateSimpleWindow(_display, XDefaultRootWindow(_display), 0, 0, (uint)width, (uint)height, 0, 0, background);
        XMapWindow(_display, Id);
        XSync(_display, 0);

        // The window manager maps it a moment later; a window that is not viewable cannot be captured or drawn into yet.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !ScreenReader.IsViewable(Id.ToString("x", System.Globalization.CultureInfo.InvariantCulture)))
        {
            Thread.Sleep(20);
        }
    }

    public nuint Id { get; }

    /// <summary>Paints the whole window one colour (0xRRGGBB), as the X server draws it.</summary>
    public void Fill(uint colour)
    {
        XSetWindowBackground(_display, Id, colour);
        XClearWindow(_display, Id);
        XSync(_display, 0);
    }

    public void Dispose()
    {
        XDestroyWindow(_display, Id);
        XCloseDisplay(_display);
    }

    [LibraryImport("libX11.so.6")]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial nuint XDefaultRootWindow(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial nuint XCreateSimpleWindow(nint display, nuint parent, int x, int y, uint width, uint height, uint border, nuint borderColour, nuint background);

    [LibraryImport("libX11.so.6")]
    private static partial int XMapWindow(nint display, nuint window);

    [LibraryImport("libX11.so.6")]
    private static partial int XDestroyWindow(nint display, nuint window);

    [LibraryImport("libX11.so.6")]
    private static partial int XSync(nint display, int discard);

    [LibraryImport("libX11.so.6")]
    private static partial int XSetWindowBackground(nint display, nuint window, nuint colour);

    [LibraryImport("libX11.so.6")]
    private static partial int XClearWindow(nint display, nuint window);
}

/// <summary>Records the monitor of WSLg's sound output and reports how loud it has been, in decibels.</summary>
[SupportedOSPlatform("linux")]
internal sealed class SoundListener : IDisposable
{
    private static readonly Regex Rms = new(@"rms=\(GValueArray\)<\s*([^>]*)>", RegexOptions.Compiled);
    private readonly GStreamerPipeline _pipeline;
    private readonly List<(DateTime At, double Db)> _levels = [];

    public SoundListener()
    {
        _pipeline = GStreamerPipeline.Parse("pulsesrc device=RDPSink.monitor ! audioconvert ! level interval=50000000 post-messages=true ! fakesink sync=false");
        _pipeline.Bus.Mask = GstMessageType.Element | GstMessageType.Error;
        _pipeline.Bus.MessageReceived += (_, m) =>
        {
            if (m.Type != GstMessageType.Element || m.Structure is not { } text || Rms.Match(text) is not { Success: true } match)
            {
                return;
            }

            var loudest = match.Groups[1].Value.Split(',').Select(v => double.Parse(v.Trim(), CultureInfo.InvariantCulture)).Max();
            lock (_levels)
            {
                _levels.Add((DateTime.UtcNow, loudest));
            }
        };
        _pipeline.Play();
    }

    /// <summary>The loudest level heard during the next <paramref name="span"/>, or -100 when nothing was heard.</summary>
    public double Loudest(TimeSpan span)
    {
        var from = DateTime.UtcNow;
        Thread.Sleep(span);
        lock (_levels)
        {
            var heard = _levels.Where(l => l.At >= from).Select(l => l.Db).ToList();
            return heard.Count == 0 ? -100 : heard.Max();
        }
    }

    public void Dispose() => _pipeline.Dispose();
}
