using System.Runtime.InteropServices;

namespace MediaToolkitNet.Windows.Native;

/// <summary>PIN_DIRECTION.</summary>
public enum PinDirection
{
    /// <summary>PINDIR_INPUT.</summary>
    Input = 0,

    /// <summary>PINDIR_OUTPUT.</summary>
    Output = 1,
}

/// <summary>FILTER_STATE.</summary>
public enum FilterState
{
    /// <summary>State_Stopped.</summary>
    Stopped = 0,

    /// <summary>State_Paused.</summary>
    Paused = 1,

    /// <summary>State_Running.</summary>
    Running = 2,
}

/// <summary>
/// Mirrors <c>AM_MEDIA_TYPE</c>, 88 bytes on a 64-bit target.
/// </summary>
/// <remarks>
/// The trailing <see cref="FormatBlock"/> points at a structure named by
/// <see cref="FormatType"/>: a VIDEOINFOHEADER, a VIDEOINFOHEADER2 or a
/// WAVEFORMATEX. DirectShow allocates both the media type and the format block
/// with the COM task allocator, so a media type received from an interface must
/// be released with <see cref="DsNative.FreeMediaType"/>.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct AmMediaType
{
    /// <summary>Major type, for example MEDIATYPE_Video.</summary>
    public Guid MajorType;

    /// <summary>Subtype, for example MEDIASUBTYPE_RGB24.</summary>
    public Guid Subtype;

    /// <summary>TRUE when every sample has the same size.</summary>
    public int FixedSizeSamples;

    /// <summary>TRUE when the stream uses temporal compression.</summary>
    public int TemporalCompression;

    /// <summary>Size of one sample, when fixed.</summary>
    public uint SampleSize;

    /// <summary>Identifies the layout of <see cref="FormatBlock"/>.</summary>
    public Guid FormatType;

    /// <summary>Optional object that owns the format block.</summary>
    public void* Unknown;

    /// <summary>Size of <see cref="FormatBlock"/> in bytes.</summary>
    public uint FormatSize;

    /// <summary>The format block itself.</summary>
    public byte* FormatBlock;
}

/// <summary>Mirrors <c>BITMAPINFOHEADER</c>, 40 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct BitmapInfoHeader
{
    /// <summary>Size of this structure.</summary>
    public uint Size;

    /// <summary>Frame width in pixels.</summary>
    public int Width;

    /// <summary>Frame height; negative means the image is stored top-down.</summary>
    public int Height;

    /// <summary>Colour planes; always 1.</summary>
    public ushort Planes;

    /// <summary>Bits per pixel.</summary>
    public ushort BitCount;

    /// <summary>FOURCC, or 0 for uncompressed RGB.</summary>
    public uint Compression;

    /// <summary>Size of the image data.</summary>
    public uint SizeImage;

    /// <summary>Horizontal resolution.</summary>
    public int XPelsPerMeter;

    /// <summary>Vertical resolution.</summary>
    public int YPelsPerMeter;

    /// <summary>Palette entries used.</summary>
    public uint ClrUsed;

    /// <summary>Palette entries that matter.</summary>
    public uint ClrImportant;
}

/// <summary>Mirrors <c>VIDEOINFOHEADER</c>, 88 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct VideoInfoHeader
{
    /// <summary>Source rectangle; all zero means the whole image.</summary>
    public int SourceLeft;

    /// <summary>Source rectangle.</summary>
    public int SourceTop;

    /// <summary>Source rectangle.</summary>
    public int SourceRight;

    /// <summary>Source rectangle.</summary>
    public int SourceBottom;

    /// <summary>Target rectangle.</summary>
    public int TargetLeft;

    /// <summary>Target rectangle.</summary>
    public int TargetTop;

    /// <summary>Target rectangle.</summary>
    public int TargetRight;

    /// <summary>Target rectangle.</summary>
    public int TargetBottom;

    /// <summary>Approximate bit rate.</summary>
    public uint BitRate;

    /// <summary>Approximate bit error rate.</summary>
    public uint BitErrorRate;

    /// <summary>Frame duration in 100-nanosecond units.</summary>
    public long AverageTimePerFrame;

    /// <summary>The bitmap description.</summary>
    public BitmapInfoHeader Bitmap;
}

/// <summary>Mirrors <c>PIN_INFO</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct PinInfo
{
    /// <summary>The filter that owns the pin. The caller must release it.</summary>
    public void* Filter;

    /// <summary>Whether the pin is an input or an output.</summary>
    public PinDirection Direction;

    /// <summary>Pin name, a fixed 128-character buffer.</summary>
    public fixed char Name[128];
}

/// <summary>Mirrors <c>FILTER_INFO</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FilterInfo
{
    /// <summary>Filter name, a fixed 128-character buffer.</summary>
    public fixed char Name[128];

    /// <summary>The graph the filter belongs to. The caller must release it.</summary>
    public void* Graph;
}

/// <summary>HRESULTs and flags that the DirectShow graph returns.</summary>
public static class DsResults
{
    /// <summary>VFW_S_PARTIAL_RENDER: the graph rendered some, but not all, streams.</summary>
    public const int PartialRender = 0x00040242;

    /// <summary>VFW_S_AUDIO_NOT_RENDERED.</summary>
    public const int AudioNotRendered = 0x00040258;

    /// <summary>VFW_S_VIDEO_NOT_RENDERED.</summary>
    public const int VideoNotRendered = 0x00040257;

    /// <summary>VFW_E_NOT_CONNECTED.</summary>
    public const int NotConnected = unchecked((int)0x80040209);

    /// <summary>VFW_E_NO_ACCEPTABLE_TYPES.</summary>
    public const int NoAcceptableTypes = unchecked((int)0x80040207);

    /// <summary>VFW_E_CANNOT_CONNECT.</summary>
    public const int CannotConnect = unchecked((int)0x80040217);

    /// <summary>VFW_E_CANNOT_RENDER: no combination of filters could render the stream.</summary>
    public const int CannotRender = unchecked((int)0x80040218);

    /// <summary>VFW_E_NOT_IN_GRAPH.</summary>
    public const int NotInGraph = unchecked((int)0x8004025F);

    /// <summary>VFW_E_NO_TIME_FORMAT_SET.</summary>
    public const int NoTimeFormatSet = unchecked((int)0x80040261);

    /// <summary>VFW_E_WRONG_STATE.</summary>
    public const int WrongState = unchecked((int)0x80040227);

    /// <summary>EC_COMPLETE, raised when every stream has finished.</summary>
    public const int EventComplete = 0x01;

    /// <summary>EC_USERABORT.</summary>
    public const int EventUserAbort = 0x02;

    /// <summary>EC_ERRORABORT.</summary>
    public const int EventErrorAbort = 0x03;

    /// <summary>AM_SEEKING_AbsolutePositioning.</summary>
    public const uint SeekAbsolute = 0x01;

    /// <summary>AM_SEEKING_NoPositioning, which leaves a position unchanged.</summary>
    public const uint SeekNone = 0x00;

    /// <summary>True when the graph rendered only part of what it was given.</summary>
    public static bool IsPartial(int hr) =>
        hr is PartialRender or AudioNotRendered or VideoNotRendered;
}
