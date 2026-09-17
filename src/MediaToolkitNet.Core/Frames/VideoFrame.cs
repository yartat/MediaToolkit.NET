using MediaToolkitNet.Core.Formats;

namespace MediaToolkitNet.Core.Frames;

/// <summary>
/// A borrowed view over one decoded or captured video frame.
/// </summary>
/// <remarks>
/// The frame does not own its memory: the pointers stay valid only for the
/// duration of the callback that received it. Copy the data out if you need to
/// keep it. Being a ref struct makes that contract compiler-enforced.
/// </remarks>
public readonly ref struct VideoFrame
{
    private readonly PlanePointers _planes;
    private readonly PlaneStrides _strides;

    /// <summary>Creates a frame view from raw plane pointers.</summary>
    /// <param name="format">Geometry and pixel layout of the frame.</param>
    /// <param name="timestamp">Presentation timestamp relative to the start of the stream.</param>
    /// <param name="planes">Pointer to the first byte of each plane.</param>
    /// <param name="strides">Byte distance between consecutive rows of each plane.</param>
    /// <param name="planeCount">Number of valid entries in <paramref name="planes"/>.</param>
    public VideoFrame(
        VideoFormat format,
        TimeSpan timestamp,
        ReadOnlySpan<nint> planes,
        ReadOnlySpan<int> strides,
        int planeCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(planeCount, MediaPlanes.MaxPlanes);

        Format = format;
        Timestamp = timestamp;
        PlaneCount = planeCount;
        for (var i = 0; i < planeCount; i++)
        {
            _planes[i] = planes[i];
            _strides[i] = strides[i];
        }
    }

    /// <summary>Creates a single-plane frame view over a contiguous buffer.</summary>
    public unsafe VideoFrame(VideoFormat format, TimeSpan timestamp, byte* data, int stride)
    {
        Format = format;
        Timestamp = timestamp;
        PlaneCount = 1;
        _planes[0] = (nint)data;
        _strides[0] = stride;
    }

    /// <summary>Geometry and pixel layout of the frame.</summary>
    public VideoFormat Format { get; }

    /// <summary>Presentation timestamp relative to the start of the stream.</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>Number of valid planes.</summary>
    public int PlaneCount { get; }

    /// <summary>Raw pointer to the first byte of the given plane.</summary>
    public nint PlanePointer(int plane) => _planes[Check(plane)];

    /// <summary>
    /// Byte distance between consecutive rows of the given plane. May be
    /// negative for bottom-up buffers such as those produced by GDI or DirectShow.
    /// </summary>
    public int Stride(int plane) => _strides[Check(plane)];

    /// <summary>
    /// The given plane as a span. The span spans the whole plane including any
    /// row padding, so its length is abs(stride) multiplied by the plane height.
    /// </summary>
    public unsafe ReadOnlySpan<byte> GetPlane(int plane)
    {
        var index = Check(plane);
        var stride = _strides[index];
        var rows = Format.PixelFormat.PlaneHeight(Format.Height, index);
        var length = Math.Abs(stride) * rows;

        // A negative stride means the topmost row sits at the end of the buffer.
        var start = stride < 0 ? _planes[index] + stride * (rows - 1) : _planes[index];
        return new ReadOnlySpan<byte>((void*)start, length);
    }

    /// <summary>
    /// Copies the given plane row by row into <paramref name="destination"/>,
    /// dropping any row padding.
    /// </summary>
    /// <returns>Number of bytes written.</returns>
    public unsafe int CopyPlaneTo(int plane, Span<byte> destination)
    {
        var index = Check(plane);
        var rows = Format.PixelFormat.PlaneHeight(Format.Height, index);
        var rowBytes = Format.PixelFormat.MinimumStride(Format.Width, index);
        if (rowBytes == 0)
        {
            // Formats such as MJPEG have no derivable geometry; copy the plane verbatim.
            var raw = GetPlane(index);
            raw.CopyTo(destination);
            return raw.Length;
        }

        var stride = _strides[index];
        var source = (byte*)_planes[index];
        var written = 0;
        for (var row = 0; row < rows; row++)
        {
            new ReadOnlySpan<byte>(source + ((long)row * stride), rowBytes)
                .CopyTo(destination[written..]);
            written += rowBytes;
        }

        return written;
    }

    private int Check(int plane)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plane);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(plane, PlaneCount);
        return plane;
    }
}

/// <summary>Receives a borrowed <see cref="VideoFrame"/>.</summary>
public delegate void VideoFrameHandler(in VideoFrame frame);
