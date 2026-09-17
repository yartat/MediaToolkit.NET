using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Capture;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Interop;
using MediaToolkitNet.Linux.Native;

namespace MediaToolkitNet.Linux;

/// <summary>Shared plumbing for the PulseAudio renderer and capture.</summary>
[SupportedOSPlatform("linux")]
public abstract unsafe class PulseStream : IDisposable
{
    private void* _handle;
    private bool _disposed;

    /// <summary>Opens a stream in the given direction.</summary>
    /// <param name="settings">Device and format. A null device means the server default sink or source.</param>
    /// <param name="direction">Playback or record.</param>
    /// <param name="applicationName">Name shown in the PulseAudio mixer.</param>
    protected PulseStream(AudioCaptureSettings settings, PulseDirection direction, string applicationName)
    {
        Pulse.EnsureLoaded();

        var sampleFormat = Pulse.ToPulse(settings.Format.SampleFormat);
        if (sampleFormat == PulseSampleFormat.Invalid)
        {
            throw new NotSupportedException($"PulseAudio does not support the sample format {settings.Format.SampleFormat}.");
        }

        var spec = new PulseSampleSpec
        {
            Format = (int)sampleFormat,
            Rate = (uint)settings.Format.SampleRate,
            Channels = (byte)settings.Format.Channels,
        };

        Span<byte> appScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> deviceScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> streamScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var app = new Utf8Scoped(applicationName, appScratch);
        using var device = new Utf8Scoped(settings.Device?.Id, deviceScratch);
        using var stream = new Utf8Scoped(direction == PulseDirection.Playback ? "playback" : "record", streamScratch);

        var error = 0;
        _handle = Pulse.pa_simple_new(
            null, app.Pointer, (int)direction, device.Pointer, stream.Pointer, &spec, null, null, &error);

        if (_handle is null)
        {
            throw new MediaToolkitNetException(Pulse.BackendName, $"pa_simple_new: {Pulse.Describe(error)}", error);
        }

        Format = settings.Format with { SampleFormat = settings.Format.SampleFormat.ToInterleaved() };
    }

    /// <summary>Backend name.</summary>
    public string Backend => Pulse.BackendName;

    /// <summary>Format the stream was opened with. PulseAudio converts as needed, so this is always the requested one.</summary>
    public AudioFormat Format { get; }

    /// <summary>Raw <c>pa_simple</c> handle.</summary>
    protected void* Handle => _handle;

    /// <summary>Server-side latency of the stream.</summary>
    public TimeSpan Latency
    {
        get
        {
            var error = 0;
            var microseconds = Pulse.pa_simple_get_latency(_handle, &error);
            return microseconds == unchecked((ulong)-1) ? TimeSpan.Zero : TimeSpan.FromMicroseconds(microseconds);
        }
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
            Pulse.pa_simple_free(_handle);
            _handle = null;
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>Plays interleaved PCM through a PulseAudio sink.</summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class PulseRenderer : PulseStream, IAudioRenderer
{
    private bool _running;

    /// <summary>Opens the sink described by <paramref name="settings"/>.</summary>
    public PulseRenderer(AudioCaptureSettings settings, string applicationName = "MediaToolkit.NET")
        : base(settings, PulseDirection.Playback, applicationName)
    {
    }

    /// <inheritdoc />
    public bool IsRunning => _running;

    /// <summary>
    /// PulseAudio starts the stream as soon as it is created, so this only flips
    /// the reported state.
    /// </summary>
    public void Start() => _running = true;

    /// <inheritdoc />
    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        var error = 0;
        Pulse.pa_simple_flush(Handle, &error);
    }

    /// <inheritdoc />
    public int Write(ReadOnlySpan<byte> interleaved)
    {
        if (interleaved.IsEmpty)
        {
            return 0;
        }

        var error = 0;
        fixed (byte* data = interleaved)
        {
            Pulse.Check(Pulse.pa_simple_write(Handle, data, (nuint)interleaved.Length, &error), error, "pa_simple_write");
        }

        return interleaved.Length;
    }

    /// <inheritdoc />
    public void Drain()
    {
        var error = 0;
        Pulse.Check(Pulse.pa_simple_drain(Handle, &error), error, "pa_simple_drain");
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _running = false;
        base.Dispose();
    }
}

/// <summary>Captures interleaved PCM from a PulseAudio source.</summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class PulseCapture : PulseStream, IAudioCapture
{
    private readonly Lock _gate = new();
    private readonly int _framesPerRead;

    private Thread? _worker;
    private CancellationTokenSource? _cancellation;
    private byte* _buffer;

    /// <summary>Opens the source described by <paramref name="settings"/>.</summary>
    public PulseCapture(AudioCaptureSettings settings, string applicationName = "MediaToolkit.NET")
        : base(settings, PulseDirection.Record, applicationName)
    {
        var duration = settings.BufferDuration <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(20) : settings.BufferDuration;
        _framesPerRead = Math.Max((int)(duration.TotalSeconds * Format.SampleRate), 1);
        _buffer = (byte*)NativeMemory.Alloc((nuint)(_framesPerRead * Format.BlockAlign));
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

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _worker = new Thread(() => CaptureLoop(token))
            {
                IsBackground = true,
                Name = "MediaToolkitNet.PulseAudio capture",
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

        // pa_simple_read blocks until the requested bytes arrive, so the worker
        // finishes its current read before noticing the cancellation.
        worker?.Join(TimeSpan.FromSeconds(2));

        lock (_gate)
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private void CaptureLoop(CancellationToken token)
    {
        var bytes = (nuint)(_framesPerRead * Format.BlockAlign);
        var position = TimeSpan.Zero;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var error = 0;
                Pulse.Check(Pulse.pa_simple_read(Handle, _buffer, bytes, &error), error, "pa_simple_read");

                var handler = FrameCaptured;
                if (handler is not null)
                {
                    var frame = new AudioFrame(Format, position, _framesPerRead, _buffer);
                    handler(in frame);
                }

                position += Format.DurationOf(_framesPerRead);
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke(
                this, ex as MediaToolkitNetException ?? new MediaToolkitNetException("PulseAudio audio capture error.", ex));
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
