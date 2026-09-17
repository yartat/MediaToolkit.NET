using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Core;
using MediaToolkitNet.Core.Capture;
using MediaToolkitNet.Core.Devices;
using MediaToolkitNet.Core.Formats;
using MediaToolkitNet.Core.Frames;
using MediaToolkitNet.Interop;
using MediaToolkitNet.Linux.Native;

namespace MediaToolkitNet.Linux;

/// <summary>Lists ALSA PCM devices through <c>snd_device_name_hint</c>.</summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class AlsaDeviceEnumerator : IMediaDeviceEnumerator
{
    /// <inheritdoc />
    public IReadOnlyList<MediaDevice> Enumerate(MediaDeviceKind kind = MediaDeviceKind.All)
    {
        Alsa.EnsureLoaded();

        var result = new List<MediaDevice>();
        void** hints;
        Span<byte> scratch = stackalloc byte[16];
        using var iface = new Utf8Scoped("pcm", scratch);

        if (Alsa.snd_device_name_hint(-1, iface.Pointer, &hints) < 0)
        {
            return result;
        }

        try
        {
            for (var entry = hints; *entry is not null; entry++)
            {
                var id = ReadHint(*entry, "NAME");
                if (id is null)
                {
                    continue;
                }

                var description = ReadHint(*entry, "DESC")?.Replace('\n', ' ');
                var direction = ReadHint(*entry, "IOID");

                // A missing IOID means the device works in both directions.
                var deviceKind = direction switch
                {
                    "Input" => MediaDeviceKind.AudioCapture,
                    "Output" => MediaDeviceKind.AudioRender,
                    _ => MediaDeviceKind.AudioCapture | MediaDeviceKind.AudioRender,
                };

                if ((kind & deviceKind) == 0)
                {
                    continue;
                }

                result.Add(new MediaDevice(
                    id,
                    string.IsNullOrWhiteSpace(description) ? id : description!,
                    deviceKind & kind,
                    Alsa.BackendName,
                    id is "default"));
            }
        }
        finally
        {
            Alsa.snd_device_name_free_hint(hints);
        }

        return result;
    }

    /// <inheritdoc />
    public MediaDevice? GetDefault(MediaDeviceKind kind)
    {
        foreach (var device in Enumerate(kind))
        {
            if (device.IsDefault)
            {
                return device;
            }
        }

        return null;
    }

    private static string? ReadHint(void* hint, string key)
    {
        Span<byte> scratch = stackalloc byte[16];
        using var keyUtf8 = new Utf8Scoped(key, scratch);
        var raw = Alsa.snd_device_name_get_hint(hint, keyUtf8.Pointer);
        if (raw is null)
        {
            return null;
        }

        try
        {
            return Utf8.ToManaged(raw);
        }
        finally
        {
            // snd_device_name_get_hint returns a string allocated with malloc.
            NativeMemory.Free(raw);
        }
    }
}

/// <summary>
/// Shared plumbing for the ALSA renderer and capture: opening a PCM handle and
/// negotiating the hardware parameters.
/// </summary>
[SupportedOSPlatform("linux")]
public abstract unsafe class AlsaPcm : IDisposable
{
    private void* _handle;
    private bool _disposed;

    /// <summary>Opens the device named by <paramref name="settings"/> in the given direction.</summary>
    protected AlsaPcm(AudioCaptureSettings settings, AlsaStream direction)
    {
        Alsa.EnsureLoaded();

        var name = settings.Device?.Id ?? "default";
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, scratch);

        void* handle;
        Alsa.Check(Alsa.snd_pcm_open(&handle, nameUtf8.Pointer, (int)direction, 0), $"snd_pcm_open({name})");
        _handle = handle;

