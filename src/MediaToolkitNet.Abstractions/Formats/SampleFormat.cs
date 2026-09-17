namespace MediaToolkitNet.Abstractions.Formats;

/// <summary>
/// PCM sample layout of an audio buffer. Values map 1:1 onto the native sample
/// formats used by libavutil, WASAPI, ALSA and CoreAudio.
/// </summary>
public enum SampleFormat
{
    /// <summary>Unknown or unsupported layout.</summary>
    Unknown = 0,

    /// <summary>Unsigned 8-bit, interleaved.</summary>
    U8,

    /// <summary>Signed 16-bit little-endian, interleaved.</summary>
    S16,

    /// <summary>Signed 32-bit little-endian, interleaved.</summary>
    S32,

    /// <summary>32-bit IEEE float, interleaved.</summary>
    F32,

    /// <summary>64-bit IEEE float, interleaved.</summary>
    F64,

    /// <summary>Unsigned 8-bit, one plane per channel.</summary>
    U8Planar,

    /// <summary>Signed 16-bit little-endian, one plane per channel.</summary>
    S16Planar,

    /// <summary>Signed 32-bit little-endian, one plane per channel.</summary>
    S32Planar,

    /// <summary>32-bit IEEE float, one plane per channel.</summary>
    F32Planar,

    /// <summary>64-bit IEEE float, one plane per channel.</summary>
    F64Planar,
}

/// <summary>Helpers describing the memory layout of a <see cref="SampleFormat"/>.</summary>
public static class SampleFormatExtensions
{
    /// <summary>Size of a single sample of one channel, in bytes.</summary>
    public static int BytesPerSample(this SampleFormat format) => format switch
    {
        SampleFormat.U8 or SampleFormat.U8Planar => 1,
        SampleFormat.S16 or SampleFormat.S16Planar => 2,
        SampleFormat.S32 or SampleFormat.S32Planar => 4,
        SampleFormat.F32 or SampleFormat.F32Planar => 4,
        SampleFormat.F64 or SampleFormat.F64Planar => 8,
        _ => 0,
    };

    /// <summary>True when every channel lives in its own plane.</summary>
    public static bool IsPlanar(this SampleFormat format) => format
        is SampleFormat.U8Planar or SampleFormat.S16Planar or SampleFormat.S32Planar
        or SampleFormat.F32Planar or SampleFormat.F64Planar;

    /// <summary>True when samples are IEEE floating point rather than integer PCM.</summary>
    public static bool IsFloat(this SampleFormat format) => format
        is SampleFormat.F32 or SampleFormat.F64 or SampleFormat.F32Planar or SampleFormat.F64Planar;

    /// <summary>Returns the interleaved counterpart of a planar format (or the format itself).</summary>
    public static SampleFormat ToInterleaved(this SampleFormat format) => format switch
    {
        SampleFormat.U8Planar => SampleFormat.U8,
        SampleFormat.S16Planar => SampleFormat.S16,
        SampleFormat.S32Planar => SampleFormat.S32,
        SampleFormat.F32Planar => SampleFormat.F32,
        SampleFormat.F64Planar => SampleFormat.F64,
        _ => format,
    };
}
