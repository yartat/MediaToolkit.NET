using MediaToolkitNet.Core;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.Mpv.Native;

/// <summary>Mirrors <c>mpv_format</c>.</summary>
public enum MpvFormat
{
    /// <summary>MPV_FORMAT_NONE.</summary>
    None = 0,

    /// <summary>MPV_FORMAT_STRING.</summary>
    String = 1,

    /// <summary>MPV_FORMAT_OSD_STRING.</summary>
    OsdString = 2,

    /// <summary>MPV_FORMAT_FLAG.</summary>
    Flag = 3,

    /// <summary>MPV_FORMAT_INT64.</summary>
    Int64 = 4,

    /// <summary>MPV_FORMAT_DOUBLE.</summary>
    Double = 5,

    /// <summary>MPV_FORMAT_NODE.</summary>
    Node = 6,
}

/// <summary>Mirrors <c>mpv_event_id</c>; only the events this backend reacts to are named.</summary>
public enum MpvEventId
{
    /// <summary>MPV_EVENT_NONE.</summary>
    None = 0,

    /// <summary>MPV_EVENT_SHUTDOWN.</summary>
    Shutdown = 1,

    /// <summary>MPV_EVENT_LOG_MESSAGE.</summary>
    LogMessage = 2,

    /// <summary>MPV_EVENT_GET_PROPERTY_REPLY.</summary>
    GetPropertyReply = 3,

    /// <summary>MPV_EVENT_SET_PROPERTY_REPLY.</summary>
    SetPropertyReply = 4,

    /// <summary>MPV_EVENT_COMMAND_REPLY.</summary>
    CommandReply = 5,

    /// <summary>MPV_EVENT_START_FILE.</summary>
    StartFile = 6,

    /// <summary>MPV_EVENT_END_FILE.</summary>
    EndFile = 7,

    /// <summary>MPV_EVENT_FILE_LOADED.</summary>
    FileLoaded = 8,

    /// <summary>MPV_EVENT_IDLE.</summary>
    Idle = 11,

    /// <summary>MPV_EVENT_TICK.</summary>
    Tick = 14,

    /// <summary>MPV_EVENT_VIDEO_RECONFIG.</summary>
    VideoReconfig = 17,

    /// <summary>MPV_EVENT_AUDIO_RECONFIG.</summary>
    AudioReconfig = 18,

    /// <summary>MPV_EVENT_SEEK.</summary>
    Seek = 20,

    /// <summary>MPV_EVENT_PLAYBACK_RESTART.</summary>
    PlaybackRestart = 21,

    /// <summary>MPV_EVENT_PROPERTY_CHANGE.</summary>
    PropertyChange = 22,
}

/// <summary>Mirrors <c>mpv_event</c>.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public unsafe struct MpvEvent
{
    /// <summary>Which event this is.</summary>
    public MpvEventId EventId;

    /// <summary>Negative mpv error code, or 0.</summary>
    public int Error;

    /// <summary>Value passed to the asynchronous call that produced the event.</summary>
    public ulong ReplyUserData;

    /// <summary>Event-specific payload, e.g. an <see cref="MpvEventProperty"/>.</summary>
    public void* Data;
}

/// <summary>Mirrors <c>mpv_event_property</c>.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public unsafe struct MpvEventProperty
{
    /// <summary>Name of the property that changed.</summary>
    public byte* Name;

    /// <summary>Format of <see cref="Data"/>, or <see cref="MpvFormat.None"/> when the value is unavailable.</summary>
    public MpvFormat Format;

    /// <summary>Pointer to the new value.</summary>
    public void* Data;
}

/// <summary>
/// Bindings for the libmpv client API.
/// </summary>
/// <remarks>
/// libmpv keeps a stable C API and a stable ABI for the structs used here, so
/// unlike the FFmpeg backend this binding needs no version gymnastics. Only the
/// client API is wrapped; the render API, which would let a host draw mpv output
/// into its own OpenGL context, is out of scope.
/// </remarks>
public static unsafe class Mpv
{
    /// <summary>Backend name used in error messages.</summary>
    public const string BackendName = "mpv";

    private static readonly Lock Gate = new();
    private static bool _initialised;
    private static Exception? _failure;

    /// <summary>The loaded libmpv module.</summary>
    public static NativeModule Module { get; private set; } = null!;

    /// <summary><c>mpv_handle *mpv_create(void)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*> mpv_create;

    /// <summary><c>int mpv_initialize(mpv_handle *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int> mpv_initialize;

    /// <summary><c>void mpv_terminate_destroy(mpv_handle *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void> mpv_terminate_destroy;

    /// <summary><c>unsigned long mpv_client_api_version(void)</c></summary>
    public static delegate* unmanaged[Cdecl]<uint> mpv_client_api_version;

    /// <summary><c>const char *mpv_error_string(int)</c></summary>
    public static delegate* unmanaged[Cdecl]<int, byte*> mpv_error_string;

    /// <summary><c>void mpv_free(void *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void> mpv_free;

    /// <summary><c>int mpv_command(mpv_handle *, const char **args)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte**, int> mpv_command;

    /// <summary><c>int mpv_command_string(mpv_handle *, const char *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, int> mpv_command_string;

    /// <summary><c>int mpv_set_option_string(mpv_handle *, const char *name, const char *data)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, byte*, int> mpv_set_option_string;

    /// <summary><c>int mpv_set_property(mpv_handle *, const char *name, mpv_format, void *data)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, int, void*, int> mpv_set_property;

