using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer.Native;

/// <summary>
/// The GStreamer, GObject and GLib entry points this backend uses.
/// </summary>
/// <remarks>
/// <para>
/// Everything is resolved once out of the shared libraries and called through
/// an unmanaged function pointer, the same way the FFmpeg binding works. No
/// varargs function is bound: <c>g_object_set</c> cannot be called safely
/// through a fixed signature, so element properties go through
/// <c>gst_util_set_object_arg</c>, which takes the value as a string and lets
/// GObject convert it.
/// </para>
/// <para>
/// GStreamer keeps a stable ABI across the whole 1.x series, so the few struct
/// offsets this backend needs live in <see cref="GstLayout"/> and are confirmed
/// at startup rather than trusted.
/// </para>
/// </remarks>
public static unsafe class Gst
{
    private static readonly Lock Gate = new();
    private static bool _initialised;
    private static Exception? _failure;

    /// <summary>libgstreamer-1.0, once loaded.</summary>
    public static NativeModule Core { get; private set; } = null!;

    /// <summary>libgstapp-1.0, which carries appsrc and appsink.</summary>
    public static NativeModule App { get; private set; } = null!;

    /// <summary>libgobject-2.0.</summary>
    public static NativeModule GObject { get; private set; } = null!;

    /// <summary>libglib-2.0.</summary>
    public static NativeModule GLib { get; private set; } = null!;

    /// <summary>The version GStreamer reports, once it has been initialised.</summary>
    public static string? VersionString { get; private set; }

    /// <summary>True once GStreamer loaded, initialised and passed the layout checks.</summary>
    public static bool IsAvailable
    {
        get
        {
            TryInitialise();
            return _failure is null;
        }
    }

    /// <summary>Loads and initialises GStreamer, throwing when it is not usable.</summary>
    /// <exception cref="MediaBackendUnavailableException">GStreamer is missing or unusable.</exception>
    public static void EnsureLoaded()
    {
        TryInitialise();
        if (_failure is not null)
        {
            throw _failure as MediaBackendUnavailableException
                  ?? new MediaBackendUnavailableException(
                      GstConstants.BackendName, "could not initialise GStreamer.", _failure);
        }
    }

    // ------------------------------------------------------------------ glib

    /// <summary><c>void g_free(gpointer)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void> g_free;

    /// <summary><c>GList *g_list_append(GList *, gpointer)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, void*> g_list_append;

    /// <summary><c>void g_list_free(GList *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void> g_list_free;

    /// <summary><c>GQuark g_quark_from_string(const gchar *)</c>, which copies the string.</summary>
    public static delegate* unmanaged[Cdecl]<byte*, uint> g_quark_from_string;

    /// <summary><c>GError *g_error_new_literal(GQuark, gint, const gchar *)</c></summary>
    public static delegate* unmanaged[Cdecl]<uint, int, byte*, void*> g_error_new_literal;

    /// <summary><c>void g_error_free(GError *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void> g_error_free;

    // --------------------------------------------------------------- gobject

    /// <summary><c>void g_object_unref(gpointer)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void> g_object_unref;

    // ------------------------------------------------------------- gstreamer

    /// <summary><c>gboolean gst_init_check(int *argc, char **argv[], GError **)</c></summary>
    public static delegate* unmanaged[Cdecl]<int*, byte***, void**, int> gst_init_check;

    /// <summary><c>gchar *gst_version_string(void)</c>, which the caller frees.</summary>
    public static delegate* unmanaged[Cdecl]<byte*> gst_version_string;

    /// <summary><c>GstElement *gst_parse_launch(const gchar *, GError **)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, void**, void*> gst_parse_launch;

    /// <summary><c>GstElement *gst_element_factory_make(const gchar *factory, const gchar *name)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, byte*, void*> gst_element_factory_make;

    /// <summary><c>GstStateChangeReturn gst_element_set_state(GstElement *, GstState)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int, int> gst_element_set_state;

    /// <summary><c>GstStateChangeReturn gst_element_get_state(GstElement *, GstState *, GstState *, GstClockTime)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int*, int*, ulong, int> gst_element_get_state;

    /// <summary><c>gboolean gst_element_query_duration(GstElement *, GstFormat, gint64 *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int, long*, int> gst_element_query_duration;

    /// <summary><c>gboolean gst_element_query_position(GstElement *, GstFormat, gint64 *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int, long*, int> gst_element_query_position;

    /// <summary><c>gboolean gst_element_seek_simple(GstElement *, GstFormat, GstSeekFlags, gint64)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int, int, long, int> gst_element_seek_simple;

    /// <summary><c>GstBus *gst_element_get_bus(GstElement *)</c>, a reference the caller owns.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_element_get_bus;

    /// <summary><c>GstElement *gst_bin_get_by_name(GstBin *, const gchar *)</c>, a reference the caller owns.</summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, void*> gst_bin_get_by_name;

