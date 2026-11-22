#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Windows;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>A fact that runs only where Windows has a desktop that can be duplicated.</summary>
public sealed class DesktopFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="DesktopFactAttribute"/> class.</summary>
    public DesktopFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Desktop Duplication is Windows only.";
            return;
        }

        try
        {
            using var capture = new DesktopCapture(0);
            if (!capture.TryGrab(TimeSpan.FromSeconds(3), (in VideoFrame _) => { }))
            {
                Skip = "the session shows no desktop to duplicate (locked, or the display is off).";
            }
        }
        catch (Exception ex)
        {
            Skip = $"this session cannot duplicate a monitor: {ex.Message}";
        }
    }
}

/// <summary>Desktop Duplication: the monitors, and a picture of one checked against GDI's.</summary>
[SupportedOSPlatform("windows")]
public unsafe class DesktopCaptureTests
{
    [DesktopFact]
    public void TheMonitorsAreListedWithTheirPlaceOnTheDesktop()
    {
        var outputs = DesktopCapture.GetOutputs();

        outputs.Should().NotBeEmpty();
        outputs.Select(o => o.Index).Should().Equal(Enumerable.Range(0, outputs.Count));
        outputs.Should().OnlyContain(o => o.Width > 0 && o.Height > 0 && o.Name.StartsWith("\\\\.\\DISPLAY", StringComparison.Ordinal));
    }

    [DesktopFact]
    public void AFrameIsTheMonitorsPictureInBgra()
    {
        var output = DesktopCapture.GetOutputs()[0];
        using var capture = new DesktopCapture(0);

        byte[]? pixels = null;
        VideoFormat format = default;
        var stride = 0;
        var got = capture.TryGrab(TimeSpan.FromSeconds(3), (in VideoFrame frame) =>
        {
            format = frame.Format;
            stride = frame.Stride(0);
            pixels = frame.GetPlane(0).ToArray();
        });

        got.Should().BeTrue("the first frame is the picture as it is");
        format.PixelFormat.Should().Be(PixelFormat.Bgra32);
        // Sizes are physical pixels whatever this process's DPI awareness is; a rotated monitor is not checked.
        if (output.Rotation == 0)
        {
            (format.Width, format.Height).Should().Be((output.Width, output.Height));
        }

        stride.Should().BeGreaterThanOrEqualTo(format.Width * 4);
        pixels!.Length.Should().BeGreaterThanOrEqualTo((format.Height - 1) * stride + format.Width * 4);
    }

    [DesktopFact]
    public void ThePixelsAreTheOnesGdiSees()
    {
        var output = DesktopCapture.GetOutputs()[0];
        if (output.Rotation != 0)
        {
            return; // GDI reads the unrotated desktop; the comparison below would need the rotation undone
        }

        using var capture = new DesktopCapture(0);
        byte[]? duplicated = null;
        var stride = 0;
        capture.TryGrab(TimeSpan.FromSeconds(3), (in VideoFrame frame) =>
        {
            stride = frame.Stride(0);
            duplicated = frame.GetPlane(0).ToArray();
        }).Should().BeTrue();
        var gdi = Gdi.Capture(output.X, output.Y, output.Width, output.Height);

        // The desktop may change between the two; most of what is on it does not.
        var compared = 0;
        var close = 0;
        for (var y = 0; y < output.Height; y += 9)
        {
            for (var x = 0; x < output.Width; x += 11)
            {
                var d = y * stride + x * 4;
                var g = (y * output.Width + x) * 4;
                compared++;
                if (Math.Abs(duplicated![d] - gdi[g]) <= 24 && Math.Abs(duplicated[d + 1] - gdi[g + 1]) <= 24 && Math.Abs(duplicated[d + 2] - gdi[g + 2]) <= 24)
                {
                    close++;
                }
            }
        }

        compared.Should().BeGreaterThan(1000);
        ((double)close / compared).Should().BeGreaterThan(0.9, "B, G and R of the sampled pixels agree with GDI's, so the order and the stride are right");
    }

    [DesktopFact]
    public void StartDeliversTheFirstFrameAndStopEndsTheThread()
    {
        using var capture = new DesktopCapture(0, frameRate: 10);
        var frames = 0;
        var first = new ManualResetEventSlim();
        capture.FrameCaptured += (in VideoFrame frame) =>
        {
            Interlocked.Increment(ref frames);
            first.Set();
        };

        capture.Start();
        capture.IsRunning.Should().BeTrue();
        first.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        capture.Stop();

        capture.IsRunning.Should().BeFalse();
        var after = Volatile.Read(ref frames);
        Thread.Sleep(300);
        Volatile.Read(ref frames).Should().Be(after, "nothing is delivered after Stop");
    }

    [DesktopFact]
    public void ThereIsNoMonitorBeyondTheLast()
    {
        var count = DesktopCapture.GetOutputs().Count;

        var open = () => new DesktopCapture(count);

        open.Should().Throw<ArgumentOutOfRangeException>();
    }

    [DesktopFact]
    public void GrabbingByHandWhileRunningIsRefused()
    {
        using var capture = new DesktopCapture(0);
        capture.Start();

        var grab = () => capture.TryGrab(TimeSpan.Zero, (in VideoFrame _) => { });

        grab.Should().Throw<InvalidOperationException>();
    }

    private static class Gdi
    {
        public static byte[] Capture(int x, int y, int width, int height)
        {
            SetProcessDPIAware(); // physical pixels, as DXGI counts them
            var screen = GetDC(0);
            var memory = CreateCompatibleDC(screen);
            var bitmap = CreateCompatibleBitmap(screen, width, height);
            var old = SelectObject(memory, bitmap);
            try
            {
                BitBlt(memory, 0, 0, width, height, screen, x, y, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
                var header = stackalloc int[10];
                header[0] = 40;
                header[1] = width;
                header[2] = -height; // top-down
                *(short*)(header + 3) = 1;
                *((short*)(header + 3) + 1) = 32;
                var pixels = new byte[width * height * 4];
                fixed (byte* p = pixels)
                {
                    GetDIBits(memory, bitmap, 0, (uint)height, p, header, 0);
                }

                return pixels;
            }
            finally
            {
                SelectObject(memory, old);
                DeleteObject(bitmap);
                DeleteDC(memory);
                ReleaseDC(0, screen);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        private static extern nint GetDC(nint window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(nint window, nint dc);

        [DllImport("gdi32.dll")]
        private static extern nint CreateCompatibleDC(nint dc);

        [DllImport("gdi32.dll")]
        private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern nint SelectObject(nint dc, nint obj);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, int rop);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte* bits, int* info, uint usage);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(nint obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(nint dc);
    }
}