    /// <summary><c>int mpv_set_property_string(mpv_handle *, const char *name, const char *data)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, byte*, int> mpv_set_property_string;

    /// <summary><c>int mpv_get_property(mpv_handle *, const char *name, mpv_format, void *data)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, int, void*, int> mpv_get_property;

    /// <summary><c>char *mpv_get_property_string(mpv_handle *, const char *name)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, byte*> mpv_get_property_string;

    /// <summary><c>int mpv_observe_property(mpv_handle *, uint64_t reply_userdata, const char *name, mpv_format)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, ulong, byte*, int, int> mpv_observe_property;

    /// <summary><c>int mpv_request_log_messages(mpv_handle *, const char *min_level)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, int> mpv_request_log_messages;

    /// <summary><c>mpv_event *mpv_wait_event(mpv_handle *, double timeout)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, double, MpvEvent*> mpv_wait_event;

    /// <summary><c>void mpv_wakeup(mpv_handle *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void> mpv_wakeup;

    /// <summary>Client API version of the loaded library, e.g. <c>2.3</c>.</summary>
    public static string? VersionString { get; private set; }

    /// <summary>True once libmpv has been found and every symbol resolved.</summary>
    public static bool IsAvailable
    {
        get
        {
            TryInitialise();
            return _failure is null;
        }
    }

    /// <summary>Loads libmpv if needed and throws when it is not usable.</summary>
    public static void EnsureLoaded()
    {
        TryInitialise();
        if (_failure is not null)
        {
            throw _failure as MediaBackendUnavailableException
                  ?? new MediaBackendUnavailableException(BackendName, "could not initialise libmpv.", _failure);
        }
    }

    private static void TryInitialise()
    {
        if (_initialised)
        {
            return;
        }

        lock (Gate)
        {
            if (_initialised)
            {
                return;
            }

            try
            {
                Module = NativeModule.Load(BackendName, Candidates());
                Bind();
                var version = mpv_client_api_version();
                VersionString = $"client API {version >> 16}.{version & 0xFFFF}";
            }
            catch (Exception ex)
            {
                _failure = ex;
            }
            finally
            {
                _initialised = true;
            }
        }
    }

    private static void Bind()
    {
        mpv_create = (delegate* unmanaged[Cdecl]<void*>)Module.GetExport(nameof(mpv_create));
        mpv_initialize = (delegate* unmanaged[Cdecl]<void*, int>)Module.GetExport(nameof(mpv_initialize));
        mpv_terminate_destroy = (delegate* unmanaged[Cdecl]<void*, void>)Module.GetExport(nameof(mpv_terminate_destroy));
        mpv_client_api_version = (delegate* unmanaged[Cdecl]<uint>)Module.GetExport(nameof(mpv_client_api_version));
        mpv_error_string = (delegate* unmanaged[Cdecl]<int, byte*>)Module.GetExport(nameof(mpv_error_string));
        mpv_free = (delegate* unmanaged[Cdecl]<void*, void>)Module.GetExport(nameof(mpv_free));
        mpv_command = (delegate* unmanaged[Cdecl]<void*, byte**, int>)Module.GetExport(nameof(mpv_command));
        mpv_command_string = (delegate* unmanaged[Cdecl]<void*, byte*, int>)Module.GetExport(nameof(mpv_command_string));
        mpv_set_option_string = (delegate* unmanaged[Cdecl]<void*, byte*, byte*, int>)Module.GetExport(nameof(mpv_set_option_string));
        mpv_set_property = (delegate* unmanaged[Cdecl]<void*, byte*, int, void*, int>)Module.GetExport(nameof(mpv_set_property));
        mpv_set_property_string = (delegate* unmanaged[Cdecl]<void*, byte*, byte*, int>)Module.GetExport(nameof(mpv_set_property_string));
        mpv_get_property = (delegate* unmanaged[Cdecl]<void*, byte*, int, void*, int>)Module.GetExport(nameof(mpv_get_property));
        mpv_get_property_string = (delegate* unmanaged[Cdecl]<void*, byte*, byte*>)Module.GetExport(nameof(mpv_get_property_string));
        mpv_observe_property = (delegate* unmanaged[Cdecl]<void*, ulong, byte*, int, int>)Module.GetExport(nameof(mpv_observe_property));
        mpv_request_log_messages = (delegate* unmanaged[Cdecl]<void*, byte*, int>)Module.GetExport(nameof(mpv_request_log_messages));
        mpv_wait_event = (delegate* unmanaged[Cdecl]<void*, double, MpvEvent*>)Module.GetExport(nameof(mpv_wait_event));
        mpv_wakeup = (delegate* unmanaged[Cdecl]<void*, void>)Module.GetExport(nameof(mpv_wakeup));
    }

    private static List<string> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            return ["libmpv-2.dll", "mpv-2.dll", "libmpv.dll", "mpv-1.dll"];
        }

        return OperatingSystem.IsMacOS()
            ? ["libmpv.2.dylib", "libmpv.dylib", "libmpv.1.dylib"]
            : ["libmpv.so.2", "libmpv.so", "libmpv.so.1"];
    }

    /// <summary>Formats an mpv error code using <c>mpv_error_string</c>.</summary>
    public static string Describe(int error) => Utf8.ToManagedOrEmpty(mpv_error_string(error));

    /// <summary>Throws when <paramref name="result"/> is a negative mpv error code.</summary>
    public static int Check(int result, string operation)
    {
        return result < 0
            ? throw new MediaToolkitNetException(BackendName, $"{operation}: {Describe(result)}", result)
            : result;
    }
}
