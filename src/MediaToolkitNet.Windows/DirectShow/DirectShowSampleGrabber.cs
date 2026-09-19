using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows.DirectShow;

/// <summary>
/// The Sample Grabber filter, which shows every sample passing through it to
/// managed code and then forwards it downstream unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Events are raised on the DirectShow streaming thread, and the frame they
/// carry borrows the filter's own buffer: it is valid only until the handler
/// returns. Copy anything that has to outlive the call.
/// </para>
/// <para>
/// Call <see cref="AcceptVideo"/> or <see cref="AcceptAudio"/> before connecting
/// the graph. A grabber that accepts anything will happily connect to a
/// compressed stream, and then the samples are compressed too.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class DirectShowSampleGrabber : IDisposable
{
    private readonly DirectShowFilter _filter;
    private ComPtr _grabber;
    private SampleGrabberCallback* _callback;
    private VideoLayout? _video;
    private AudioFormat? _audio;
    private bool _resolved;
    private bool _disposed;

    private DirectShowSampleGrabber(DirectShowFilter filter)
    {
        _filter = filter;
        _grabber = filter.QueryInterface(DsGuids.ISampleGrabber);
        _callback = SampleGrabberCallbackFactory.Create(this);

        // 0 selects SampleCB; SetCallback takes its own reference.
        HResult.ThrowIfFailed(
            DsNative.SetCallback(_grabber.Pointer, _callback, 0), "ISampleGrabber::SetCallback");
    }

    /// <summary>Raised for every video sample that reaches the filter.</summary>
    public event VideoFrameHandler? VideoGrabbed;

    /// <summary>Raised for every audio sample that reaches the filter.</summary>
    public event AudioFrameHandler? AudioGrabbed;

    /// <summary>Adds a Sample Grabber to the graph and wraps it.</summary>
    /// <remarks>The graph owns the result and disposes it with itself.</remarks>
    public static DirectShowSampleGrabber AddTo(DirectShowGraph graph, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var filter = graph.AddFilter(DsFilter.SampleGrabber, name);
        var grabber = new DirectShowSampleGrabber(filter);
        graph.Track(grabber);
        return grabber;
    }

    /// <summary>The filter itself, for connecting its pins like any other.</summary>
    public DirectShowFilter Filter => _filter;

    /// <summary>The media type the filter's pins actually agreed on.</summary>
    public DsMediaType? ConnectedMediaType
    {
        get
        {
            if (HResult.Failed(DsNative.GetGrabberMediaType(_grabber.Pointer, out var native)))
            {
                return null;
            }

            try
            {
                return DsMediaType.From(native);
            }
            finally
            {
                DsNative.FreeMediaTypeContents(&native);
            }
        }
    }

    /// <summary>
    /// Restricts the filter to uncompressed video of the given layout, so that
    /// the graph builder inserts a decoder and a colour converter as needed.
    /// </summary>
    public void AcceptVideo(PixelFormat format = PixelFormat.Bgr24)
    {
        var subtype = DsMediaType.ToSubtype(format);
        var wanted = new AmMediaType
        {
            MajorType = DsGuids.MediaTypeVideo,
            Subtype = subtype,
            FormatType = DsGuids.FormatVideoInfo,
        };

        HResult.ThrowIfFailed(
            DsNative.SetGrabberMediaType(_grabber.Pointer, &wanted), "ISampleGrabber::SetMediaType");
    }

    /// <summary>Restricts the filter to uncompressed PCM audio.</summary>
    public void AcceptAudio()
    {
        var wanted = new AmMediaType
        {
            MajorType = DsGuids.MediaTypeAudio,
            Subtype = DsGuids.SubtypePcm,
            FormatType = DsGuids.FormatWaveFormatEx,
        };

        HResult.ThrowIfFailed(
            DsNative.SetGrabberMediaType(_grabber.Pointer, &wanted), "ISampleGrabber::SetMediaType");
    }

    /// <summary>
    /// Stops the graph after the first sample, which is how a single frame is
    /// grabbed out of a file.
    /// </summary>
    public void SetOneShot(bool oneShot) =>
        HResult.ThrowIfFailed(DsNative.SetOneShot(_grabber.Pointer, oneShot), "ISampleGrabber::SetOneShot");

    /// <summary>
    /// Makes the filter keep a copy of the most recent sample, which
    /// <see cref="CopyCurrentBuffer"/> then reads. Off by default, because the
    /// copy costs one memcpy per sample.
    /// </summary>
    public void BufferSamples(bool buffer) =>
        HResult.ThrowIfFailed(
            DsNative.SetBufferSamples(_grabber.Pointer, buffer), "ISampleGrabber::SetBufferSamples");

    /// <summary>
    /// Size in bytes of the buffered sample, or 0 when there is none. Requires
    /// <see cref="BufferSamples"/>.
    /// </summary>
    public int CurrentBufferSize
    {
        get
        {
            var size = 0;
            return HResult.Failed(DsNative.GetCurrentBuffer(_grabber.Pointer, ref size, null)) ? 0 : size;
        }
    }

    /// <summary>
    /// Copies the buffered sample into <paramref name="destination"/>, which
    /// must be at least <see cref="CurrentBufferSize"/> bytes long.
    /// </summary>
    /// <returns>Number of bytes written.</returns>
    public int CopyCurrentBuffer(Span<byte> destination)
    {
        var size = destination.Length;
        fixed (byte* p = destination)
        {
            HResult.ThrowIfFailed(
                DsNative.GetCurrentBuffer(_grabber.Pointer, ref size, p), "ISampleGrabber::GetCurrentBuffer");
        }

        return size;
    }

    /// <summary>Called from the streaming thread by the native callback object.</summary>
    internal void Deliver(double sampleTime, void* sample)
    {
        var video = VideoGrabbed;
        var audio = AudioGrabbed;
        if (video is null && audio is null)
        {
            return;
        }

        // A sample carries a media type only when the format changed since the
        // previous one, which is the only notice DirectShow gives.
        if (DsNative.GetSampleMediaType(sample, out var changed) == HResult.Ok && changed is not null)
        {
            try
            {
                Adopt(*changed);
            }
            finally
            {
                DsNative.FreeMediaType(changed);
            }
        }
        else if (!_resolved)
        {
            Resolve();
        }

        if (HResult.Failed(DsNative.GetSamplePointer(sample, out var data)) || data is null)
        {
            return;
        }

        var length = DsNative.GetSampleLength(sample);
        if (length <= 0)
        {
            return;
        }

        var timestamp = TimeSpan.FromSeconds(sampleTime);
        if (video is not null && _video is { } layout)
        {
            DeliverVideo(video, layout, data, timestamp);
        }
        else if (audio is not null && _audio is { } format)
        {
            DeliverAudio(audio, format, data, length, timestamp);
        }
    }

    private static void DeliverVideo(
        VideoFrameHandler handler, in VideoLayout layout, byte* data, TimeSpan timestamp)
    {
        var format = layout.Format;
        Span<nint> planes = stackalloc nint[MediaPlanes.MaxPlanes];
        Span<int> strides = stackalloc int[MediaPlanes.MaxPlanes];
        int planeCount;

        if (format.PixelFormat == PixelFormat.Nv12)
        {
            planes[0] = (nint)data;
            strides[0] = layout.Stride;
            planes[1] = (nint)(data + ((long)layout.Stride * format.Height));
            strides[1] = layout.Stride;
            planeCount = 2;
        }
        else
        {
            // A bottom-up DIB stores the last row first, so the frame points at
            // the top row and walks backwards.
            planes[0] = layout.BottomUp
                ? (nint)(data + ((long)layout.Stride * (format.Height - 1)))
                : (nint)data;
            strides[0] = layout.BottomUp ? -layout.Stride : layout.Stride;
            planeCount = 1;
        }

        var frame = new VideoFrame(format, timestamp, planes, strides, planeCount);
        handler(in frame);
    }

    private static void DeliverAudio(
        AudioFrameHandler handler, AudioFormat format, byte* data, int length, TimeSpan timestamp)
    {
        var blockAlign = format.SampleFormat.BytesPerSample() * format.Channels;
        if (blockAlign <= 0)
        {
            return;
        }

        var frame = new AudioFrame(format, timestamp, length / blockAlign, data);
        handler(in frame);
    }

    private void Resolve()
    {
        if (HResult.Failed(DsNative.GetGrabberMediaType(_grabber.Pointer, out var native)))
        {
            return;
        }

        try
        {
            Adopt(native);
        }
        finally
        {
            DsNative.FreeMediaTypeContents(&native);
        }
    }

    private void Adopt(in AmMediaType native)
    {
        _resolved = true;
        var parsed = DsMediaType.From(native);
        _audio = parsed.Audio;
        _video = null;

        if (parsed.Video is not { } format
            || native.FormatType != DsGuids.FormatVideoInfo
            || native.FormatBlock is null
            || native.FormatSize < (uint)sizeof(VideoInfoHeader))
        {
            return;
        }

        var header = *(VideoInfoHeader*)native.FormatBlock;
        _video = new VideoLayout(format, StrideOf(header.Bitmap, format.PixelFormat), header.Bitmap.Height > 0);
    }

    /// <summary>
    /// Row pitch of the buffer. Uncompressed RGB arrives as a DIB, whose rows
    /// are padded to a four-byte boundary; a FOURCC format is packed.
    /// </summary>
    private static int StrideOf(in BitmapInfoHeader bitmap, PixelFormat format) =>
        format is PixelFormat.Bgr24 or PixelFormat.Bgra32
            ? (bitmap.Width * bitmap.BitCount + 31) / 32 * 4
            : format.MinimumStride(bitmap.Width);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        VideoGrabbed = null;
        AudioGrabbed = null;

        if (!_grabber.IsNull)
        {
            // Drops the filter's reference to the callback object before the
            // last one goes, so nothing can call back into a dead grabber.
            DsNative.SetCallback(_grabber.Pointer, null, 0);
        }

        _grabber.Dispose();

        if (_callback is not null)
        {
            Com.Release(_callback);
            _callback = null;
        }

        _filter.Dispose();
    }

    /// <summary>What the frame view needs beyond the format itself.</summary>
    private readonly record struct VideoLayout(VideoFormat Format, int Stride, bool BottomUp);
}
