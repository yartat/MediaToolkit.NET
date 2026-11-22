#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.FFmpeg.Native;

namespace MediaToolkitNet.FFmpeg;

/// <summary>The kind of downmix a <see cref="FFmpegDownmixMatrix"/> produces.</summary>
public enum FFmpegDownmixType
{
    /// <summary>Not indicated.</summary>
    Unknown = 0,

    /// <summary>Lo/Ro two-channel stereo.</summary>
    LoRo = 1,

    /// <summary>Lt/Rt two-channel stereo, Dolby Surround compatible.</summary>
    LtRt = 2,

    /// <summary>Lt/Rt two-channel stereo, Dolby Pro Logic II compatible.</summary>
    DolbyProLogicII = 3,
}

/// <summary>
/// A remixing matrix that turns the channels of a stream into fewer, as a stream
/// carries it for a player to apply (FFmpeg 9.1).
/// </summary>
/// <param name="Type">The kind of downmix; it fixes the number of output channels.</param>
/// <param name="InputChannels">Number of input channels.</param>
/// <param name="OutputChannels">Number of output channels.</param>
/// <param name="Coefficients">The weights, row by row: output 0 over every input, then output 1, and so on.</param>
public sealed record FFmpegDownmixMatrix(
    FFmpegDownmixType Type,
    int InputChannels,
    int OutputChannels,
    IReadOnlyList<double> Coefficients)
{
    /// <summary>The weight of an input channel in an output channel.</summary>
    /// <param name="output">The output channel.</param>
    /// <param name="input">The input channel.</param>
    public double Coefficient(int output, int input)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(output);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(output, OutputChannels);
        ArgumentOutOfRangeException.ThrowIfNegative(input);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(input, InputChannels);
        return Coefficients[input + (InputChannels * output)];
    }

    /// <summary>Reads the side data FFmpeg attaches to a frame.</summary>
    /// <param name="sideData">The bytes of an <see cref="Reading.FFmpegSideDataType.DownmixMatrix"/> block.</param>
    public static FFmpegDownmixMatrix? Parse(ReadOnlySpan<byte> sideData) =>
        FFmpegFeatures.DownmixMatrices ? AbiLayout.ReadDownmixMatrix(sideData) : null;

    /// <summary>Writes the matrix in the form it is attached to a frame in.</summary>
    /// <exception cref="NotSupportedException">The libraries are older than FFmpeg 9.1.</exception>
    public byte[] ToSideData()
    {
        if (!FFmpegFeatures.DownmixMatrices)
        {
            throw new NotSupportedException(
                "downmix matrices need av_downmix_matrix_alloc, which FFmpeg has from 9.1 on; check FFmpegFeatures.DownmixMatrices first");
        }

        return AbiLayout.WriteDownmixMatrix(this);
    }
}
