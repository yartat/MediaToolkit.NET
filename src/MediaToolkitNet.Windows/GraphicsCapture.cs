#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Capture;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>A top-level window that can be captured.</summary>
/// <param name="Handle">The window handle (<c>HWND</c>), which <see cref="GraphicsCapture.ForWindow"/> takes.</param>
/// <param name="Title">The window's title.</param>
/// <param name="ProcessId">The process that owns it.</param>
/// <param name="X">Left edge on the virtual desktop.</param>
/// <param name="Y">Top edge on the virtual desktop.</param>
/// <param name="Width">Width of the window, shadow excluded where Windows says so.</param>
/// <param name="Height">Height of the window.</param>
public sealed record CaptureWindow(nint Handle, string Title, int ProcessId, int X, int Y, int Width, int Height);

/// <summary>
/// Captures one window or one monitor with Windows.Graphics.Capture (Windows 10 version 1903 and later): frames of
/// what the window draws, as BGRA, taken from the GPU, even when other windows cover it and even for a window drawn
/// with DirectX, which <c>PrintWindow</c> and a screen grab get wrong. The mouse pointer can be in the picture and
/// the yellow border Windows puts round a captured window can be switched off.
/// </summary>
/// <remarks>
/// <para>
/// A frame arrives when the window's content changes, so a still window delivers one frame at the start and then
/// nothing; a minimised window delivers none. When the window is resized the frames change size and
/// <see cref="Format"/> follows. When the window is destroyed <see cref="Closed"/> is raised and the capture ends.
/// </para>
/// <para>
/// Hiding the border (<c>showBorder: false</c>) is only honoured on Windows 11, and on some builds only for a
/// process Windows has granted that access; where it is refused the border stays. The pointer switch needs Windows 10
/// version 2004.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class GraphicsCapture : IVideoCapture
{
    private const string BackendName = "Windows.GraphicsCapture";
    private const int ToolWindow = 0x80;
    private const int AppWindow = 0x40000;
    private const int ExStyle = -20;
    private const int BufferCount = 2;

    private static bool? _supported;

    private readonly Lock _gate = new();
    private readonly Stopwatch _clock = new();
    private readonly nint _window;
    private readonly bool _cursor;
    private readonly bool _border;
    private readonly TimeSpan _interval;
    private readonly int _frameRate;

    private ComPtr _item;
    private ComPtr _device;
    private ComPtr _context;
    private ComPtr _direct3DDevice;
    private ComPtr _pool;
    private ComPtr _session;
    private ComPtr _staging;
    private uint _stagingWidth;
    private uint _stagingHeight;
    private int _poolWidth;
    private int _poolHeight;
    private Thread? _worker;
    private CancellationTokenSource? _cancellation;
    private TimeSpan _lastDelivered = TimeSpan.MinValue;
    private bool _closedRaised;
    private bool _disposed;

    private GraphicsCapture(nint window, nint monitor, int frameRate, bool captureCursor, bool showBorder)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        if (!IsSupported)
        {
            throw new MediaBackendUnavailableException(BackendName, "Windows.Graphics.Capture needs Windows 10 version 1903 or later");
        }

        _window = window;
        _cursor = captureCursor;
        _border = showBorder;
        _frameRate = frameRate;
        _interval = TimeSpan.FromSeconds(1.0 / frameRate);

        try
        {
            Ole32.Initialize();
            CreateItem(window, monitor);
            CreateDevice();
            var size = stackalloc int[2];
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, int*, int>)Com.Slot(_item.Pointer, Wgc.ItemGetSize))(_item.Pointer, size),
                "IGraphicsCaptureItem::get_Size");
            _poolWidth = size[0];
            _poolHeight = size[1];
            CreatePool();
            Format = new VideoFormat(_poolWidth, _poolHeight, PixelFormat.Bgra32, frameRate);

            nint name;
            if (HResult.Succeeded(((delegate* unmanaged[Stdcall]<void*, nint*, int>)Com.Slot(_item.Pointer, Wgc.ItemGetDisplayName))(_item.Pointer, &name)))
            {
                Title = Wgc.TakeString(name);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>True when this Windows can capture windows this way.</summary>
    public static bool IsSupported => _supported ??= Probe();

    /// <inheritdoc />
    public string Backend => BackendName;

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _worker is not null;
            }
        }
    }

    /// <summary>The name Windows gives the captured item: a window's title, or the monitor's name.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>The size of the item's picture in BGRA, with the frame rate this capture is limited to. It follows a resize.</summary>
    public VideoFormat Format { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<VideoFormat> SupportedFormats => [Format];

    /// <inheritdoc />
    public event VideoFrameHandler? FrameCaptured;

    /// <inheritdoc />
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <summary>Raised, from the capture thread, when the captured window has been destroyed. The capture then stops delivering.</summary>
    public event EventHandler? Closed;

    /// <summary>The windows that can be captured: visible, not minimised, titled, not cloaked, and not a tool window.</summary>
    public static IReadOnlyList<CaptureWindow> GetWindows()
    {
        var list = new List<CaptureWindow>();
        var handle = GCHandle.Alloc(list);
        try
        {
            Wgc.EnumWindows(&EnumerateWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return list;
    }

    /// <summary>Captures a window.</summary>
    /// <param name="window">The window handle, for instance from <see cref="GetWindows"/>.</param>
    /// <param name="frameRate">The most frames a second delivered; a frame that comes sooner than that is dropped.</param>
    /// <param name="captureCursor">Whether the mouse pointer is drawn into the picture.</param>
    /// <param name="showBorder">Whether Windows draws its border round the window while it is captured.</param>
    /// <exception cref="ArgumentException">The handle is not a window.</exception>
    public static GraphicsCapture ForWindow(nint window, int frameRate = 30, bool captureCursor = true, bool showBorder = true)
    {
        if (Wgc.IsWindow(window) == 0)
        {
            throw new ArgumentException($"0x{window:X} is not a window.", nameof(window));
        }

        return new GraphicsCapture(window, 0, frameRate, captureCursor, showBorder);
    }

    /// <summary>Captures a monitor, the same one <see cref="DesktopCapture"/> takes with that index.</summary>
    /// <param name="outputIndex">Which monitor, as numbered by <see cref="DesktopCapture.GetOutputs"/>.</param>
    /// <param name="frameRate">The most frames a second delivered.</param>
    /// <param name="captureCursor">Whether the mouse pointer is drawn into the picture.</param>
    /// <param name="showBorder">Whether Windows draws its border round the monitor while it is captured.</param>
    public static GraphicsCapture ForMonitor(int outputIndex = 0, int frameRate = 30, bool captureCursor = true, bool showBorder = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(outputIndex);
        return new GraphicsCapture(0, DesktopCapture.GetMonitorHandle(outputIndex), frameRate, captureCursor, showBorder);
    }

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_worker is not null)
            {
                return;
            }

            EnsureSession();
            _clock.Restart();
            _lastDelivered = TimeSpan.MinValue;
            _closedRaised = false;
            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _worker = new Thread(() => CaptureLoop(token))
            {
                IsBackground = true,
                Name = "MediaToolkitNet.GraphicsCapture capture",
            };
            _worker.Start();
        }
    }

    /// <summary>
    /// Stops delivering frames and ends the capture session, which takes the border off the window. A session that
    /// <see cref="TryGrab"/> opened ends here too.
    /// </summary>
    public void Stop()
    {
        Thread? worker;
        lock (_gate)
        {
            worker = _worker;
            _worker = null;
            _cancellation?.Cancel();
        }

        worker?.Join(TimeSpan.FromSeconds(2));

        lock (_gate)
        {
            _cancellation?.Dispose();
            _cancellation = null;
            CloseSession();
        }
    }

    /// <summary>
    /// Waits for the item to draw something and hands over the picture, once: for a screenshot of a window, or to
    /// pull frames at your own pace. Not for use while <see cref="Start"/> is running. The capture session this
    /// opens stays until <see cref="Stop"/> or <see cref="Dispose"/>.
    /// </summary>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="handler">Gets the frame, which is valid only during the call.</param>
    /// <returns>False when no frame came within <paramref name="timeout"/>, or the window is gone.</returns>
    public bool TryGrab(TimeSpan timeout, VideoFrameHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            throw new InvalidOperationException("The capture is running; stop it before grabbing frames by hand.");
        }

        lock (_gate)
        {
            EnsureSession();
            _clock.Restart();
            var deadline = Stopwatch.StartNew();
            do
            {
                if (_window != 0 && Wgc.IsWindow(_window) == 0)
                {
                    return false;
                }

                if (NextFrame(handler, throttle: false))
                {
                    return true;
                }

                Thread.Sleep(2);
            }
            while (deadline.Elapsed < timeout);

            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        lock (_gate)
        {
            _staging.Dispose();
            if (!_pool.IsNull)
            {
                Wgc.Close(_pool.Pointer);
            }

            _pool.Dispose();
            _item.Dispose();
            _direct3DDevice.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }

    // ----------------------------------------------------------------- native

    private static bool Probe()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            return false;
        }

        try
        {
            Ole32.Initialize();
            using var interop = Wgc.GetActivationFactory(Wgc.ItemClass, Wgc.IidItemInterop);
            using var statics = Wgc.GetActivationFactory(Wgc.FramePoolClass, Wgc.IidFramePoolStatics2);
            return true;
        }
        catch (Exception ex) when (ex is MediaToolkitNetException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumerateWindow(nint window, nint parameter)
    {
        try
        {
            // A minimised window draws nothing, so a capture of it would send no frames.
            if (Wgc.IsWindowVisible(window) == 0 || Wgc.IsIconic(window) != 0)
            {
                return 1;
            }

            var style = Wgc.GetWindowLongW(window, ExStyle);
            if ((style & ToolWindow) != 0 && (style & AppWindow) == 0)
            {
                return 1;
            }

            int cloaked;
            if (HResult.Succeeded(Wgc.DwmGetWindowAttribute(window, 14, &cloaked, 4)) && cloaked != 0)
            {
                return 1;
            }

            var title = stackalloc char[256];
            var length = Wgc.GetWindowTextW(window, title, 256);
            if (length <= 0)
            {
                return 1;
            }

            var rect = stackalloc int[4];
            if (Wgc.GetWindowRect(window, rect) == 0 || rect[2] <= rect[0] || rect[3] <= rect[1])
            {
                return 1;
            }

            uint processId;
            Wgc.GetWindowThreadProcessId(window, &processId);
            ((List<CaptureWindow>)GCHandle.FromIntPtr(parameter).Target!).Add(
                new CaptureWindow(window, new string(title, 0, length), (int)processId, rect[0], rect[1], rect[2] - rect[0], rect[3] - rect[1]));
        }
        catch
        {
            // Nothing may throw into Windows; a window that cannot be described is left out.
        }

        return 1;
    }

    private void CreateItem(nint window, nint monitor)
    {
        using var interop = Wgc.GetActivationFactory(Wgc.ItemClass, Wgc.IidItemInterop);
        void* item;
        var iid = Wgc.IidItem;
        var hr = window != 0
            ? ((delegate* unmanaged[Stdcall]<void*, nint, Guid*, void**, int>)Com.Slot(interop.Pointer, Wgc.InteropCreateForWindow))(
                interop.Pointer, window, &iid, &item)
            : ((delegate* unmanaged[Stdcall]<void*, nint, Guid*, void**, int>)Com.Slot(interop.Pointer, Wgc.InteropCreateForMonitor))(
                interop.Pointer, monitor, &iid, &item);
        HResult.ThrowIfFailed(hr, window != 0 ? "IGraphicsCaptureItemInterop::CreateForWindow" : "IGraphicsCaptureItemInterop::CreateForMonitor");
        _item = new ComPtr(item);
    }

    // A Direct3D 11 device with BGRA support on the default adapter, wrapped as the WinRT IDirect3DDevice.
    private void CreateDevice()
    {
        void* device;
        void* context;
        int level;
        HResult.ThrowIfFailed(
            Dxgi.D3D11CreateDevice(null, 1, 0, Dxgi.CreateDeviceBgraSupport, null, 0, Dxgi.SdkVersion, &device, &level, &context),
            "D3D11CreateDevice");
        _device = new ComPtr(device);
        _context = new ComPtr(context);

        using var dxgiDevice = _device.QueryInterface(Wgc.IidDxgiDevice);
        void* wrapped;
        HResult.ThrowIfFailed(Wgc.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.Pointer, &wrapped), "CreateDirect3D11DeviceFromDXGIDevice");
        _direct3DDevice = new ComPtr(wrapped);
    }

    // A free-threaded pool, so frames can be fetched from any thread with no dispatcher queue.
    private void CreatePool()
    {
        using var statics = Wgc.GetActivationFactory(Wgc.FramePoolClass, Wgc.IidFramePoolStatics2);
        void* pool;
        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, void*, int, int, long, void**, int>)Com.Slot(statics.Pointer, Wgc.StaticsCreateFreeThreaded))(
                statics.Pointer, _direct3DDevice.Pointer, Wgc.PixelFormatB8G8R8A8, BufferCount, PackSize(_poolWidth, _poolHeight), &pool),
            "IDirect3D11CaptureFramePoolStatics2::CreateFreeThreaded");
        _pool = new ComPtr(pool);
    }

    // SizeInt32 {Width, Height} is eight bytes passed by value: one register on x64, Width in the low half.
    private static long PackSize(int width, int height) => (uint)width | ((long)height << 32);

    private void EnsureSession()
    {
        if (!_session.IsNull)
        {
            return;
        }

        // A pool makes one session in its life: after Stop the next Start needs a pool of its own.
        if (_pool.IsNull)
        {
            var size = stackalloc int[2];
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, int*, int>)Com.Slot(_item.Pointer, Wgc.ItemGetSize))(_item.Pointer, size),
                "IGraphicsCaptureItem::get_Size");
            _poolWidth = size[0];
            _poolHeight = size[1];
            Format = new VideoFormat(_poolWidth, _poolHeight, PixelFormat.Bgra32, _frameRate);
            CreatePool();
        }

        void* session;
        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, void*, void**, int>)Com.Slot(_pool.Pointer, Wgc.PoolCreateCaptureSession))(
                _pool.Pointer, _item.Pointer, &session),
            "IDirect3D11CaptureFramePool::CreateCaptureSession");
        _session = new ComPtr(session);

        // Both switches are optional features of newer builds: a refusal leaves the default.
        if (HResult.Succeeded(Com.QueryInterface(session, Wgc.IidSession2, out var session2)))
        {
            ((delegate* unmanaged[Stdcall]<void*, byte, int>)Com.Slot(session2, Wgc.Session2PutCursor))(session2, _cursor ? (byte)1 : (byte)0);
            Com.Release(session2);
        }

        if (!_border && HResult.Succeeded(Com.QueryInterface(session, Wgc.IidSession3, out var session3)))
        {
            ((delegate* unmanaged[Stdcall]<void*, byte, int>)Com.Slot(session3, Wgc.Session3PutBorder))(session3, 0);
            Com.Release(session3);
        }

        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(session, Wgc.SessionStartCapture))(session),
            "IGraphicsCaptureSession::StartCapture");
    }

    private void CloseSession()
    {
        if (_session.IsNull)
        {
            return;
        }

        Wgc.Close(_session.Pointer);
        _session.Dispose();
        _session = default;
        Wgc.Close(_pool.Pointer);
        _pool.Dispose();
        _pool = default;
    }

    private void CaptureLoop(CancellationToken token)
    {
        var pause = Math.Clamp((int)(_interval.TotalMilliseconds / 4), 1, 8);
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_window != 0 && Wgc.IsWindow(_window) == 0)
                {
                    RaiseClosed();
                    return;
                }

                var handler = FrameCaptured;
                if (!NextFrame(handler is null ? static (in VideoFrame _) => { } : handler.Invoke, throttle: true))
                {
                    Thread.Sleep(pause);
                }
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                var error = ex as MediaToolkitNetException ?? new MediaToolkitNetException(BackendName, $"capture stopped: {ex.Message}", 0);
                SafeEvents.RaiseFailed(Failed, this, error);
            }
        }
    }

    private void RaiseClosed()
    {
        if (_closedRaised)
        {
            return;
        }

        _closedRaised = true;
        try
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            SafeEvents.RaiseFailed(Failed, this, new MediaToolkitNetException(BackendName, $"a Closed handler threw: {ex.Message}", 0));
        }
    }

    // Takes the next frame the pool has, follows a change of size, and hands it over unless it comes too soon.
    private bool NextFrame(VideoFrameHandler handler, bool throttle)
    {
        void* frame;
        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, void**, int>)Com.Slot(_pool.Pointer, Wgc.PoolTryGetNextFrame))(_pool.Pointer, &frame),
            "IDirect3D11CaptureFramePool::TryGetNextFrame");
        if (frame is null)
        {
            return false;
        }

        var resized = false;
        try
        {
            var content = stackalloc int[2];
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, int*, int>)Com.Slot(frame, Wgc.FrameGetContentSize))(frame, content),
                "IDirect3D11CaptureFrame::get_ContentSize");
            if (content[0] <= 0 || content[1] <= 0)
            {
                return false;
            }

            // This frame is in a texture of the old size, so it is not the picture. A pool made again at the new size
            // delivers frames only when the window next draws; a new session sends its first frame at once, so the
            // session is made again (once the frame is let go, below) and that frame is the picture at its new size.
            if (content[0] != _poolWidth || content[1] != _poolHeight)
            {
                resized = true;
                return false;
            }

            var now = _clock.Elapsed;
            if (throttle && _lastDelivered != TimeSpan.MinValue && now - _lastDelivered < _interval)
            {
                return false;
            }

            void* surface;
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, void**, int>)Com.Slot(frame, Wgc.FrameGetSurface))(frame, &surface),
                "IDirect3D11CaptureFrame::get_Surface");
            using var surfacePtr = new ComPtr(surface);
            using var access = surfacePtr.QueryInterface(Wgc.IidDxgiInterfaceAccess);
            void* texture;
            var iid = Dxgi.IidTexture2D;
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Com.Slot(access.Pointer, Wgc.DxgiAccessGetInterface))(
                    access.Pointer, &iid, &texture),
                "IDirect3DDxgiInterfaceAccess::GetInterface");
            using var texturePtr = new ComPtr(texture);

            if (!Deliver(texture, content[0], content[1], now, handler))
            {
                return false;
            }

            _lastDelivered = now;
            return true;
        }
        finally
        {
            Wgc.Close(frame);
            Com.Release(frame);
            if (resized)
            {
                CloseSession();
                EnsureSession();
            }
        }
    }

    // D3D11_TEXTURE2D_DESC: Width 0, Height 4, MipLevels 8, ArraySize 12, Format 16, SampleDesc.Count 20, .Quality 24,
    // Usage 28, BindFlags 32, CPUAccessFlags 36, MiscFlags 40; 44 bytes.
    // D3D11_MAPPED_SUBRESOURCE: void *pData 0, UINT RowPitch 8, UINT DepthPitch 12; 16 bytes.
    private bool Deliver(void* texture, int width, int height, TimeSpan timestamp, VideoFrameHandler handler)
    {
        var description = stackalloc uint[11];
        ((delegate* unmanaged[Stdcall]<void*, uint*, void>)Com.Slot(texture, Dxgi.Texture2DGetDesc))(texture, description);
        if (description[4] != Dxgi.FormatB8G8R8A8)
        {
            throw new MediaToolkitNetException(BackendName, $"the captured image has the DXGI format {description[4]}, not BGRA", 0);
        }

        // The first frame after a resize is still in a texture of the old size, with the new size as its content: it
        // is clipped, and the next frame, from the pool made again, is whole. A texture larger than the content holds the
        // picture in its top left.
        if ((uint)width > description[0] || (uint)height > description[1])
        {
            return false;
        }

        EnsureStaging(description);
        ((delegate* unmanaged[Stdcall]<void*, void*, void*, void>)Com.Slot(_context.Pointer, Dxgi.ContextCopyResource))(
            _context.Pointer, _staging.Pointer, texture);

        var mapped = stackalloc byte[16];
        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, void*, uint, uint, uint, byte*, int>)Com.Slot(_context.Pointer, Dxgi.ContextMap))(
                _context.Pointer, _staging.Pointer, 0, Dxgi.MapRead, 0, mapped),
            "ID3D11DeviceContext::Map");
        try
        {
            var data = *(byte**)mapped;
            var stride = *(uint*)(mapped + 8);
            var format = new VideoFormat(width, height, PixelFormat.Bgra32, _frameRate);
            var planes = stackalloc nint[1] { (nint)data };
            var strides = stackalloc int[1] { (int)stride };
            if (!FrameBounds.FitsIn(format, new ReadOnlySpan<nint>(planes, 1), new ReadOnlySpan<int>(strides, 1), 1, (nint)data, (long)stride * description[1]))
            {
                return false;
            }

            var frame = new VideoFrame(format, timestamp, data, (int)stride);
            handler(in frame);
            return true;
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<void*, void*, uint, void>)Com.Slot(_context.Pointer, Dxgi.ContextUnmap))(
                _context.Pointer, _staging.Pointer, 0);
        }
    }

    private void EnsureStaging(uint* source)
    {
        if (!_staging.IsNull && _stagingWidth == source[0] && _stagingHeight == source[1])
        {
            return;
        }

        _staging.Dispose();
        var description = stackalloc uint[11];
        for (var i = 0; i < 11; i++)
        {
            description[i] = source[i];
        }

        description[2] = 1; // MipLevels
        description[3] = 1; // ArraySize
        description[5] = 1; // SampleDesc.Count
        description[6] = 0; // SampleDesc.Quality
        description[7] = Dxgi.UsageStaging;
        description[8] = 0; // BindFlags
        description[9] = Dxgi.CpuAccessRead;
        description[10] = 0; // MiscFlags

        void* staging;
        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, uint*, void*, void**, int>)Com.Slot(_device.Pointer, Dxgi.DeviceCreateTexture2D))(
                _device.Pointer, description, null, &staging),
            "ID3D11Device::CreateTexture2D");
        _staging = new ComPtr(staging);
        _stagingWidth = source[0];
        _stagingHeight = source[1];
    }
}
