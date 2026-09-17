namespace MediaToolkitNet.FFmpeg.Native;

/// <summary>
/// One release series of FFmpeg: the major versions its libraries carry, and
/// the struct offsets that differ between series.
/// </summary>
/// <param name="Release">The FFmpeg release this describes, e.g. 9 for the 9.x series.</param>
/// <param name="AvUtil">libavutil major version.</param>
/// <param name="AvCodec">libavcodec major version.</param>
/// <param name="AvFormat">libavformat major version.</param>
/// <param name="SwScale">libswscale major version.</param>
/// <param name="SwResample">libswresample major version.</param>
/// <param name="CodecContextTimeBase">Offset of <c>AVCodecContext::time_base</c>.</param>
/// <param name="CodecContextPixFmt">Offset of <c>AVCodecContext::pix_fmt</c>.</param>
/// <remarks>
/// FFmpeg keeps its public struct layout stable for the life of a major version
/// and rearranges it freely between them, so a series is the right unit to key
/// an offset on. The values were read with <c>offsetof</c> against the headers
/// of each release on a 64-bit target; only these two move across 7, 8 and 9,
/// and everything else in <see cref="AbiLayout"/> has held since FFmpeg 6.
/// </remarks>
public readonly record struct FFmpegGeneration(
    int Release,
    int AvUtil,
    int AvCodec,
    int AvFormat,
    int SwScale,
    int SwResample,
    int CodecContextTimeBase,
    int CodecContextPixFmt)
{
    /// <summary>libavdevice carries the same major version as libavformat.</summary>
    public int AvDevice => AvFormat;

    /// <summary>The series this binding knows, newest first.</summary>
    public static IReadOnlyList<FFmpegGeneration> Known { get; } =
    [
        new(Release: 9, AvUtil: 61, AvCodec: 63, AvFormat: 63, SwScale: 10, SwResample: 7,
            CodecContextTimeBase: 84, CodecContextPixFmt: 136),
        new(Release: 8, AvUtil: 60, AvCodec: 62, AvFormat: 62, SwScale: 9, SwResample: 6,
            CodecContextTimeBase: 84, CodecContextPixFmt: 136),
        new(Release: 7, AvUtil: 59, AvCodec: 61, AvFormat: 61, SwScale: 8, SwResample: 5,
            CodecContextTimeBase: 84, CodecContextPixFmt: 140),
    ];

    /// <summary>
    /// Finds the series whose libraries these major versions are, or nothing when
    /// they are not a series this binding knows or do not belong together.
    /// </summary>
    /// <param name="avUtil">libavutil major version that was loaded.</param>
    /// <param name="avCodec">libavcodec major version that was loaded.</param>
    /// <param name="avFormat">libavformat major version that was loaded.</param>
    /// <returns>Returns the series, or <see langword="null"/>.</returns>
    public static FFmpegGeneration? Find(int avUtil, int avCodec, int avFormat)
    {
        foreach (var generation in Known)
        {
            if (generation.AvUtil == avUtil && generation.AvCodec == avCodec && generation.AvFormat == avFormat)
            {
                return generation;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public override string ToString() => $"FFmpeg {Release}.x";
}
