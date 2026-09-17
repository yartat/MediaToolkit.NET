using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Core;
using MediaToolkitNet.Core.Capture;
using MediaToolkitNet.Core.Formats;
using MediaToolkitNet.Core.Frames;
using MediaToolkitNet.MacOS.Native;

namespace MediaToolkitNet.MacOS;

/// <summary>
/// Plays interleaved PCM through an AudioQueue output.
/// </summary>
/// <remarks>
/// A ring of buffers is cycled through the queue: <see cref="Write"/> fills the
/// next free one and blocks on a semaphore that the completion callback
/// releases, which gives back-pressure without a polling loop.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed unsafe class AudioQueueRenderer : IAudioRenderer
{
    private const int BufferCount = 3;

    private readonly SemaphoreSlim _free = new(BufferCount, BufferCount);
    private readonly Queue<nint> _available = new(BufferCount);
    private readonly Lock _gate = new();
    private readonly GCHandle _self;

    private nint _queue;
    private bool _running;
    private bool _disposed;

    /// <summary>Opens the output described by <paramref name="settings"/>.</summary>
    public AudioQueueRenderer(AudioCaptureSettings settings)
    {
        Format = settings.Format with { SampleFormat = settings.Format.SampleFormat.ToInterleaved() };
        _self = GCHandle.Alloc(this, GCHandleType.Normal);

        var description = AudioStreamBasicDescription.From(Format);
        nint queue;
        CoreAudio.Check(
            CoreAudio.AudioQueueNewOutput(
                &description, &OnBufferPlayed, (void*)GCHandle.ToIntPtr(_self), 0, 0, 0, &queue),
            "AudioQueueNewOutput");
        _queue = queue;

        var duration = settings.BufferDuration <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(20) : settings.BufferDuration;
        BufferBytes = Math.Max((int)(duration.TotalSeconds * Format.SampleRate), 1) * Format.BlockAlign;

        for (var i = 0; i < BufferCount; i++)
        {
            AudioQueueBuffer* buffer;
            CoreAudio.Check(CoreAudio.AudioQueueAllocateBuffer(_queue, (uint)BufferBytes, &buffer), "AudioQueueAllocateBuffer");
            _available.Enqueue((nint)buffer);
        }
    }

    /// <inheritdoc />
    public string Backend => CoreAudio.BackendName;

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    public bool IsRunning => _running;

    /// <summary>Size of one queue buffer, in bytes.</summary>
    public int BufferBytes { get; }

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
        {
            return;
        }

        CoreAudio.Check(CoreAudio.AudioQueueStart(_queue, null), "AudioQueueStart");
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
        CoreAudio.Check(CoreAudio.AudioQueueStop(_queue, true), "AudioQueueStop");
    }

    /// <inheritdoc />
    public int Write(ReadOnlySpan<byte> interleaved)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var offset = 0;
        while (offset < interleaved.Length)
        {
            _free.Wait();

            nint raw;
            lock (_gate)
            {
                if (_available.Count == 0)
                {
                    // The semaphore and the queue disagree only during disposal.
                    break;
                }

                raw = _available.Dequeue();
            }

            var buffer = (AudioQueueBuffer*)raw;
            var bytes = Math.Min(interleaved.Length - offset, (int)buffer->AudioDataBytesCapacity);
            interleaved.Slice(offset, bytes).CopyTo(new Span<byte>(buffer->AudioData, bytes));
            buffer->AudioDataByteSize = (uint)bytes;

            CoreAudio.Check(CoreAudio.AudioQueueEnqueueBuffer(_queue, buffer, 0, null), "AudioQueueEnqueueBuffer");
            offset += bytes;
        }

        return offset;
    }

    /// <inheritdoc />
    public void Drain()
    {
        // Every buffer being back in the free list means the queue has played
        // everything that was handed to it.
        for (var i = 0; i < BufferCount; i++)
        {
            _free.Wait();
        }

        _free.Release(BufferCount);
    }

    /// <summary>Sets the queue volume, from 0.0 to 1.0.</summary>
    public void SetVolume(double volume) =>
        CoreAudio.Check(
            CoreAudio.AudioQueueSetParameter(_queue, CoreAudio.ParameterVolume, (float)Math.Clamp(volume, 0d, 1d)),
            "AudioQueueSetParameter");

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnBufferPlayed(void* userData, nint queue, AudioQueueBuffer* buffer)
    {
        var handle = GCHandle.FromIntPtr((nint)userData);
        if (handle.Target is not AudioQueueRenderer renderer)
        {
            return;
        }

        lock (renderer._gate)
        {
            renderer._available.Enqueue((nint)buffer);
        }

        renderer._free.Release();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_queue != 0)
        {
            CoreAudio.AudioQueueStop(_queue, true);
            CoreAudio.AudioQueueDispose(_queue, true);
            _queue = 0;
        }

        _running = false;
        _free.Dispose();

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}

