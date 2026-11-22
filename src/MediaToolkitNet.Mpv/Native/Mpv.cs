#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions;
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

    /// <summary>MPV_FORMAT_NODE_ARRAY: only inside an <c>mpv_node</c>.</summary>
    NodeArray = 7,

    /// <summary>MPV_FORMAT_NODE_MAP: only inside an <c>mpv_node</c>.</summary>
    NodeMap = 8,

    /// <summary>MPV_FORMAT_BYTE_ARRAY: only inside an <c>mpv_node</c>.</summary>
    ByteArray = 9,
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

    /// <summary>MPV_EVENT_HOOK.</summary>
    Hook = 25,
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

    /// <summary><c>int mpv_command_node(mpv_handle *, mpv_node *args, mpv_node *result)</c>; null when libmpv lacks it.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, void*, int> mpv_command_node;

    /// <summary><c>int mpv_hook_add(mpv_handle *, uint64_t reply_userdata, const char *name, int priority)</c>; null when libmpv lacks it.</summary>
    public static delegate* unmanaged[Cdecl]<void*, ulong, byte*, int, int> mpv_hook_add;

    /// <summary><c>int mpv_hook_continue(mpv_handle *, uint64_t id)</c>; null when libmpv lacks it.</summary>
    public static delegate* unmanaged[Cdecl]<void*, ulong, int> mpv_hook_continue;

    /// <summary><c>int mpv_command_node_async(mpv_handle *, uint64_t reply_userdata, mpv_node *args)</c>; null when libmpv lacks it.</summary>
    public static delegate* unmanaged[Cdecl]<void*, ulong, void*, int> mpv_command_node_async;

    /// <summary><c>void mpv_abort_async_command(mpv_handle *, uint64_t reply_userdata)</c>; null when libmpv lacks it.</summary>
    public static delegate* unmanaged[Cdecl]<void*, ulong, void> mpv_abort_async_command;

    /// <summary>True when libmpv has hooks.</summary>
    public static bool HasHooks
    {
        get
        {
            TryInitialise();
            return _failure is null && mpv_hook_add is not null && mpv_hook_continue is not null;
        }
    }

    /// <summary>True when libmpv runs commands asynchronously and can abort them.</summary>
    public static bool HasAsyncCommands
    {
        get
        {
            TryInitialise();
            return _failure is null && mpv_command_node_async is not null && mpv_abort_async_command is not null && mpv_free_node_contents is not null;
        }
    }

    /// <summary><c>void mpv_free_node_contents(mpv_node *)</c>; null when libmpv lacks it.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void> mpv_free_node_contents;

    /// <summary>
    /// <c>int mpv_stream_cb_add_ro(mpv_handle *, const char *protocol, void *user_data, mpv_stream_cb_open_ro_fn)</c>;
    /// null when libmpv lacks it. Its open callback is <c>int (void *user_data, char *uri, mpv_stream_cb_info *)</c>.
    /// </summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, void*, delegate* unmanaged[Cdecl]<void*, byte*, nint*, int>, int> mpv_stream_cb_add_ro;

    /// <summary>The client API version of the loaded library as <c>major &lt;&lt; 16 | minor</c>; 0 when none is loaded.</summary>
    public static uint ApiVersion { get; private set; }

    /// <summary>True when libmpv can read from a stream the host supplies (<c>stream_cb</c>).</summary>
    public static bool HasStreamCallbacks
    {
        get
        {
            TryInitialise();
            return _failure is null && mpv_stream_cb_add_ro is not null;
        }
    }

    /// <summary>
    /// True when libmpv has the node API: structured property values and commands with named arguments or a
    /// result. Every libmpv of the 1.x and 2.x series that anyone ships does; this is for the ones that do not.
    /// </summary>
    public static bool HasNodeApi
    {
        get
        {
            TryInitialise();
            return _failure is null && mpv_command_node is not null && mpv_free_node_contents is not null;
        }
    }

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
                ApiVersion = version;
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
        mpv_command_node = Module.TryGetExport(nameof(mpv_command_node), out var commandNode)
            ? (delegate* unmanaged[Cdecl]<void*, void*, void*, int>)commandNode
            : null;
        mpv_stream_cb_add_ro = Module.TryGetExport(nameof(mpv_stream_cb_add_ro), out var streamAdd)
            ? (delegate* unmanaged[Cdecl]<void*, byte*, void*, delegate* unmanaged[Cdecl]<void*, byte*, nint*, int>, int>)streamAdd
            : null;
        mpv_hook_add = Module.TryGetExport(nameof(mpv_hook_add), out var hookAdd)
            ? (delegate* unmanaged[Cdecl]<void*, ulong, byte*, int, int>)hookAdd
            : null;
        mpv_hook_continue = Module.TryGetExport(nameof(mpv_hook_continue), out var hookContinue)
            ? (delegate* unmanaged[Cdecl]<void*, ulong, int>)hookContinue
            : null;
        mpv_command_node_async = Module.TryGetExport(nameof(mpv_command_node_async), out var commandAsync)
            ? (delegate* unmanaged[Cdecl]<void*, ulong, void*, int>)commandAsync
            : null;
        mpv_abort_async_command = Module.TryGetExport(nameof(mpv_abort_async_command), out var abortAsync)
            ? (delegate* unmanaged[Cdecl]<void*, ulong, void>)abortAsync
            : null;
        mpv_free_node_contents = Module.TryGetExport(nameof(mpv_free_node_contents), out var freeNode)
            ? (delegate* unmanaged[Cdecl]<void*, void>)freeNode
            : null;
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
