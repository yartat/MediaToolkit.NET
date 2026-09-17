using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions.Capture;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>
/// Plays PCM through a WASAPI render endpoint in shared mode.
/// </summary>
/// <remarks>
/// The client runs event-driven: <see cref="Write"/> blocks on the endpoint
/// event while the buffer is full, which keeps latency at the requested buffer
/// duration without a polling loop.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class WasapiRenderer : IAudioRenderer
{
    private readonly EventWaitHandle _bufferReady = new(false, EventResetMode.AutoReset);

    private ComPtr _device;
    private ComPtr _client;
    private ComPtr _render;
    private uint _bufferFrames;
    private int _blockAlign;
    private bool _running;
    private bool _disposed;

    /// <summary>Opens the endpoint described by <paramref name="settings"/>.</summary>
    public WasapiRenderer(AudioCaptureSettings settings)
    {
        // COM stays initialised for the life of the process: the endpoint objects
        // created here outlive this call, so tearing the apartment back down
        // would invalidate them.
        Ole32.Initialize();

        try
        {
            _device = WasapiDeviceEnumerator.OpenDevice(settings.Device, DataFlow.Render);

            HResult.ThrowIfFailed(
                Wasapi.Activate(_device.Pointer, WinGuids.IAudioClient, out var client), "IMMDevice::Activate(IAudioClient)");
            _client = new ComPtr(client);

            Format = Configure(settings);

            HResult.ThrowIfFailed(
                Wasapi.GetService(_client.Pointer, WinGuids.IAudioRenderClient, out var render),
                "IAudioClient::GetService(IAudioRenderClient)");
            _render = new ComPtr(render);

            HResult.ThrowIfFailed(Wasapi.GetBufferSize(_client.Pointer, out _bufferFrames), "IAudioClient::GetBufferSize");
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

    /// <inheritdoc />
    public bool IsRunning => _running;

    /// <summary>Endpoint buffer size, in sample frames.</summary>
    public uint BufferFrames => _bufferFrames;

    private AudioFormat Configure(AudioCaptureSettings settings)
    {
        var requested = WaveFormatEx.From(settings.Format);
        var chosen = requested;

        // Shared mode only accepts the mix format or something the engine can
        // convert to it, so fall back to the mix format when the request is
        // rejected.
        WaveFormatEx* closest = null;
        var hr = Wasapi.IsFormatSupported(_client.Pointer, AudioShareMode.Shared, &requested, &closest);
        WaveFormatEx* mixFormat = null;

        try
        {
            if (HResult.Failed(hr))
            {
                if (closest is not null)
                {
                    chosen = *closest;
                }
                else
                {
                    HResult.ThrowIfFailed(Wasapi.GetMixFormat(_client.Pointer, out mixFormat), "IAudioClient::GetMixFormat");
                    chosen = *mixFormat;
                }
            }

            var duration = settings.BufferDuration <= TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(20)
                : settings.BufferDuration;

            var source = mixFormat is not null ? mixFormat : closest is not null ? closest : &chosen;
            HResult.ThrowIfFailed(
                Wasapi.Initialize(
                    _client.Pointer,
                    AudioShareMode.Shared,
                    Wasapi.StreamFlagsEventCallback | Wasapi.StreamFlagsAutoConvertPcm | Wasapi.StreamFlagsSrcDefaultQuality,
                    duration.Ticks,
                    0,
                    source),
                "IAudioClient::Initialize");

            HResult.ThrowIfFailed(
                Wasapi.SetEventHandle(_client.Pointer, _bufferReady.SafeWaitHandle.DangerousGetHandle()),
                "IAudioClient::SetEventHandle");

            var format = Wasapi.ReadFormat(source);
            _blockAlign = source->BlockAlign;
            return format;
        }
        finally
        {
            if (closest is not null)
            {
                Ole32.CoTaskMemFree(closest);
            }

            if (mixFormat is not null)
            {
                Ole32.CoTaskMemFree(mixFormat);
            }
        }
    }

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
        {
            return;
        }

        HResult.ThrowIfFailed(Wasapi.Start(_client.Pointer), "IAudioClient::Start");
        _running = true;
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        HResult.ThrowIfFailed(Wasapi.Stop(_client.Pointer), "IAudioClient::Stop");
        HResult.ThrowIfFailed(Wasapi.Reset(_client.Pointer), "IAudioClient::Reset");
    }

    /// <inheritdoc />
    public int Write(ReadOnlySpan<byte> interleaved)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_blockAlign <= 0)
        {
            return 0;
        }

        var offset = 0;
        while (offset < interleaved.Length)
        {
            var free = FreeFrames();
            if (free == 0)
            {
                _bufferReady.WaitOne(100);
                continue;
            }

            var pending = (interleaved.Length - offset) / _blockAlign;
            if (pending == 0)
            {
                // A partial frame cannot be submitted; leave it to the caller.
                break;
            }

            var frames = (uint)Math.Min(free, pending);
            HResult.ThrowIfFailed(
                Wasapi.GetRenderBuffer(_render.Pointer, frames, out var destination), "IAudioRenderClient::GetBuffer");

            var bytes = (int)frames * _blockAlign;
            interleaved.Slice(offset, bytes).CopyTo(new Span<byte>(destination, bytes));
            HResult.ThrowIfFailed(
                Wasapi.ReleaseRenderBuffer(_render.Pointer, frames, 0), "IAudioRenderClient::ReleaseBuffer");

            offset += bytes;
        }

        return offset;
    }

    /// <inheritdoc />
    public void Drain()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (_running && FreeFrames() < _bufferFrames)
        {
            _bufferReady.WaitOne(100);
        }
    }

    private uint FreeFrames()
    {
        HResult.ThrowIfFailed(Wasapi.GetCurrentPadding(_client.Pointer, out var padding), "IAudioClient::GetCurrentPadding");
        return _bufferFrames > padding ? _bufferFrames - padding : 0;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_running && !_client.IsNull)
        {
            Wasapi.Stop(_client.Pointer);
            _running = false;
        }

        _render.Dispose();
        _client.Dispose();
        _device.Dispose();
        _bufferReady.Dispose();
    }
}
