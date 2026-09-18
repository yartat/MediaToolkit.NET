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
    StateChanged = 1 << 5,

    /// <summary>GST_MESSAGE_ASYNC_DONE.</summary>
    AsyncDone = 1 << 18,

    /// <summary>GST_MESSAGE_APPLICATION, which only an application ever posts.</summary>
    Application = 1 << 22,

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

    /// <summary>GST_SEEK_FLAG_KEY_UNIT, which lands on the nearest key frame.</summary>
    KeyUnit = 1 << 2,
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