    /// <summary><c>gchar *gst_object_get_name(GstObject *)</c>, which the caller frees.</summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*> gst_object_get_name;

    /// <summary><c>gpointer gst_object_ref_sink(gpointer)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_object_ref_sink;

    /// <summary><c>void gst_object_unref(gpointer)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void> gst_object_unref;

    /// <summary><c>void gst_util_set_object_arg(GObject *, const gchar *name, const gchar *value)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, byte*, void> gst_util_set_object_arg;

    /// <summary><c>GstBus *gst_bus_new(void)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*> gst_bus_new;

    /// <summary><c>gboolean gst_bus_post(GstBus *, GstMessage *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, int> gst_bus_post;

    /// <summary><c>GstMessage *gst_bus_timed_pop_filtered(GstBus *, GstClockTime, GstMessageType)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, ulong, uint, void*> gst_bus_timed_pop_filtered;

    /// <summary><c>GstMessage *gst_message_new_application(GstObject *, GstStructure *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, void*> gst_message_new_application;

    /// <summary><c>void gst_message_parse_error(GstMessage *, GError **, gchar **debug)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void**, byte**, void> gst_message_parse_error;

    /// <summary><c>void gst_mini_object_unref(GstMiniObject *)</c>, behind the message and sample macros.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void> gst_mini_object_unref;

    /// <summary><c>GstStructure *gst_structure_new_empty(const gchar *name)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, void*> gst_structure_new_empty;

    /// <summary><c>const gchar *gst_structure_get_name(const GstStructure *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*> gst_structure_get_name;

    /// <summary><c>gboolean gst_structure_get_int(const GstStructure *, const gchar *, gint *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, int*, int> gst_structure_get_int;

    /// <summary><c>const gchar *gst_structure_get_string(const GstStructure *, const gchar *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, byte*> gst_structure_get_string;

    /// <summary><c>gboolean gst_structure_get_fraction(const GstStructure *, const gchar *, gint *, gint *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, int*, int*, int> gst_structure_get_fraction;

    /// <summary><c>GstStructure *gst_caps_get_structure(const GstCaps *, guint)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, uint, void*> gst_caps_get_structure;

    /// <summary><c>gchar *gst_caps_to_string(const GstCaps *)</c>, which the caller frees.</summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*> gst_caps_to_string;

    /// <summary><c>GstBuffer *gst_buffer_new_allocate(GstAllocator *, gsize, GstAllocationParams *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, nuint, void*, void*> gst_buffer_new_allocate;

    /// <summary><c>gboolean gst_buffer_map(GstBuffer *, GstMapInfo *, GstMapFlags)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, int, int> gst_buffer_map;

    /// <summary><c>void gst_buffer_unmap(GstBuffer *, GstMapInfo *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, void> gst_buffer_unmap;

    /// <summary><c>GstRegistry *gst_registry_get(void)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*> gst_registry_get;

    /// <summary><c>GList *gst_registry_get_feature_list(GstRegistry *, GType)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, nuint, void*> gst_registry_get_feature_list;

    /// <summary><c>GType gst_element_factory_get_type(void)</c></summary>
    public static delegate* unmanaged[Cdecl]<nuint> gst_element_factory_get_type;

    /// <summary><c>void gst_plugin_feature_list_free(GList *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void> gst_plugin_feature_list_free;

    /// <summary><c>const gchar *gst_plugin_feature_get_name(GstPluginFeature *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*> gst_plugin_feature_get_name;

    /// <summary><c>const gchar *gst_element_factory_get_metadata(GstElementFactory *, const gchar *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, byte*> gst_element_factory_get_metadata;

    // ----------------------------------------------------------- gstapp

    /// <summary><c>GstSample *gst_app_sink_try_pull_sample(GstAppSink *, GstClockTime timeout)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, ulong, void*> gst_app_sink_try_pull_sample;

    /// <summary><c>gboolean gst_app_sink_is_eos(GstAppSink *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int> gst_app_sink_is_eos;

    /// <summary><c>GstCaps *gst_app_sink_get_caps(GstAppSink *)</c>, a reference the caller owns.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_app_sink_get_caps;

    /// <summary><c>GstBuffer *gst_sample_get_buffer(GstSample *)</c>, borrowed from the sample.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_sample_get_buffer;

    /// <summary><c>GstCaps *gst_sample_get_caps(GstSample *)</c>, borrowed from the sample.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_sample_get_caps;

    /// <summary>Reads a NUL-terminated string GLib allocated and frees it.</summary>
    public static string TakeString(byte* text)
    {
        if (text is null)
        {
            return string.Empty;
        }

        try
        {
            return Utf8.ToManagedOrEmpty(text);
        }
        finally
        {
            g_free(text);
        }
    }

