#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.FFmpeg.Native;

namespace MediaToolkitNet.FFmpeg;

/// <summary>The gain map parameters of one colour channel.</summary>
/// <param name="Min">Lower bound of the encoded gain, in log2 space.</param>
/// <param name="Max">Upper bound of the encoded gain, in log2 space.</param>
/// <param name="Gamma">The gamma the map is stored with.</param>
/// <param name="BaseOffset">Constant added to the base rendition.</param>
/// <param name="AlternateOffset">Constant added to the alternate rendition.</param>
public readonly record struct FFmpegGainMapChannel(
    Rational Min,
    Rational Max,
    Rational Gamma,
    Rational BaseOffset,
    Rational AlternateOffset);

/// <summary>
/// The parameters of an HDR gain map as ISO 21496-1 describes them (FFmpeg 9.1),
/// which is what Ultra HDR photographs carry: how to turn the SDR base picture
/// into the HDR alternate one, or back, with a second image of per-pixel gains.
/// </summary>
/// <param name="Version">The version of the ISO 21496-1 payload.</param>
/// <param name="UseBaseColorSpace">Whether the map is applied in the colour space of the base rendition.</param>
/// <param name="BaseHdrHeadroom">Headroom of the base rendition, in log2 space.</param>
/// <param name="AlternateHdrHeadroom">Headroom of the alternate rendition, in log2 space.</param>
/// <param name="Channels">One entry, or three for R, G and B.</param>
public sealed record FFmpegGainMap(
    int Version,
    bool UseBaseColorSpace,
    Rational BaseHdrHeadroom,
    Rational AlternateHdrHeadroom,
    IReadOnlyList<FFmpegGainMapChannel> Channels)
{
    /// <summary>Reads the ISO 21496-1 payload a file stores its gain map parameters in.</summary>
    /// <param name="data">The payload.</param>
    /// <exception cref="NotSupportedException">The libraries are older than FFmpeg 9.1.</exception>
    /// <exception cref="Abstractions.MediaToolkitNetException">The payload is not a valid one.</exception>
    public static unsafe FFmpegGainMap FromIso21496(ReadOnlySpan<byte> data)
    {
        Require();
        var native = stackalloc byte[AbiLayout.GainMapSize];
        new Span<byte>(native, AbiLayout.GainMapSize).Clear();
        int result;
        fixed (byte* bytes = data)
        {
            result = AVModern.av_gain_map_params_from_iso21496(native, bytes, (nuint)data.Length);
        }

        FFmpegError.Check(result, "av_gain_map_params_from_iso21496");
        return AbiLayout.ReadGainMap(native);
    }

    /// <summary>Writes the parameters as an ISO 21496-1 payload.</summary>
    /// <exception cref="NotSupportedException">The libraries are older than FFmpeg 9.1.</exception>
    /// <exception cref="Abstractions.MediaToolkitNetException">The parameters are not valid.</exception>
    public unsafe byte[] ToIso21496()
    {
        Require();
        var native = stackalloc byte[AbiLayout.GainMapSize];
        AbiLayout.WriteGainMap(this, native);
        FFmpegError.Check(AVModern.av_gain_map_params_validate(native), "av_gain_map_params_validate");

        // AV_GAIN_MAP_MAX_PAYLOAD_SIZE is 141.
        var buffer = new byte[141];
        int written;
        fixed (byte* bytes = buffer)
        {
            written = AVModern.av_gain_map_params_to_iso21496(native, bytes);
        }

        FFmpegError.Check(written, "av_gain_map_params_to_iso21496");
        return buffer[..written];
    }

    /// <summary>True when FFmpeg accepts the parameters: bounds in order, a positive gamma, and so on.</summary>
    /// <exception cref="NotSupportedException">The libraries are older than FFmpeg 9.1.</exception>
    public unsafe bool IsValid()
    {
        Require();
        var native = stackalloc byte[AbiLayout.GainMapSize];
        AbiLayout.WriteGainMap(this, native);
        return AVModern.av_gain_map_params_validate(native) >= 0;
    }

    private static void Require()
    {
        if (!FFmpegFeatures.GainMaps)
        {
            throw new NotSupportedException(
                "gain maps need av_gain_map_params_*, which FFmpeg has from 9.1 on; check FFmpegFeatures.GainMaps first");
        }
    }
}

/// <summary>The native <c>AVGainMapParams</c>, which a frame carries as side data.</summary>
internal static class FFmpegGainMapNative
{
    public const int Size = 148;

    public static unsafe byte[] ToNative(FFmpegGainMap map)
    {
        var bytes = new byte[Size];
        fixed (byte* native = bytes)
        {
            AbiLayout.WriteGainMap(map, native);
        }

        return bytes;
    }

    public static unsafe FFmpegGainMap? FromNative(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size || !AbiLayout.GainMapLayoutVerified)
        {
            return null;
        }

        fixed (byte* native = data)
        {
            return AbiLayout.ReadGainMap(native);
        }
    }
}
