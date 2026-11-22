#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Capture;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>Which processes <see cref="WasapiCapture.ForProcess"/> listens to.</summary>
public enum ProcessLoopbackMode
{
    /// <summary>The process and every process it started.</summary>
    IncludeProcessTree = 0,

    /// <summary>Everything the system plays except the process and the processes it started.</summary>
    ExcludeProcessTree = 1,
}

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

    // Wraps a client that has been activated and initialised for event-driven capture elsewhere (process loopback).
    private WasapiCapture(ComPtr client, AudioFormat format, int blockAlign)
    {
        _loopback = false;
        Ole32.Initialize();
        try
        {
            _client = client;
            Format = format;
            _blockAlign = blockAlign;

            HResult.ThrowIfFailed(
                Wasapi.SetEventHandle(_client.Pointer, _dataReady.SafeWaitHandle.DangerousGetHandle()),
                "IAudioClient::SetEventHandle");
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

    /// <summary>
    /// True when this Windows can capture the sound of one process (<see cref="ForProcess"/>): Windows 10 version 2004
    /// (build 19041) or newer.
    /// </summary>
    public static bool SupportsProcessLoopback => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    /// <summary>
    /// Captures what one process, and the processes it started, plays: a game, a browser, a player; or everything
    /// except that process. Needs no device: it listens to the audio engine, not to an endpoint, and works whichever
    /// device the process plays through.
    /// </summary>
    /// <param name="processId">The process.</param>
    /// <param name="mode">Whether to capture the process and its children, or the whole system without them.</param>
    /// <param name="format">
    /// What to deliver; Windows converts to it. The default is 48 kHz stereo 32-bit float. The sound of a process
    /// has no format of its own to ask for.
    /// </param>
    /// <param name="bufferDuration">The length of the buffer; the default is 20 ms.</param>
    /// <exception cref="NotSupportedException">This Windows is older than 10 version 2004.</exception>
    /// <exception cref="MediaToolkitNetException">Windows refused, for instance for a process that does not exist.</exception>
    public static WasapiCapture ForProcess(
        int processId,
        ProcessLoopbackMode mode = ProcessLoopbackMode.IncludeProcessTree,
        AudioFormat? format = null,
        TimeSpan? bufferDuration = null)
    {
        if (!SupportsProcessLoopback)
        {
            throw new NotSupportedException("capturing one process's sound needs Windows 10 version 2004 (build 19041) or newer");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(processId);
        Ole32.Initialize();

        // AUDIOCLIENT_ACTIVATION_PARAMS: ActivationType 0 (1 is process loopback), TargetProcessId 4, ProcessLoopbackMode 8.
        // PROPVARIANT (24 bytes on x64): vt 0 (VT_BLOB is 65), blob.cbSize 8, blob.pBlobData 16.
        var activation = stackalloc int[3] { 1, processId, (int)mode };
        var variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        *(ushort*)variant = 65;
        *(uint*)(variant + 8) = 12;
        *(void**)(variant + 16) = activation;

        var client = ActivateAudio.Activate("VAD\\Process_Loopback", WinGuids.IAudioClient, variant, TimeSpan.FromSeconds(10));

        try
        {
            var wanted = format ?? new AudioFormat(48000, 2, SampleFormat.F32);
            var wave = WaveFormatEx.From(wanted);
            var duration = bufferDuration is { } d && d > TimeSpan.Zero ? d : TimeSpan.FromMilliseconds(20);

            // A process loopback client has no mix format; it takes the one asked for and converts to it.
            HResult.ThrowIfFailed(
                Wasapi.Initialize(
                    client.Pointer,
                    AudioShareMode.Shared,
                    Wasapi.StreamFlagsLoopback | Wasapi.StreamFlagsEventCallback | Wasapi.StreamFlagsAutoConvertPcm | Wasapi.StreamFlagsSrcDefaultQuality,
                    duration.Ticks,
                    0,
                    &wave),
                "IAudioClient::Initialize");
            return new WasapiCapture(client, wanted, wave.BlockAlign);
        }
        catch
        {
            client.Dispose();
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
        var comInitialised = Ole32.Initialize();
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
            SafeEvents.RaiseFailed(Failed, this, ex as MediaToolkitNetException ?? new MediaToolkitNetException("Audio capture error.", ex));
        }
        finally
        {
            // The endpoint objects belong to the apartment the opening thread keeps up.
            if (comInitialised)
            {
                Ole32.Uninitialize();
            }
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
