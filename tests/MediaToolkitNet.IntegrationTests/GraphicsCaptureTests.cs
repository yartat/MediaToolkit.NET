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

/// <summary>A fact that runs only where Windows.Graphics.Capture works and there is a desktop to draw a window on.</summary>
public sealed class GraphicsCaptureFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="GraphicsCaptureFactAttribute"/> class.</summary>
    public GraphicsCaptureFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows.Graphics.Capture is Windows only.";
            return;
        }

        try
        {
            if (!GraphicsCapture.IsSupported)
            {
                Skip = "this Windows is older than 10 version 1903.";
                return;
            }

            using var window = new TestWindow(100, 100, 200, 100, colour: TestWindow.White);
            using var capture = GraphicsCapture.ForWindow(window.Handle);
            if (!capture.TryGrab(TimeSpan.FromSeconds(3), (in VideoFrame _) => { }))
            {
                Skip = "the session draws no window (locked, or the display is off).";
            }
        }
        catch (Exception ex)
        {
            Skip = $"this session cannot capture a window: {ex.Message}";
        }
    }
}

/// <summary>
/// A window of a known solid colour on its own message thread: a popup <c>STATIC</c> control drawn as a white or a
/// black rectangle, with no frame, so what a capture should see is exactly known.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TestWindow : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WmClose = 0x10;
    private const uint WmQuit = 0x12;
    private const uint WsExTopmost = 0x8;
    private const uint WsExNoActivate = 0x08000000;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _created = new();
    private uint _threadId;

    /// <summary>Creates the window and waits until it exists.</summary>
    public TestWindow(int x, int y, int width, int height, uint colour, bool physical = false)
    {
        Title = $"MediaToolkitNet capture test {Guid.NewGuid():N}";
        _thread = new Thread(() =>
        {
            if (physical)
            {
                SetThreadDpiAwarenessContext(-4); // per monitor v2: this thread's coordinates are physical pixels
            }

            _threadId = GetCurrentThreadId();
            var className = $"MediaToolkitNetTest{colour:X6}";
            var brush = CreateSolidBrush(colour);
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Procedure = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW"),
                Background = brush,
                ClassName = className,
            };
            RegisterClassExW(ref windowClass);

            // Topmost and never activated: a monitor capture shows what is in front, and the test must not take the focus.
            Handle = CreateWindowExW(WsExTopmost | WsExNoActivate, className, Title, WsPopup | WsVisible, x, y, width, height, 0, 0, 0, 0);
            _created.Set();
            while (GetMessageW(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
        })
        {
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _created.Wait();
        if (Handle == 0)
        {
            throw new InvalidOperationException("the test window could not be created");
        }
    }

    /// <summary>White, as a <c>COLORREF</c> (0x00BBGGRR).</summary>
    public const uint White = 0xFFFFFF;

    /// <summary>R 200, G 100, B 30: a colour whose three channels differ, so a wrong byte order shows.</summary>
    public const uint Orange = 200 | (100 << 8) | (30 << 16);

    /// <summary>The window's title, unique to this window.</summary>
    public string Title { get; }

    /// <summary>The window handle.</summary>
    public nint Handle { get; private set; }

    /// <summary>Moves and resizes the window.</summary>
    public void Place(int x, int y, int width, int height) => SetWindowPos(Handle, 0, x, y, width, height, 0x14); // no z-order, no activation

    /// <summary>Destroys the window, leaving the thread running.</summary>
    public void Close() => PostMessageW(Handle, WmClose, 0, 0);

    /// <inheritdoc />
    public void Dispose()
    {
        PostMessageW(Handle, WmClose, 0, 0);
        PostThreadMessageW(_threadId, WmQuit, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _created.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WindowClass windowClass);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint colour);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern nint GetProcAddress(nint module, string name);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Message message, nint window, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern int TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Message message);

    [DllImport("user32.dll")]
    private static extern int PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int PostThreadMessageW(uint thread, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}

/// <summary>
/// Windows.Graphics.Capture on windows this test draws itself, in colours that are known, so a frame is checked
/// against what is on the window rather than against another capture.
/// </summary>
[SupportedOSPlatform("windows")]
public unsafe class GraphicsCaptureTests
{
    [GraphicsCaptureFact]
    public void AWhiteWindowIsCapturedAsWhiteBgraOfItsOwnSize()
    {
        using var window = new TestWindow(100, 100, 320, 200, colour: TestWindow.White);
        using var capture = GraphicsCapture.ForWindow(window.Handle, captureCursor: false, showBorder: false);

        var (size, wrong) = GrabSized(capture, 255, 255, 255);

        size.Should().Be((320, 200));
        wrong.Should().Be(0, "every pixel of a white window is B=G=R=255 with alpha 255");
    }

    [GraphicsCaptureFact]
    public void AWindowOfAnotherColourIsCapturedInThatColour()
    {
        using var window = new TestWindow(100, 100, 320, 200, colour: TestWindow.Orange);
        using var capture = GraphicsCapture.ForWindow(window.Handle, captureCursor: false, showBorder: false);

        var (size, wrong) = GrabSized(capture, blue: 30, green: 100, red: 200);

        size.Should().Be((320, 200));
        wrong.Should().Be(0, "every pixel is R200 G100 B30, which in memory is B, G, R, A");
    }

    [GraphicsCaptureFact]
    public void TheCaptureTitleIsTheWindowsOwn()
    {
        using var window = new TestWindow(100, 100, 200, 100, colour: TestWindow.White);
        using var capture = GraphicsCapture.ForWindow(window.Handle);

        capture.Title.Should().Be(window.Title);
        capture.Format.Width.Should().Be(200);
        capture.Format.Height.Should().Be(100);
    }

    [GraphicsCaptureFact]
    public void AMonitorShowsTheWindowThatIsOnIt()
    {
        var output = DesktopCapture.GetOutputs()[0];
        if (output.Rotation != 0)
        {
            return;
        }

        // Output sizes are physical pixels; this thread and the window's must use them too, or a scaled display moves the window.
        var before = SetThreadDpiAwarenessContext(-4);
        try
        {
            MonitorShowsWindow(output);
        }
        finally
        {
            SetThreadDpiAwarenessContext(before);
        }
    }

    private static void MonitorShowsWindow(DesktopOutput output)
    {
        using var window = new TestWindow(output.X + 100, output.Y + 100, 320, 200, colour: TestWindow.White, physical: true);
        using var capture = GraphicsCapture.ForMonitor(0, captureCursor: false, showBorder: false);
        capture.Format.Width.Should().Be(output.Width);
        capture.Format.Height.Should().Be(output.Height);
        Thread.Sleep(300);

        // The monitor's first picture may predate the window; grab until the window is in it.
        var white = 0;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var seen = false;
        while (!seen && DateTime.UtcNow < deadline)
        {
            capture.TryGrab(TimeSpan.FromMilliseconds(500), (in VideoFrame frame) =>
            {
                var data = frame.GetPlane(0);
                var stride = frame.Stride(0);
                white = 0;
                for (var y = 120; y < 280; y += 4)
                {
                    for (var x = 120; x < 400; x += 4)
                    {
                        var p = (y * stride) + (x * 4);
                        if (data[p] == 255 && data[p + 1] == 255 && data[p + 2] == 255)
                        {
                            white++;
                        }
                    }
                }

                seen = white == 40 * 70;
            });
        }

        seen.Should().BeTrue($"the window's area on the monitor is white ({white} of {40 * 70} sampled pixels were)");
    }

    [GraphicsCaptureFact]
    public void StartDeliversFramesAndStopEndsTheThread()
    {
        using var window = new TestWindow(100, 100, 200, 100, colour: TestWindow.White);
        using var capture = GraphicsCapture.ForWindow(window.Handle, frameRate: 10);
        var frames = 0;
        var failures = 0;
        var first = new ManualResetEventSlim();
        capture.FrameCaptured += (in VideoFrame frame) =>
        {
            Interlocked.Increment(ref frames);
            first.Set();
        };
        capture.Failed += (_, _) => failures++;

        capture.Start();
        capture.IsRunning.Should().BeTrue();
        first.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        capture.Stop();

        capture.IsRunning.Should().BeFalse();
        failures.Should().Be(0);
        frames.Should().BeGreaterThan(0);
        capture.Start();
        capture.Stop();
    }

    [GraphicsCaptureFact]
    public void AResizedWindowDeliversFramesOfTheNewSize()
    {
        using var window = new TestWindow(100, 100, 200, 100, colour: TestWindow.White);
        using var capture = GraphicsCapture.ForWindow(window.Handle, frameRate: 30);
        var sizes = new List<(int, int)>();
        capture.FrameCaptured += (in VideoFrame frame) =>
        {
            lock (sizes)
            {
                sizes.Add((frame.Format.Width, frame.Format.Height));
            }
        };

        capture.Start();
        Thread.Sleep(400);
        window.Place(100, 100, 400, 240);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (sizes)
            {
                if (sizes.Contains((400, 240)))
                {
                    break;
                }
            }

            Thread.Sleep(25);
        }

        capture.Stop();
        lock (sizes)
        {
            sizes.Should().Contain((200, 100));
            sizes.Should().Contain((400, 240));
        }

        capture.Format.Width.Should().Be(400);
    }

    [GraphicsCaptureFact]
    public void ADestroyedWindowRaisesClosed()
    {
        using var window = new TestWindow(100, 100, 200, 100, colour: TestWindow.White);
        using var capture = GraphicsCapture.ForWindow(window.Handle);
        var closed = new ManualResetEventSlim();
        capture.Closed += (_, _) => closed.Set();

        capture.Start();
        window.Close();

        closed.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        capture.Stop();
    }

    [GraphicsCaptureFact]
    public void TheWindowListHasTheTestWindowWithItsPlace()
    {
        using var window = new TestWindow(120, 140, 220, 130, colour: TestWindow.White);
        Thread.Sleep(200);

        var found = GraphicsCapture.GetWindows().Where(w => w.Title == window.Title).ToList();

        found.Should().ContainSingle();
        var entry = found[0];
        entry.Handle.Should().Be(window.Handle);
        entry.ProcessId.Should().Be(Environment.ProcessId);
        (entry.X, entry.Y, entry.Width, entry.Height).Should().Be((120, 140, 220, 130));
    }

    [GraphicsCaptureFact]
    public void ThePointerIsInThePictureOnlyWhenAsked()
    {
        using var window = new TestWindow(100, 100, 320, 200, colour: TestWindow.White);
        GetCursorPos(out var before);
        try
        {
            SetCursorPos(260, 200);
            Thread.Sleep(200);

            var (_, withPointer) = GrabWith(window.Handle, captureCursor: true);
            var (_, without) = GrabWith(window.Handle, captureCursor: false);

            without.Should().Be(0, "nothing but white is on the window");
            withPointer.Should().BeGreaterThan(0, "the pointer is drawn over it");
        }
        finally
        {
            SetCursorPos(before.X, before.Y);
        }
    }

    [GraphicsCaptureFact]
    public void ANonWindowIsRefused()
    {
        var act = () => GraphicsCapture.ForWindow(0x1234);

        act.Should().Throw<ArgumentException>();
    }

    // The pixels of one frame that are not what a window of the colour has: white is 255, black 0, alpha 255 for both.
    private static (int Width, int Height, int Wrong) GrabCore(GraphicsCapture capture, byte blue, byte green, byte red)
    {
        var width = 0;
        var height = 0;
        var wrong = -1;
        capture.TryGrab(TimeSpan.FromSeconds(3), (in VideoFrame frame) =>
        {
            width = frame.Format.Width;
            height = frame.Format.Height;
            var data = frame.GetPlane(0);
            var stride = frame.Stride(0);
            wrong = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var p = (y * stride) + (x * 4);
                    if (data[p] != blue || data[p + 1] != green || data[p + 2] != red || data[p + 3] != 255)
                    {
                        if (wrong == 0)
                        {
                            Console.Error.WriteLine($"first wrong pixel ({x},{y}): B{data[p]} G{data[p + 1]} R{data[p + 2]} A{data[p + 3]}");
                        }

                        wrong++;
                    }
                }
            }
        }).Should().BeTrue();
        return (width, height, wrong);
    }

    private static ((int, int) Size, int Wrong) GrabSized(GraphicsCapture capture, byte blue, byte green, byte red)
    {
        var r = GrabCore(capture, blue, green, red);
        return ((r.Width, r.Height), r.Wrong);
    }

    private static (int, int) GrabWith(nint window, bool captureCursor)
    {
        using var capture = GraphicsCapture.ForWindow(window, captureCursor: captureCursor, showBorder: false);
        var r = GrabCore(capture, 255, 255, 255);
        return (r.Width * r.Height, r.Wrong);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    private static extern int SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern int GetCursorPos(out Point point);
}