/// <summary>Captures interleaved PCM through an AudioQueue input.</summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class AudioQueueCapture : IAudioCapture
{
    private const int BufferCount = 3;

    private readonly GCHandle _self;
    private readonly List<nint> _buffers = new(BufferCount);

    private nint _queue;
    private TimeSpan _position;
    private bool _running;
    private bool _disposed;

    /// <summary>Opens the input described by <paramref name="settings"/>.</summary>
    public AudioQueueCapture(AudioCaptureSettings settings)
    {
        Format = settings.Format with { SampleFormat = settings.Format.SampleFormat.ToInterleaved() };
        _self = GCHandle.Alloc(this, GCHandleType.Normal);

        var description = AudioStreamBasicDescription.From(Format);
        nint queue;
        CoreAudio.Check(
            CoreAudio.AudioQueueNewInput(
                &description, &OnBufferFilled, (void*)GCHandle.ToIntPtr(_self), 0, 0, 0, &queue),
            "AudioQueueNewInput");
        _queue = queue;

        var duration = settings.BufferDuration <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(20) : settings.BufferDuration;
        var bufferBytes = Math.Max((int)(duration.TotalSeconds * Format.SampleRate), 1) * Format.BlockAlign;

        for (var i = 0; i < BufferCount; i++)
        {
            AudioQueueBuffer* buffer;
            CoreAudio.Check(CoreAudio.AudioQueueAllocateBuffer(_queue, (uint)bufferBytes, &buffer), "AudioQueueAllocateBuffer");
            CoreAudio.Check(CoreAudio.AudioQueueEnqueueBuffer(_queue, buffer, 0, null), "AudioQueueEnqueueBuffer");
            _buffers.Add((nint)buffer);
        }
    }

    /// <inheritdoc />
    public string Backend => CoreAudio.BackendName;

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    public bool IsRunning => _running;

    /// <inheritdoc />
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <inheritdoc />
    public event AudioFrameHandler? FrameCaptured;

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
        {
            return;
        }

        CoreAudio.Check(CoreAudio.AudioQueueStart(_queue, null), "AudioQueueStart");
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
        CoreAudio.Check(CoreAudio.AudioQueueStop(_queue, true), "AudioQueueStop");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnBufferFilled(
        void* userData, nint queue, AudioQueueBuffer* buffer, void* startTime, uint packetCount, void* packets)
    {
        var handle = GCHandle.FromIntPtr((nint)userData);
        if (handle.Target is not AudioQueueCapture capture)
        {
            return;
        }

        try
        {
            var blockAlign = capture.Format.BlockAlign;
            if (blockAlign > 0 && buffer->AudioDataByteSize > 0)
            {
                var frames = (int)(buffer->AudioDataByteSize / blockAlign);
                var handler = capture.FrameCaptured;
                if (handler is not null)
                {
                    var frame = new AudioFrame(capture.Format, capture._position, frames, (byte*)buffer->AudioData);
                    handler(in frame);
                }

                capture._position += capture.Format.DurationOf(frames);
            }
        }
        catch (Exception ex)
        {
            capture.Failed?.Invoke(
                capture, ex as MediaToolkitNetException ?? new MediaToolkitNetException("AudioQueue capture error.", ex));
        }
        finally
        {
            if (capture._running)
            {
                CoreAudio.AudioQueueEnqueueBuffer(queue, buffer, 0, null);
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
        _running = false;

        if (_queue != 0)
        {
            CoreAudio.AudioQueueStop(_queue, true);
            CoreAudio.AudioQueueDispose(_queue, true);
            _queue = 0;
        }

        _buffers.Clear();

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}
