namespace MediaToolkitNet.Core.Formats;

/// <summary>
/// Raw video buffer layout. Only the formats that every backend in this
/// repository can actually produce or consume are listed.
/// </summary>
public enum PixelFormat
{
    /// <summary>Unknown or compressed payload.</summary>
    Unknown = 0,

    /// <summary>Planar Y, U, V; chroma subsampled 2x2 (three planes).</summary>
    Yuv420P,

    /// <summary>Planar Y, U, V; chroma subsampled 2x1 (three planes).</summary>
    Yuv422P,

    /// <summary>Planar Y, U, V; no chroma subsampling (three planes).</summary>
    Yuv444P,

    /// <summary>Planar Y plus interleaved UV; chroma subsampled 2x2 (two planes).</summary>
    Nv12,

    /// <summary>Packed Y0 U Y1 V, subsampled 2x1 (one plane).</summary>
    Yuyv422,

    /// <summary>Packed U Y0 V Y1, subsampled 2x1 (one plane).</summary>
    Uyvy422,

    /// <summary>Packed 8-bit RGB (one plane, 3 bytes per pixel).</summary>
    Rgb24,

    /// <summary>Packed 8-bit BGR (one plane, 3 bytes per pixel).</summary>
    Bgr24,

    /// <summary>Packed 8-bit RGBA (one plane, 4 bytes per pixel).</summary>
    Rgba32,

    /// <summary>Packed 8-bit BGRA (one plane, 4 bytes per pixel).</summary>
    Bgra32,

    /// <summary>Motion JPEG payload; the buffer holds a complete JPEG image.</summary>
    Mjpeg,
}

/// <summary>Helpers describing the memory layout of a <see cref="PixelFormat"/>.</summary>
public static class PixelFormatExtensions
{
    /// <summary>Number of separately addressable planes.</summary>
    public static int PlaneCount(this PixelFormat format) => format switch
    {
        PixelFormat.Yuv420P or PixelFormat.Yuv422P or PixelFormat.Yuv444P => 3,
        PixelFormat.Nv12 => 2,
        PixelFormat.Unknown => 0,
        _ => 1,
    };

    /// <summary>
    /// Bytes required for one row of the given plane, without any alignment padding.
    /// Returns 0 for formats whose size cannot be derived from the geometry (e.g. MJPEG).
    /// </summary>
    public static int MinimumStride(this PixelFormat format, int width, int plane = 0) => format switch
    {
        PixelFormat.Yuv420P => plane == 0 ? width : (width + 1) / 2,
        PixelFormat.Yuv422P => plane == 0 ? width : (width + 1) / 2,
        PixelFormat.Yuv444P => width,
        PixelFormat.Nv12 => width,
        PixelFormat.Yuyv422 or PixelFormat.Uyvy422 => width * 2,
        PixelFormat.Rgb24 or PixelFormat.Bgr24 => width * 3,
        PixelFormat.Rgba32 or PixelFormat.Bgra32 => width * 4,
        _ => 0,
    };

    /// <summary>Number of rows stored in the given plane.</summary>
    public static int PlaneHeight(this PixelFormat format, int height, int plane = 0) => format switch
    {
        PixelFormat.Yuv420P => plane == 0 ? height : (height + 1) / 2,
        PixelFormat.Nv12 => plane == 0 ? height : (height + 1) / 2,
        _ => height,
    };

    /// <summary>Total unpadded size of one frame, or 0 when it is not derivable.</summary>
    public static int FrameSize(this PixelFormat format, int width, int height)
    {
        var planes = format.PlaneCount();
        if (planes == 0)
        {
            return 0;
        }

        var total = 0;
        for (var i = 0; i < planes; i++)
        {
            var stride = format.MinimumStride(width, i);
            if (stride == 0)
            {
                return 0;
            }

            total += stride * format.PlaneHeight(height, i);
        }

        return total;
    }
}
