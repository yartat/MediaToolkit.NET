#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer;

/// <summary>
/// The <c>appsrc</c> at the start of a pipeline, which is how frames enter GStreamer from managed code: the
/// counterpart of <see cref="GStreamerSink"/>.
/// </summary>
/// <remarks>
/// <para>
/// Frames are copied into a buffer GStreamer owns, so the caller's memory is free the moment a push returns. Video
/// rows are repacked to GStreamer's strides (each row padded to four bytes, planes back to back), whatever stride the
/// frame had. Timestamps are the frames' own, so the source runs in time format.
/// </para>
/// <para>
/// A push returns <see langword="false"/> when the pipeline no longer takes data (it is flushing, or the stream has
/// ended) and throws for a real failure such as caps the downstream elements refuse.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerSource : IDisposable
{
    private const int FlowFlushing = -2;
    private const int FlowEos = -3;

    private readonly GStreamerElement _element;
    private VideoFormat? _video;
    private AudioFormat? _audio;
    private bool _disposed;

    internal GStreamerSource(GStreamerElement element)
    {
        _element = element;
        _element.Set("format", "time");
        _element.Connect("need-data", args => NeedData?.Invoke(this, args.Get<uint>(0)));
        _element.Connect("enough-data", _ => EnoughData?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>The element wanted more data. The argument is how many bytes it hints at, 0 for any amount.</summary>
    /// <remarks>Raised on a thread GStreamer owns, often inside a push; a handler that throws is reported by <see cref="GStreamerElement.Failed"/>.</remarks>
    public event EventHandler<uint>? NeedData;

    /// <summary>The element's queue is full (its <c>max-bytes</c> is reached); pushing on makes a blocking source wait.</summary>
    public event EventHandler? EnoughData;

    /// <summary>The appsrc element itself, for setting its properties (<c>max-bytes</c>, <c>block</c>, <c>is-live</c>).</summary>
    public GStreamerElement Element => _element;

    /// <summary>Declares that this source carries raw video of that format. Frames pushed later must match it.</summary>
    public void SetFormat(VideoFormat format)
    {
        SetCaps(GstMediaFormat.CapsOf(format));
        _video = format;
    }

    /// <summary>Declares that this source carries raw audio of that format. Blocks pushed later must match it.</summary>
    public void SetFormat(AudioFormat format)
    {
        SetCaps(GstMediaFormat.CapsOf(format));
        _audio = format;
    }

    /// <summary>Sets the caps of everything this source pushes, in GStreamer's text form: encoded streams, for one, go through here.</summary>
    /// <exception cref="MediaToolkitNetException">The text is not caps.</exception>
    public void SetCaps(string caps)
    {
        ArgumentException.ThrowIfNullOrEmpty(caps);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(caps, scratch);
        var parsed = Gst.gst_caps_from_string(utf8.Pointer);
        if (parsed is null)
        {
            throw new MediaToolkitNetException(GstConstants.BackendName, $"{caps} is not a valid caps string.", 0);
        }

        try
        {
            Gst.gst_app_src_set_caps(_element.Handle, parsed);
        }
        finally
        {
            Gst.gst_mini_object_unref(parsed);
        }

        _video = null;
        _audio = null;
    }

    /// <summary>Pushes one video frame, stamped with the frame's timestamp and the duration of one frame of its rate.</summary>
    /// <returns>False when the pipeline takes no more data.</returns>
    /// <exception cref="InvalidOperationException">No video format was set.</exception>
    /// <exception cref="MediaToolkitNetException">The frame's size or pixel format is not the one declared.</exception>
    public bool PushVideo(in VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var declared = _video ?? throw new InvalidOperationException("Call SetFormat with a video format first.");
        var format = frame.Format;
        if (format.Width != declared.Width || format.Height != declared.Height || format.PixelFormat != declared.PixelFormat)
        {
            throw new MediaToolkitNetException(
                GstConstants.BackendName,
                $"a {format.Width}x{format.Height} {format.PixelFormat} frame was pushed to a source declared as {declared.Width}x{declared.Height} {declared.PixelFormat}.",
                0);
        }

        var planeCount = format.PixelFormat.PlaneCount();
        if (frame.PlaneCount < planeCount)
        {
            throw new MediaToolkitNetException(GstConstants.BackendName, $"the frame has {frame.PlaneCount} planes and {format.PixelFormat} needs {planeCount}.", 0);
        }

        var total = 0L;
        for (var plane = 0; plane < planeCount; plane++)
        {
            total += (long)Align(format.PixelFormat.MinimumStride(format.Width, plane)) * format.PixelFormat.PlaneHeight(format.Height, plane);
        }

        var buffer = Allocate(total);
        try
        {
            var info = stackalloc byte[GstLayout.MapInfoSize];
            Map(buffer, info);
            try
            {
                var data = *(byte**)(info + GstLayout.MapInfoData);
                new Span<byte>(data, (int)total).Clear();

                var offset = 0L;
                for (var plane = 0; plane < planeCount; plane++)
                {
                    var rowBytes = format.PixelFormat.MinimumStride(format.Width, plane);
                    var stride = Align(rowBytes);
                    var rows = format.PixelFormat.PlaneHeight(format.Height, plane);
                    var source = (byte*)frame.PlanePointer(plane);
                    var sourceStride = frame.Stride(plane);
                    var copy = Math.Min(rowBytes, Math.Abs(sourceStride));
                    for (var row = 0; row < rows; row++)
                    {
                        Buffer.MemoryCopy(source + ((long)row * sourceStride), data + offset + ((long)row * stride), stride, copy);
                    }

                    offset += (long)stride * rows;
                }
            }
            finally
            {
                Gst.gst_buffer_unmap(buffer, info);
            }
        }
        catch
        {
            Gst.gst_mini_object_unref(buffer);
            throw;
        }

        var duration = declared.FrameRate.Denominator > 0 && declared.FrameRate.Numerator > 0
            ? TimeSpan.FromTicks(TimeSpan.TicksPerSecond * declared.FrameRate.Denominator / declared.FrameRate.Numerator)
            : (TimeSpan?)null;
        return Submit(buffer, frame.Timestamp, duration);
    }

    /// <summary>Pushes one block of audio, stamped with its timestamp and the duration its samples make.</summary>
    /// <returns>False when the pipeline takes no more data.</returns>
    /// <exception cref="InvalidOperationException">No audio format was set.</exception>
    /// <exception cref="MediaToolkitNetException">The block's rate, channels or sample format is not the one declared.</exception>
    public bool PushAudio(in AudioFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var declared = _audio ?? throw new InvalidOperationException("Call SetFormat with an audio format first.");
        var format = frame.Format;
        if (format.SampleRate != declared.SampleRate || format.Channels != declared.Channels || format.SampleFormat != declared.SampleFormat)
        {
            throw new MediaToolkitNetException(
                GstConstants.BackendName,
                $"a block of {format.SampleRate} Hz, {format.Channels} channels, {format.SampleFormat} was pushed to a source declared as {declared.SampleRate} Hz, {declared.Channels} channels, {declared.SampleFormat}.",
                0);
        }

        var bytesPerSample = format.SampleFormat.BytesPerSample();
        var planeBytes = (long)frame.SampleCount * bytesPerSample;
        var planes = format.SampleFormat.IsPlanar() ? format.Channels : 1;
        var total = planeBytes * format.Channels;

        var buffer = Allocate(total);
        try
        {
            var info = stackalloc byte[GstLayout.MapInfoSize];
            Map(buffer, info);
            try
            {
                var data = *(byte**)(info + GstLayout.MapInfoData);
                var chunk = planes == 1 ? total : planeBytes;
                for (var plane = 0; plane < planes; plane++)
                {
                    Buffer.MemoryCopy((void*)frame.PlanePointer(plane), data + (plane * chunk), chunk, chunk);
                }
            }
            finally
            {
                Gst.gst_buffer_unmap(buffer, info);
            }
        }
        catch
        {
            Gst.gst_mini_object_unref(buffer);
            throw;
        }

        return Submit(buffer, frame.Timestamp, frame.Duration);
    }

    /// <summary>Pushes bytes as they are, for a stream whose caps were given by <see cref="SetCaps"/>: an encoded stream, for one.</summary>
    /// <param name="data">Copied before the call returns.</param>
    /// <param name="timestamp">The presentation time of the buffer.</param>
    /// <param name="duration">How long it lasts, or <see langword="null"/> when that is not known.</param>
    /// <returns>False when the pipeline takes no more data.</returns>
    public bool Push(ReadOnlySpan<byte> data, TimeSpan timestamp, TimeSpan? duration = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var buffer = Allocate(data.Length);
        try
        {
            var info = stackalloc byte[GstLayout.MapInfoSize];
            Map(buffer, info);
            try
            {
                data.CopyTo(new Span<byte>(*(byte**)(info + GstLayout.MapInfoData), data.Length));
            }
            finally
            {
                Gst.gst_buffer_unmap(buffer, info);
            }
        }
        catch
        {
            Gst.gst_mini_object_unref(buffer);
            throw;
        }

        return Submit(buffer, timestamp, duration);
    }

    /// <summary>
    /// Pushes the caller's own memory without copying it. It must stay as it is, and in place, until
    /// <paramref name="released"/> says that nothing in the pipeline uses it any more.
    /// </summary>
    /// <param name="data">The bytes, laid out as the caps say: raw video with GStreamer's strides.</param>
    /// <param name="size">How many.</param>
    /// <param name="timestamp">The presentation time of the buffer.</param>
    /// <param name="duration">How long it lasts, or null when that is not known.</param>
    /// <param name="released">
    /// Called once, on whatever thread lets go last, whether the buffer was played, refused or thrown away. Without it
    /// the memory has to outlive the pipeline. A sink keeps the last buffer it had until the next one or until it
    /// stops, unless its <c>enable-last-sample</c> is false; <c>appsink</c> also keeps the first one, its preroll, until it stops. Plan for
    /// some buffers to come back only when the pipeline does.
    /// </param>
    /// <returns>False when the pipeline takes no more data.</returns>
    public bool PushNoCopy(nint data, int size, TimeSpan timestamp, TimeSpan? duration = null, Action? released = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull((void*)data, nameof(data));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        return Submit(GstMemory.Wrap((void*)data, size, Reported(released)), timestamp, duration);
    }

    /// <summary>
    /// Pushes the memory behind a file descriptor as a DMABuf, for an element that can use it without reading it into
    /// the application's memory.
    /// </summary>
    /// <param name="fd">
    /// The descriptor. GStreamer takes it over and closes it; give it a duplicate to keep using the original.
    /// </param>
    /// <param name="size">The size of the memory.</param>
    /// <param name="timestamp">The presentation time of the buffer.</param>
    /// <param name="duration">How long it lasts, or null when that is not known.</param>
    /// <param name="released">Called once when nothing in the pipeline uses the memory any more, just before it is closed.</param>
    /// <returns>False when the pipeline takes no more data.</returns>
    /// <exception cref="NotSupportedException"><c>libgstallocators</c> is not installed.</exception>
    public bool PushDmaBuf(int fd, int size, TimeSpan timestamp, TimeSpan? duration = null, Action? released = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(fd);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        var buffer = GstMemory.DmaBuf(fd, size, Reported(released));
        return buffer is not null
            ? Submit(buffer, timestamp, duration)
            : throw new MediaToolkitNetException(GstConstants.BackendName, $"the descriptor {fd} could not be wrapped.", 0);
    }

    // What the caller's action throws is reported, since it runs where nothing could catch it.
    private Action? Reported(Action? released)
    {
        if (released is null)
        {
            return null;
        }

        var element = _element;
        return () =>
        {
            try
            {
                released();
            }
            catch (Exception ex)
            {
                element.ReportFailure($"releasing a buffer threw: {ex.Message}");
            }
        };
    }

    /// <summary>Tells the pipeline no more data is coming. Everything pushed before is still delivered.</summary>
    public void EndOfStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var flow = Gst.gst_app_src_end_of_stream(_element.Handle);
        if (flow is not (0 or FlowFlushing or FlowEos))
        {
            throw new MediaToolkitNetException(GstConstants.BackendName, "the end of the stream was refused", flow);
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
        _element.Dispose();
    }

    private static int Align(int stride) => (stride + 3) / 4 * 4;

    private static ulong Nanoseconds(TimeSpan time) =>
        time < TimeSpan.Zero ? 0 : (ulong)(time.Ticks * GstConstants.NanosecondsPerTick);

    private static void* Allocate(long size)
    {
        var buffer = Gst.gst_buffer_new_allocate(null, (nuint)size, null);
        if (buffer is null)
        {
            throw new MediaToolkitNetException(GstConstants.BackendName, $"a buffer of {size} bytes could not be allocated", 0);
        }

        return buffer;
    }

    private static void Map(void* buffer, byte* info)
    {
        new Span<byte>(info, GstLayout.MapInfoSize).Clear();
        if (Gst.gst_buffer_map(buffer, info, (int)GstMapFlags.Write) == 0)
        {
            throw new MediaToolkitNetException(GstConstants.BackendName, "a new buffer could not be mapped for writing", 0);
        }
    }

    // The buffer is handed over whatever happens: gst_app_src_push_buffer takes it even when it refuses it.
    private bool Submit(void* buffer, TimeSpan timestamp, TimeSpan? duration)
    {
        GstLayout.SetBufferTimes(buffer, Nanoseconds(timestamp), duration is { } d ? Nanoseconds(d) : GstConstants.ClockTimeNone);
        var flow = Gst.gst_app_src_push_buffer(_element.Handle, buffer);
        return flow switch
        {
            0 => true,
            FlowFlushing or FlowEos => false,
            _ => throw new MediaToolkitNetException(
                GstConstants.BackendName,
                "a buffer was refused (the downstream elements may not accept the caps this source declared)",
                flow),
        };
    }
}
