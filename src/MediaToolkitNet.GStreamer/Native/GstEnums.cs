#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

namespace MediaToolkitNet.GStreamer.Native;

/// <summary>GstState.</summary>
public enum GstState
{
    /// <summary>GST_STATE_VOID_PENDING.</summary>
    VoidPending = 0,

    /// <summary>GST_STATE_NULL, where the element holds no resources.</summary>
    Null = 1,

    /// <summary>GST_STATE_READY.</summary>
    Ready = 2,

    /// <summary>GST_STATE_PAUSED, where data flows until the first frame is prerolled.</summary>
    Paused = 3,

    /// <summary>GST_STATE_PLAYING.</summary>
    Playing = 4,
}

/// <summary>GstStateChangeReturn.</summary>
public enum GstStateChangeReturn
{
    /// <summary>GST_STATE_CHANGE_FAILURE.</summary>
    Failure = 0,

    /// <summary>GST_STATE_CHANGE_SUCCESS.</summary>
    Success = 1,

    /// <summary>GST_STATE_CHANGE_ASYNC, meaning the change is still under way.</summary>
    Async = 2,

    /// <summary>GST_STATE_CHANGE_NO_PREROLL, returned by live sources.</summary>
    NoPreroll = 3,
}

/// <summary>GstMessageType, the bits <c>gst_bus_timed_pop_filtered</c> matches on.</summary>
/// <remarks>
/// The values were read out of gstmessage.h by compiling against it, after the
/// first version of this enum, written from memory, had three of them wrong.
/// The probe for <c>GstMessage::type</c> is what caught it.
/// </remarks>
[Flags]
public enum GstMessageType : uint
{
    /// <summary>GST_MESSAGE_UNKNOWN.</summary>
    Unknown = 0,

    /// <summary>GST_MESSAGE_EOS.</summary>
    Eos = 1 << 0,

    /// <summary>GST_MESSAGE_ERROR.</summary>
    Error = 1 << 1,

    /// <summary>GST_MESSAGE_WARNING.</summary>
    Warning = 1 << 2,

    /// <summary>GST_MESSAGE_INFO.</summary>
    Info = 1 << 3,

    /// <summary>GST_MESSAGE_STATE_CHANGED.</summary>
    StateChanged = 1 << 6,

    /// <summary>GST_MESSAGE_ASYNC_DONE.</summary>
    AsyncDone = 1 << 21,

    /// <summary>GST_MESSAGE_APPLICATION, which only an application ever posts.</summary>
    Application = 1 << 14,

    /// <summary>GST_MESSAGE_ANY.</summary>
    Any = 0xFFFFFFFF,
}

/// <summary>GstFormat.</summary>
public enum GstFormat
{
    /// <summary>GST_FORMAT_UNDEFINED.</summary>
    Undefined = 0,

    /// <summary>GST_FORMAT_DEFAULT.</summary>
    Default = 1,

    /// <summary>GST_FORMAT_BYTES.</summary>
    Bytes = 2,

    /// <summary>GST_FORMAT_TIME, in nanoseconds.</summary>
    Time = 3,
}

/// <summary>GstMapFlags.</summary>
[Flags]
public enum GstMapFlags
{
    /// <summary>GST_MAP_READ.</summary>
    Read = 1 << 0,

    /// <summary>GST_MAP_WRITE.</summary>
    Write = 1 << 1,
}

/// <summary>GstSeekFlags.</summary>
[Flags]
public enum GstSeekFlags
{
    /// <summary>GST_SEEK_FLAG_NONE.</summary>
    None = 0,

    /// <summary>GST_SEEK_FLAG_FLUSH, which discards what is already buffered.</summary>
    Flush = 1 << 0,

    /// <summary>GST_SEEK_FLAG_ACCURATE, which lands on the exact position at the cost of decoding up to it.</summary>
    Accurate = 1 << 1,

    /// <summary>GST_SEEK_FLAG_KEY_UNIT, which lands on the nearest key frame.</summary>
    KeyUnit = 1 << 2,

    /// <summary>GST_SEEK_FLAG_SNAP_BEFORE, which with <see cref="KeyUnit"/> takes the key frame at or before the position.</summary>
    SnapBefore = 1 << 5,
}

/// <summary>Constants that are macros in the GStreamer headers.</summary>
public static class GstConstants
{
    /// <summary>GST_CLOCK_TIME_NONE.</summary>
    public const ulong ClockTimeNone = ulong.MaxValue;

    /// <summary>Nanoseconds in one <see cref="TimeSpan"/> tick.</summary>
    public const long NanosecondsPerTick = 100;

    /// <summary>Backend name used in error messages.</summary>
    public const string BackendName = "gstreamer";
}

/// <summary>GstPadProbeType, the kinds of data a pad probe is called for.</summary>
[Flags]
public enum GstPadProbeType
{
    /// <summary>GST_PAD_PROBE_TYPE_BUFFER.</summary>
    Buffer = 1 << 4,

    /// <summary>GST_PAD_PROBE_TYPE_BUFFER_LIST.</summary>
    BufferList = 1 << 5,

    /// <summary>GST_PAD_PROBE_TYPE_EVENT_DOWNSTREAM.</summary>
    EventDownstream = 1 << 6,

    /// <summary>GST_PAD_PROBE_TYPE_EVENT_FLUSH, which the downstream mask does not include.</summary>
    EventFlush = 1 << 8,
}

/// <summary>GstPadProbeReturn.</summary>
public enum GstPadProbeReturn
{
    /// <summary>GST_PAD_PROBE_DROP: the data goes no further.</summary>
    Drop = 0,

    /// <summary>GST_PAD_PROBE_OK: the data passes.</summary>
    Ok = 1,
}

/// <summary>GstSeekType.</summary>
public enum GstSeekType
{
    /// <summary>GST_SEEK_TYPE_NONE: leave the position as it is.</summary>
    None = 0,

    /// <summary>GST_SEEK_TYPE_SET: an absolute position.</summary>
    Set = 1,
}

/// <summary>
/// The GstEventType values this backend tells apart. Each is
/// <c>GST_EVENT_MAKE_TYPE(number, flags)</c> in the header; the values were
/// read by compiling against gstevent.h.
/// </summary>
public enum GstEventType
{
    /// <summary>GST_EVENT_FLUSH_START.</summary>
    FlushStart = 2563,

    /// <summary>GST_EVENT_FLUSH_STOP.</summary>
    FlushStop = 5127,

    /// <summary>GST_EVENT_EOS.</summary>
    Eos = 28174,
}

/// <summary>GstTagMergeMode.</summary>
public enum GstTagMergeMode
{
    /// <summary>GST_TAG_MERGE_REPLACE: tags that arrive replace the ones already set.</summary>
    Replace = 2,

    /// <summary>GST_TAG_MERGE_KEEP: the tags already set win over the ones that arrive.</summary>
    Keep = 5,
}

/// <summary>Values GObject and GStreamer define as macros.</summary>
public static class GstTypes
{
    /// <summary>G_TYPE_STRING, the fundamental type 16 shifted by G_TYPE_FUNDAMENTAL_SHIFT.</summary>
    public const nuint String = 16 << 2;

    /// <summary>sizeof(GValue): a GType and two 8-byte data slots.</summary>
    public const int ValueSize = 24;

    /// <summary>GST_TOC_ENTRY_TYPE_CHAPTER.</summary>
    public const int TocEntryChapter = 3;

    /// <summary>GST_PARSE_FLAG_FATAL_ERRORS: any error, a failed link included, fails the parse.</summary>
    public const int ParseFatalErrors = 1 << 0;
}
