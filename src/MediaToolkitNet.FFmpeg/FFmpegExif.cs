#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.FFmpeg.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg;

/// <summary>The data types of a TIFF or EXIF entry, numbered as in the TIFF specification.</summary>
public enum TiffDataType
{
    /// <summary>Unsigned 8-bit integers.</summary>
    Byte = 1,

    /// <summary>A NUL-terminated ASCII string.</summary>
    String = 2,

    /// <summary>Unsigned 16-bit integers.</summary>
    Short = 3,

    /// <summary>Unsigned 32-bit integers.</summary>
    Long = 4,

    /// <summary>Unsigned fractions: two 32-bit integers.</summary>
    Rational = 5,

    /// <summary>Signed 8-bit integers.</summary>
    SByte = 6,

    /// <summary>Bytes without a defined type.</summary>
    Undefined = 7,

    /// <summary>Signed 16-bit integers.</summary>
    SShort = 8,

    /// <summary>Signed 32-bit integers.</summary>
    SLong = 9,

    /// <summary>Signed fractions: two signed 32-bit integers.</summary>
    SRational = 10,

    /// <summary>32-bit floating point.</summary>
    Float = 11,

    /// <summary>64-bit floating point.</summary>
    Double = 12,

    /// <summary>A nested image file directory.</summary>
    Ifd = 13,
}

/// <summary>Where the TIFF structure starts in a buffer handed to <see cref="FFmpegExif.Parse"/>.</summary>
public enum ExifHeaderMode
{
    /// <summary>The buffer starts with a TIFF header: <c>II*\0</c> or <c>MM\0*</c>. This is how FFmpeg carries EXIF in frame side data.</summary>
    TiffHeader = 0,

    /// <summary>There is no header; the data is little endian.</summary>
    AssumeLittleEndian = 1,

    /// <summary>There is no header; the data is big endian.</summary>
    AssumeBigEndian = 2,

    /// <summary>The first four bytes give the offset of the TIFF header.</summary>
    TiffOffset = 3,

    /// <summary>The buffer starts with <c>Exif\0\0</c>, as the payload of a JPEG APP1 segment does, and then has a TIFF header.</summary>
    Exif00 = 4,
}

/// <summary>One entry of an EXIF image file directory.</summary>
/// <param name="Id">The tag number.</param>
/// <param name="Name">FFmpeg's name for the tag, or <see langword="null"/> when it has none.</param>
/// <param name="Type">How the values are stored.</param>
/// <param name="Values">
/// The values: <see cref="ulong"/> for <see cref="TiffDataType.Byte"/>, <see cref="TiffDataType.Short"/> and
/// <see cref="TiffDataType.Long"/>; <see cref="long"/> for the signed types; <see cref="double"/> for
/// <see cref="TiffDataType.Float"/> and <see cref="TiffDataType.Double"/>; one <see cref="string"/> for
/// <see cref="TiffDataType.String"/>; <c>(int Numerator, int Denominator)</c> pairs for the rational types;
/// one <see cref="byte"/> array for <see cref="TiffDataType.Undefined"/>. Empty for a nested directory.
/// </param>
/// <param name="Children">The entries of a nested directory, or <see langword="null"/>.</param>
public sealed record ExifEntry(
    ushort Id,
    string? Name,
    TiffDataType Type,
    IReadOnlyList<object> Values,
    IReadOnlyList<ExifEntry>? Children = null);

/// <summary>
/// Reads EXIF metadata through the API FFmpeg 8.1 added for it (<c>av_exif_*</c>).
/// Needs <see cref="FFmpegFeatures.Exif"/>.
/// </summary>
public static class FFmpegExif
{
    /// <summary>Parses an EXIF or TIFF buffer into its entries.</summary>
    /// <param name="data">The buffer.</param>
    /// <param name="mode">Where the TIFF structure starts in it.</param>
    /// <returns>Returns the entries of the first directory.</returns>
    /// <exception cref="NotSupportedException">The libraries are older than FFmpeg 8.1.</exception>
    /// <exception cref="Abstractions.MediaToolkitNetException">The buffer is not valid EXIF.</exception>
    public static unsafe IReadOnlyList<ExifEntry> Parse(ReadOnlySpan<byte> data, ExifHeaderMode mode = ExifHeaderMode.Exif00)
    {
        RequireExif();

        var ifd = stackalloc byte[16];
        new Span<byte>(ifd, 16).Clear();
        int result;
        fixed (byte* bytes = data)
        {
            result = AVModern.av_exif_parse_buffer(null, bytes, (nuint)data.Length, ifd, (int)mode);
        }

        try
        {
            if (result < 0)
            {
                throw new Abstractions.MediaToolkitNetException(
                    FFmpegLibraries.BackendName, $"av_exif_parse_buffer: {FFmpegError.Describe(result)}", result);
            }

            return Read(ifd);
        }
        finally
        {
            AVModern.av_exif_free(ifd);
        }
    }

