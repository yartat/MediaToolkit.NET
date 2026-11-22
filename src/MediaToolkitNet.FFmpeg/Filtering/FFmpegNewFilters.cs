#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Globalization;
using MediaToolkitNet.FFmpeg.Transcoding;

namespace MediaToolkitNet.FFmpeg.Filtering;

/// <summary>What <c>colordetect</c> (FFmpeg 8.0) looks for.</summary>
[Flags]
public enum ColorDetectMode
{
    /// <summary>The colour range of YUV video: limited or full.</summary>
    ColorRange = 1,

    /// <summary>The alpha mode of a picture with an alpha channel: straight or premultiplied.</summary>
    AlphaMode = 2,

    /// <summary>Everything it can detect.</summary>
    All = ColorRange | AlphaMode,
}

/// <summary>How <c>latticepal</c> (FFmpeg 9.1) re-diffuses the error of the colours it drops.</summary>
public enum LatticeRefine
{
    /// <summary>No refinement.</summary>
    None,

    /// <summary>Rediffuse the whole frame against the final palette.</summary>
    Full,

    /// <summary>Diffuse only the residual of the dropped colours.</summary>
    Residual,

    /// <summary>Interleave removal and rediffusion in geometric batches.</summary>
    Batched,
}

/// <summary>How <c>latticepal</c> dithers.</summary>
public enum LatticeDither
{
    /// <summary>No dithering.</summary>
    None,

    /// <summary>Ordered 8x8 Bayer dithering.</summary>
    Bayer,

    /// <summary>Void-and-cluster blue noise.</summary>
    BlueNoise,

    /// <summary>Floyd-Steinberg error diffusion.</summary>
    FloydSteinberg,
}

/// <summary>The form <c>whisper</c> (FFmpeg 8.0) writes its transcript in.</summary>
public enum WhisperFormat
{
    /// <summary>Plain text.</summary>
    Text,

    /// <summary>SubRip subtitles.</summary>
    Srt,

    /// <summary>JSON.</summary>
    Json,
}

/// <summary>The quantity <c>gainmap</c> (FFmpeg 9.1) computes the gain over.</summary>
public enum GainMapMode
{
    /// <summary>One gain channel per colour component.</summary>
    Rgb,

    /// <summary>A single channel, from luminance.</summary>
    Luma,

    /// <summary>A single channel, from the largest of R, G and B.</summary>
    MaxRgb,
}

/// <summary>Whose colour space <c>gainmap</c> applies the map in.</summary>
public enum GainMapColorSpace
{
    /// <summary>The base rendition's.</summary>
    Base,

    /// <summary>The alternate rendition's.</summary>
    Alternate,
}

/// <summary>
/// Typed descriptions of filters FFmpeg 8.x and 9.x added, for a chain given to
/// <see cref="FFmpegFilterGraph"/>. Each method returns the filter in libavfilter's
/// syntax, with its options checked against the ranges the filter declares and its
/// text escaped, so a path or a model name cannot end the option list early.
/// </summary>
/// <remarks>
/// The filters themselves are in the libraries or they are not: ask
/// <see cref="FFmpegFilters.Exists"/> or the <c>IsAvailable</c> member of each group
/// before using one. These helpers are the FFmpeg backend's own; there is nothing
/// shared with the other backends.
/// </remarks>
public static class FFmpegNewFilters
{
    /// <summary>True when the loaded libavfilter has <c>colordetect</c> (FFmpeg 8.0).</summary>
    public static bool ColorDetectAvailable => FFmpegFilters.Exists("colordetect");

    /// <summary>True when the loaded libavfilter has <c>latticepal</c> (FFmpeg 9.1).</summary>
    public static bool LatticePalAvailable => FFmpegFilters.Exists("latticepal");

    /// <summary>True when the loaded libavfilter has <c>whisper</c>, which needs a build with whisper.cpp (FFmpeg 8.0).</summary>
    public static bool WhisperAvailable => FFmpegFilters.Exists("whisper");

    /// <summary>True when the loaded libavfilter has <c>gainmap</c> (FFmpeg 9.1).</summary>
    public static bool GainMapAvailable => FFmpegFilters.Exists("gainmap");

    /// <summary>
    /// <c>colordetect</c>: finds out the colour range or the alpha mode of video whose
    /// metadata does not say, and reports it as frame metadata.
    /// </summary>
    /// <param name="mode">What to detect.</param>
    /// <param name="threshold">How far from the limit a value counts, as a fraction of the full range, 0 to 0.05.</param>
    public static string ColorDetect(ColorDetectMode mode = ColorDetectMode.All, double threshold = 0.001)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(threshold);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(threshold, 0.05);
        if ((mode & ColorDetectMode.All) == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Name at least one property to detect.");
        }

        var options = new List<string>();
        if ((mode & ColorDetectMode.All) != ColorDetectMode.All)
        {
            options.Add("mode=" + (mode == ColorDetectMode.ColorRange ? "color_range" : "alpha_mode"));
        }

        if (threshold != 0.001)
        {
            options.Add("threshold=" + Number(threshold));
        }

