#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Capture;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>
/// Plays PCM through a WASAPI render endpoint, shared with other applications or alone (<see cref="WasapiMode"/>).
/// </summary>
/// <remarks>
/// The client runs event-driven: <see cref="Write"/> blocks on the endpoint
/// event while the buffer is full, which keeps latency at the requested buffer
/// duration without a polling loop.
/// <para>
/// In <see cref="WasapiMode.Exclusive"/> the device takes one whole buffer at a time: <see cref="Write"/> collects what it
/// is given into periods and waits for the device before each one, and a caller that does not keep up is heard as a gap or a
/// repeat. In the other modes than <see cref="WasapiMode.Shared"/> the format asked for is used as it is or refused.
/// </para>
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

    // Exclusive mode: the period being collected, how much of it there is, and whether the device has been given one.
    private byte[] _period = [];
    private int _staged;
    private bool _primed;
    private ComPtr _clock;
    private ulong _submitted;

    /// <summary>Opens the endpoint described by <paramref name="settings"/>.</summary>
    /// <param name="settings">
    /// Device, format and buffer. In <see cref="WasapiMode.Shared"/> the format may be replaced by one the engine takes
    /// (see <see cref="Format"/>). In exclusive mode the buffer duration is the period, raised to the device's minimum when
    /// shorter; in low-latency mode it picks the nearest period the engine offers, and zero the shortest.
    /// </param>
    /// <param name="mode">How to share the endpoint; the other parameters are as for shared mode.</param>
    public WasapiRenderer(AudioCaptureSettings settings, WasapiMode mode = WasapiMode.Shared)
    {
        Mode = mode;

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

            if (mode == WasapiMode.Shared)
            {
                Format = Configure(settings);
                PeriodFrames = WasapiStreams.SharedPeriod(_client.Pointer, Format.SampleRate);
            }
            else if (mode == WasapiMode.LowLatencyShared)
            {
                Format = ConfigureLowLatency(settings);
            }
            else
            {
                PeriodFrames = WasapiStreams.InitializeExclusive(
                    _device.Pointer, ref _client, settings.Format, settings.BufferDuration, _bufferReady.SafeWaitHandle.DangerousGetHandle());
                Format = settings.Format;
                _blockAlign = Format.Channels * Format.SampleFormat.BytesPerSample();
                _period = new byte[PeriodFrames * _blockAlign];
                _clock = WasapiStreams.OpenClock(_client.Pointer);
            }

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

    /// <summary>How the stream shares its endpoint.</summary>
    public WasapiMode Mode { get; }

    /// <summary>How many sample frames the device takes at a time.</summary>
    public uint PeriodFrames { get; private set; }

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

    private AudioFormat ConfigureLowLatency(AudioCaptureSettings settings)
    {
        HResult.ThrowIfFailed(Wasapi.GetMixFormat(_client.Pointer, out var mix), "IAudioClient::GetMixFormat");
        try
        {
            var format = Wasapi.ReadFormat(mix);
            if (settings.Format != format)
            {
                throw new MediaToolkitNetException(
                    Backend,
                    $"low-latency shared mode takes only the device's mix format: {format.SampleRate} Hz, " +
                    $"{format.Channels} channels, {format.SampleFormat}",
                    Wasapi.UnsupportedFormat);
            }

            PeriodFrames = WasapiStreams.InitializeLowLatency(
                _client.Pointer, mix, settings.BufferDuration, _bufferReady.SafeWaitHandle.DangerousGetHandle());
            _blockAlign = mix->BlockAlign;
            return format;
        }
        finally
        {
            Ole32.CoTaskMemFree(mix);
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

        if (Mode == WasapiMode.Exclusive && !_primed)
        {
            // The device plays its first buffer as soon as it starts: give it silence rather than whatever is there.
            HResult.ThrowIfFailed(Wasapi.GetRenderBuffer(_render.Pointer, _bufferFrames, out _), "IAudioRenderClient::GetBuffer");
            HResult.ThrowIfFailed(
                Wasapi.ReleaseRenderBuffer(_render.Pointer, _bufferFrames, Wasapi.BufferFlagsSilent),
                "IAudioRenderClient::ReleaseBuffer");
            _primed = true;
            _submitted += _bufferFrames;
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
        _primed = false;
        _staged = 0;
        _submitted = 0;
        _bufferReady.Reset();
    }

    /// <inheritdoc />
    public int Write(ReadOnlySpan<byte> interleaved)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_blockAlign <= 0)
        {
            return 0;
        }

        if (Mode == WasapiMode.Exclusive)
        {
            return WritePeriods(interleaved);
        }

        var offset = 0;
        while (offset < interleaved.Length)
        {
            var free = FreeFrames();
            if (free == 0)
            {
                // Nothing drains a stopped endpoint, so waiting would never end;
                // the caller gets the count written and decides what to do.
                if (!_running)
                {
                    break;
                }

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

    // Takes whole sample frames, collects them into periods and submits each as it fills.
    private int WritePeriods(ReadOnlySpan<byte> interleaved)
    {
        var whole = interleaved.Length - (interleaved.Length % _blockAlign);
        var offset = 0;
        while (offset < whole && (_staged < _period.Length || TrySubmitPeriod()))
        {
            var take = Math.Min(_period.Length - _staged, whole - offset);
            interleaved.Slice(offset, take).CopyTo(_period.AsSpan(_staged));
            _staged += take;
            offset += take;
        }

        if (_staged == _period.Length)
        {
            TrySubmitPeriod();
        }

        return offset;
    }

    // False when the device holds a buffer already and is not running, so that none will come free.
    private bool TrySubmitPeriod()
    {
        if (_primed)
        {
            if (!_running)
            {
                return false;
            }

            WaitForDevice();
        }

        HResult.ThrowIfFailed(
            Wasapi.GetRenderBuffer(_render.Pointer, _bufferFrames, out var destination), "IAudioRenderClient::GetBuffer");
        _period.CopyTo(new Span<byte>(destination, _period.Length));
        HResult.ThrowIfFailed(
            Wasapi.ReleaseRenderBuffer(_render.Pointer, _bufferFrames, 0), "IAudioRenderClient::ReleaseBuffer");
        _primed = true;
        _staged = 0;
        _submitted += _bufferFrames;
        return true;
    }

    private void WaitForDevice()
    {
        if (!_bufferReady.WaitOne(2000))
        {
            throw new MediaToolkitNetException(Backend, "the device stopped asking for audio", 0);
        }
    }

    /// <inheritdoc />
    public void Drain()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Mode == WasapiMode.Exclusive)
        {
            if (!_running)
            {
                return;
            }

            if (_staged > 0)
            {
                _period.AsSpan(_staged).Clear();
                _staged = _period.Length;
                TrySubmitPeriod();
            }

            // An event says that the device has taken a buffer, not that it has played it: it takes several ahead, and
            // stopping then would cut off the end. Silence follows until the clock has passed the last of what was written.
            var end = _submitted;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (WasapiStreams.PlayedFrames(_clock.Pointer, Format.SampleRate) < end)
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(5))
                {
                    throw new MediaToolkitNetException(Backend, "the device's clock stopped moving", 0);
                }

                _period.AsSpan().Clear();
                _staged = _period.Length;
                TrySubmitPeriod();
            }

            return;
        }

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

        _clock.Dispose();
        _render.Dispose();
        _client.Dispose();
        _device.Dispose();
        _bufferReady.Dispose();
    }
}