    /// <summary>FFmpeg's name for a tag number, for example <c>Orientation</c> for 0x0112, or <see langword="null"/>.</summary>
    /// <param name="id">The tag number.</param>
    /// <exception cref="NotSupportedException">The libraries are older than FFmpeg 8.1.</exception>
    public static unsafe string? TagName(ushort id)
    {
        RequireExif();
        var name = AVModern.av_exif_get_tag_name(id);
        return name is null ? null : Utf8.ToManagedOrEmpty(name);
    }

    private static void RequireExif()
    {
        if (!FFmpegFeatures.Exif)
        {
            throw new NotSupportedException(
                "EXIF needs the av_exif_* API, which FFmpeg has from 8.1 on; check FFmpegFeatures.Exif first");
        }
    }

    private static unsafe List<ExifEntry> Read(void* ifd)
    {
        var entries = new List<ExifEntry>(AbiLayout.ExifCount(ifd));
        for (var i = 0; i < AbiLayout.ExifCount(ifd); i++)
        {
            var entry = AbiLayout.ExifEntryAt(ifd, i);
            var id = AbiLayout.ExifId(entry);
            var type = (TiffDataType)AbiLayout.ExifType(entry);
            var count = AbiLayout.ExifValueCount(entry);
            var name = AVModern.av_exif_get_tag_name(id);
            var tag = name is null ? null : Utf8.ToManagedOrEmpty(name);

            if (type == TiffDataType.Ifd)
            {
                entries.Add(new ExifEntry(id, tag, type, [], Read(AbiLayout.ExifNested(entry))));
                continue;
            }

            entries.Add(new ExifEntry(id, tag, type, ValuesOf(type, AbiLayout.ExifValue(entry), count)));
        }

        return entries;
    }

    // How libavcodec stores each type: 16- and 32-bit integers widened to 64, fractions as AVRational.
    private static unsafe List<object> ValuesOf(TiffDataType type, void* value, int count)
    {
        var values = new List<object>();
        if (value is null)
        {
            return values;
        }

        switch (type)
        {
            case TiffDataType.Short:
            case TiffDataType.Long:
                for (var i = 0; i < count; i++)
                {
                    values.Add(((ulong*)value)[i]);
                }

                break;
            case TiffDataType.SShort:
            case TiffDataType.SLong:
                for (var i = 0; i < count; i++)
                {
                    values.Add(((long*)value)[i]);
                }

                break;
            case TiffDataType.Float:
            case TiffDataType.Double:
                for (var i = 0; i < count; i++)
                {
                    values.Add(((double*)value)[i]);
                }

                break;
            case TiffDataType.Rational:
            case TiffDataType.SRational:
                for (var i = 0; i < count; i++)
                {
                    var pair = ((int*)value) + (2 * i);
                    values.Add((pair[0], pair[1]));
                }

                break;
            case TiffDataType.Byte:
                for (var i = 0; i < count; i++)
                {
                    values.Add((ulong)((byte*)value)[i]);
                }

                break;
            case TiffDataType.SByte:
                for (var i = 0; i < count; i++)
                {
                    values.Add((long)((sbyte*)value)[i]);
                }

                break;
            case TiffDataType.String:
                values.Add(Utf8.ToManagedOrEmpty((byte*)value));
                break;
            default:
                values.Add(new ReadOnlySpan<byte>(value, count).ToArray());
                break;
        }

        return values;
    }
}
