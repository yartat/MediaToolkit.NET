#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.GStreamer.Native;

namespace MediaToolkitNet.GStreamer;

/// <summary>What a quality of service message says.</summary>
/// <param name="IsLive">Whether a live element posted it.</param>
/// <param name="RunningTime">The running time of the buffer, or null.</param>
/// <param name="Timestamp">The buffer's presentation time, or null.</param>
/// <param name="Jitter">How late the buffer was; negative when it was early.</param>
/// <param name="Proportion">The rate the element would need to keep up: above 1 it is too slow.</param>
/// <param name="Processed">Buffers or frames handled so far.</param>
/// <param name="Dropped">Buffers or frames dropped so far.</param>
public readonly record struct GStreamerQos(
    bool IsLive, TimeSpan? RunningTime, TimeSpan? Timestamp, TimeSpan Jitter, double Proportion, ulong Processed, ulong Dropped);

/// <summary>What a progress message says.</summary>
/// <param name="Type">The stage.</param>
/// <param name="Code">A short code of the operation, such as <c>open</c> or <c>connect</c>.</param>
/// <param name="Text">A description for people.</param>
public readonly record struct GStreamerProgress(GstProgressType Type, string Code, string Text);

/// <summary>One entry of a table of contents.</summary>
/// <param name="Type">What it is.</param>
/// <param name="Start">Where it begins.</param>
/// <param name="Stop">Where it ends.</param>
/// <param name="Title">Its title tag, or null.</param>
/// <param name="Entries">What it holds, as an edition holds chapters.</param>
public sealed record GStreamerTocEntry(
    GstTocEntryType Type, TimeSpan Start, TimeSpan Stop, string? Title, IReadOnlyList<GStreamerTocEntry> Entries);

/// <summary>
/// One message from a pipeline's bus, parsed into the parts that kind of message has. A field that does not belong
/// to the message's <see cref="Type"/> is left at its default.
/// </summary>
/// <param name="Type">What kind of message it is.</param>
/// <param name="Source">The name of the element (or pad, or bin) that posted it, or <see langword="null"/>.</param>
/// <param name="FromPipeline">True when the pipeline itself posted it, rather than an element inside.</param>
[SupportedOSPlatform("linux")]
public sealed record GStreamerMessage(GstMessageType Type, string? Source, bool FromPipeline)
{
    /// <summary>For an error, warning or info: what GStreamer says happened.</summary>
    public string? Text { get; init; }

    /// <summary>For an error, warning or info: the debugging detail, which names the source file and function.</summary>
    public string? Debug { get; init; }

    /// <summary>For a state change: the state it left.</summary>
    public GstState OldState { get; init; }

    /// <summary>For a state change: the state it reached.</summary>
    public GstState NewState { get; init; }

    /// <summary>For a state change: the state it is on its way to, or <see cref="GstState.VoidPending"/>.</summary>
    public GstState PendingState { get; init; }

    /// <summary>For buffering: how full the buffer is, 0 to 100.</summary>
    public int Percent { get; init; }

    /// <summary>For tags: the tags that have a text value, such as <c>title</c>, <c>artist</c> and <c>video-codec</c>.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    /// <summary>For an element or application message: the name of its structure.</summary>
    public string? StructureName { get; init; }

    /// <summary>For an element or application message: the structure in GStreamer's text form, fields included.</summary>
    public string? Structure { get; init; }

    /// <summary>
    /// For an element or application message: the structure with typed fields, such as the <c>rms</c> levels of the
    /// <c>level</c> element.
    /// </summary>
    public GStreamerStructure? Details { get; init; }

    /// <summary>For a quality of service message: what was late or dropped.</summary>
    public GStreamerQos? Qos { get; init; }

    /// <summary>For a progress message: what an element is busy with.</summary>
    public GStreamerProgress? Progress { get; init; }

    /// <summary>For a table of contents message: its entries, chapters among them.</summary>
    public IReadOnlyList<GStreamerTocEntry> Toc { get; init; } = [];

    /// <summary>For a stream start: the group, equal for the streams that belong together.</summary>
    public uint? GroupId { get; init; }

    /// <inheritdoc />
    public override string ToString() => Type switch
    {
        GstMessageType.Error or GstMessageType.Warning or GstMessageType.Info => $"{Type} from {Source}: {Text}",
        GstMessageType.StateChanged => $"{Source}: {OldState} -> {NewState} (pending {PendingState})",
        GstMessageType.Buffering => $"{Source}: buffering {Percent}%",
        GstMessageType.Tag => $"{Source}: tags {string.Join(", ", Tags.Select(t => $"{t.Key}={t.Value}"))}",
        GstMessageType.Element or GstMessageType.Application => $"{Type} from {Source}: {Structure ?? StructureName}",
        _ => $"{Type} from {Source}",
    };
}
