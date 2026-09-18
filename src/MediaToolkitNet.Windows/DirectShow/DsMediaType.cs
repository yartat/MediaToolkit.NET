using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows.DirectShow;

/// <summary>
/// A media type read off a pin, translated into terms this library uses.
/// </summary>
/// <param name="MajorType">MEDIATYPE_Video, MEDIATYPE_Audio, MEDIATYPE_Stream and so on.</param>
/// <param name="Subtype">The specific format inside the major type.</param>
/// <param name="FormatType">Identifies which structure the format block held.</param>
/// <param name="Video">Set when the format block was a VIDEOINFOHEADER.</param>
/// <param name="Audio">Set when the format block was a WAVEFORMATEX.</param>
/// <param name="SampleSize">Fixed sample size, or 0 when samples vary.</param>
[SupportedOSPlatform("windows")]
public readonly record struct DsMediaType(
    Guid MajorType,
    Guid Subtype,
    Guid FormatType,
    VideoFormat? Video,
    AudioFormat? Audio,
    uint SampleSize)
{
    /// <summary>True when this describes a video stream.</summary>
    public bool IsVideo => MajorType == DsGuids.MediaTypeVideo;

    /// <summary>True when this describes an audio stream.</summary>
    public bool IsAudio => MajorType == DsGuids.MediaTypeAudio;

    /// <summary>True when this is an unparsed byte stream, as a file source produces.</summary>
    public bool IsStream => MajorType == DsGuids.MediaTypeStream;

    /// <summary>
    /// Reads an <c>AM_MEDIA_TYPE</c>, decoding the format block when it is one of
    /// the two layouts that carry a usable description.
    /// </summary>
    public static unsafe DsMediaType From(in AmMediaType native)
    {
        VideoFormat? video = null;
        AudioFormat? audio = null;

        if (native.FormatType == DsGuids.FormatVideoInfo
            && native.FormatBlock is not null
            && native.FormatSize >= (uint)sizeof(VideoInfoHeader))
        {
            var header = *(VideoInfoHeader*)native.FormatBlock;
            var rate = header.AverageTimePerFrame > 0
                ? Rational.FromDouble(10_000_000d / header.AverageTimePerFrame)
                : Rational.Zero;

            // A negative height means the rows are stored top-down.
            video = new VideoFormat(
                header.Bitmap.Width,
                Math.Abs(header.Bitmap.Height),
                ToPixelFormat(native.Subtype),
                rate);
        }
        else if (native.FormatType == DsGuids.FormatWaveFormatEx
                 && native.FormatBlock is not null
                 && native.FormatSize >= 16)
        {
            var wave = (WaveFormatEx*)native.FormatBlock;
            audio = Wasapi.ReadFormat(wave);
        }

        return new DsMediaType(
            native.MajorType, native.Subtype, native.FormatType, video, audio, native.SampleSize);
    }

    /// <summary>Maps a DirectShow subtype onto a toolkit pixel format.</summary>
    public static PixelFormat ToPixelFormat(Guid subtype)
    {
        if (subtype == DsGuids.SubtypeRgb24)
        {
            return PixelFormat.Bgr24;
        }

        if (subtype == DsGuids.SubtypeRgb32 || subtype == DsGuids.SubtypeArgb32)
        {
            return PixelFormat.Bgra32;
        }

        if (subtype == DsGuids.SubtypeYuy2)
        {
            return PixelFormat.Yuyv422;
        }

        if (subtype == DsGuids.SubtypeUyvy)
        {
            return PixelFormat.Uyvy422;
        }

        if (subtype == DsGuids.SubtypeNv12)
        {
            return PixelFormat.Nv12;
        }

        return subtype == DsGuids.SubtypeMjpg ? PixelFormat.Mjpeg : PixelFormat.Unknown;
    }

    /// <summary>
    /// Maps a toolkit pixel format back onto a DirectShow subtype, for asking a
    /// filter to produce one particular layout.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The format has no DirectShow subtype this library knows.
    /// </exception>
    public static Guid ToSubtype(PixelFormat format) => format switch
    {
        PixelFormat.Bgr24 => DsGuids.SubtypeRgb24,
        PixelFormat.Bgra32 => DsGuids.SubtypeRgb32,
        PixelFormat.Yuyv422 => DsGuids.SubtypeYuy2,
        PixelFormat.Uyvy422 => DsGuids.SubtypeUyvy,
        PixelFormat.Nv12 => DsGuids.SubtypeNv12,
        _ => throw new ArgumentOutOfRangeException(
            nameof(format), format, "The pixel format has no DirectShow subtype."),
    };

    /// <inheritdoc />
    public override string ToString()
    {
        if (Video is { } v)
        {
            return $"video {v}";
        }

        if (Audio is { } a)
        {
            return $"audio {a}";
        }

        return IsStream ? "stream" : $"{MajorType:D}/{Subtype:D}";
    }
}
