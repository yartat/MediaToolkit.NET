#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Capture;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>One monitor the desktop can be duplicated from.</summary>
/// <param name="Index">Position in <see cref="DesktopCapture.GetOutputs"/>; what <see cref="DesktopCapture"/> takes.</param>
/// <param name="Name">The display's device name, for example <c>\\.\DISPLAY1</c>.</param>
/// <param name="X">Left edge on the virtual desktop.</param>
/// <param name="Y">Top edge on the virtual desktop.</param>
/// <param name="Width">Width in pixels of the area on the virtual desktop.</param>
/// <param name="Height">Height in pixels of the area on the virtual desktop.</param>
/// <param name="Rotation">Rotation in degrees (0, 90, 180 or 270) the monitor is set to.</param>
public sealed record DesktopOutput(int Index, string Name, int X, int Y, int Width, int Height, int Rotation);

/// <summary>
/// Captures a monitor with the Desktop Duplication API (DXGI 1.2): frames of what the monitor shows, as BGRA, taken
/// from the GPU without a screen grab through GDI. A frame arrives when the desktop changes, so a still desktop
/// delivers one frame at the start and then nothing until something moves; the mouse pointer is not in the picture.
/// </summary>
/// <remarks>
/// <para>
/// The duplication is lost when the display mode changes or the secure desktop (the lock screen, a UAC prompt) takes
/// over; the capture then makes a new one on its own. A process may hold a few duplications of one output at a time,
/// and a session without a desktop (a service, a locked remote session) has none.
/// </para>
/// <para>
/// A monitor set to a rotation other than 0 delivers its picture as the display scans it out, which is rotated.
/// <see cref="DesktopOutput.Rotation"/> says by how much.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class DesktopCapture : IVideoCapture
{
    private const string BackendName = "Windows.DesktopDuplication";

    private readonly Lock _gate = new();
    private readonly Stopwatch _clock = new();
    private readonly int _outputIndex;
    private readonly TimeSpan _interval;

    private ComPtr _device;
    private ComPtr _context;
    private ComPtr _output1;
    private ComPtr _duplication;
    private ComPtr _staging;
    private uint _stagingWidth;
    private uint _stagingHeight;
    private Thread? _worker;
    private CancellationTokenSource? _cancellation;
    private TimeSpan _lastDelivered = TimeSpan.MinValue;
    private bool _disposed;

    /// <summary>Opens a duplication of one monitor.</summary>
    /// <param name="outputIndex">Which monitor, as numbered by <see cref="GetOutputs"/>.</param>
    /// <param name="frameRate">The most frames a second delivered; a frame that comes sooner than that is dropped.</param>
    /// <exception cref="ArgumentOutOfRangeException">There is no such monitor.</exception>
    /// <exception cref="MediaToolkitNetException">The duplication could not be made, for instance with no desktop.</exception>
    public DesktopCapture(int outputIndex = 0, int frameRate = 30)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(outputIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        _outputIndex = outputIndex;
        _interval = TimeSpan.FromSeconds(1.0 / frameRate);

        try
        {
            Open();
            var description = stackalloc uint[9];
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, uint*, int>)Com.Slot(_duplication.Pointer, Dxgi.DuplicationGetDesc))(
                    _duplication.Pointer, description),
                "IDXGIOutputDuplication::GetDesc");

            // DXGI_OUTDUPL_DESC: DXGI_MODE_DESC (Width 0, Height 4, ...), Rotation 28, DesktopImageInSystemMemory 32.
            Format = new VideoFormat((int)description[0], (int)description[1], PixelFormat.Bgra32, frameRate);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

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

    /// <summary>The size of the monitor's picture, in BGRA, with the frame rate this capture is limited to.</summary>
    public VideoFormat Format { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<VideoFormat> SupportedFormats => [Format];

    /// <inheritdoc />
    public event VideoFrameHandler? FrameCaptured;

    /// <inheritdoc />
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <summary>The monitors that can be duplicated, across every adapter, in a fixed order.</summary>
    public static IReadOnlyList<DesktopOutput> GetOutputs()
    {
        var result = new List<DesktopOutput>();
        var factory = CreateFactory();
        try
        {
            for (var adapterIndex = 0u; ; adapterIndex++)
            {
                void* adapter;
                var hr = ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Com.Slot(factory.Pointer, Dxgi.FactoryEnumAdapters1))(
                    factory.Pointer, adapterIndex, &adapter);
                if (hr == Dxgi.ErrorNotFound)
                {
                    break;
                }

                HResult.ThrowIfFailed(hr, "IDXGIFactory1::EnumAdapters1");
                using var adapterPtr = new ComPtr(adapter);
                for (var outputIndex = 0u; ; outputIndex++)
                {
                    void* output;
                    hr = ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Com.Slot(adapter, Dxgi.AdapterEnumOutputs))(
                        adapter, outputIndex, &output);
                    if (hr == Dxgi.ErrorNotFound)
                    {
                        break;
                    }

                    HResult.ThrowIfFailed(hr, "IDXGIAdapter::EnumOutputs");
                    using var outputPtr = new ComPtr(output);
                    result.Add(Describe(output, result.Count));
                }
            }
        }
        finally
        {
            factory.Dispose();
        }

        return result;
    }

    // The HMONITOR of the output with this index in GetOutputs' order (DXGI_OUTPUT_DESC.Monitor, offset 88).
    internal static nint GetMonitorHandle(int outputIndex)
    {
        var counted = 0;
        var factory = CreateFactory();
        try
        {
            for (var adapterIndex = 0u; ; adapterIndex++)
            {
                void* adapter;
                var hr = ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Com.Slot(factory.Pointer, Dxgi.FactoryEnumAdapters1))(
                    factory.Pointer, adapterIndex, &adapter);
                if (hr == Dxgi.ErrorNotFound)
                {
                    throw new ArgumentOutOfRangeException(nameof(outputIndex), outputIndex, $"There are only {counted} monitors.");
                }

                HResult.ThrowIfFailed(hr, "IDXGIFactory1::EnumAdapters1");
                using var adapterPtr = new ComPtr(adapter);
                for (var index = 0u; ; index++)
                {
                    void* output;
                    hr = ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Com.Slot(adapter, Dxgi.AdapterEnumOutputs))(adapter, index, &output);
                    if (hr == Dxgi.ErrorNotFound)
                    {
                        break;
                    }

                    HResult.ThrowIfFailed(hr, "IDXGIAdapter::EnumOutputs");
                    using var outputPtr = new ComPtr(output);
                    if (counted++ == outputIndex)
                    {
                        var description = stackalloc byte[96];
                        HResult.ThrowIfFailed(
                            ((delegate* unmanaged[Stdcall]<void*, byte*, int>)Com.Slot(output, Dxgi.OutputGetDesc))(output, description),
                            "IDXGIOutput::GetDesc");
                        return *(nint*)(description + 88);
                    }
                }
            }
        }
        finally
        {
            factory.Dispose();
        }
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

            _clock.Restart();
            _lastDelivered = TimeSpan.MinValue;
            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _worker = new Thread(() => CaptureLoop(token))
            {
                IsBackground = true,
                Name = "MediaToolkitNet.DesktopDuplication capture",
            };
            _worker.Start();
        }
    }

    /// <inheritdoc />
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
        }
    }

    /// <summary>
    /// Waits for the desktop to change and hands over the picture, once: for a screenshot, or to pull frames at your
    /// own pace. Not for use while <see cref="Start"/> is running.
    /// </summary>
    /// <param name="timeout">How long to wait for a change; the first call returns at once with the picture as it is.</param>
    /// <param name="handler">Gets the frame, which is valid only during the call.</param>
    /// <returns>False when nothing changed within <paramref name="timeout"/>.</returns>
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
            _clock.Restart();

            // A lost duplication (a mode change, the lock screen) is made again and the wait goes on.
            var deadline = Stopwatch.StartNew();
            do
            {
                var left = (int)Math.Clamp((timeout - deadline.Elapsed).TotalMilliseconds, 0, int.MaxValue);
                if (Acquire(left, handler, throttle: false))
                {
                    return true;
                }
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
            _duplication.Dispose();
            _output1.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }

    // ----------------------------------------------------------------- native

    private static ComPtr CreateFactory()
    {
        void* factory;
        var iid = Dxgi.IidFactory1;
        HResult.ThrowIfFailed(Dxgi.CreateDXGIFactory1(&iid, &factory), "CreateDXGIFactory1");
        return new ComPtr(factory);
    }

    // DXGI_OUTPUT_DESC: WCHAR DeviceName[32] 0, RECT DesktopCoordinates 64 (left, top, right, bottom), BOOL Attached 80,
    // DXGI_MODE_ROTATION Rotation 84 (1 identity, 2 90, 3 180, 4 270), HMONITOR 88; 96 bytes.
    private static DesktopOutput Describe(void* output, int index)
    {
        var description = stackalloc byte[96];
        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, byte*, int>)Com.Slot(output, Dxgi.OutputGetDesc))(output, description),
            "IDXGIOutput::GetDesc");
        var rect = (int*)(description + 64);
        var rotation = *(int*)(description + 84);
        var name = new string((char*)description);

        // DXGI reports DesktopCoordinates in the caller's DPI-virtualised units, which differ from the pixels of the
        // picture in a process that is not DPI aware. The display's current mode is always in physical pixels.
        // DEVMODEW (220 bytes): dmSize 68, dmPosition 76 (x, y), dmPelsWidth 172, dmPelsHeight 176.
        var mode = stackalloc byte[220];
        new Span<byte>(mode, 220).Clear();
        *(ushort*)(mode + 68) = 220;
        if (Dxgi.EnumDisplaySettingsW(name, -1, mode) != 0 && *(uint*)(mode + 172) != 0)
        {
            return new DesktopOutput(
                index, name, *(int*)(mode + 76), *(int*)(mode + 80), (int)*(uint*)(mode + 172), (int)*(uint*)(mode + 176),
                rotation switch { 2 => 90, 3 => 180, 4 => 270, _ => 0 });
        }

        return new DesktopOutput(
            index,
            name,
            rect[0],
            rect[1],
            rect[2] - rect[0],
            rect[3] - rect[1],
            rotation switch { 2 => 90, 3 => 180, 4 => 270, _ => 0 });
    }

    // Finds the adapter that owns the monitor, makes a Direct3D 11 device on it (a duplication has to be made with a
    // device of the output's own adapter) and duplicates the output.
    private void Open()
    {
        var factory = CreateFactory();
        try
        {
            var counted = 0;
            for (var adapterIndex = 0u; ; adapterIndex++)
            {
                void* adapter;
                var hr = ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Com.Slot(factory.Pointer, Dxgi.FactoryEnumAdapters1))(
                    factory.Pointer, adapterIndex, &adapter);
                if (hr == Dxgi.ErrorNotFound)
                {
                    throw new ArgumentOutOfRangeException(nameof(_outputIndex), _outputIndex, $"There are only {counted} monitors.");
                }

                HResult.ThrowIfFailed(hr, "IDXGIFactory1::EnumAdapters1");
                using var adapterPtr = new ComPtr(adapter);
                for (var outputIndex = 0u; ; outputIndex++)
                {
                    void* output;
                    hr = ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Com.Slot(adapter, Dxgi.AdapterEnumOutputs))(
                        adapter, outputIndex, &output);
                    if (hr == Dxgi.ErrorNotFound)
                    {
                        break;
                    }

                    HResult.ThrowIfFailed(hr, "IDXGIAdapter::EnumOutputs");
                    if (counted++ != _outputIndex)
                    {
                        Com.Release(output);
                        continue;
                    }

                    using var outputPtr = new ComPtr(output);
                    CreateDevice(adapter);
                    _output1 = outputPtr.QueryInterface(Dxgi.IidOutput1);
                    Duplicate();
                    return;
                }
            }
        }
        finally
        {
            factory.Dispose();
        }
    }

    private void CreateDevice(void* adapter)
    {
        void* device;
        void* context;
        int level;
        HResult.ThrowIfFailed(
            Dxgi.D3D11CreateDevice(adapter, Dxgi.DriverTypeUnknown, 0, Dxgi.CreateDeviceBgraSupport, null, 0, Dxgi.SdkVersion, &device, &level, &context),
            "D3D11CreateDevice");
        _device = new ComPtr(device);
        _context = new ComPtr(context);
    }

    private void Duplicate()
    {
        _duplication.Dispose();
        void* duplication;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void*, void**, int>)Com.Slot(_output1.Pointer, Dxgi.Output1DuplicateOutput))(
            _output1.Pointer, _device.Pointer, &duplication);
        if (hr == Dxgi.ErrorNotCurrentlyAvailable)
        {
            throw new MediaToolkitNetException(
                BackendName,
                "the output cannot be duplicated: this process already has the most duplications of it that Windows allows, or the session has no desktop",
                hr);
        }

        HResult.ThrowIfFailed(hr, "IDXGIOutput1::DuplicateOutput");
        _duplication = new ComPtr(duplication);
    }

    private void CaptureLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                _ = Acquire(100, FrameCaptured is { } handler ? handler.Invoke : static (in VideoFrame _) => { }, throttle: true);
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                var error = ex as MediaToolkitNetException ?? new MediaToolkitNetException(BackendName, $"desktop capture stopped: {ex.Message}", 0);
                SafeEvents.RaiseFailed(Failed, this, error);
            }
        }
    }

    // One round of AcquireNextFrame. Returns true when a frame was handed over.
    private bool Acquire(int timeoutMs, VideoFrameHandler handler, bool throttle)
    {
        var info = stackalloc byte[64];
        void* resource;
        var hr = ((delegate* unmanaged[Stdcall]<void*, uint, byte*, void**, int>)Com.Slot(_duplication.Pointer, Dxgi.DuplicationAcquireNextFrame))(
            _duplication.Pointer, (uint)timeoutMs, info, &resource);
        if (hr == Dxgi.ErrorWaitTimeout)
        {
            return false;
        }

        if (hr == Dxgi.ErrorAccessLost)
        {
            // A mode change or the secure desktop: the old duplication is dead, make another.
            _staging.Dispose();
            Duplicate();
            return false;
        }

        HResult.ThrowIfFailed(hr, "IDXGIOutputDuplication::AcquireNextFrame");
        try
        {
            // DXGI_OUTDUPL_FRAME_INFO.LastPresentTime (offset 0) is zero when only the pointer moved: no new picture.
            var picture = *(long*)info != 0;
            var now = _clock.Elapsed;
            if (!picture || (throttle && _lastDelivered != TimeSpan.MinValue && now - _lastDelivered < _interval))
            {
                return false;
            }

            using var resourcePtr = new ComPtr(resource);
            using var texture = resourcePtr.QueryInterface(Dxgi.IidTexture2D);
            Deliver(texture.Pointer, now, handler);
            _lastDelivered = now;
            return true;
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(_duplication.Pointer, Dxgi.DuplicationReleaseFrame))(_duplication.Pointer);
        }
    }

    // D3D11_TEXTURE2D_DESC: Width 0, Height 4, MipLevels 8, ArraySize 12, Format 16, SampleDesc.Count 20, .Quality 24,
    // Usage 28, BindFlags 32, CPUAccessFlags 36, MiscFlags 40; 44 bytes.
    // D3D11_MAPPED_SUBRESOURCE: void *pData 0, UINT RowPitch 8, UINT DepthPitch 12; 16 bytes.
    private void Deliver(void* texture, TimeSpan timestamp, VideoFrameHandler handler)
    {
        var description = stackalloc uint[11];
        ((delegate* unmanaged[Stdcall]<void*, uint*, void>)Com.Slot(texture, Dxgi.Texture2DGetDesc))(texture, description);
        if (description[4] != Dxgi.FormatB8G8R8A8)
        {
            throw new MediaToolkitNetException(BackendName, $"the desktop image has the DXGI format {description[4]}, not BGRA", 0);
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
            var format = new VideoFormat((int)description[0], (int)description[1], PixelFormat.Bgra32, Format.FrameRate);
            var planes = stackalloc nint[1] { (nint)data };
            var strides = stackalloc int[1] { (int)stride };
            if (!FrameBounds.FitsIn(format, new ReadOnlySpan<nint>(planes, 1), new ReadOnlySpan<int>(strides, 1), 1, (nint)data, (long)stride * description[1]))
            {
                return;
            }

            var frame = new VideoFrame(format, timestamp, data, (int)stride);
            handler(in frame);
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
        description[8] = 0; // BindFlags: a staging texture binds to nothing
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