    /// <summary>Sets one property of an element, converting the value from its string form.</summary>
    public static void SetProperty(void* element, string name, string value)
    {
        Span<byte> nameScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> valueScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, nameScratch);
        using var valueUtf8 = new Utf8Scoped(value, valueScratch);
        gst_util_set_object_arg(element, nameUtf8.Pointer, valueUtf8.Pointer);
    }

    /// <summary>Reads the message out of a <c>GError</c> and frees it.</summary>
    public static string TakeError(void* error)
    {
        if (error is null)
        {
            return string.Empty;
        }

        try
        {
            return GstLayout.MessageOfError(error);
        }
        finally
        {
            g_error_free(error);
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
                Initialise();
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

    private static void Initialise()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new MediaBackendUnavailableException(
                GstConstants.BackendName,
                "this backend binds the Linux library names; GStreamer on another platform needs its own names.");
        }

        if (nint.Size != 8)
        {
            throw new MediaBackendUnavailableException(
                GstConstants.BackendName, "only 64-bit processes are supported.");
        }

        GLib = NativeModule.Load(GstConstants.BackendName, ["libglib-2.0.so.0", "libglib-2.0.so"]);
        GObject = NativeModule.Load(GstConstants.BackendName, ["libgobject-2.0.so.0", "libgobject-2.0.so"]);
        Core = NativeModule.Load(GstConstants.BackendName, ["libgstreamer-1.0.so.0", "libgstreamer-1.0.so"]);
        App = NativeModule.Load(GstConstants.BackendName, ["libgstapp-1.0.so.0", "libgstapp-1.0.so"]);

        Bind();

        // gst_init_check reports a failure instead of aborting the process,
        // which gst_init would do.
        void* error = null;
        if (gst_init_check(null, null, &error) == 0)
        {
            throw new MediaBackendUnavailableException(
                GstConstants.BackendName, $"gst_init_check failed: {TakeError(error)}");
        }

        VersionString = TakeString(gst_version_string());
        GstLayout.Validate();
    }

    private static void Bind()
    {
        g_free = (delegate* unmanaged[Cdecl]<void*, void>)GLib.GetExport(nameof(g_free));
        g_list_append = (delegate* unmanaged[Cdecl]<void*, void*, void*>)GLib.GetExport(nameof(g_list_append));
        g_list_free = (delegate* unmanaged[Cdecl]<void*, void>)GLib.GetExport(nameof(g_list_free));
        g_quark_from_string = (delegate* unmanaged[Cdecl]<byte*, uint>)GLib.GetExport(nameof(g_quark_from_string));
        g_error_new_literal = (delegate* unmanaged[Cdecl]<uint, int, byte*, void*>)GLib.GetExport(nameof(g_error_new_literal));
        g_error_free = (delegate* unmanaged[Cdecl]<void*, void>)GLib.GetExport(nameof(g_error_free));

        g_object_unref = (delegate* unmanaged[Cdecl]<void*, void>)GObject.GetExport(nameof(g_object_unref));

        gst_init_check = (delegate* unmanaged[Cdecl]<int*, byte***, void**, int>)Core.GetExport(nameof(gst_init_check));
        gst_version_string = (delegate* unmanaged[Cdecl]<byte*>)Core.GetExport(nameof(gst_version_string));
        gst_parse_launch = (delegate* unmanaged[Cdecl]<byte*, void**, void*>)Core.GetExport(nameof(gst_parse_launch));
        gst_element_factory_make = (delegate* unmanaged[Cdecl]<byte*, byte*, void*>)Core.GetExport(nameof(gst_element_factory_make));
        gst_element_set_state = (delegate* unmanaged[Cdecl]<void*, int, int>)Core.GetExport(nameof(gst_element_set_state));
        gst_element_get_state = (delegate* unmanaged[Cdecl]<void*, int*, int*, ulong, int>)Core.GetExport(nameof(gst_element_get_state));
        gst_element_query_duration = (delegate* unmanaged[Cdecl]<void*, int, long*, int>)Core.GetExport(nameof(gst_element_query_duration));
        gst_element_query_position = (delegate* unmanaged[Cdecl]<void*, int, long*, int>)Core.GetExport(nameof(gst_element_query_position));
        gst_element_seek_simple = (delegate* unmanaged[Cdecl]<void*, int, int, long, int>)Core.GetExport(nameof(gst_element_seek_simple));
        gst_element_get_bus = (delegate* unmanaged[Cdecl]<void*, void*>)Core.GetExport(nameof(gst_element_get_bus));
        gst_bin_get_by_name = (delegate* unmanaged[Cdecl]<void*, byte*, void*>)Core.GetExport(nameof(gst_bin_get_by_name));
        gst_object_get_name = (delegate* unmanaged[Cdecl]<void*, byte*>)Core.GetExport(nameof(gst_object_get_name));
        gst_object_ref_sink = (delegate* unmanaged[Cdecl]<void*, void*>)Core.GetExport(nameof(gst_object_ref_sink));
        gst_object_unref = (delegate* unmanaged[Cdecl]<void*, void>)Core.GetExport(nameof(gst_object_unref));
        gst_util_set_object_arg = (delegate* unmanaged[Cdecl]<void*, byte*, byte*, void>)Core.GetExport(nameof(gst_util_set_object_arg));
        gst_bus_new = (delegate* unmanaged[Cdecl]<void*>)Core.GetExport(nameof(gst_bus_new));
        gst_bus_post = (delegate* unmanaged[Cdecl]<void*, void*, int>)Core.GetExport(nameof(gst_bus_post));
        gst_bus_timed_pop_filtered = (delegate* unmanaged[Cdecl]<void*, ulong, uint, void*>)Core.GetExport(nameof(gst_bus_timed_pop_filtered));
        gst_message_new_application = (delegate* unmanaged[Cdecl]<void*, void*, void*>)Core.GetExport(nameof(gst_message_new_application));
        gst_message_parse_error = (delegate* unmanaged[Cdecl]<void*, void**, byte**, void>)Core.GetExport(nameof(gst_message_parse_error));
        gst_mini_object_unref = (delegate* unmanaged[Cdecl]<void*, void>)Core.GetExport(nameof(gst_mini_object_unref));
        gst_structure_new_empty = (delegate* unmanaged[Cdecl]<byte*, void*>)Core.GetExport(nameof(gst_structure_new_empty));
        gst_structure_get_name = (delegate* unmanaged[Cdecl]<void*, byte*>)Core.GetExport(nameof(gst_structure_get_name));
        gst_structure_get_int = (delegate* unmanaged[Cdecl]<void*, byte*, int*, int>)Core.GetExport(nameof(gst_structure_get_int));
        gst_structure_get_string = (delegate* unmanaged[Cdecl]<void*, byte*, byte*>)Core.GetExport(nameof(gst_structure_get_string));
        gst_structure_get_fraction = (delegate* unmanaged[Cdecl]<void*, byte*, int*, int*, int>)Core.GetExport(nameof(gst_structure_get_fraction));
        gst_caps_get_structure = (delegate* unmanaged[Cdecl]<void*, uint, void*>)Core.GetExport(nameof(gst_caps_get_structure));
        gst_caps_to_string = (delegate* unmanaged[Cdecl]<void*, byte*>)Core.GetExport(nameof(gst_caps_to_string));
        gst_buffer_new_allocate = (delegate* unmanaged[Cdecl]<void*, nuint, void*, void*>)Core.GetExport(nameof(gst_buffer_new_allocate));
        gst_buffer_map = (delegate* unmanaged[Cdecl]<void*, byte*, int, int>)Core.GetExport(nameof(gst_buffer_map));
        gst_buffer_unmap = (delegate* unmanaged[Cdecl]<void*, byte*, void>)Core.GetExport(nameof(gst_buffer_unmap));
        gst_registry_get = (delegate* unmanaged[Cdecl]<void*>)Core.GetExport(nameof(gst_registry_get));
        gst_registry_get_feature_list = (delegate* unmanaged[Cdecl]<void*, nuint, void*>)Core.GetExport(nameof(gst_registry_get_feature_list));
        gst_element_factory_get_type = (delegate* unmanaged[Cdecl]<nuint>)Core.GetExport(nameof(gst_element_factory_get_type));
        gst_plugin_feature_list_free = (delegate* unmanaged[Cdecl]<void*, void>)Core.GetExport(nameof(gst_plugin_feature_list_free));
        gst_plugin_feature_get_name = (delegate* unmanaged[Cdecl]<void*, byte*>)Core.GetExport(nameof(gst_plugin_feature_get_name));
        gst_element_factory_get_metadata = (delegate* unmanaged[Cdecl]<void*, byte*, byte*>)Core.GetExport(nameof(gst_element_factory_get_metadata));

        gst_app_sink_try_pull_sample = (delegate* unmanaged[Cdecl]<void*, ulong, void*>)App.GetExport(nameof(gst_app_sink_try_pull_sample));
        gst_app_sink_is_eos = (delegate* unmanaged[Cdecl]<void*, int>)App.GetExport(nameof(gst_app_sink_is_eos));
        gst_app_sink_get_caps = (delegate* unmanaged[Cdecl]<void*, void*>)App.GetExport(nameof(gst_app_sink_get_caps));
        gst_sample_get_buffer = (delegate* unmanaged[Cdecl]<void*, void*>)Core.GetExport(nameof(gst_sample_get_buffer));
        gst_sample_get_caps = (delegate* unmanaged[Cdecl]<void*, void*>)Core.GetExport(nameof(gst_sample_get_caps));
    }
}
