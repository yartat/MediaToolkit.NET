#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions;

namespace MediaToolkitNet.Mpv;

/// <summary>What <c>screenshot-raw</c> draws into the picture.</summary>
public enum MpvScreenshotContent
{
    /// <summary>The video with subtitles, without the on-screen display. mpv's default.</summary>
    Subtitles,

    /// <summary>The decoded video alone, at its own size.</summary>
    Video,

    /// <summary>What the window shows, scaled to the window, with subtitles and the on-screen display.</summary>
    Window,
}

/// <summary>The pixel layout of an <see cref="MpvScreenshot"/>.</summary>
public enum MpvScreenshotFormat
{
    /// <summary>B, G, R, then a padding byte whose value is undefined; 4 bytes per pixel.</summary>
    Bgr0,

    /// <summary>B, G, R, A; 4 bytes per pixel.</summary>
    Bgra,

    /// <summary>R, G, B, A; 4 bytes per pixel.</summary>
    Rgba,

    /// <summary>R, G, B, A as 16-bit little-endian components, which keeps the precision of a high bit depth source; 8 bytes per pixel.</summary>
    Rgba64,
}

/// <summary>A picture taken by mpv: the frame as it is shown, after its filters, scaling and subtitles.</summary>
public sealed class MpvScreenshot
{
    internal MpvScreenshot(int width, int height, int stride, MpvScreenshotFormat format, ReadOnlyMemory<byte> data)
    {
        Width = width;
        Height = height;
        Stride = stride;
        Format = format;
        Data = data;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Bytes from the start of one row to the start of the next, which can be more than a row holds.</summary>
    public int Stride { get; }

    /// <summary>The layout of <see cref="Data"/>.</summary>
    public MpvScreenshotFormat Format { get; }

    /// <summary>Bytes per pixel: 8 for <see cref="MpvScreenshotFormat.Rgba64"/>, 4 for the others.</summary>
    public int BytesPerPixel => Format == MpvScreenshotFormat.Rgba64 ? 8 : 4;

    /// <summary>The pixels, top row first, <see cref="Stride"/> bytes apart.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>The pixels of one row, without the padding after it.</summary>
    public ReadOnlySpan<byte> GetRow(int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return Data.Span.Slice(y * Stride, Width * BytesPerPixel);
    }

    internal static string Name(MpvScreenshotContent content) => content switch
    {
        MpvScreenshotContent.Video => "video",
        MpvScreenshotContent.Window => "window",
        _ => "subtitles",
    };

    internal static string Name(MpvScreenshotFormat format) => format switch
    {
        MpvScreenshotFormat.Bgr0 => "bgr0",
        MpvScreenshotFormat.Rgba => "rgba",
        MpvScreenshotFormat.Rgba64 => "rgba64",
        _ => "bgra",
    };

    internal static MpvScreenshot From(MpvNode result, MpvScreenshotFormat format)
    {
        var width = result["w"]?.AsInt64();
        var height = result["h"]?.AsInt64();
        var stride = result["stride"]?.AsInt64();
        var data = result["data"]?.AsBytes();
        if (width is not > 0 or > int.MaxValue || height is not > 0 or > int.MaxValue || stride is null || data is null)
        {
            throw new MediaToolkitNetException(Native.Mpv.BackendName, "screenshot-raw returned no picture", 0);
        }

        var bytesPerPixel = format == MpvScreenshotFormat.Rgba64 ? 8 : 4;
        if (stride < width * bytesPerPixel || stride > int.MaxValue
            || ((height - 1) * stride) + (width * bytesPerPixel) > data.Value.Length)
        {
            // A negative stride would mean the rows run bottom-up from the end of the data; mpv has no source of one.
            throw new MediaToolkitNetException(
                Native.Mpv.BackendName,
                $"screenshot-raw returned a picture that does not fit its data ({width}x{height}, stride {stride}, {data.Value.Length} bytes)",
                0);
        }

        return new MpvScreenshot((int)width, (int)height, (int)stride, format, data.Value);
    }
}
