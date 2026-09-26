#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.FFmpeg.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg;

/// <summary>
/// Finds and opens encoders. Shared by <see cref="FFmpegRecorder"/> and the
/// transcoder, so both pick encoders and apply options the same way.
/// </summary>
internal static unsafe class EncoderFactory
{
    /// <summary>
    /// Finds the encoder to use: the one the caller named, or the first of those
    /// known for the codec that this FFmpeg build actually carries.
    /// </summary>
    /// <param name="requested">Codec asked for.</param>
    /// <param name="fallback">Codec to use when none was asked for.</param>
    /// <param name="encoderName">Encoder named outright, which overrides the codec.</param>
    /// <param name="chosen">Receives the name of the encoder that was found.</param>
    /// <returns>Returns the encoder.</returns>
    /// <exception cref="MediaToolkitNetException">This build carries none of them.</exception>
    public static void* Find(MediaCodec requested, MediaCodec fallback, string? encoderName, out string chosen)
    {
        if (TryFind(requested, fallback, encoderName, out chosen) is { } codec && codec != 0)
        {
            return (void*)codec;
        }

        var names = Names(requested, fallback, encoderName);
        throw new MediaToolkitNetException(
            FFmpegLibraries.BackendName,
            names.Length == 0
                ? $"no encoder is known for {requested}"
                : $"none of the encoders [{string.Join(", ", names)}] is available in this FFmpeg build",
            AVConstants.ErrorInvalid);
    }

    /// <summary>Like <see cref="Find"/>, but answers with zero instead of throwing.</summary>
    public static nint? TryFind(MediaCodec requested, MediaCodec fallback, string? encoderName, out string chosen)
    {
        Span<byte> scratch = stackalloc byte[64];
        foreach (var name in Names(requested, fallback, encoderName))
        {
            using var utf8 = new Utf8Scoped(name, scratch);
            var codec = AV.avcodec_find_encoder_by_name(utf8.Pointer);
            if (codec is not null)
            {
                chosen = name;
                return (nint)codec;
            }
        }

        chosen = string.Empty;
        return null;
    }

    /// <summary>
    /// Opens an encoder, answering <see langword="null"/> rather than throwing
    /// when this format or configuration is refused, so the caller can try the next.
    /// </summary>
    /// <param name="codec">The encoder.</param>
    /// <param name="mediaType">Video or audio.</param>
    /// <param name="format">An AVPixelFormat or AVSampleFormat to open with.</param>
    /// <param name="bitrate">Target bit rate, or 0.</param>
    /// <param name="width">Frame width; ignored for audio.</param>
    /// <param name="height">Frame height; ignored for audio.</param>
    /// <param name="timeBase">The encoder time base.</param>
    /// <param name="globalHeader">True when the container wants codec headers out of band.</param>
    /// <param name="configure">Sets anything else on the context before it opens.</param>
    /// <param name="extraFlags">Bits to add to the codec flags.</param>
    /// <param name="commonOptions">Options for every stream of the output, applied first.</param>
    /// <param name="streamOptions">Options for this stream, applied last, so they win.</param>
    /// <returns>Returns the open context, or <see langword="null"/>.</returns>
    public static void* TryOpen(
        void* codec,
        AVMediaType mediaType,
        int format,
        long bitrate,
        int width,
        int height,
        AVRationalNative timeBase,
        bool globalHeader,
        Action<nint> configure,
        int extraFlags = 0,
        IReadOnlyDictionary<string, string>? commonOptions = null,
        IReadOnlyDictionary<string, string>? streamOptions = null)
    {
        var context = AV.avcodec_alloc_context3(codec);
        if (context is null)
        {
            return null;
        }

        var parameters = AV.avcodec_parameters_alloc();
        if (parameters is null)
        {
            AV.avcodec_free_context(&context);
            return null;
        }

        void* options = null;
        try
        {
            // The encoder's own default bit rate, before the parameters below
            // overwrite it: 0 for libx264, which then runs at constant quality,
            // 200 kbit/s for most others. Media Foundation's H.264 encoder
            // refuses to start at 0.
            var defaultBitrate = AV.GetOption(context, "b", 0);

            AbiLayout.FillCodecParameters(
                parameters, mediaType, AbiLayout.IdOfCodec(codec), format, bitrate, width, height);

            if (AV.avcodec_parameters_to_context(context, parameters) < 0)
            {
                return Fail(context);
            }

            AbiLayout.SetCodecTimeBase(context, timeBase);
            AV.SetOption(context, "b", bitrate > 0 ? bitrate : defaultBitrate);

            // One assignment, because "flags" is the whole field: setting it twice
            // would drop whichever bit was set first.
            var flags = extraFlags;
            if (globalHeader)
            {
                flags |= AVConstants.CodecFlagGlobalHeader;
            }

            if (flags != 0)
            {
                AV.SetOption(context, "flags", flags);
            }

            configure((nint)context);

            // The output-wide options first, then the ones this stream asked for,
            // so a stream can disagree with the output about its own encoder.
            if (commonOptions is not null)
            {
                foreach (var (key, value) in commonOptions)
                {
                    AV.DictionarySet(&options, key, value);
                }
            }

            if (streamOptions is not null)
            {
                foreach (var (key, value) in streamOptions)
                {
                    AV.DictionarySet(&options, key, value);
                }
            }

            return AV.avcodec_open2(context, codec, &options) < 0 ? Fail(context) : context;
        }
        finally
        {
            var localParameters = parameters;
            AV.avcodec_parameters_free(&localParameters);
            if (options is not null)
            {
                AV.av_dict_free(&options);
            }
        }

        static void* Fail(void* context)
        {
            var local = context;
            AV.avcodec_free_context(&local);
            return null;
        }
    }

