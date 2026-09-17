namespace MediaToolkitNet.Abstractions.Formats;

/// <summary>
/// Speaker positions and the standard layouts built out of them, one bit per
/// speaker.
/// </summary>
/// <remarks>
/// <para>
/// The bit values are the ones libavutil, WASAPI and CoreAudio all agree on, so
/// a mask travels through every backend unchanged.
/// </para>
/// <para>
/// A channel count alone does not describe a stream: four channels are quad as
/// often as they are 3.1, and an encoder told the wrong one writes a file whose
/// speakers are in the wrong places. Only <see cref="Default"/> guesses, and it
/// guesses exactly as FFmpeg does.
/// </para>
/// </remarks>
public static class ChannelLayout
{
    /// <summary>Front left.</summary>
    public const ulong FrontLeft = 0x1;

    /// <summary>Front right.</summary>
    public const ulong FrontRight = 0x2;

    /// <summary>Front centre.</summary>
    public const ulong FrontCenter = 0x4;

    /// <summary>Low frequency effects.</summary>
    public const ulong LowFrequency = 0x8;

    /// <summary>Back left.</summary>
    public const ulong BackLeft = 0x10;

    /// <summary>Back right.</summary>
    public const ulong BackRight = 0x20;

    /// <summary>Front left of centre.</summary>
    public const ulong FrontLeftOfCenter = 0x40;

    /// <summary>Front right of centre.</summary>
    public const ulong FrontRightOfCenter = 0x80;

    /// <summary>Back centre.</summary>
    public const ulong BackCenter = 0x100;

    /// <summary>Side left.</summary>
    public const ulong SideLeft = 0x200;

    /// <summary>Side right.</summary>
    public const ulong SideRight = 0x400;

    /// <summary>One centre speaker. FFmpeg calls it <c>mono</c>.</summary>
    public const ulong Mono = FrontCenter;

    /// <summary>The front pair. FFmpeg calls it <c>stereo</c>.</summary>
    public const ulong Stereo = FrontLeft | FrontRight;

    /// <summary>The front pair with a subwoofer. FFmpeg calls it <c>2.1</c>.</summary>
    public const ulong TwoPoint1 = Stereo | LowFrequency;

    /// <summary>The front pair with a centre. FFmpeg calls it <c>3.0</c>.</summary>
    public const ulong Surround = Stereo | FrontCenter;

    /// <summary>Front three with a subwoofer. FFmpeg calls it <c>3.1</c>.</summary>
    public const ulong ThreePoint1 = Surround | LowFrequency;

    /// <summary>The front pair and the back pair. FFmpeg calls it <c>quad</c>.</summary>
    public const ulong Quad = Stereo | BackLeft | BackRight;

    /// <summary>The front pair and the side pair. FFmpeg calls it <c>quad(side)</c>.</summary>
    public const ulong QuadSide = Stereo | SideLeft | SideRight;

    /// <summary>Front three with one back speaker. FFmpeg calls it <c>4.0</c>.</summary>
    public const ulong FourPoint0 = Surround | BackCenter;

    /// <summary>Front three and the back pair. FFmpeg calls it <c>5.0</c>.</summary>
    public const ulong FivePoint0Back = Surround | BackLeft | BackRight;

    /// <summary>Front three and the side pair. FFmpeg calls it <c>5.0(side)</c>.</summary>
    public const ulong FivePoint0Side = Surround | SideLeft | SideRight;

    /// <summary>
    /// Front three, the back pair and a subwoofer. FFmpeg calls it <c>5.1</c>,
    /// and this is what its name table gives for that string.
    /// </summary>
    public const ulong FivePoint1Back = FivePoint0Back | LowFrequency;

    /// <summary>
    /// Front three, the side pair and a subwoofer. FFmpeg calls it
    /// <c>5.1(side)</c>, and this is what it defaults six channels to.
    /// </summary>
    public const ulong FivePoint1Side = FivePoint0Side | LowFrequency;

    /// <summary>Five point one on the sides plus a back speaker. FFmpeg calls it <c>6.1</c>.</summary>
    public const ulong SixPoint1 = FivePoint1Side | BackCenter;

    /// <summary>Front three, both pairs and a subwoofer. FFmpeg calls it <c>7.1</c>.</summary>
    public const ulong SevenPoint1 = FivePoint1Side | BackLeft | BackRight;

    /// <summary>Nothing stated; the backend decides.</summary>
    public const ulong Unspecified = 0;

    /// <summary>
    /// The layout FFmpeg assumes for a bare channel count, as
    /// <c>av_channel_layout_default</c> defines it.
    /// </summary>
    /// <param name="channels">Number of channels.</param>
    /// <returns>
    /// Returns the mask, or <see cref="Unspecified"/> when there is no
    /// conventional layout of that size.
    /// </returns>
    /// <remarks>
    /// Note that six channels default to <see cref="FivePoint1Side"/> while the
    /// string <c>5.1</c> means <see cref="FivePoint1Back"/>. Both are six
    /// channels, and they are not the same file.
    /// </remarks>
    public static ulong Default(int channels) => channels switch
    {
        1 => Mono,
        2 => Stereo,
        3 => Surround,
        4 => Quad,
        5 => FivePoint0Side,
        6 => FivePoint1Side,
        7 => SixPoint1,
        8 => SevenPoint1,
        _ => Unspecified,
    };
}
