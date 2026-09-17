using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Capture;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>
/// Captures PCM from a WASAPI endpoint in shared mode.
/// </summary>
/// <remarks>
/// Setting <see cref="Loopback"/> before construction turns a render endpoint
/// into a capture source, which is how "record what the speakers are playing"
/// works on Windows.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class WasapiCapture : IAudioCapture
{
    private readonly EventWaitHandle _dataReady = new(false, EventResetMode.AutoReset);
    private readonly Lock _gate = new();
    private readonly bool _loopback;

    private ComPtr _device;
    private ComPtr _client;
    private ComPtr _capture;
    private int _blockAlign;
    private Thread? _worker;
    private CancellationTokenSource? _cancellation;
    private byte* _silence;
    private int _silenceBytes;
    private bool _disposed;

    /// <summary>Opens the endpoint described by <paramref name="settings"/>.</summary>
    /// <param name="settings">Device and requested format.</param>
    /// <param name="loopback">
    /// When true the device is a render endpoint captured in loopback mode.
    /// </param>
    public WasapiCapture(AudioCaptureSettings settings, bool loopback = false)
    {
        _loopback = loopback;

        // As in the renderer, COM is initialised but never torn back down here.
        Ole32.Initialize();

        try
        {
            _device = WasapiDeviceEnumerator.OpenDevice(
                settings.Device, loopback ? DataFlow.Render : DataFlow.Capture);

            HResult.ThrowIfFailed(
                Wasapi.Activate(_device.Pointer, WinGuids.IAudioClient, out var client),
                "IMMDevice::Activate(IAudioClient)");
            _client = new ComPtr(client);

            Format = Configure(settings);

            HResult.ThrowIfFailed(
                Wasapi.GetService(_client.Pointer, WinGuids.IAudioCaptureClient, out var capture),
                "IAudioClient::GetService(IAudioCaptureClient)");
            _capture = new ComPtr(capture);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public string Backend => WasapiDeviceEnumerator.BackendName;

    /// <inheritdoc />
    public AudioFormat Format { get; private set; }

    /// <summary>True when this instance captures the output of a render endpoint.</summary>
    public bool Loopback => _loopback;

    /// <inheritdoc />
    public bool IsRunning => _worker is not null;

    /// <inheritdoc />
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <inheritdoc />
    public event AudioFrameHandler? FrameCaptured;

    private AudioFormat Configure(AudioCaptureSettings settings)
    {
        // Loopback capture only works with the endpoint mix format, and shared
        // mode capture is most reliable with it too.
        HResult.ThrowIfFailed(Wasapi.GetMixFormat(_client.Pointer, out var mixFormat), "IAudioClient::GetMixFormat");

        try
        {
            var duration = settings.BufferDuration <= TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(20)
                : settings.BufferDuration;

            var flags = Wasapi.StreamFlagsEventCallback;
            if (_loopback)
            {
                // A loopback stream is driven by the render endpoint, so it must
                // not ask for its own event callbacks.
                flags = Wasapi.StreamFlagsLoopback;
            }

            HResult.ThrowIfFailed(
                Wasapi.Initialize(_client.Pointer, AudioShareMode.Shared, flags, duration.Ticks, 0, mixFormat),
                "IAudioClient::Initialize");

            if (!_loopback)
            {
                HResult.ThrowIfFailed(
                    Wasapi.SetEventHandle(_client.Pointer, _dataReady.SafeWaitHandle.DangerousGetHandle()),
                    "IAudioClient::SetEventHandle");
            }

            _blockAlign = mixFormat->BlockAlign;
            return Wasapi.ReadFormat(mixFormat);
        }
        finally
        {
            Ole32.CoTaskMemFree(mixFormat);
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

            HResult.ThrowIfFailed(Wasapi.Start(_client.Pointer), "IAudioClient::Start");

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _worker = new Thread(() => CaptureLoop(token))
            {
                IsBackground = true,
                Name = "MediaToolkitNet.WASAPI capture",
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

        if (worker is null)
        {
            return;
        }

        _dataReady.Set();
        worker.Join(TimeSpan.FromSeconds(2));

        if (!_client.IsNull)
        {
            Wasapi.Stop(_client.Pointer);
            Wasapi.Reset(_client.Pointer);
        }

        lock (_gate)
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private void CaptureLoop(CancellationToken token)
    {
        Ole32.Initialize();
        var position = TimeSpan.Zero;

        try
        {
            while (!token.IsCancellationRequested)
            {
                // Loopback streams get no events, so they are polled instead.
                if (_loopback)
                {
                    Thread.Sleep(5);
                }
                else
                {
                    _dataReady.WaitOne(100);
                }

                while (!token.IsCancellationRequested)
                {
                    HResult.ThrowIfFailed(
                        Wasapi.GetNextPacketSize(_capture.Pointer, out var packetFrames),
                        "IAudioCaptureClient::GetNextPacketSize");

                    if (packetFrames == 0)
                    {
                        break;
                    }

                    HResult.ThrowIfFailed(
                        Wasapi.GetCaptureBuffer(_capture.Pointer, out var data, out var frames, out var flags, out _, out _),
                        "IAudioCaptureClient::GetBuffer");

                    try
                    {
                        if (frames > 0)
                        {
                            Deliver(data, (int)frames, flags, position);
                            position += Format.DurationOf(frames);
                        }
                    }
                    finally
                    {
                        Wasapi.ReleaseCaptureBuffer(_capture.Pointer, frames);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex as MediaToolkitNetException ?? new MediaToolkitNetException("Audio capture error.", ex));
        }
    }

    private void Deliver(byte* data, int frames, uint flags, TimeSpan timestamp)
    {
        var handler = FrameCaptured;
        if (handler is null)
        {
            return;
        }

        if ((flags & Wasapi.BufferFlagsSilent) != 0)
        {
            // WASAPI signals silence by flag and may leave the buffer untouched,
            // so hand out explicit zeros instead. The scratch block is native
            // memory rather than a stackalloc because a packet can be large.
            var silent = new AudioFrame(Format, timestamp, frames, EnsureSilence(frames * _blockAlign));
            handler(in silent);
            return;
        }

        var frame = new AudioFrame(Format, timestamp, frames, data);
        handler(in frame);
    }

    private byte* EnsureSilence(int bytes)
    {
        if (_silence is null || _silenceBytes < bytes)
        {
            if (_silence is not null)
            {
                NativeMemory.Free(_silence);
            }

            _silence = (byte*)NativeMemory.AllocZeroed((nuint)bytes);
            _silenceBytes = bytes;
        }

        return _silence;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

        if (_silence is not null)
        {
            NativeMemory.Free(_silence);
            _silence = null;
            _silenceBytes = 0;
        }

        _capture.Dispose();
        _client.Dispose();
        _device.Dispose();
        _dataReady.Dispose();
    }
}