    /// <summary>The pixel formats an encoder takes, or <see langword="null"/> when it takes any or cannot say.</summary>
    public static int[]? SupportedPixelFormats(void* codec) => Supported(codec, ConfigPixelFormat, terminator: -1);

    /// <summary>The sample rates an encoder takes, or <see langword="null"/> when it takes any or cannot say.</summary>
    public static int[]? SupportedSampleRates(void* codec) => Supported(codec, ConfigSampleRate, terminator: 0);

    /// <summary>The sample formats an encoder takes, or <see langword="null"/> when it takes any or cannot say.</summary>
    public static int[]? SupportedSampleFormats(void* codec) => Supported(codec, ConfigSampleFormat, terminator: -1);

    /// <summary>
    /// Puts the preferred values first, keeping only those the encoder takes; when
    /// none of them is, the encoder's own list, in its own order.
    /// </summary>
    public static IEnumerable<int> Prefer(IEnumerable<int> preferred, int[]? supported)
    {
        var ordered = preferred.Where(v => v >= 0).Distinct().ToList();
        if (supported is null)
        {
            return ordered;
        }

        var accepted = ordered.Where(supported.Contains).ToList();
        return accepted.Count > 0 ? accepted : supported;
    }

    /// <summary>
    /// The rate to encode at: the one asked for when the encoder takes it,
    /// otherwise the nearest it takes that is not lower, otherwise its highest.
    /// Opus, for one, takes 48 kHz but not 44.1.
    /// </summary>
    public static int ChooseSampleRate(int wanted, int[]? supported)
    {
        if (supported is null || supported.Length == 0 || supported.Contains(wanted))
        {
            return wanted;
        }

        var higher = supported.Where(r => r >= wanted).DefaultIfEmpty(0).Min();
        return higher > 0 ? higher : supported.Max();
    }

    private const int ConfigPixelFormat = 0;
    private const int ConfigSampleRate = 2;
    private const int ConfigSampleFormat = 3;

    private static int[]? Supported(void* codec, int config, int terminator)
    {
        if (AV.avcodec_get_supported_config is null)
        {
            return null;
        }

        void* values = null;
        int count;
        if (AV.avcodec_get_supported_config(null, codec, config, 0, &values, &count) < 0 || values is null)
        {
            return null;
        }

        var result = new List<int>(Math.Max(count, 0));
        var items = (int*)values;
        for (var i = 0; count > 0 ? i < count : items[i] != terminator; i++)
        {
            result.Add(items[i]);
        }

        return [.. result];
    }

    private static string[] Names(MediaCodec requested, MediaCodec fallback, string? encoderName) =>
        string.IsNullOrWhiteSpace(encoderName)
            ? FFmpegFormatMap.EncoderNames(requested == MediaCodec.Default ? fallback : requested)
            : [encoderName];
}
