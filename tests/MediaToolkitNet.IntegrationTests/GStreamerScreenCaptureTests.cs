#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.GStreamer;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// X11 capture through <c>ximagesrc</c>, on windows the test makes itself and paints a colour it knows, so what comes back
/// is compared with what the X server was told to show, and a change of the colour has to reach the next frame.
/// </summary>
[Collection(RealOutputCollection.Name)]
[SupportedOSPlatform("linux")]
public unsafe partial class GStreamerScreenCaptureTests
{
    private const uint Orange = 0xC8641E; // R200 G100 B30

    [DisplayFact]
    public void AWindowIsCapturedAtItsSizeInTheColourItHas()
    {
        using var window = new XWindow(200, 120, Orange);
        using var capture = GStreamerScreenCapture.ForWindow(window.Id, frameRate: 15);

        capture.Format.Should().Be(new VideoFormat(200, 120, PixelFormat.Bgra32, new Rational(15, 1)));
        capture.SupportedFormats.Should().Contain(capture.Format);
        capture.Backend.Should().Be("gstreamer");

        var seen = new List<(int Width, int Height, int R, int G, int B)>();
        capture.FrameCaptured += (in VideoFrame f) =>
        {
            var plane = f.GetPlane(0);
            var at = (60 * f.Stride(0)) + (100 * 4);
            lock (seen)
            {
                seen.Add((f.Format.Width, f.Format.Height, plane[at + 2], plane[at + 1], plane[at]));
            }
        };
        capture.Start();
        capture.IsRunning.Should().BeTrue();
        WaitFor(() => Count(seen) >= 3);

        lock (seen)
        {
            seen.Should().NotBeEmpty();
            seen.Should().OnlyContain(p => p.Width == 200 && p.Height == 120);
            seen.Last().R.Should().BeCloseTo(200, 2);
            seen.Last().G.Should().BeCloseTo(100, 2);
            seen.Last().B.Should().BeCloseTo(30, 2);
        }
    }

    [DisplayFact]
    public void AChangeOfColourReachesTheNextFrame()
    {
        using var window = new XWindow(160, 100, Orange);
        using var capture = GStreamerScreenCapture.ForWindow(window.Id, frameRate: 20);
        var reds = new List<int>();
        capture.FrameCaptured += (in VideoFrame f) =>
        {
            lock (reds)
            {
                reds.Add(f.GetPlane(0)[(50 * f.Stride(0)) + (80 * 4) + 2]);
            }
        };
        capture.Start();
        WaitFor(() => Count(reds) >= 2);

        // The first frame of a window that has just appeared can be blank, so what came before the change is looked at as a whole.
        WaitFor(() =>
        {
            lock (reds)
            {
                return reds.Contains(200);
            }
        });
        List<int> early;
        lock (reds)
        {
            early = [.. reds];
        }

        window.Fill(0x1E64C8); // R30 G100 B200
        var before = Count(reds);
        WaitFor(() => Count(reds) >= before + 4);

        lock (reds)
        {
            early.Should().Contain(r => Math.Abs(r - 200) <= 2, "the window was orange before it was painted blue");
            reds.Last().Should().BeCloseTo(30, 2, "the window was painted blue and the capture is live");
        }
    }

    [DisplayFact]
    public void TheFrameRateIsKept()
    {
        using var window = new XWindow(100, 60, Orange);
        using var capture = GStreamerScreenCapture.ForWindow(window.Id, frameRate: 10);
        var frames = 0;
        capture.FrameCaptured += (in VideoFrame _) => Interlocked.Increment(ref frames);

        capture.Start();
        Thread.Sleep(2000);
        capture.Stop();

        frames.Should().BeInRange(15, 22, "ten a second for two seconds, give or take the first and the last");
    }

    [DisplayFact]
    public void StoppingEndsTheFramesAndStartingAgainResumesThem()
    {
        using var window = new XWindow(100, 60, Orange);
        using var capture = GStreamerScreenCapture.ForWindow(window.Id, frameRate: 20);
        var frames = 0;
        capture.FrameCaptured += (in VideoFrame _) => Interlocked.Increment(ref frames);

        capture.Start();
        WaitFor(() => Volatile.Read(ref frames) >= 3);
        capture.Stop();
        capture.IsRunning.Should().BeFalse();
        var stopped = Volatile.Read(ref frames);
        Thread.Sleep(400);
        Volatile.Read(ref frames).Should().Be(stopped, "nothing arrives once it is stopped");

        capture.Start();
        WaitFor(() => Volatile.Read(ref frames) >= stopped + 3);
        Volatile.Read(ref frames).Should().BeGreaterThanOrEqualTo(stopped + 3);
    }

    [DisplayFact]
    public void OneFrameCanBeGrabbedWithoutStarting()
    {
        using var window = new XWindow(120, 80, Orange);
        using var capture = GStreamerScreenCapture.ForWindow(window.Id);
        var grabs = new List<(int W, int H, int R)>();

        // A window that was mapped a moment ago may still be unpainted in the first frame; the colour has to arrive within a few grabs.
        for (var i = 0; i < 8 && !grabs.Any(g => g.R == 200); i++)
        {
            capture.TryGrab(TimeSpan.FromSeconds(2), (in VideoFrame f) => grabs.Add((f.Format.Width, f.Format.Height, f.GetPlane(0)[(40 * f.Stride(0)) + (60 * 4) + 2]))).Should().BeTrue();
        }

        grabs.Should().NotBeEmpty();
        grabs.Should().OnlyContain(g => g.W == 120 && g.H == 80, "the size is the window's every time");
        grabs.Last().R.Should().Be(200, $"the window's colour comes through (grabs: {string.Join(", ", grabs.Select(g => g.R))})");
        capture.IsRunning.Should().BeFalse();
        capture.Start();
        var refused = () => capture.TryGrab(TimeSpan.FromSeconds(1), (in VideoFrame _) => { });
        refused.Should().Throw<InvalidOperationException>("a running capture is not grabbed from by hand");
    }

