using System.Runtime.InteropServices;
using MediaToolkitNet.Abstractions.Formats;

namespace MediaToolkitNet.FFmpeg.Native;

/// <summary>Mirrors <c>AVRational</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AVRationalNative
{
    /// <summary>Numerator.</summary>
    public int Num;

    /// <summary>Denominator.</summary>
    public int Den;

    /// <summary>Creates a rational.</summary>
    public AVRationalNative(int num, int den)
    {
        Num = num;
        Den = den;
    }

    /// <summary>Converts to the backend-agnostic rational type.</summary>
    public readonly Abstractions.Formats.Rational ToRational() => new(Num, Den);

    /// <summary>Converts from the backend-agnostic rational type.</summary>
    public static AVRationalNative From(Abstractions.Formats.Rational value) => new(value.Numerator, value.Denominator);
}

/// <summary>
/// Mirrors <c>AVPacket</c>. The layout has been stable since FFmpeg 5.0 and is
/// validated at startup by <see cref="AbiLayout"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct AVPacketNative
{
    /// <summary>Reference-counted buffer backing <see cref="Data"/>.</summary>
    public void* Buf;

    /// <summary>Presentation timestamp in the stream time base.</summary>
    public long Pts;

    /// <summary>Decode timestamp in the stream time base.</summary>
    public long Dts;

    /// <summary>Payload.</summary>
    public byte* Data;

    /// <summary>Payload size in bytes.</summary>
    public int Size;

    /// <summary>Index of the stream this packet belongs to.</summary>
    public int StreamIndex;

    /// <summary>AV_PKT_FLAG_* bits.</summary>
    public int Flags;

    /// <summary>Side data array.</summary>
    public void* SideData;

    /// <summary>Number of side data entries.</summary>
    public int SideDataElems;

    /// <summary>Duration in the stream time base.</summary>
    public long Duration;

    /// <summary>Byte position in the source file, or -1.</summary>
    public long Pos;

    /// <summary>User data.</summary>
    public void* Opaque;

    /// <summary>Reference-counted user data.</summary>
    public void* OpaqueRef;

    /// <summary>Time base the timestamps in this packet are expressed in.</summary>
    public AVRationalNative TimeBase;
}

/// <summary>
/// Mirrors the head of <c>AVFrame</c>, up to and including <c>time_base</c>.
/// </summary>
/// <remarks>
/// Only the head is mapped on purpose. Everything up to <c>time_base</c> has kept
/// the same layout across FFmpeg 5, 6 and 7, while the tail of the struct moves
/// between majors. The fields past this point (sample_rate, ch_layout, buffers)
/// are deliberately not read: the audio format is always known from the codec
/// context that produced the frame.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct AVFrameHead
{
    /// <summary>Plane pointers; for planar audio with more than 8 channels use <see cref="ExtendedData"/>.</summary>
    public fixed long Data[AVConstants.NumDataPointers];

    /// <summary>Per-plane stride, in bytes. For audio, only index 0 is meaningful.</summary>
    public fixed int Linesize[AVConstants.NumDataPointers];

    /// <summary>
    /// Always points at valid plane pointers, even when there are more than
    /// <see cref="AVConstants.NumDataPointers"/> planes.
    /// </summary>
    public byte** ExtendedData;

    /// <summary>Frame width in pixels (video only).</summary>
    public int Width;

    /// <summary>Frame height in pixels (video only).</summary>
    public int Height;

    /// <summary>Samples per channel (audio only).</summary>
    public int NbSamples;

    /// <summary>An <see cref="AVPixelFormat"/> for video or an <see cref="AVSampleFormat"/> for audio.</summary>
    public int Format;

    /// <summary>Deprecated key frame flag; kept only to preserve the layout.</summary>
    public int KeyFrame;

    /// <summary>Picture type.</summary>
    public int PictType;

    /// <summary>Sample aspect ratio.</summary>
    public AVRationalNative SampleAspectRatio;

    /// <summary>Presentation timestamp in <see cref="TimeBase"/> units.</summary>
    public long Pts;

    /// <summary>DTS copied from the packet that produced this frame.</summary>
    public long PktDts;

    /// <summary>Time base of <see cref="Pts"/>; may be 0/0 when the decoder did not set it.</summary>
    public AVRationalNative TimeBase;
}

/// <summary>
/// Mirrors <c>AVChannelLayout</c> as introduced in FFmpeg 5.1. Passed by
/// pointer to <c>swr_alloc_set_opts2</c> and to the parameter helpers.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct AVChannelLayoutNative
{
    /// <summary>AVChannelOrder; 1 is AV_CHANNEL_ORDER_NATIVE.</summary>
    public int Order;

    /// <summary>Number of channels.</summary>
    public int NbChannels;

    /// <summary>Channel mask for the native order.</summary>
    public ulong Mask;

    /// <summary>Opaque pointer used by custom orders.</summary>
    public void* Opaque;

    /// <summary>
    /// Builds the layout for a channel count and an optional speaker mask.
    /// </summary>
    /// <param name="channels">Number of channels.</param>
    /// <param name="mask">
    /// The speakers to place them at, or <see cref="ChannelLayout.Unspecified"/>
    /// to let FFmpeg assume the conventional layout of that size.
    /// </param>
    /// <returns>Returns the layout.</returns>
    public static AVChannelLayoutNative Of(int channels, ulong mask = ChannelLayout.Unspecified)
    {
        if (mask != ChannelLayout.Unspecified)
        {
            return new AVChannelLayoutNative
            {
                Order = 1,
                NbChannels = channels,
                Mask = mask,
                Opaque = null,
            };
        }

        // Guessing here in managed code is how four channels used to come out as
        // 3.1 rather than quad; libavutil has the table, so it answers.
        AVChannelLayoutNative layout;
        AV.av_channel_layout_default(&layout, channels);
        return layout;
    }

    /// <summary>Builds the layout FFmpeg assumes for a bare channel count.</summary>
    public static AVChannelLayoutNative Default(int channels) => Of(channels);
}
