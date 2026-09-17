namespace MediaToolkitNet.Abstractions.Formats;

/// <summary>
/// Description of an uncompressed audio stream.
/// </summary>
/// <param name="SampleRate">Samples per second per channel, e.g. 48000.</param>
/// <param name="Channels">Number of interleaved or planar channels.</param>
/// <param name="SampleFormat">PCM sample layout.</param>
/// <param name="ChannelMask">
/// Which speakers the channels feed, as a <see cref="ChannelLayout"/> mask.
/// Left at <see cref="ChannelLayout.Unspecified"/>, the backend assumes the
/// conventional layout for that many channels.
/// </param>
public readonly record struct AudioFormat(
    int SampleRate,
    int Channels,
    SampleFormat SampleFormat,
    ulong ChannelMask = ChannelLayout.Unspecified)
{
    /// <summary>A common default: 48 kHz stereo 16-bit PCM.</summary>
    public static AudioFormat Cd48Stereo => new(48000, 2, SampleFormat.S16);

    /// <summary>
    /// The layout to encode with: the one that was asked for, or the
    /// conventional one for this channel count when none was.
    /// </summary>
    public ulong EffectiveChannelMask =>
        ChannelMask != ChannelLayout.Unspecified ? ChannelMask : ChannelLayout.Default(Channels);

    /// <summary>Bytes occupied by one sample across all channels (interleaved layout).</summary>
    public int BlockAlign => SampleFormat.BytesPerSample() * Channels;

    /// <summary>Bytes per second of an interleaved stream in this format.</summary>
    public int AverageBytesPerSecond => BlockAlign * SampleRate;

    /// <summary>Number of planes a frame in this format occupies.</summary>
    public int PlaneCount => SampleFormat.IsPlanar() ? Channels : 1;

    /// <summary>True when the format is fully specified and usable.</summary>
    public bool IsValid => SampleRate > 0 && Channels > 0 && SampleFormat != SampleFormat.Unknown;

    /// <summary>Duration covered by the given number of samples per channel.</summary>
    public TimeSpan DurationOf(long sampleCount) =>
        SampleRate > 0
            ? TimeSpan.FromTicks(sampleCount * TimeSpan.TicksPerSecond / SampleRate)
            : TimeSpan.Zero;

    /// <inheritdoc />
    public override string ToString() => $"{SampleRate} Hz, {Channels} ch, {SampleFormat}";
}