        try
        {
            Format = Configure(settings);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Backend name.</summary>
    public string Backend => Alsa.BackendName;

    /// <summary>Format the device was actually opened with.</summary>
    public AudioFormat Format { get; private set; }

    /// <summary>Period size in frames, which is the natural transfer size.</summary>
    public int PeriodFrames { get; private set; }

    /// <summary>Raw <c>snd_pcm_t</c> handle.</summary>
    protected void* Handle => _handle;

    private AudioFormat Configure(AudioCaptureSettings settings)
    {
        var alsaFormat = Alsa.ToAlsa(settings.Format.SampleFormat);
        if (alsaFormat == AlsaFormat.Unknown)
        {
            throw new NotSupportedException($"ALSA does not support the sample format {settings.Format.SampleFormat}.");
        }

        void* parameters;
        Alsa.Check(Alsa.snd_pcm_hw_params_malloc(&parameters), "snd_pcm_hw_params_malloc");

        try
        {
            Alsa.Check(Alsa.snd_pcm_hw_params_any(_handle, parameters), "snd_pcm_hw_params_any");
            Alsa.Check(
                Alsa.snd_pcm_hw_params_set_access(_handle, parameters, Alsa.AccessRwInterleaved),
                "snd_pcm_hw_params_set_access");
            Alsa.Check(
                Alsa.snd_pcm_hw_params_set_format(_handle, parameters, (int)alsaFormat),
                "snd_pcm_hw_params_set_format");
            Alsa.Check(
                Alsa.snd_pcm_hw_params_set_channels(_handle, parameters, (uint)settings.Format.Channels),
                "snd_pcm_hw_params_set_channels");

            var rate = (uint)settings.Format.SampleRate;
            var direction = 0;
            Alsa.Check(
                Alsa.snd_pcm_hw_params_set_rate_near(_handle, parameters, &rate, &direction),
                "snd_pcm_hw_params_set_rate_near");

            var bufferMicroseconds = (uint)Math.Max(
                settings.BufferDuration.TotalMicroseconds * 4, TimeSpan.FromMilliseconds(40).TotalMicroseconds);
            direction = 0;
            Alsa.snd_pcm_hw_params_set_buffer_time_near(_handle, parameters, &bufferMicroseconds, &direction);

            var periodMicroseconds = (uint)Math.Max(settings.BufferDuration.TotalMicroseconds, 1000);
            direction = 0;
            Alsa.snd_pcm_hw_params_set_period_time_near(_handle, parameters, &periodMicroseconds, &direction);

            Alsa.Check(Alsa.snd_pcm_hw_params(_handle, parameters), "snd_pcm_hw_params");

            nuint periodFrames;
            direction = 0;
            Alsa.Check(
                Alsa.snd_pcm_hw_params_get_period_size(parameters, &periodFrames, &direction),
                "snd_pcm_hw_params_get_period_size");
            PeriodFrames = (int)periodFrames;

            Alsa.Check(Alsa.snd_pcm_prepare(_handle), "snd_pcm_prepare");
            return settings.Format with { SampleRate = (int)rate };
        }
        finally
        {
            Alsa.snd_pcm_hw_params_free(parameters);
        }
    }

    /// <summary>
    /// Recovers from an underrun or a suspended device, returning true when the
    /// transfer can be retried.
    /// </summary>
    protected bool Recover(nint result)
    {
        if (result >= 0)
        {
            return false;
        }

        // silent = 1 keeps ALSA from writing to stderr.
        return Alsa.snd_pcm_recover(_handle, (int)result, 1) >= 0;
    }

    /// <inheritdoc />
    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle is not null)
        {
            Alsa.snd_pcm_close(_handle);
            _handle = null;
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>Plays interleaved PCM through an ALSA playback device.</summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class AlsaRenderer : AlsaPcm, IAudioRenderer
{
    private bool _running;

    /// <summary>Opens the playback device described by <paramref name="settings"/>.</summary>
    public AlsaRenderer(AudioCaptureSettings settings)
        : base(settings, AlsaStream.Playback)
    {
    }

    /// <inheritdoc />
    public bool IsRunning => _running;

    /// <inheritdoc />
    public void Start()
    {
        if (_running)
        {
            return;
        }

        Alsa.Check(Alsa.snd_pcm_prepare(Handle), "snd_pcm_prepare");
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
        Alsa.Check(Alsa.snd_pcm_drop(Handle), "snd_pcm_drop");
    }

    /// <inheritdoc />
    public int Write(ReadOnlySpan<byte> interleaved)
    {
        var blockAlign = Format.BlockAlign;
        if (blockAlign <= 0)
        {
            return 0;
        }

        var offset = 0;
        fixed (byte* source = interleaved)
        {
            while (offset + blockAlign <= interleaved.Length)
            {
                var frames = (nuint)((interleaved.Length - offset) / blockAlign);
                var written = Alsa.snd_pcm_writei(Handle, source + offset, frames);
                if (written < 0)
                {
                    if (Recover(written))
                    {
                        continue;
                    }

                    Alsa.Check((int)written, "snd_pcm_writei");
                }

                offset += (int)written * blockAlign;
            }
        }

        return offset;
    }

    /// <inheritdoc />
    public void Drain() => Alsa.Check(Alsa.snd_pcm_drain(Handle), "snd_pcm_drain");

    /// <inheritdoc />
    public override void Dispose()
    {
        _running = false;
        base.Dispose();
    }
}

/// <summary>Captures interleaved PCM from an ALSA capture device.</summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class AlsaCapture : AlsaPcm, IAudioCapture
{
    private readonly Lock _gate = new();

    private Thread? _worker;
    private CancellationTokenSource? _cancellation;
    private byte* _buffer;

    /// <summary>Opens the capture device described by <paramref name="settings"/>.</summary>
    public AlsaCapture(AudioCaptureSettings settings)
        : base(settings, AlsaStream.Capture)
    {
        _buffer = (byte*)NativeMemory.Alloc((nuint)(Math.Max(PeriodFrames, 1) * Format.BlockAlign));
    }

    /// <inheritdoc />
    public bool IsRunning => _worker is not null;

    /// <inheritdoc />
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <inheritdoc />
    public event AudioFrameHandler? FrameCaptured;

    /// <inheritdoc />
    public void Start()
    {
        lock (_gate)
        {
            if (_worker is not null)
            {
                return;
            }

            Alsa.Check(Alsa.snd_pcm_prepare(Handle), "snd_pcm_prepare");
            Alsa.Check(Alsa.snd_pcm_start(Handle), "snd_pcm_start");

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _worker = new Thread(() => CaptureLoop(token))
            {
                IsBackground = true,
                Name = "MediaToolkitNet.ALSA capture",
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

        // snd_pcm_drop unblocks a reader parked in snd_pcm_readi.
        Alsa.snd_pcm_drop(Handle);
        worker.Join(TimeSpan.FromSeconds(2));

        lock (_gate)
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private void CaptureLoop(CancellationToken token)
    {
        var position = TimeSpan.Zero;
        var frames = (nuint)Math.Max(PeriodFrames, 1);

        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = Alsa.snd_pcm_readi(Handle, _buffer, frames);
                if (read < 0)
                {
                    if (Recover(read))
                    {
                        continue;
                    }

                    Alsa.Check((int)read, "snd_pcm_readi");
                }

                if (read == 0)
                {
                    continue;
                }

                var handler = FrameCaptured;
                if (handler is not null)
                {
                    var frame = new AudioFrame(Format, position, (int)read, _buffer);
                    handler(in frame);
                }

                position += Format.DurationOf(read);
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex as MediaToolkitNetException ?? new MediaToolkitNetException("ALSA audio capture error.", ex));
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        Stop();

        if (_buffer is not null)
        {
            NativeMemory.Free(_buffer);
            _buffer = null;
        }

        base.Dispose();
    }
}
