#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

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
                      GstConstants.BackendName, $"could not initialise GStreamer: {_failure.Message}", _failure);
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

    /// <summary><c>GParamSpec *g_object_class_find_property(GObjectClass *, const gchar *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, void*> g_object_class_find_property;

    /// <summary><c>gboolean g_type_check_instance_is_a(GTypeInstance *, GType)</c>, behind the G_TYPE_CHECK macros.</summary>
    public static delegate* unmanaged[Cdecl]<void*, nuint, int> g_type_check_instance_is_a;

    /// <summary><c>GValue *g_value_init(GValue *, GType)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, nuint, byte*> g_value_init;

    /// <summary><c>void g_value_set_string(GValue *, const gchar *)</c>, which copies the string.</summary>
    public static delegate* unmanaged[Cdecl]<byte*, byte*, void> g_value_set_string;

    /// <summary><c>void g_value_unset(GValue *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, void> g_value_unset;

    // ------------------------------------------------------------- gstreamer

    /// <summary><c>gboolean gst_init_check(int *argc, char **argv[], GError **)</c></summary>
    public static delegate* unmanaged[Cdecl]<int*, byte***, void**, int> gst_init_check;

    /// <summary><c>gchar *gst_version_string(void)</c>, which the caller frees.</summary>
    public static delegate* unmanaged[Cdecl]<byte*> gst_version_string;

    /// <summary><c>GstElement *gst_parse_launch(const gchar *, GError **)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, void**, void*> gst_parse_launch;

    /// <summary><c>GstElement *gst_parse_launch_full(const gchar *, GstParseContext *, GstParseFlags, GError **)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, void*, int, void**, void*> gst_parse_launch_full;

    /// <summary><c>gchar *gst_filename_to_uri(const gchar *, GError **)</c>, which the caller frees.</summary>
    public static delegate* unmanaged[Cdecl]<byte*, void**, byte*> gst_filename_to_uri;

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

    /// <summary><c>const gchar *gst_element_factory_get_metadata(GstElementFactory *, const gchar *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, byte*> gst_element_factory_get_metadata;

    /// <summary><c>GstEvent *gst_event_new_seek(gdouble rate, GstFormat, GstSeekFlags, GstSeekType, gint64 start, GstSeekType, gint64 stop)</c></summary>
    public static delegate* unmanaged[Cdecl]<double, int, int, int, long, int, long, void*> gst_event_new_seek;

    /// <summary><c>GstEvent *gst_event_new_flush_start(void)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*> gst_event_new_flush_start;

    /// <summary><c>GstEvent *gst_event_new_flush_stop(gboolean reset_time)</c></summary>
    public static delegate* unmanaged[Cdecl]<int, void*> gst_event_new_flush_stop;

    /// <summary><c>GstEvent *gst_event_new_eos(void)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*> gst_event_new_eos;

    /// <summary><c>gboolean gst_pad_send_event(GstPad *, GstEvent *)</c>, which takes the event.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, int> gst_pad_send_event;

    /// <summary>
    /// <c>gulong gst_pad_add_probe(GstPad *, GstPadProbeType, GstPadProbeCallback, gpointer, GDestroyNotify)</c>;
    /// the callback is <c>GstPadProbeReturn (*)(GstPad *, GstPadProbeInfo *, gpointer)</c>.
    /// </summary>
    public static delegate* unmanaged[Cdecl]<void*, int, delegate* unmanaged[Cdecl]<void*, void*, void*, int>, void*, void*, nuint> gst_pad_add_probe;

    /// <summary><c>GstEvent *gst_pad_probe_info_get_event(GstPadProbeInfo *)</c>, valid only in an event probe.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_pad_probe_info_get_event;

    /// <summary><c>gint gst_tag_list_n_tags(const GstTagList *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int> gst_tag_list_n_tags;

    /// <summary><c>const gchar *gst_tag_list_nth_tag_name(const GstTagList *, guint)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, uint, byte*> gst_tag_list_nth_tag_name;

    /// <summary><c>gboolean gst_tag_list_get_string(const GstTagList *, const gchar *, gchar **)</c>; the caller frees the string.</summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, byte**, int> gst_tag_list_get_string;

    /// <summary><c>gboolean gst_tag_exists(const gchar *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, int> gst_tag_exists;

    /// <summary><c>GType gst_tag_get_type(const gchar *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, nuint> gst_tag_get_type;

    /// <summary><c>GType gst_tag_setter_get_type(void)</c></summary>
    public static delegate* unmanaged[Cdecl]<nuint> gst_tag_setter_get_type;

    /// <summary><c>void gst_tag_setter_add_tag_value(GstTagSetter *, GstTagMergeMode, const gchar *, const GValue *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int, byte*, byte*, void> gst_tag_setter_add_tag_value;

    /// <summary><c>void gst_tag_setter_set_tag_merge_mode(GstTagSetter *, GstTagMergeMode)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int, void> gst_tag_setter_set_tag_merge_mode;

    /// <summary><c>GList *gst_toc_get_entries(const GstToc *)</c>, borrowed from the TOC.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_toc_get_entries;

    /// <summary><c>GList *gst_toc_entry_get_sub_entries(const GstTocEntry *)</c>, borrowed from the entry.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_toc_entry_get_sub_entries;

    /// <summary><c>GstTocEntryType gst_toc_entry_get_entry_type(const GstTocEntry *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int> gst_toc_entry_get_entry_type;

    /// <summary><c>gboolean gst_toc_entry_get_start_stop_times(const GstTocEntry *, gint64 *, gint64 *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, long*, long*, int> gst_toc_entry_get_start_stop_times;

    /// <summary><c>GstTagList *gst_toc_entry_get_tags(const GstTocEntry *)</c>, borrowed from the entry.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_toc_entry_get_tags;

    // ----------------------------------------------------------- gstapp

    /// <summary><c>GstSample *gst_app_sink_try_pull_sample(GstAppSink *, GstClockTime timeout)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, ulong, void*> gst_app_sink_try_pull_sample;

    /// <summary><c>gboolean gst_app_sink_is_eos(GstAppSink *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int> gst_app_sink_is_eos;

    /// <summary><c>GstPad *gst_element_get_static_pad(GstElement *, const gchar *)</c>, a reference the caller owns.</summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, void*> gst_element_get_static_pad;

    /// <summary><c>GstCaps *gst_pad_get_current_caps(GstPad *)</c>, a reference the caller owns, or null before negotiation.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_pad_get_current_caps;

    /// <summary><c>GstBuffer *gst_sample_get_buffer(GstSample *)</c>, borrowed from the sample.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_sample_get_buffer;

    /// <summary><c>GstCaps *gst_sample_get_caps(GstSample *)</c>, borrowed from the sample.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_sample_get_caps;

    /// <summary><c>gulong g_signal_connect_closure(gpointer, const gchar *detailed_signal, GClosure *, gboolean after)</c>; sinks the closure.</summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, void*, int, nuint> g_signal_connect_closure;

    /// <summary><c>void g_signal_handler_disconnect(gpointer, gulong)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, nuint, void> g_signal_handler_disconnect;

    /// <summary><c>guint g_signal_lookup(const gchar *name, GType)</c>, 0 when the type has no such signal; the name has no <c>::detail</c>.</summary>
    public static delegate* unmanaged[Cdecl]<byte*, nuint, uint> g_signal_lookup;

    /// <summary><c>GClosure *g_cclosure_new(GCallback, gpointer, GClosureNotify)</c>, floating.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, void*, void*> g_cclosure_new;

    /// <summary><c>void g_closure_set_meta_marshal(GClosure *, gpointer marshal_data, GClosureMarshal)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, void*, void> g_closure_set_meta_marshal;

    /// <summary><c>void g_closure_add_finalize_notifier(GClosure *, gpointer, GClosureNotify)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, void*, void> g_closure_add_finalize_notifier;

    /// <summary><c>GType g_type_fundamental(GType)</c></summary>
    public static delegate* unmanaged[Cdecl]<nuint, nuint> g_type_fundamental;

    /// <summary><c>const gchar *g_type_name(GType)</c>, borrowed.</summary>
    public static delegate* unmanaged[Cdecl]<nuint, byte*> g_type_name;

    /// <summary><c>const gchar *g_type_name_from_instance(GTypeInstance *)</c>, borrowed.</summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*> g_type_name_from_instance;

    /// <summary><c>GType g_type_from_name(const gchar *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, nuint> g_type_from_name;

    /// <summary><c>gboolean g_value_get_boolean(const GValue *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, int> g_value_get_boolean;

    /// <summary><c>gint g_value_get_int(const GValue *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, int> g_value_get_int;

    /// <summary><c>guint g_value_get_uint(const GValue *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, uint> g_value_get_uint;

    /// <summary><c>glong g_value_get_long(const GValue *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, long> g_value_get_long;

    /// <summary><c>gulong g_value_get_ulong(const GValue *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, ulong> g_value_get_ulong;

    /// <summary><c>gint64 g_value_get_int64(const GValue *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, long> g_value_get_int64;

    /// <summary><c>guint64 g_value_get_uint64(const GValue *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, ulong> g_value_get_uint64;

    /// <summary><c>gfloat g_value_get_float(const GValue *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, float> g_value_get_float;

    /// <summary><c>gdouble g_value_get_double(const GValue *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, double> g_value_get_double;

    /// <summary><c>const gchar *g_value_get_string(const GValue *)</c>, borrowed.</summary>
    public static delegate* unmanaged[Cdecl]<byte*, byte*> g_value_get_string;

    /// <summary><c>gpointer g_value_get_object(const GValue *)</c>, borrowed.</summary>
    public static delegate* unmanaged[Cdecl]<byte*, void*> g_value_get_object;

    /// <summary><c>gpointer g_value_get_pointer(const GValue *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, void*> g_value_get_pointer;

    /// <summary><c>gpointer g_value_get_boxed(const GValue *)</c>, borrowed.</summary>
    public static delegate* unmanaged[Cdecl]<byte*, void*> g_value_get_boxed;

    /// <summary><c>gint g_value_get_enum(const GValue *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, int> g_value_get_enum;

    /// <summary><c>guint g_value_get_flags(const GValue *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, uint> g_value_get_flags;

    /// <summary><c>void g_value_set_boolean(GValue *, gboolean)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, int, void> g_value_set_boolean;

    /// <summary><c>void g_value_set_int(GValue *, gint)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, int, void> g_value_set_int;

    /// <summary><c>void g_value_set_int64(GValue *, gint64)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, long, void> g_value_set_int64;

    /// <summary><c>void g_value_set_enum(GValue *, gint)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, int, void> g_value_set_enum;

    /// <summary><c>void g_value_set_uint(GValue *, guint)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, uint, void> g_value_set_uint;

    /// <summary><c>void g_value_set_uint64(GValue *, guint64)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, ulong, void> g_value_set_uint64;

    /// <summary><c>void g_value_set_float(GValue *, gfloat)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, float, void> g_value_set_float;

    /// <summary><c>void g_value_set_double(GValue *, gdouble)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, double, void> g_value_set_double;

    /// <summary><c>void g_value_set_flags(GValue *, guint)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, uint, void> g_value_set_flags;

    /// <summary><c>void g_object_set_property(GObject *, const gchar *, const GValue *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, byte*, void> g_object_set_property;

    /// <summary><c>void g_object_get_property(GObject *, const gchar *, GValue *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, byte*, void> g_object_get_property;

    /// <summary><c>const GValue *g_param_spec_get_default_value(GParamSpec *)</c>, borrowed; its type is the property's type.</summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*> g_param_spec_get_default_value;

    /// <summary><c>GstCaps *gst_caps_from_string(const gchar *)</c>, a reference the caller owns, or null for text that is not caps.</summary>
    public static delegate* unmanaged[Cdecl]<byte*, void*> gst_caps_from_string;

    /// <summary><c>GstFlowReturn gst_app_src_push_buffer(GstAppSrc *, GstBuffer *)</c>; takes the buffer, whatever it returns.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, int> gst_app_src_push_buffer;

    /// <summary><c>GstFlowReturn gst_app_src_end_of_stream(GstAppSrc *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int> gst_app_src_end_of_stream;

    /// <summary><c>void gst_app_src_set_caps(GstAppSrc *, const GstCaps *)</c>; takes its own reference.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, void> gst_app_src_set_caps;

    /// <summary><c>GstMessage *gst_bus_timed_pop(GstBus *, GstClockTime)</c>, any message type.</summary>
    public static delegate* unmanaged[Cdecl]<void*, ulong, void*> gst_bus_timed_pop;

    /// <summary><c>void gst_message_parse_warning(GstMessage *, GError **, gchar **debug)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void**, byte**, void> gst_message_parse_warning;

    /// <summary><c>void gst_message_parse_info(GstMessage *, GError **, gchar **debug)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void**, byte**, void> gst_message_parse_info;

    /// <summary><c>void gst_message_parse_state_changed(GstMessage *, GstState *old, GstState *new, GstState *pending)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int*, int*, int*, void> gst_message_parse_state_changed;

    /// <summary><c>void gst_message_parse_buffering(GstMessage *, gint *percent)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int*, void> gst_message_parse_buffering;

    /// <summary><c>void gst_message_parse_tag(GstMessage *, GstTagList **)</c>; the list is a reference the caller owns.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void**, void> gst_message_parse_tag;

    /// <summary><c>const GstStructure *gst_message_get_structure(GstMessage *)</c>, borrowed from the message.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_message_get_structure;

    /// <summary><c>gchar *gst_structure_to_string(const GstStructure *)</c>, which the caller frees.</summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*> gst_structure_to_string;

    /// <summary><c>GParamSpec *g_value_get_param(const GValue *)</c>, borrowed.</summary>
    public static delegate* unmanaged[Cdecl]<byte*, void*> g_value_get_param;

    /// <summary><c>const gchar *g_param_spec_get_name(GParamSpec *)</c>, borrowed.</summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*> g_param_spec_get_name;

    /// <summary><c>GType gst_object_get_type(void)</c></summary>
    public static delegate* unmanaged[Cdecl]<nuint> gst_object_get_type;

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
        g_object_class_find_property = (delegate* unmanaged[Cdecl]<void*, byte*, void*>)GObject.GetExport(nameof(g_object_class_find_property));
        g_type_check_instance_is_a = (delegate* unmanaged[Cdecl]<void*, nuint, int>)GObject.GetExport(nameof(g_type_check_instance_is_a));
        g_value_init = (delegate* unmanaged[Cdecl]<byte*, nuint, byte*>)GObject.GetExport(nameof(g_value_init));
        g_value_set_string = (delegate* unmanaged[Cdecl]<byte*, byte*, void>)GObject.GetExport(nameof(g_value_set_string));
        g_value_unset = (delegate* unmanaged[Cdecl]<byte*, void>)GObject.GetExport(nameof(g_value_unset));

        gst_init_check = (delegate* unmanaged[Cdecl]<int*, byte***, void**, int>)Core.GetExport(nameof(gst_init_check));
        gst_version_string = (delegate* unmanaged[Cdecl]<byte*>)Core.GetExport(nameof(gst_version_string));
        gst_parse_launch = (delegate* unmanaged[Cdecl]<byte*, void**, void*>)Core.GetExport(nameof(gst_parse_launch));
        gst_parse_launch_full = (delegate* unmanaged[Cdecl]<byte*, void*, int, void**, void*>)Core.GetExport(nameof(gst_parse_launch_full));
        gst_filename_to_uri = (delegate* unmanaged[Cdecl]<byte*, void**, byte*>)Core.GetExport(nameof(gst_filename_to_uri));
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
        gst_element_factory_get_metadata = (delegate* unmanaged[Cdecl]<void*, byte*, byte*>)Core.GetExport(nameof(gst_element_factory_get_metadata));
        gst_event_new_seek = (delegate* unmanaged[Cdecl]<double, int, int, int, long, int, long, void*>)Core.GetExport(nameof(gst_event_new_seek));
        gst_event_new_flush_start = (delegate* unmanaged[Cdecl]<void*>)Core.GetExport(nameof(gst_event_new_flush_start));
        gst_event_new_flush_stop = (delegate* unmanaged[Cdecl]<int, void*>)Core.GetExport(nameof(gst_event_new_flush_stop));
        gst_event_new_eos = (delegate* unmanaged[Cdecl]<void*>)Core.GetExport(nameof(gst_event_new_eos));
        gst_pad_send_event = (delegate* unmanaged[Cdecl]<void*, void*, int>)Core.GetExport(nameof(gst_pad_send_event));
        gst_pad_add_probe = (delegate* unmanaged[Cdecl]<void*, int, delegate* unmanaged[Cdecl]<void*, void*, void*, int>, void*, void*, nuint>)Core.GetExport(nameof(gst_pad_add_probe));
        gst_pad_probe_info_get_event = (delegate* unmanaged[Cdecl]<void*, void*>)Core.GetExport(nameof(gst_pad_probe_info_get_event));
        gst_tag_list_n_tags = (delegate* unmanaged[Cdecl]<void*, int>)Core.GetExport(nameof(gst_tag_list_n_tags));
        gst_tag_list_nth_tag_name = (delegate* unmanaged[Cdecl]<void*, uint, byte*>)Core.GetExport(nameof(gst_tag_list_nth_tag_name));
        gst_tag_list_get_string = (delegate* unmanaged[Cdecl]<void*, byte*, byte**, int>)Core.GetExport(nameof(gst_tag_list_get_string));
        gst_tag_exists = (delegate* unmanaged[Cdecl]<byte*, int>)Core.GetExport(nameof(gst_tag_exists));
        gst_tag_get_type = (delegate* unmanaged[Cdecl]<byte*, nuint>)Core.GetExport(nameof(gst_tag_get_type));
        gst_tag_setter_get_type = (delegate* unmanaged[Cdecl]<nuint>)Core.GetExport(nameof(gst_tag_setter_get_type));
        gst_tag_setter_add_tag_value = (delegate* unmanaged[Cdecl]<void*, int, byte*, byte*, void>)Core.GetExport(nameof(gst_tag_setter_add_tag_value));
        gst_tag_setter_set_tag_merge_mode = (delegate* unmanaged[Cdecl]<void*, int, void>)Core.GetExport(nameof(gst_tag_setter_set_tag_merge_mode));
        gst_toc_get_entries = (delegate* unmanaged[Cdecl]<void*, void*>)Core.GetExport(nameof(gst_toc_get_entries));
        gst_toc_entry_get_sub_entries = (delegate* unmanaged[Cdecl]<void*, void*>)Core.GetExport(nameof(gst_toc_entry_get_sub_entries));
        gst_toc_entry_get_entry_type = (delegate* unmanaged[Cdecl]<void*, int>)Core.GetExport(nameof(gst_toc_entry_get_entry_type));
        gst_toc_entry_get_start_stop_times = (delegate* unmanaged[Cdecl]<void*, long*, long*, int>)Core.GetExport(nameof(gst_toc_entry_get_start_stop_times));
        gst_toc_entry_get_tags = (delegate* unmanaged[Cdecl]<void*, void*>)Core.GetExport(nameof(gst_toc_entry_get_tags));

        gst_app_sink_try_pull_sample = (delegate* unmanaged[Cdecl]<void*, ulong, void*>)App.GetExport(nameof(gst_app_sink_try_pull_sample));
        gst_app_sink_is_eos = (delegate* unmanaged[Cdecl]<void*, int>)App.GetExport(nameof(gst_app_sink_is_eos));
        gst_element_get_static_pad = (delegate* unmanaged[Cdecl]<void*, byte*, void*>)Core.GetExport(nameof(gst_element_get_static_pad));
        gst_pad_get_current_caps = (delegate* unmanaged[Cdecl]<void*, void*>)Core.GetExport(nameof(gst_pad_get_current_caps));
        gst_sample_get_buffer = (delegate* unmanaged[Cdecl]<void*, void*>)Core.GetExport(nameof(gst_sample_get_buffer));
        gst_sample_get_caps = (delegate* unmanaged[Cdecl]<void*, void*>)Core.GetExport(nameof(gst_sample_get_caps));
        g_value_get_param = (delegate* unmanaged[Cdecl]<byte*, void*>)GObject.GetExport(nameof(g_value_get_param));
        g_param_spec_get_name = (delegate* unmanaged[Cdecl]<void*, byte*>)GObject.GetExport(nameof(g_param_spec_get_name));
        gst_object_get_type = (delegate* unmanaged[Cdecl]<nuint>)Core.GetExport(nameof(gst_object_get_type));
        g_signal_connect_closure = (delegate* unmanaged[Cdecl]<void*, byte*, void*, int, nuint>)GObject.GetExport(nameof(g_signal_connect_closure));
        g_signal_handler_disconnect = (delegate* unmanaged[Cdecl]<void*, nuint, void>)GObject.GetExport(nameof(g_signal_handler_disconnect));
        g_signal_lookup = (delegate* unmanaged[Cdecl]<byte*, nuint, uint>)GObject.GetExport(nameof(g_signal_lookup));
        g_cclosure_new = (delegate* unmanaged[Cdecl]<void*, void*, void*, void*>)GObject.GetExport(nameof(g_cclosure_new));
        g_closure_set_meta_marshal = (delegate* unmanaged[Cdecl]<void*, void*, void*, void>)GObject.GetExport(nameof(g_closure_set_meta_marshal));
        g_closure_add_finalize_notifier = (delegate* unmanaged[Cdecl]<void*, void*, void*, void>)GObject.GetExport(nameof(g_closure_add_finalize_notifier));
        g_type_fundamental = (delegate* unmanaged[Cdecl]<nuint, nuint>)GObject.GetExport(nameof(g_type_fundamental));
        g_type_name = (delegate* unmanaged[Cdecl]<nuint, byte*>)GObject.GetExport(nameof(g_type_name));
        g_type_name_from_instance = (delegate* unmanaged[Cdecl]<void*, byte*>)GObject.GetExport(nameof(g_type_name_from_instance));
        g_type_from_name = (delegate* unmanaged[Cdecl]<byte*, nuint>)GObject.GetExport(nameof(g_type_from_name));
        g_value_get_boolean = (delegate* unmanaged[Cdecl]<byte*, int>)GObject.GetExport(nameof(g_value_get_boolean));
        g_value_get_int = (delegate* unmanaged[Cdecl]<byte*, int>)GObject.GetExport(nameof(g_value_get_int));
        g_value_get_uint = (delegate* unmanaged[Cdecl]<byte*, uint>)GObject.GetExport(nameof(g_value_get_uint));
        g_value_get_long = (delegate* unmanaged[Cdecl]<byte*, long>)GObject.GetExport(nameof(g_value_get_long));
        g_value_get_ulong = (delegate* unmanaged[Cdecl]<byte*, ulong>)GObject.GetExport(nameof(g_value_get_ulong));
        g_value_get_int64 = (delegate* unmanaged[Cdecl]<byte*, long>)GObject.GetExport(nameof(g_value_get_int64));
        g_value_get_uint64 = (delegate* unmanaged[Cdecl]<byte*, ulong>)GObject.GetExport(nameof(g_value_get_uint64));
        g_value_get_float = (delegate* unmanaged[Cdecl]<byte*, float>)GObject.GetExport(nameof(g_value_get_float));
        g_value_get_double = (delegate* unmanaged[Cdecl]<byte*, double>)GObject.GetExport(nameof(g_value_get_double));
        g_value_get_string = (delegate* unmanaged[Cdecl]<byte*, byte*>)GObject.GetExport(nameof(g_value_get_string));
        g_value_get_object = (delegate* unmanaged[Cdecl]<byte*, void*>)GObject.GetExport(nameof(g_value_get_object));
        g_value_get_pointer = (delegate* unmanaged[Cdecl]<byte*, void*>)GObject.GetExport(nameof(g_value_get_pointer));
        g_value_get_boxed = (delegate* unmanaged[Cdecl]<byte*, void*>)GObject.GetExport(nameof(g_value_get_boxed));
        g_value_get_enum = (delegate* unmanaged[Cdecl]<byte*, int>)GObject.GetExport(nameof(g_value_get_enum));
        g_value_get_flags = (delegate* unmanaged[Cdecl]<byte*, uint>)GObject.GetExport(nameof(g_value_get_flags));
        g_value_set_boolean = (delegate* unmanaged[Cdecl]<byte*, int, void>)GObject.GetExport(nameof(g_value_set_boolean));
        g_value_set_int = (delegate* unmanaged[Cdecl]<byte*, int, void>)GObject.GetExport(nameof(g_value_set_int));
        g_value_set_int64 = (delegate* unmanaged[Cdecl]<byte*, long, void>)GObject.GetExport(nameof(g_value_set_int64));
        g_value_set_enum = (delegate* unmanaged[Cdecl]<byte*, int, void>)GObject.GetExport(nameof(g_value_set_enum));
        g_value_set_uint = (delegate* unmanaged[Cdecl]<byte*, uint, void>)GObject.GetExport(nameof(g_value_set_uint));
        g_value_set_uint64 = (delegate* unmanaged[Cdecl]<byte*, ulong, void>)GObject.GetExport(nameof(g_value_set_uint64));
        g_value_set_float = (delegate* unmanaged[Cdecl]<byte*, float, void>)GObject.GetExport(nameof(g_value_set_float));
        g_value_set_double = (delegate* unmanaged[Cdecl]<byte*, double, void>)GObject.GetExport(nameof(g_value_set_double));
        g_value_set_flags = (delegate* unmanaged[Cdecl]<byte*, uint, void>)GObject.GetExport(nameof(g_value_set_flags));
        g_object_set_property = (delegate* unmanaged[Cdecl]<void*, byte*, byte*, void>)GObject.GetExport(nameof(g_object_set_property));
        g_object_get_property = (delegate* unmanaged[Cdecl]<void*, byte*, byte*, void>)GObject.GetExport(nameof(g_object_get_property));
        g_param_spec_get_default_value = (delegate* unmanaged[Cdecl]<void*, byte*>)GObject.GetExport(nameof(g_param_spec_get_default_value));
        gst_caps_from_string = (delegate* unmanaged[Cdecl]<byte*, void*>)Core.GetExport(nameof(gst_caps_from_string));
        gst_app_src_push_buffer = (delegate* unmanaged[Cdecl]<void*, void*, int>)App.GetExport(nameof(gst_app_src_push_buffer));
        gst_app_src_end_of_stream = (delegate* unmanaged[Cdecl]<void*, int>)App.GetExport(nameof(gst_app_src_end_of_stream));
        gst_app_src_set_caps = (delegate* unmanaged[Cdecl]<void*, void*, void>)App.GetExport(nameof(gst_app_src_set_caps));
        gst_bus_timed_pop = (delegate* unmanaged[Cdecl]<void*, ulong, void*>)Core.GetExport(nameof(gst_bus_timed_pop));
        gst_message_parse_warning = (delegate* unmanaged[Cdecl]<void*, void**, byte**, void>)Core.GetExport(nameof(gst_message_parse_warning));
        gst_message_parse_info = (delegate* unmanaged[Cdecl]<void*, void**, byte**, void>)Core.GetExport(nameof(gst_message_parse_info));
        gst_message_parse_state_changed = (delegate* unmanaged[Cdecl]<void*, int*, int*, int*, void>)Core.GetExport(nameof(gst_message_parse_state_changed));
        gst_message_parse_buffering = (delegate* unmanaged[Cdecl]<void*, int*, void>)Core.GetExport(nameof(gst_message_parse_buffering));
        gst_message_parse_tag = (delegate* unmanaged[Cdecl]<void*, void**, void>)Core.GetExport(nameof(gst_message_parse_tag));
        gst_message_get_structure = (delegate* unmanaged[Cdecl]<void*, void*>)Core.GetExport(nameof(gst_message_get_structure));
        gst_structure_to_string = (delegate* unmanaged[Cdecl]<void*, byte*>)Core.GetExport(nameof(gst_structure_to_string));
    }
}
