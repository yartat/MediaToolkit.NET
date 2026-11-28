#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Playback;
using MediaToolkitNet.Interop;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>
/// Plays a file or URL with the Media Foundation Media Engine. Windows plays the sound itself on the default device; the
/// picture is handed back as <see cref="PixelFormat.Bgra32"/> frames from the engine's frame server.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AudioFrameDecoded"/> is never raised: the Media Engine does not give out the sound it plays. Take audio from
/// the FFmpeg player, or capture it with <see cref="WasapiCapture.ForProcess"/>. Nothing is drawn into a window.
/// </para>
/// <para>
/// Which files open is what the codecs installed in Windows can decode. Frames are raised on a thread of the player's own,
/// valid for the call only, and state changes on a Media Foundation thread. A handler may call <see cref="Dispose"/>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class MediaFoundationPlayer : IMediaPlayer
{
    private const string BackendName = "windows";
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);

    [ThreadStatic]
    private static bool _inCallback;

    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _loaded = new();
    private readonly ManualResetEventSlim _seeked = new();

    private ComPtr _engine;
    private ComPtr _manager;
    private ComPtr _device;
    private ComPtr _context;
    private ComPtr _target;
    private ComPtr _staging;
    private void* _notify;
    private Thread? _pump;
    private CancellationTokenSource? _stopPump;
    private PlaybackState _state = PlaybackState.Closed;
    private string? _error;
    private double _volume = 1.0;
    private int _width;
    private int _height;
    private bool _opening;
    private bool _disposed;

    /// <inheritdoc />
    public string Backend => BackendName;

    /// <inheritdoc />
    public PlaybackState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public TimeSpan Duration => _engine.IsNull ? TimeSpan.Zero : Seconds(GetDouble(MediaEngine.GetDuration));

    /// <inheritdoc />
    public TimeSpan Position => _engine.IsNull ? TimeSpan.Zero : Seconds(GetDouble(MediaEngine.GetCurrentTime));

    /// <inheritdoc />
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0.0, 1.0);
            if (!_engine.IsNull)
            {
                SetDouble(MediaEngine.SetVolume, _volume);
            }
        }
    }

    /// <inheritdoc />
    public bool CanSeek => !_engine.IsNull && Duration > TimeSpan.Zero;

    /// <inheritdoc />
    public event EventHandler<PlaybackStateChange>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <inheritdoc />
    public event VideoFrameHandler? VideoFrameDecoded;

#pragma warning disable CS0067 // The Media Engine plays the sound itself and hands none out; see the class remarks.
    /// <inheritdoc />
    public event AudioFrameHandler? AudioFrameDecoded;
