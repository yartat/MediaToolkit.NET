#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Globalization;
using System.Text;
using MediaToolkitNet.Abstractions.Transcoding;

namespace MediaToolkitNet.FFmpeg.Transcoding;

/// <summary>
/// Turns output settings into libavfilter chains. mpv takes the same syntax
/// through its <c>lavfi</c> filter, so its transcoder uses these too.
/// </summary>
public static class FilterChains
{
    /// <summary>The name <see cref="VideoFilter.Custom"/> and <see cref="AudioFilter.Custom"/> use for this syntax.</summary>
    public const string Backend = "ffmpeg";

    /// <summary>
    /// The libavfilter form of a video filter, or <see langword="null"/> for a
    /// custom filter written for another backend.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="alsoAccept">Another backend name whose custom filters are also libavfilter syntax, such as <c>mpv</c>.</param>
    public static string? Describe(VideoFilter filter, string? alsoAccept = null) => filter switch
    {
        VideoFilter.Scale scale => $"scale={Side(scale.Width)}:{Side(scale.Height)}",
        VideoFilter.Crop crop => $"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}",
        VideoFilter.FrameRate rate => $"fps={rate.Rate.Numerator}/{rate.Rate.Denominator}",
        VideoFilter.Rotate { Degrees: 90 } => "transpose=clock",
        VideoFilter.Rotate { Degrees: 270 } => "transpose=cclock",
        VideoFilter.Rotate => "hflip,vflip",
        VideoFilter.Flip { Horizontal: true, Vertical: true } => "hflip,vflip",
        VideoFilter.Flip { Horizontal: true } => "hflip",
        VideoFilter.Flip { Vertical: true } => "vflip",
        VideoFilter.Flip => "null",
        VideoFilter.Deinterlace => "bwdif",
        VideoFilter.Custom custom when Accepts(custom.Backend, alsoAccept) => custom.Description,
        _ => null,
    };

    /// <summary>The libavfilter form of an audio filter, or <see langword="null"/> for one written for another backend.</summary>
    /// <param name="filter">The filter.</param>
    /// <param name="alsoAccept">Another backend name whose custom filters are also libavfilter syntax.</param>
    public static string? Describe(AudioFilter filter, string? alsoAccept = null) => filter switch
    {
        AudioFilter.Volume volume => $"volume={Number(volume.Gain)}",
        AudioFilter.Custom custom when Accepts(custom.Backend, alsoAccept) => custom.Description,
        _ => null,
    };

    /// <summary>
    /// The filters and the size and rate of a video output, in order, without
    /// trimming. Filters another backend owns are left out; validation reports them.
    /// </summary>
    public static IEnumerable<string> VideoSettings(VideoOutputSettings settings, string? alsoAccept = null)
    {
        foreach (var filter in settings.Filters)
        {
            if (Describe(filter, alsoAccept) is { } described)
            {
                yield return described;
            }
        }

        if (settings.Width is not null || settings.Height is not null)
        {
            yield return $"scale={Side(settings.Width ?? -1)}:{Side(settings.Height ?? -1)}";
        }

        if (settings.FrameRate is { } rate)
        {
            yield return $"fps={rate.Numerator}/{rate.Denominator}";
        }
    }

    /// <summary>The filters of an audio output, in order, without trimming or format conversion.</summary>
    public static IEnumerable<string> AudioSettings(AudioOutputSettings settings, string? alsoAccept = null)
    {
        foreach (var filter in settings.Filters)
        {
            if (Describe(filter, alsoAccept) is { } described)
            {
                yield return described;
            }
        }
    }

    /// <summary>
    /// Writes a value the way a libavfilter chain needs it inside an option:
    /// escaped once for the option parser, which treats <c>\</c>, <c>'</c> and
    /// <c>:</c> as syntax, and again for the graph parser, which also treats
    /// <c>[</c>, <c>]</c>, <c>,</c> and <c>;</c> as syntax.
    /// </summary>
    /// <remarks>
    /// This is the two-level escaping the "Notes on filtergraph escaping" of the
    /// FFmpeg filter documentation describe; a file name holding a colon, a comma
    /// or a quote otherwise ends the option or the filter early.
    /// </remarks>
    public static string EscapeOptionValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Escape(Escape(value, "\\':"), "\\'[],;");
    }

    /// <summary>A number as libavfilter reads it: invariant, no exponent.</summary>
    public static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>A time in seconds, as the trim filters and setpts expressions read it.</summary>
    public static string Seconds(TimeSpan value) => Number(value.TotalSeconds);

    /// <summary>Joins filters into a chain, leaving out empty ones.</summary>
    public static string Join(IEnumerable<string?> filters) =>
        string.Join(",", filters.Where(f => !string.IsNullOrWhiteSpace(f)));

    private static bool Accepts(string backend, string? alsoAccept) =>
        string.Equals(backend, Backend, StringComparison.OrdinalIgnoreCase)
        || (alsoAccept is not null && string.Equals(backend, alsoAccept, StringComparison.OrdinalIgnoreCase));

    // -1 keeps the aspect ratio, but may land on an odd size that 4:2:0 encoders
    // refuse; -2 keeps it too and rounds to an even one.
    private static string Side(int value) => value == -1 ? "-2" : value.ToString(CultureInfo.InvariantCulture);

    private static string Escape(string value, string special)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (special.Contains(c))
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
