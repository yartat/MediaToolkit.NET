#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.GStreamer.Native;

namespace MediaToolkitNet.GStreamer;

/// <summary>Receives one sample's bytes, which are valid only for the call.</summary>
/// <param name="data">The bytes with their times and flags.</param>
public delegate void GStreamerDataHandler(in GStreamerData data);

/// <summary>The bytes of one sample, borrowed from GStreamer: copy what has to outlive the handler.</summary>
[SupportedOSPlatform("linux")]
public readonly unsafe ref struct GStreamerData
{
    private readonly void* _caps;

    internal GStreamerData(
        ReadOnlySpan<byte> bytes, TimeSpan? timestamp, TimeSpan? decodeTimestamp, TimeSpan? duration, bool isKeyFrame, bool isHeader, void* caps)
    {
        Bytes = bytes;
        Timestamp = timestamp;
        DecodeTimestamp = decodeTimestamp;
        Duration = duration;
        IsKeyFrame = isKeyFrame;
        IsHeader = isHeader;
        _caps = caps;
    }

    // A mapped buffer and the caps it travels with.
    internal static GStreamerData Of(void* buffer, byte* map, void* caps) => new(
        new ReadOnlySpan<byte>(*(byte**)(map + GstLayout.MapInfoData), checked((int)*(nuint*)(map + GstLayout.MapInfoSizeField))),
        Time(GstLayout.PtsOfBuffer(buffer)),
        Time(GstLayout.DtsOfBuffer(buffer)),
        Time(GstLayout.DurationOfBuffer(buffer)),
        Gst.gst_buffer_has_flags(buffer, GstLayout.BufferFlagDeltaUnit) == 0,
        Gst.gst_buffer_has_flags(buffer, GstLayout.BufferFlagHeader) != 0,
        caps);

    private static TimeSpan? Time(ulong nanoseconds) =>
        nanoseconds == GstConstants.ClockTimeNone ? null : new TimeSpan((long)nanoseconds / GstConstants.NanosecondsPerTick);

    /// <summary>The sample's bytes.</summary>
    public ReadOnlySpan<byte> Bytes { get; }

    /// <summary>When it is presented, or null.</summary>
    public TimeSpan? Timestamp { get; }

    /// <summary>When it is decoded, which differs from <see cref="Timestamp"/> for reordered frames, or null.</summary>
    public TimeSpan? DecodeTimestamp { get; }

    /// <summary>How long it lasts, or null.</summary>
    public TimeSpan? Duration { get; }

    /// <summary>True when it can be decoded on its own; always so for raw media and most audio.</summary>
    public bool IsKeyFrame { get; }

    /// <summary>True for a stream header, such as the setup packets of Theora and Vorbis.</summary>
    public bool IsHeader { get; }

    /// <summary>The format of the sample, copied at each read of this property; null when the sample has none.</summary>
    public GStreamerCaps? Caps => _caps is null ? null : GStreamerCaps.From(_caps);
}
