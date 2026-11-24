#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Globalization;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Capture;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer;

/// <summary>
/// Captures an X11 screen, a region of it or one window with <c>ximagesrc</c>, as <see cref="PixelFormat.Bgra32"/> frames at
/// the rate asked for: an <see cref="IVideoCapture"/> for Linux desktops on X (Wayland sessions reach it through XWayland, and
/// see only what runs there).
/// </summary>
/// <remarks>
/// <para>
/// The size is learned by running the capture for one frame when it is made, so <see cref="Format"/> is what the frames
/// really are, and a window or display that cannot be captured fails there and not at <see cref="Start"/>. A window is
/// captured at its own size; a resize changes the size of the frames that follow.
/// </para>
/// <para>
/// Frames are raised on a thread GStreamer owns and are valid for the call only. A handler may dispose the capture: it is
/// then taken down from another thread.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerScreenCapture : IVideoCapture
{
    [ThreadStatic]
    private static bool _inHandler;

    private readonly Lock _gate = new();
    private readonly GStreamerPipeline _pipeline;
    private readonly GStreamerSink _sink;
    private readonly bool _guarded;
    private volatile bool _delivering;
    private int _reported;
    private volatile bool _running;
    private bool _disposed;

    private GStreamerScreenCapture(IReadOnlyList<(string Property, string Value)> source, int frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);

        // An X error would otherwise end the process: see XErrors.
        XErrors.Acquire();
        _guarded = true;

        // The properties of the source go into the description: linking it to the next element asks it for its caps, which
        // opens the display, and the window id and the region cannot be set after that (ximagesrc warns and ignores them).
        // They are numbers and flags, so nothing needs quoting.
        var properties = string.Concat(source.Select(p => $" {p.Property}={p.Value}"));
        try
        {
            _pipeline = GStreamerPipeline.Parse(
                $"ximagesrc name=src use-damage=false{properties} ! video/x-raw,framerate={frameRate.ToString(CultureInfo.InvariantCulture)}/1 ! videoconvert ! " +
                "video/x-raw,format=BGRA ! appsink name=sink emit-signals=true sync=false max-buffers=2 drop=true",
                strict: true);
        }
        catch
        {
            XErrors.Release();
            throw;
        }

        try
        {
            _sink = _pipeline.GetSink("sink");
            _sink.Element.Failed += (_, e) => Raise(e);
            _sink.Element.Connect("new-sample", _ => Deliver());
            _pipeline.Bus.MessageReceived += OnMessage;
            XErrors.Taken += OnXError;

            // A live source has nothing to preroll, so the size is read off the first frame it makes.
            _pipeline.Play();
            VideoFormat? format = null;
            if (!_sink.TryPullVideo(TimeSpan.FromSeconds(5), (in VideoFrame frame) => format = frame.Format))
            {
                throw new MediaToolkitNetException(Backend, $"the capture produced no frame: {_pipeline.LastError() ?? XErrors.Last ?? "ximagesrc sent nothing"}", 0);
            }

            Format = format!.Value;
            _pipeline.Stop();
        }
        catch (MediaToolkitNetException ex)
        {
            Teardown();
            throw new MediaToolkitNetException(GstConstants.BackendName, $"could not open the capture: {ex.Message}{(XErrors.Last is { } x ? $" ({x})" : string.Empty)}", 0);
        }
        catch
        {
            Teardown();
            throw;
        }
    }

    /// <inheritdoc />
    public string Backend => GstConstants.BackendName;

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <inheritdoc />
    public VideoFormat Format { get; }

    /// <inheritdoc />
    public IReadOnlyList<VideoFormat> SupportedFormats => [Format];

    /// <inheritdoc />
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <inheritdoc />
    public event VideoFrameHandler? FrameCaptured;

    /// <summary>The whole X screen.</summary>
    /// <param name="frameRate">Frames a second.</param>
    /// <param name="showPointer">Whether the mouse pointer is drawn into the frames.</param>
    public static GStreamerScreenCapture ForScreen(int frameRate = 30, bool showPointer = true) =>
        new([("show-pointer", Flag(showPointer))], frameRate);

    /// <summary>A rectangle of the screen, in pixels from its top left corner.</summary>
    public static GStreamerScreenCapture ForRegion(int x, int y, int width, int height, int frameRate = 30, bool showPointer = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        // ximagesrc takes the last pixel, not the size.
        return new(
            [
                ("startx", x.ToString(CultureInfo.InvariantCulture)),
                ("starty", y.ToString(CultureInfo.InvariantCulture)),
                ("endx", (x + width - 1).ToString(CultureInfo.InvariantCulture)),
                ("endy", (y + height - 1).ToString(CultureInfo.InvariantCulture)),
                ("show-pointer", Flag(showPointer)),
            ],
            frameRate);
    }

    /// <summary>One X11 window, by its id, whatever lies over it on the screen.</summary>
    /// <param name="xid">The window's X11 id (for instance as <c>xwininfo</c> prints it).</param>
    /// <param name="frameRate">Frames a second.</param>
    /// <param name="showPointer">Whether the mouse pointer is drawn into the frames.</param>
    /// <exception cref="MediaToolkitNetException">There is no such window.</exception>
    public static GStreamerScreenCapture ForWindow(nuint xid, int frameRate = 30, bool showPointer = false)
    {
        // ximagesrc takes an id that names no window for the whole screen and captures that without a word, so it is asked first.
        XErrors.Acquire();
        try
        {
            if (!XErrors.WindowExists(xid))
            {
                throw new MediaToolkitNetException(
                    GstConstants.BackendName, $"could not open the capture: the X11 window 0x{(ulong)xid:x} does not exist", 0);
            }
        }
        finally
        {
            XErrors.Release();
        }

        return new([("xid", ((ulong)xid).ToString(CultureInfo.InvariantCulture)), ("show-pointer", Flag(showPointer))], frameRate);
    }

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_running)
            {
                return;
            }

            _delivering = false;
            Volatile.Write(ref _reported, 0);
            _pipeline.Play();
            Discard();
            _delivering = true;
            _running = true;
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _delivering = false;
            _running = false;
            _pipeline.Stop();
        }
    }

    /// <summary>
    /// Waits for one fresh frame and hands it over, once: for a screenshot, or to pull frames at your own pace. Not while the
    /// capture is running.
    /// </summary>
    /// <param name="timeout">How long to wait for the frame.</param>
    /// <param name="handler">Gets the frame, which is valid only during the call.</param>
    /// <returns>False when no frame came within <paramref name="timeout"/>.</returns>
    /// <exception cref="InvalidOperationException">The capture is running.</exception>
    public bool TryGrab(TimeSpan timeout, VideoFrameHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_running)
            {
                throw new InvalidOperationException("The capture is running; stop it before grabbing frames by hand.");
            }

            _pipeline.Play();
            try
            {
                Discard();
                return _sink.TryPullVideo(timeout, handler);
            }
            finally
            {
                _pipeline.Stop();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _delivering = false;
        lock (_gate)
        {
            _running = false;
        }

        if (_inHandler)
        {
            // Taking a pipeline to NULL waits for its streaming threads, and this is one of them.
            ThreadPool.QueueUserWorkItem(_ => Teardown());
        }
        else
        {
            Teardown();
        }
    }

    private static string Flag(bool value) => value ? "true" : "false";

    // A stopped capture is taken to NULL, not PAUSED: from PAUSED a live source sometimes took the whole five seconds of the
    // state change to go on, once in six starts, while from NULL it starts in a few milliseconds every time, as the first
    // start in the constructor does.
    // Frames that were queued before this start or grab are old: take them out so the first one is fresh.
    private void Discard()
    {
        while (_sink.TryPullVideo(TimeSpan.Zero, (in VideoFrame _) => { }))
        {
        }
    }

    private void Deliver()
    {
        if (!_delivering)
        {
            return;
        }

        _inHandler = true;
        try
        {
            _sink.TryPullVideo(TimeSpan.Zero, (in VideoFrame frame) => FrameCaptured?.Invoke(in frame));
        }
        finally
        {
            _inHandler = false;
        }
    }

    private void OnMessage(object? sender, GStreamerMessage message)
    {
        if (message.Type == GstMessageType.Error && !_disposed)
        {
            Raise(new MediaToolkitNetException(Backend, $"{message.Text} ({message.Debug})", 0));
        }
    }

    // A request of the capture failed, and the handler kept the process alive: say so once. The usual cause is that the window
    // closed. It is the server's error for the whole process, so with several captures open each of them is told.
    private void OnXError(string text)
    {
        // Read without the lock: this runs on the thread that made the failed request, which Stop is waiting for while it
        // holds the lock.
        if (!_disposed && _running && Interlocked.Exchange(ref _reported, 1) == 0)
        {
            _delivering = false;
            Raise(new MediaToolkitNetException(Backend, $"the X server refused a request of the capture; the window may be gone ({text}); the capture stopped", 0));

            // What follows would be frames of nothing. Stopping takes the pipeline to NULL, which waits for the thread this
            // runs on, so it is done from another.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Stop();
                }
                catch (ObjectDisposedException)
                {
                }
            });
        }
    }

    private void Raise(MediaToolkitNetException ex) => SafeEvents.RaiseFailed(Failed, this, ex);

    // NULL first, which ends the streaming threads, and only then the handlers and the objects.
    private void Teardown()
    {
        XErrors.Taken -= OnXError;
        try
        {
            _pipeline.Stop();
        }
        catch (MediaToolkitNetException)
        {
            // Going to NULL fails only for a pipeline already broken, which is being thrown away.
        }

        _sink?.Dispose();
        _pipeline.Dispose();
        if (_guarded)
        {
            XErrors.Release();
        }
    }
}