#pragma warning restore CS0067

    /// <inheritdoc />
    public void Open(string uri)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        Close();
        var isAddress = uri.Contains("://", StringComparison.Ordinal);
        var location = isAddress ? uri : Path.GetFullPath(uri);
        if (!isAddress && !File.Exists(location))
        {
            throw new MediaToolkitNetException(BackendName, $"could not open {uri}: there is no such file", 0);
        }

        Ole32.Initialize();
        MfApi.Startup();
        try
        {
            _opening = true;
            _error = null;
            _loaded.Reset();
            CreateEngine();

            var source = Marshal.StringToBSTR(location);
            try
            {
                HResult.ThrowIfFailed(
                    ((delegate* unmanaged[Stdcall]<void*, nint, int>)Com.Slot(_engine.Pointer, MediaEngine.SetSource))(_engine.Pointer, source),
                    "IMFMediaEngine::SetSource");
            }
            finally
            {
                Marshal.FreeBSTR(source);
            }

            HResult.ThrowIfFailed(Call(MediaEngine.Load), "IMFMediaEngine::Load");
            if (!_loaded.Wait(OpenTimeout))
            {
                throw new MediaToolkitNetException(BackendName, $"could not open {uri}: the media engine did not load it within {OpenTimeout.TotalSeconds:0} s", 0);
            }

            if (_error is { } error)
            {
                throw new MediaToolkitNetException(BackendName, $"could not open {uri}: {error}", 0);
            }

            SetDouble(MediaEngine.SetVolume, _volume);
            StartPump();
        }
        catch
        {
            Close();
            throw;
        }
        finally
        {
            _opening = false;
        }

        TransitionTo(PlaybackState.Stopped);
    }

    /// <inheritdoc />
    public void Play()
    {
        Require();
        HResult.ThrowIfFailed(Call(MediaEngine.Play), "IMFMediaEngine::Play");
        TransitionTo(PlaybackState.Playing);
    }

    /// <inheritdoc />
    public void Pause()
    {
        if (_engine.IsNull || State != PlaybackState.Playing)
        {
            return;
        }

        HResult.ThrowIfFailed(Call(MediaEngine.Pause), "IMFMediaEngine::Pause");
        TransitionTo(PlaybackState.Paused);
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_engine.IsNull || State == PlaybackState.Faulted)
        {
            return;
        }

        HResult.ThrowIfFailed(Call(MediaEngine.Pause), "IMFMediaEngine::Pause");
        Seek(TimeSpan.Zero);
        TransitionTo(PlaybackState.Stopped);
    }

    /// <inheritdoc />
    public void Seek(TimeSpan position)
    {
        Require();
        if (!CanSeek)
        {
            throw new InvalidOperationException("Seeking is unavailable: the media has no known length.");
        }

        var wasEnded = State == PlaybackState.Ended;
        if (wasEnded)
        {
            HResult.ThrowIfFailed(Call(MediaEngine.Pause), "IMFMediaEngine::Pause");
        }

        _seeked.Reset();
        SetDouble(MediaEngine.SetCurrentTime, Math.Max(position.TotalSeconds, 0));

        // A seek finishes asynchronously; the position is the target once it has.
        _seeked.Wait(TimeSpan.FromSeconds(3));
        if (wasEnded)
        {
            TransitionTo(PlaybackState.Paused);
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
        if (_inCallback)
        {
            // Closing waits for the thread this runs on, so it is done from another.
            ThreadPool.QueueUserWorkItem(_ => Close());
        }
        else
        {
            Close();
        }

        TransitionTo(PlaybackState.Closed);
    }

    private static TimeSpan Seconds(double value) =>
        double.IsFinite(value) && value > 0 ? TimeSpan.FromSeconds(value) : TimeSpan.Zero;

    private void Require()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_engine.IsNull)
        {
            throw new InvalidOperationException("Open the media first.");
        }

        if (State == PlaybackState.Faulted)
        {
            throw new InvalidOperationException("The player faulted; open the media again.");
        }
    }

    private int Call(int slot) => ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(_engine.Pointer, slot))(_engine.Pointer);

    private double GetDouble(int slot) => ((delegate* unmanaged[Stdcall]<void*, double>)Com.Slot(_engine.Pointer, slot))(_engine.Pointer);

    private void SetDouble(int slot, double value) =>
        HResult.ThrowIfFailed(((delegate* unmanaged[Stdcall]<void*, double, int>)Com.Slot(_engine.Pointer, slot))(_engine.Pointer, value), "IMFMediaEngine");

    // A D3D11 device the engine draws its frames into, handed over through a DXGI device manager, then the engine itself.
    private void CreateEngine()
    {
        CreateDevice();

        uint token;
        void* manager;
        HResult.ThrowIfFailed(MediaEngine.MFCreateDXGIDeviceManager(&token, &manager), "MFCreateDXGIDeviceManager");
        _manager = new ComPtr(manager);
        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, void*, uint, int>)Com.Slot(manager, MediaEngine.ManagerResetDevice))(manager, _device.Pointer, token),
            "IMFDXGIDeviceManager::ResetDevice");

        _notify = MediaEngine.Notify.Create(OnEngineEvent);

        void* attributes;
        HResult.ThrowIfFailed(MfApi.MFCreateAttributes(&attributes, 3), "MFCreateAttributes");
        using var held = new ComPtr(attributes);
        HResult.ThrowIfFailed(Mf.SetUnknown(attributes, MediaEngine.AttributeCallback, _notify), "SetUnknown(callback)");
        HResult.ThrowIfFailed(Mf.SetUnknown(attributes, MediaEngine.AttributeDxgiManager, manager), "SetUnknown(DXGI manager)");
        HResult.ThrowIfFailed(Mf.SetUint32(attributes, MediaEngine.AttributeVideoOutputFormat, Dxgi.FormatB8G8R8A8), "SetUINT32(output format)");

        using var factory = Ole32.CreateInstance(MediaEngine.ClassFactoryClsid, MediaEngine.IidClassFactory);
        void* engine;
        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, uint, void*, void**, int>)Com.Slot(factory.Pointer, MediaEngine.FactoryCreateInstance))(
                factory.Pointer, 0, attributes, &engine),
            "IMFMediaEngineClassFactory::CreateInstance");
        _engine = new ComPtr(engine);
    }

    private void CreateDevice()
    {
        void* device = null;
        void* context = null;
        int level;
        var flags = Dxgi.CreateDeviceBgraSupport | MediaEngine.CreateDeviceVideoSupport;
        var hr = Dxgi.D3D11CreateDevice(null, MediaEngine.DriverTypeHardware, 0, flags, null, 0, Dxgi.SdkVersion, &device, &level, &context);
        if (HResult.Failed(hr))
        {
            // A machine with no usable GPU, such as a virtual one, still has the software rasteriser.
            hr = Dxgi.D3D11CreateDevice(null, MediaEngine.DriverTypeWarp, 0, Dxgi.CreateDeviceBgraSupport, null, 0, Dxgi.SdkVersion, &device, &level, &context);
        }

        HResult.ThrowIfFailed(hr, "D3D11CreateDevice");
        _device = new ComPtr(device);
        _context = new ComPtr(context);

        // The engine uses the device from its own threads.
        if (HResult.Succeeded(Com.QueryInterface(device, MediaEngine.IidMultithread, out var multithread)))
        {
            ((delegate* unmanaged[Stdcall]<void*, int, int>)Com.Slot(multithread, MediaEngine.MultithreadSetProtected))(multithread, 1);
            Com.Release(multithread);
        }
    }

    // On a Media Foundation thread.
    private void OnEngineEvent(uint mediaEvent, nuint param1, uint param2)
    {
        _inCallback = true;
        try
        {
            switch (mediaEvent)
            {
                case MediaEngine.EventLoadedMetadata:
                    _loaded.Set();
                    break;
                case MediaEngine.EventSeeked:
                    _seeked.Set();
                    break;
                case MediaEngine.EventEnded:
                    TransitionTo(PlaybackState.Ended);
                    break;
                case MediaEngine.EventError:
                    var text = $"media engine error {(uint)param1} (0x{param2:X8})";
                    _error = text;
                    _loaded.Set();
                    if (!_opening && State != PlaybackState.Faulted)
                    {
                        SafeEvents.RaiseFailed(Failed, this, new MediaToolkitNetException(BackendName, text, unchecked((int)param2)));
                        TransitionTo(PlaybackState.Faulted);
                    }

                    break;
            }
        }
        finally
        {
            _inCallback = false;
        }
    }

    private void StartPump()
    {
        if (((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(_engine.Pointer, MediaEngine.HasVideo))(_engine.Pointer) == 0)
        {
            return;
        }

        uint width;
        uint height;
        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, uint*, uint*, int>)Com.Slot(_engine.Pointer, MediaEngine.GetNativeVideoSize))(_engine.Pointer, &width, &height),
            "IMFMediaEngine::GetNativeVideoSize");
        _width = (int)width;
        _height = (int)height;

        // D3D11_TEXTURE2D_DESC: Width, Height, MipLevels, ArraySize, Format, SampleDesc.Count, .Quality, Usage, BindFlags,
        // CPUAccessFlags, MiscFlags. The engine draws into the first; the second is the copy the CPU can read.
        var description = stackalloc uint[11] { width, height, 1, 1, Dxgi.FormatB8G8R8A8, 1, 0, 0, MediaEngine.BindShaderResourceAndRenderTarget, 0, 0 };
        _target = CreateTexture(description);
        description[7] = Dxgi.UsageStaging;
        description[8] = 0;
        description[9] = Dxgi.CpuAccessRead;
        _staging = CreateTexture(description);

        _stopPump = new CancellationTokenSource();
        var token = _stopPump.Token;
        _pump = new Thread(() => Pump(token)) { IsBackground = true, Name = "MediaToolkitNet.MediaEngine frames" };
        _pump.Start();
    }

    private ComPtr CreateTexture(uint* description)
    {
        void* texture;
        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, uint*, void*, void**, int>)Com.Slot(_device.Pointer, Dxgi.DeviceCreateTexture2D))(
                _device.Pointer, description, null, &texture),
            "ID3D11Device::CreateTexture2D");
        return new ComPtr(texture);
    }

    // The frame server is polled: OnVideoStreamTick answers S_OK when a new frame is due. It is asked in every state and the
    // frame is handed out only while playing: a frame left unasked for stays due, so the one from opening came out first after a
    // seek and a Play, stamped zero.
    private void Pump(CancellationToken token)
    {
        Ole32.Initialize();
        try
        {
            while (!token.IsCancellationRequested)
            {
                long time;
                var due = ((delegate* unmanaged[Stdcall]<void*, long*, int>)Com.Slot(_engine.Pointer, MediaEngine.OnVideoStreamTick))(_engine.Pointer, &time) == HResult.Ok;
                if (due && State == PlaybackState.Playing)
                {
                    Deliver(new TimeSpan(time), token);
                }
                else
                {
                    Thread.Sleep(4);
                }
            }
        }
        catch (Exception ex)
        {
            SafeEvents.RaiseFailed(Failed, this, new MediaToolkitNetException(BackendName, $"reading a video frame failed: {ex.Message}", 0));
        }
    }

    private void Deliver(TimeSpan timestamp, CancellationToken token)
    {
        var rectangle = stackalloc int[4] { 0, 0, _width, _height };
        var hr = ((delegate* unmanaged[Stdcall]<void*, void*, void*, int*, void*, int>)Com.Slot(_engine.Pointer, MediaEngine.TransferVideoFrame))(
            _engine.Pointer, _target.Pointer, null, rectangle, null);
        if (HResult.Failed(hr) || VideoFrameDecoded is not { } handler || token.IsCancellationRequested)
        {
            return;
        }

        ((delegate* unmanaged[Stdcall]<void*, void*, void*, void>)Com.Slot(_context.Pointer, Dxgi.ContextCopyResource))(
            _context.Pointer, _staging.Pointer, _target.Pointer);

        // D3D11_MAPPED_SUBRESOURCE: void *pData 0, UINT RowPitch 8.
        var mapped = stackalloc byte[16];
        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, void*, uint, uint, uint, byte*, int>)Com.Slot(_context.Pointer, Dxgi.ContextMap))(
                _context.Pointer, _staging.Pointer, 0, Dxgi.MapRead, 0, mapped),
            "ID3D11DeviceContext::Map");
        try
        {
            var data = *(byte**)mapped;
            var stride = *(uint*)(mapped + 8);
            var format = new VideoFormat(_width, _height, PixelFormat.Bgra32, Rational.Zero);
            var planes = stackalloc nint[1] { (nint)data };
            var strides = stackalloc int[1] { (int)stride };
            if (!FrameBounds.FitsIn(format, new ReadOnlySpan<nint>(planes, 1), new ReadOnlySpan<int>(strides, 1), 1, (nint)data, (long)stride * _height))
            {
                return;
            }

            var frame = new VideoFrame(format, timestamp, data, (int)stride);
            _inCallback = true;
            try
            {
                handler(in frame);
            }
            finally
            {
                _inCallback = false;
            }
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<void*, void*, uint, void>)Com.Slot(_context.Pointer, Dxgi.ContextUnmap))(
                _context.Pointer, _staging.Pointer, 0);
        }
    }

    private void TransitionTo(PlaybackState next)
    {
        PlaybackState previous;
        lock (_gate)
        {
            // A fault is final until the media is opened again, which passes through Closed first.
            if (_state == next
                || (_state == PlaybackState.Closed && _disposed && next != PlaybackState.Closed)
                || (_state == PlaybackState.Faulted && next != PlaybackState.Closed))
            {
                return;
            }

            previous = _state;
            _state = next;
        }

        var handler = StateChanged;
        if (handler is null)
        {
            return;
        }

        var outer = _inCallback;
        _inCallback = true;
        try
        {
            handler(this, new PlaybackStateChange(previous, next));
        }
        catch (Exception ex)
        {
            SafeEvents.RaiseFailed(Failed, this, new MediaToolkitNetException(BackendName, $"a StateChanged handler threw: {ex.Message}", 0));
        }
        finally
        {
            _inCallback = outer;
        }
    }

    // The frame thread goes first, then the engine, which makes no more calls once it is shut down, then what it drew into.
    private void Close()
    {
        _stopPump?.Cancel();
        if (_pump is not null && _pump != Thread.CurrentThread)
        {
            _pump.Join();
        }

        _pump = null;
        _stopPump?.Dispose();
        _stopPump = null;

        if (!_engine.IsNull)
        {
            Call(MediaEngine.Shutdown);
            _engine.Dispose();
        }

        if (_notify is not null)
        {
            Com.Release(_notify);
            _notify = null;
        }

        _staging.Dispose();
        _target.Dispose();
        _manager.Dispose();
        _context.Dispose();
        _device.Dispose();

        lock (_gate)
        {
            if (!_disposed)
            {
                _state = PlaybackState.Closed;
            }
        }
    }
}
