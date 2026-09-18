using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer;

/// <summary>
/// The <c>appsink</c> at the end of a pipeline, which is how frames leave
/// GStreamer and reach managed code.
/// </summary>
/// <remarks>
/// <para>
/// This is the counterpart of the DirectShow Sample Grabber, with the pull
/// model instead of a callback: the caller asks for a sample and GStreamer
/// hands over the one at the head of the queue. That keeps the frame's lifetime
/// visible — it is mapped for the duration of the handler and unmapped as soon
/// as the handler returns.
/// </para>
/// <para>
/// The sink reports whatever format negotiation settled on, so put a
/// <c>videoconvert ! video/x-raw,format=BGR</c> (or the audio equivalent) in
/// front of it to pin one.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerSink : IDisposable
{
    private readonly GStreamerElement _element;
    private bool _disposed;

    internal GStreamerSink(GStreamerElement element) => _element = element;

    /// <summary>The appsink element itself, for setting its properties.</summary>
    public GStreamerElement Element => _element;

    /// <summary>True once the stream has ended and the queue has been drained.</summary>
    public bool IsEndOfStream => Gst.gst_app_sink_is_eos(_element.Handle) != 0;

    /// <summary>
    /// Drops samples rather than blocking the pipeline when the caller is not
    /// keeping up, which is what a live preview wants.
    /// </summary>
    /// <param name="drop">Whether to drop.</param>
    /// <param name="maxBuffers">How many samples to queue before dropping starts.</param>
    public void DropWhenFull(bool drop, int maxBuffers = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBuffers);
        _element.Set("drop", drop ? "true" : "false");
        _element.Set("max-buffers", maxBuffers.ToString());
    }

    /// <summary>
    /// The format the sink negotiated, once the pipeline has prerolled.
    /// </summary>
    public GstMediaFormat? NegotiatedFormat
    {
        get
        {
            var caps = Gst.gst_app_sink_get_caps(_element.Handle);
            if (caps is null)
            {
                return null;
            }

            try
            {
                return GstMediaFormat.From(caps);
            }
            finally
            {
                // gst_app_sink_get_caps hands over a reference of its own.
                Gst.gst_mini_object_unref(caps);
            }
        }
    }

    /// <summary>
    /// Takes the next video frame, waiting up to <paramref name="timeout"/>.
    /// </summary>
    /// <param name="timeout">How long to wait for a sample; negative waits forever.</param>
    /// <param name="handler">Receives the frame, which is valid only for the call.</param>
    /// <returns>False on a timeout, at the end of the stream, or when the sink carries audio.</returns>
    public bool TryPullVideo(TimeSpan timeout, VideoFrameHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sample = Gst.gst_app_sink_try_pull_sample(_element.Handle, Nanoseconds(timeout));
        if (sample is null)
        {
            return false;
        }

        try
        {
            var caps = Gst.gst_sample_get_caps(sample);
            var buffer = Gst.gst_sample_get_buffer(sample);
            if (caps is null || buffer is null || GstMediaFormat.From(caps).Video is not { } format)
            {
                return false;
            }

            var info = stackalloc byte[GstLayout.MapInfoSize];
            new Span<byte>(info, GstLayout.MapInfoSize).Clear();
            if (Gst.gst_buffer_map(buffer, info, (int)GstMapFlags.Read) == 0)
            {
                return false;
            }

            try
            {
                Span<nint> planes = stackalloc nint[MediaPlanes.MaxPlanes];
                Span<int> strides = stackalloc int[MediaPlanes.MaxPlanes];
                var data = *(byte**)(info + GstLayout.MapInfoData);
                var planeCount = BuildPlanes(format, data, planes, strides);

                var frame = new VideoFrame(format, TimestampOf(buffer), planes, strides, planeCount);
                handler(in frame);
                return true;
            }
            finally
            {
                Gst.gst_buffer_unmap(buffer, info);
            }
        }
        finally
        {
            Gst.gst_mini_object_unref(sample);
        }
    }

    /// <summary>Takes the next block of audio, waiting up to <paramref name="timeout"/>.</summary>
    /// <param name="timeout">How long to wait for a sample; negative waits forever.</param>
    /// <param name="handler">Receives the frame, which is valid only for the call.</param>
    /// <returns>False on a timeout, at the end of the stream, or when the sink carries video.</returns>
    public bool TryPullAudio(TimeSpan timeout, AudioFrameHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sample = Gst.gst_app_sink_try_pull_sample(_element.Handle, Nanoseconds(timeout));
        if (sample is null)
        {
            return false;
        }

        try
        {
            var caps = Gst.gst_sample_get_caps(sample);
            var buffer = Gst.gst_sample_get_buffer(sample);
            if (caps is null || buffer is null || GstMediaFormat.From(caps).Audio is not { } format)
            {
                return false;
            }

            var blockAlign = format.SampleFormat.BytesPerSample() * format.Channels;
            if (blockAlign <= 0)
            {
                return false;
            }

            var info = stackalloc byte[GstLayout.MapInfoSize];
            new Span<byte>(info, GstLayout.MapInfoSize).Clear();
            if (Gst.gst_buffer_map(buffer, info, (int)GstMapFlags.Read) == 0)
            {
                return false;
            }

            try
            {
                var data = *(byte**)(info + GstLayout.MapInfoData);
                var length = (long)*(nuint*)(info + GstLayout.MapInfoSizeField);

                var frame = new AudioFrame(format, TimestampOf(buffer), (int)(length / blockAlign), data);
                handler(in frame);
                return true;
            }
            finally
            {
                Gst.gst_buffer_unmap(buffer, info);
            }
        }
        finally
        {
            Gst.gst_mini_object_unref(sample);
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

    private static TimeSpan TimestampOf(void* buffer)
    {
        var pts = GstLayout.PtsOfBuffer(buffer);
        return pts == GstConstants.ClockTimeNone
            ? TimeSpan.Zero
            : new TimeSpan((long)(pts / GstConstants.NanosecondsPerTick));
    }

    private static ulong Nanoseconds(TimeSpan timeout) =>
        timeout < TimeSpan.Zero
            ? GstConstants.ClockTimeNone
            : (ulong)(timeout.Ticks * GstConstants.NanosecondsPerTick);

    /// <summary>
    /// Splits the one contiguous buffer an appsink delivers into planes.
    /// </summary>
    /// <remarks>
    /// GStreamer pads rows to four bytes for the raw video formats, which is the
    /// same rule the DIB formats follow, and stores planes back to back.
    /// </remarks>
    private static int BuildPlanes(VideoFormat format, byte* data, Span<nint> planes, Span<int> strides)
    {
        var planeCount = format.PixelFormat.PlaneCount();
        if (planeCount <= 1)
        {
            planes[0] = (nint)data;
            strides[0] = Align(format.PixelFormat.MinimumStride(format.Width));
            return 1;
        }

        var offset = 0L;
        for (var plane = 0; plane < planeCount; plane++)
        {
            var stride = Align(format.PixelFormat.MinimumStride(format.Width, plane));
            planes[plane] = (nint)(data + offset);
            strides[plane] = stride;
            offset += (long)stride * format.PixelFormat.PlaneHeight(format.Height, plane);
        }

        return planeCount;
    }

    private static int Align(int stride) => (stride + 3) / 4 * 4;
}

/// <summary>
/// A GStreamer caps structure, read as a format this library understands.
/// </summary>
/// <param name="MediaType">The caps name, for example <c>video/x-raw</c>.</param>
/// <param name="Video">Set when the caps describe raw video.</param>
/// <param name="Audio">Set when the caps describe raw audio.</param>
/// <param name="Caps">The caps as GStreamer prints them, which is what a mismatch is diagnosed from.</param>
[SupportedOSPlatform("linux")]
public readonly record struct GstMediaFormat(
    string MediaType,
    VideoFormat? Video,
    AudioFormat? Audio,
    string Caps)
{
    /// <summary>Reads the first structure of a <c>GstCaps</c>.</summary>
    public static unsafe GstMediaFormat From(void* caps)
    {
        var text = Gst.TakeString(Gst.gst_caps_to_string(caps));
        var structure = Gst.gst_caps_get_structure(caps, 0);
        if (structure is null)
        {
            return new GstMediaFormat(string.Empty, null, null, text);
        }

        var name = Utf8.ToManagedOrEmpty(Gst.gst_structure_get_name(structure));
        return name switch
        {
            "video/x-raw" => new GstMediaFormat(name, ReadVideo(structure), null, text),
            "audio/x-raw" => new GstMediaFormat(name, null, ReadAudio(structure), text),
            _ => new GstMediaFormat(name, null, null, text),
        };
    }

    private static unsafe VideoFormat ReadVideo(void* structure)
    {
        var width = ReadInt(structure, "width");
        var height = ReadInt(structure, "height");
        var (num, den) = ReadFraction(structure, "framerate");
        return new VideoFormat(
            width,
            height,
            ToPixelFormat(ReadString(structure, "format")),
            den > 0 ? new Rational(num, den) : Rational.Zero);
    }

    private static unsafe AudioFormat ReadAudio(void* structure)
    {
        var planar = ReadString(structure, "layout") == "non-interleaved";
        return new AudioFormat(
            ReadInt(structure, "rate"),
            ReadInt(structure, "channels"),
            ToSampleFormat(ReadString(structure, "format"), planar));
    }

    /// <summary>Maps a GStreamer raw video format name onto a toolkit pixel format.</summary>
    public static PixelFormat ToPixelFormat(string name) => name switch
    {
        "BGR" => PixelFormat.Bgr24,
        "RGB" => PixelFormat.Rgb24,
        "BGRA" or "BGRx" => PixelFormat.Bgra32,
        "RGBA" or "RGBx" => PixelFormat.Rgba32,
        "I420" => PixelFormat.Yuv420P,
        "Y42B" => PixelFormat.Yuv422P,
        "Y444" => PixelFormat.Yuv444P,
        "NV12" => PixelFormat.Nv12,
        "YUY2" => PixelFormat.Yuyv422,
        "UYVY" => PixelFormat.Uyvy422,
        _ => PixelFormat.Unknown,
    };

    /// <summary>Maps a GStreamer raw audio format name onto a toolkit sample format.</summary>
    /// <param name="name">The caps format, for example <c>S16LE</c>.</param>
    /// <param name="planar">True when the caps say <c>layout=non-interleaved</c>.</param>
    public static SampleFormat ToSampleFormat(string name, bool planar) => name switch
    {
        "U8" => planar ? SampleFormat.U8Planar : SampleFormat.U8,
        "S16LE" => planar ? SampleFormat.S16Planar : SampleFormat.S16,
        "S32LE" => planar ? SampleFormat.S32Planar : SampleFormat.S32,
        "F32LE" => planar ? SampleFormat.F32Planar : SampleFormat.F32,
        "F64LE" => planar ? SampleFormat.F64Planar : SampleFormat.F64,
        _ => SampleFormat.Unknown,
    };

    private static unsafe int ReadInt(void* structure, string field)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(field, scratch);
        int value;
        return Gst.gst_structure_get_int(structure, utf8.Pointer, &value) == 0 ? 0 : value;
    }

    private static unsafe string ReadString(void* structure, string field)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(field, scratch);

        // The string belongs to the structure and must not be freed.
        return Utf8.ToManagedOrEmpty(Gst.gst_structure_get_string(structure, utf8.Pointer));
    }

    private static unsafe (int Numerator, int Denominator) ReadFraction(void* structure, string field)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(field, scratch);
        int numerator;
        int denominator;
        return Gst.gst_structure_get_fraction(structure, utf8.Pointer, &numerator, &denominator) == 0
            ? (0, 0)
            : (numerator, denominator);
    }

    /// <inheritdoc />
    public override string ToString() => Caps;
}