    [DisplayFact]
    public void ARegionOfTheScreenHasTheSizeAsked()
    {
        using var capture = GStreamerScreenCapture.ForRegion(10, 20, 64, 48, frameRate: 10);

        capture.Format.Width.Should().Be(64);
        capture.Format.Height.Should().Be(48);
        var got = (0, 0);
        capture.TryGrab(TimeSpan.FromSeconds(2), (in VideoFrame f) => got = (f.Format.Width, f.Format.Height)).Should().BeTrue();
        got.Should().Be((64, 48));
    }

    [DisplayFact]
    public void AWindowThatDoesNotExistIsRefusedAtOnce()
    {
        var act = () => GStreamerScreenCapture.ForWindow(0x7FFFFFF0);

        act.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("capture");
    }

    [DisplayFact]
    public void DisposingWhileRunningLetsGo()
    {
        using var window = new XWindow(100, 60, Orange);
        for (var i = 0; i < 6; i++)
        {
            var capture = GStreamerScreenCapture.ForWindow(window.Id, frameRate: 30);
            capture.FrameCaptured += (in VideoFrame _) => { };
            capture.Start();
            Thread.Sleep(80);
            capture.Dispose();
            capture.IsRunning.Should().BeFalse();
        }
    }

    [DisplayFact]
    public void AWindowThatClosesUnderARunningCaptureDoesNotEndTheProcess()
    {
        var window = new XWindow(100, 60, Orange);
        using var capture = GStreamerScreenCapture.ForWindow(window.Id, frameRate: 20);
        var frames = 0;
        var failures = new List<string>();
        capture.FrameCaptured += (in VideoFrame _) => Interlocked.Increment(ref frames);
        capture.Failed += (_, e) =>
        {
            lock (failures)
            {
                failures.Add(e.Message);
            }
        };
        capture.Start();
        WaitFor(() => Volatile.Read(ref frames) >= 3);

        // Without the error handler the X server's BadWindow ends this test run.
        window.Dispose();
        WaitFor(() => Count(failures) > 0);

        Count(failures).Should().Be(1, "the capture reports that its window is gone, once");
        WaitFor(() => !capture.IsRunning);
        capture.IsRunning.Should().BeFalse("a capture whose window is gone stops");
        var after = Volatile.Read(ref frames);
        Thread.Sleep(300);
        Volatile.Read(ref frames).Should().Be(after, "no frames follow a window that is not there");
    }

    [DisplayFact]
    public void ErrorsOfOtherRequestsStillReachTheHandlerThatWasThereBeforeTheCapture()
    {
        var display = XClient.XOpenDisplay(0);
        var attributes = stackalloc byte[256];
        var window = new XWindow(100, 60, Orange);
        var previous = XClient.XSetErrorHandler(&Observe);
        _observed.Clear();
        try
        {
            using (var capture = GStreamerScreenCapture.ForWindow(window.Id))
            {
                // The capture's own kind of request, on a window that is not there: taken, and not passed on.
                XClient.XGetWindowAttributes(display, 0x7FFFFFF0, attributes);
                XClient.XSync(display, 0);
                _observed.Should().BeEmpty("the capture keeps its own errors from the earlier handler");

                // Anything else is the application's: BadAtom from GetAtomName goes to the handler that was there.
                XClient.XGetAtomName(display, 0x7FFFFFF0);
                XClient.XSync(display, 0);
                _observed.Should().ContainSingle().Which.Should().Be(17, "request 17 is GetAtomName");
            }

            // The capture is gone, and the earlier handler is back for every request.
            _observed.Clear();
            XClient.XGetWindowAttributes(display, 0x7FFFFFF0, attributes);
            XClient.XSync(display, 0);
            _observed.Should().ContainSingle().Which.Should().Be(3, "request 3 is GetWindowAttributes");
        }
        finally
        {
            XClient.XSetErrorHandler((delegate* unmanaged[Cdecl]<nint, byte*, int>)previous);
            XClient.XCloseDisplay(display);
            window.Dispose();
        }
    }

    private static readonly List<int> _observed = [];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Observe(nint display, byte* error)
    {
        _observed.Add(error[33]);
        return 0;
    }

    private static int Count<T>(List<T> list)
    {
        lock (list)
        {
            return list.Count;
        }
    }

    private static void WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !condition())
        {
            Thread.Sleep(20);
        }
    }
}

/// <summary>The few Xlib calls the error-handler test makes on a display of its own.</summary>
internal static unsafe partial class XClient
{
    [LibraryImport("libX11.so.6")]
    public static partial nint XOpenDisplay(nint name);

    [LibraryImport("libX11.so.6")]
    public static partial int XCloseDisplay(nint display);

    [LibraryImport("libX11.so.6")]
    public static partial int XSync(nint display, int discard);

    [LibraryImport("libX11.so.6")]
    public static partial nint XSetErrorHandler(delegate* unmanaged[Cdecl]<nint, byte*, int> handler);

    [LibraryImport("libX11.so.6")]
    public static partial int XGetWindowAttributes(nint display, nuint window, byte* attributes);

    [LibraryImport("libX11.so.6")]
    public static partial nint XGetAtomName(nint display, nuint atom);
}