        return Filter("colordetect", options);
    }

    /// <summary>
    /// <c>latticepal</c>: reduces a picture to a palette by quantising on a lattice
    /// in colour space, for GIF-like output.
    /// </summary>
    /// <param name="density">Lattice steps along one colour axis, from 1.</param>
    /// <param name="maxColors">The most palette entries to use, 2 to 256.</param>
    /// <param name="alpha">Quantise alpha as well, on a four-dimensional lattice.</param>
    /// <param name="refine">How to re-diffuse the error of dropped colours.</param>
    /// <param name="dither">The dithering mode.</param>
    public static string LatticePal(
        int density = 20,
        int maxColors = 256,
        bool alpha = false,
        LatticeRefine refine = LatticeRefine.None,
        LatticeDither dither = LatticeDither.FloydSteinberg)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(density, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxColors, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxColors, 256);

        var options = new List<string>();
        if (density != 20)
        {
            options.Add("density=" + density.ToString(CultureInfo.InvariantCulture));
        }

        if (maxColors != 256)
        {
            options.Add("max_colors=" + maxColors.ToString(CultureInfo.InvariantCulture));
        }

        if (alpha)
        {
            options.Add("alpha=1");
        }

        if (refine != LatticeRefine.None)
        {
            options.Add("refine=" + refine switch
            {
                LatticeRefine.Full => "full",
                LatticeRefine.Residual => "residual",
                LatticeRefine.Batched => "batched",
                _ => throw new ArgumentOutOfRangeException(nameof(refine), refine, null),
            });
        }

        if (dither != LatticeDither.FloydSteinberg)
        {
            options.Add("dither=" + dither switch
            {
                LatticeDither.None => "none",
                LatticeDither.Bayer => "bayer",
                LatticeDither.BlueNoise => "blue_noise",
                _ => throw new ArgumentOutOfRangeException(nameof(dither), dither, null),
            });
        }

        return Filter("latticepal", options);
    }

    /// <summary>
    /// <c>whisper</c>: transcribes speech with a whisper.cpp model. The filter passes the
    /// audio through and writes the transcript to <paramref name="destination"/>.
    /// </summary>
    /// <param name="model">Path of the whisper.cpp model file.</param>
    /// <param name="language">A language code, or <c>auto</c>.</param>
    /// <param name="translate">Translate to English.</param>
    /// <param name="queue">How much audio to collect before transcribing it, from 20 ms.</param>
    /// <param name="useGpu">Use the GPU.</param>
    /// <param name="gpuDevice">Which GPU.</param>
    /// <param name="destination">Where the transcript goes: a file path or a URL; empty means the log.</param>
    /// <param name="format">The form of the transcript.</param>
    /// <param name="maxLength">The longest segment in characters; 0 means no limit.</param>
    public static string Whisper(
        string model,
        string language = "auto",
        bool translate = false,
        TimeSpan? queue = null,
        bool useGpu = true,
        int gpuDevice = 0,
        string destination = "",
        WhisperFormat format = WhisperFormat.Text,
        int maxLength = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(gpuDevice);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        if (queue is { } wait && wait < TimeSpan.FromMilliseconds(20))
        {
            throw new ArgumentOutOfRangeException(nameof(queue), queue, "The queue is at least 20 ms.");
        }

        var options = new List<string> { "model=" + FilterChains.EscapeOptionValue(model) };
        if (language != "auto")
        {
            options.Add("language=" + FilterChains.EscapeOptionValue(language));
        }

        if (translate)
        {
            options.Add("translate=1");
        }

        if (queue is { } length && length != TimeSpan.FromSeconds(3))
        {
            options.Add("queue=" + Number(length.TotalSeconds));
        }

        if (!useGpu)
        {
            options.Add("use_gpu=0");
        }

        if (gpuDevice != 0)
        {
            options.Add("gpu_device=" + gpuDevice.ToString(CultureInfo.InvariantCulture));
        }

        if (destination.Length > 0)
        {
            options.Add("destination=" + FilterChains.EscapeOptionValue(destination));
        }

        if (format != WhisperFormat.Text)
        {
            options.Add("format=" + (format == WhisperFormat.Srt ? "srt" : "json"));
        }

        if (maxLength != 0)
        {
            options.Add("max_len=" + maxLength.ToString(CultureInfo.InvariantCulture));
        }

        return Filter("whisper", options);
    }

    /// <summary>
    /// <c>gainmap</c>: computes an HDR gain map from a base and an alternate rendition of
    /// a picture (two inputs). Left out, a bound is measured from the pictures.
    /// </summary>
    /// <param name="mode">The quantity the gain is computed over.</param>
    /// <param name="colorSpace">Whose colour space the map is applied in.</param>
    /// <param name="minGain">Lower bound of the encoded gain in log2 space, -32 to 32.</param>
    /// <param name="maxGain">Upper bound of the encoded gain in log2 space, -32 to 32.</param>
    /// <param name="gamma">Encoding gamma of the stored map, 0.0001 to 100.</param>
    public static string GainMap(
        GainMapMode mode = GainMapMode.Rgb,
        GainMapColorSpace colorSpace = GainMapColorSpace.Base,
        double? minGain = null,
        double? maxGain = null,
        double? gamma = null)
    {
        CheckRange(minGain, -32, 32, nameof(minGain));
        CheckRange(maxGain, -32, 32, nameof(maxGain));
        CheckRange(gamma, 0.0001, 100, nameof(gamma));

        var options = new List<string>();
        if (mode != GainMapMode.Rgb)
        {
            options.Add("mode=" + (mode == GainMapMode.Luma ? "luma" : "maxrgb"));
        }

        if (colorSpace != GainMapColorSpace.Base)
        {
            options.Add("colorspace=alternate");
        }

        if (minGain is { } min)
        {
            options.Add("min=" + Number(min));
        }

        if (maxGain is { } max)
        {
            options.Add("max=" + Number(max));
        }

        if (gamma is { } g)
        {
            options.Add("gamma=" + Number(g));
        }

        return Filter("gainmap", options);
    }

    private static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    private static string Filter(string name, List<string> options) =>
        options.Count == 0 ? name : name + "=" + string.Join(':', options);

    private static void CheckRange(double? value, double low, double high, string name)
    {
        if (value is { } v)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(v, low, name);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(v, high, name);
        }
    }
}
