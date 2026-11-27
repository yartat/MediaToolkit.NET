#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer.Native;

/// <summary>
/// Every struct field this backend reads, and the startup checks that confirm
/// them.
/// </summary>
/// <remarks>
/// <para>
/// GStreamer exposes almost everything through functions, but a handful of
/// things are macros over struct fields and have no accessor at all:
/// <c>GST_MESSAGE_TYPE</c>, <c>GST_EVENT_TYPE</c>, <c>GST_BUFFER_PTS</c>, the
/// contents of <c>GstMapInfo</c>, the links of a <c>GList</c> and the class
/// pointer behind <c>G_OBJECT_GET_CLASS</c>. Those six are the whole list, and
/// none of them is hard-coded: each is found by building an object whose
/// contents are known and looking for the value in it.
/// </para>
/// <para>
/// A probe that fails leaves <see cref="Verified"/> false, and the pipeline
/// refuses to run rather than reading a field at a guessed offset.
/// </para>
/// </remarks>
public static unsafe class GstLayout
{
    /// <summary>Size of <c>GstMapInfo</c>: memory, flags, data, size, maxsize and two pointer arrays.</summary>
    public const int MapInfoSize = 104;

    /// <summary>Offset of <c>GstMapInfo::data</c>.</summary>
    public const int MapInfoData = 16;

    /// <summary>Offset of <c>GstMapInfo::size</c>.</summary>
    public const int MapInfoSizeField = 24;

    /// <summary>Size of <c>GSignalQuery</c>; its <c>signal_id</c> comes first and is checked after every query.</summary>
    public const int SignalQuerySize = 56;

    /// <summary>Offset of <c>GSignalQuery::signal_flags</c>.</summary>
    public const int SignalQueryFlags = 24;

    /// <summary>Offset of <c>GSignalQuery::return_type</c>.</summary>
    public const int SignalQueryReturnType = 32;

    /// <summary>Offset of <c>GSignalQuery::n_params</c>.</summary>
    public const int SignalQueryParameterCount = 40;

    /// <summary>Offset of <c>GSignalQuery::param_types</c>.</summary>
    public const int SignalQueryParameterTypes = 48;

    /// <summary><c>G_SIGNAL_ACTION</c>.</summary>
    public const uint SignalActionFlag = 1 << 5;

    /// <summary><c>G_TYPE_NONE</c>.</summary>
    public const int TypeNone = 1 << 2;

    /// <summary>Offset of <c>GParamSpec::flags</c>, after the type instance and the name.</summary>
    public const int ParamSpecFlags = 16;

    /// <summary>Offset of <c>GValueArray::values</c>; the count of them comes first.</summary>
    public const int ValueArrayValues = 8;

    /// <summary>Offsets in <c>GstStaticPadTemplate</c>: name, direction, presence and the caps' text.</summary>
    public const int PadTemplateName = 0, PadTemplateDirection = 8, PadTemplatePresence = 12, PadTemplateCaps = 24;

    /// <summary>Offset of <c>GList::data</c>.</summary>
    public const int ListData = 0;

    /// <summary>Offset of <c>GList::next</c>.</summary>
    public const int ListNext = 8;

    /// <summary>Offset of <c>GError::code</c>.</summary>
    public const int ErrorCode = 4;

    /// <summary>Offset of <c>GError::message</c>.</summary>
    public const int ErrorMessage = 8;

    /// <summary>Offset of <c>GstMessage::type</c>, found by <see cref="Validate"/>.</summary>
    public static int MessageType { get; private set; } = -1;

    /// <summary>Offset of <c>GstMessage::src</c>, the object that posted the message, found by <see cref="Validate"/>.</summary>
    public static int MessageSource { get; private set; } = -1;

    /// <summary>Offset of <c>GstEvent::type</c>, found by <see cref="Validate"/>.</summary>
    public static int EventType { get; private set; } = -1;

    /// <summary>Offset of <c>GTypeInstance::g_class</c>, the first field of every GObject.</summary>
    public const int InstanceClass = 0;

    /// <summary>Offset of <c>GstBuffer::pts</c>, found by <see cref="Validate"/>.</summary>
    public static int BufferPts { get; private set; } = -1;

    /// <summary>True once every layout below was confirmed against a real object.</summary>
    public static bool Verified { get; private set; }

    /// <summary>Reads <c>GST_MESSAGE_TYPE</c>.</summary>
    public static GstMessageType TypeOfMessage(void* message) =>
        (GstMessageType)(*(uint*)((byte*)message + MessageType));

    /// <summary>Reads <c>GST_MESSAGE_SRC</c>: the object that posted the message, not a reference, or null.</summary>
    public static void* SourceOfMessage(void* message) => *(void**)((byte*)message + MessageSource);

    /// <summary>Reads <c>GST_EVENT_TYPE</c>.</summary>
    public static GstEventType TypeOfEvent(void* evt) => (GstEventType)(*(int*)((byte*)evt + EventType));

    /// <summary>Reads <c>G_OBJECT_GET_CLASS</c>, the class structure of an object.</summary>
    public static void* ClassOf(void* instance) => *(void**)((byte*)instance + InstanceClass);

    /// <summary>Reads <c>GST_BUFFER_PTS</c>, in nanoseconds.</summary>
    public static ulong PtsOfBuffer(void* buffer) => *(ulong*)((byte*)buffer + BufferPts);

    // GstBuffer keeps pts, dts, duration, offset and offset_end in that order, and the probe found the run they form,
    // so the duration is two qwords after the pts.
    private const int BufferDurationDelta = 16;

    /// <summary>Reads <c>GST_BUFFER_DURATION</c>, in nanoseconds.</summary>
    public static ulong DurationOfBuffer(void* buffer) => *(ulong*)((byte*)buffer + BufferPts + BufferDurationDelta);

    /// <summary>Reads <c>GST_BUFFER_DTS</c>, in nanoseconds.</summary>
    public static ulong DtsOfBuffer(void* buffer) => *(ulong*)((byte*)buffer + BufferPts + sizeof(ulong));

    /// <summary><c>GST_BUFFER_FLAG_HEADER</c>.</summary>
    public const uint BufferFlagHeader = 1 << 10;

    /// <summary><c>GST_BUFFER_FLAG_DELTA_UNIT</c>: not decodable on its own.</summary>
    public const uint BufferFlagDeltaUnit = 1 << 13;

    /// <summary>Writes <c>GST_BUFFER_PTS</c> and <c>GST_BUFFER_DURATION</c> of a buffer nothing else holds yet.</summary>
    public static void SetBufferTimes(void* buffer, ulong pts, ulong duration)
    {
        *(ulong*)((byte*)buffer + BufferPts) = pts;
        *(ulong*)((byte*)buffer + BufferPts + BufferDurationDelta) = duration;
    }

    /// <summary>Reads <c>GList::data</c>.</summary>
    public static void* DataOfListNode(void* node) => *(void**)((byte*)node + ListData);

    /// <summary>Reads <c>GList::next</c>.</summary>
    public static void* NextListNode(void* node) => *(void**)((byte*)node + ListNext);

    /// <summary>Reads <c>GError::message</c>.</summary>
    public static string MessageOfError(void* error) =>
        Utf8.ToManagedOrEmpty(*(byte**)((byte*)error + ErrorMessage));

    /// <summary>Throws unless every layout was confirmed.</summary>
    public static void Require()
    {
        if (!Verified)
        {
            throw new MediaBackendUnavailableException(
                GstConstants.BackendName,
                "the GStreamer struct layout could not be confirmed against this build, so the backend is disabled " +
                $"(failed: {string.Join(", ", FailedProbes)}).");
        }
    }

    /// <summary>
    /// Runs every probe. Called once, after <c>gst_init_check</c> has succeeded.
    /// </summary>
    internal static void Validate()
    {
        MessageType = ProbeMessageType();
        MessageSource = ProbeMessageSource();
        EventType = ProbeEventType();
        BufferPts = ProbeBufferPts();

        // Every probe runs, so the message names all of them that failed rather
        // than the first.
        var failed = new List<string>();
        if (MessageType < 0)
        {
            failed.Add("GstMessage::type");
        }

        if (MessageSource < 0)
        {
            failed.Add("GstMessage::src");
        }

        if (EventType < 0)
        {
            failed.Add("GstEvent::type");
        }

        if (!ProbeInstanceClass())
        {
            failed.Add("GTypeInstance::g_class");
        }

        if (BufferPts < 0)
        {
            failed.Add("GstBuffer::pts");
        }

        if (!ProbeList())
        {
            failed.Add("GList");
        }

        if (!ProbeMapInfo())
        {
            failed.Add("GstMapInfo");
        }

        if (!ProbeError())
        {
            failed.Add("GError");
        }

        FailedProbes = failed;
        Verified = failed.Count == 0;
    }

    /// <summary>The layouts that did not check out, empty once <see cref="Verified"/> is true.</summary>
    public static IReadOnlyList<string> FailedProbes { get; private set; } = [];

    /// <summary>
    /// Finds <c>GstMessage::type</c> by posting a message of a type no element
    /// ever posts.
    /// </summary>
    /// <remarks>
    /// GST_MESSAGE_APPLICATION is 1&lt;&lt;14, a value that does not occur in the
    /// reference counts, flags or pointers that surround the field, so the first
    /// match is the field.
    /// </remarks>
    private static int ProbeMessageType()
    {
        const int searchLimit = 128;
        const uint application = (uint)GstMessageType.Application;

        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var name = new Utf8Scoped("mediatoolkitnet-probe", scratch);
        var structure = Gst.gst_structure_new_empty(name.Pointer);
        if (structure is null)
        {
            return -1;
        }

        // gst_message_new_application takes the structure, whether or not it
        // succeeds in building the message.
        var message = Gst.gst_message_new_application(null, structure);
        if (message is null)
        {
            return -1;
        }

        try
        {
            // The scan starts past the GType that opens every GstMiniObject.
            var raw = (byte*)message;
            for (var offset = 8; offset + 4 <= searchLimit; offset += 4)
            {
                if (*(uint*)(raw + offset) == application)
                {
                    return offset;
                }
            }

            return -1;
        }
        finally
        {
            Gst.gst_mini_object_unref(message);
        }
    }

    /// <summary>
    /// Finds <c>GstMessage::src</c> by posting a message that names an element made here as its source: the field
    /// is the one that holds that pointer.
    /// </summary>
    private static int ProbeMessageSource()
    {
        const int searchLimit = 128;

        Span<byte> factoryScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> nameScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var factory = new Utf8Scoped("identity", factoryScratch);
        using var name = new Utf8Scoped("mediatoolkitnet-probe", nameScratch);

        var element = Gst.gst_element_factory_make(factory.Pointer, null);
        if (element is null)
        {
            return -1;
        }

        Gst.gst_object_ref_sink(element);
        try
        {
            var structure = Gst.gst_structure_new_empty(name.Pointer);
            if (structure is null)
            {
                return -1;
            }

            var message = Gst.gst_message_new_application(element, structure);
            if (message is null)
            {
                return -1;
            }

            try
            {
                // Pointers are eight-aligned; the scan starts past the GType that opens every GstMiniObject.
                var raw = (byte*)message;
                for (var offset = 8; offset + 8 <= searchLimit; offset += 8)
                {
                    if (*(void**)(raw + offset) == element)
                    {
                        return offset;
                    }
                }

                return -1;
            }
            finally
            {
                Gst.gst_mini_object_unref(message);
            }
        }
        finally
        {
            Gst.gst_object_unref(element);
        }
    }

    /// <summary>
    /// Finds <c>GstEvent::type</c> as the one offset at which a flush-start, a
    /// flush-stop and an end-of-stream event each hold their own type.
    /// </summary>
    /// <remarks>
    /// Any one of the values could turn up in a neighbouring field by chance;
    /// all three at the same offset, in three different events, cannot.
    /// </remarks>
    private static int ProbeEventType()
    {
        const int searchLimit = 128;

        var events = new[]
        {
            ((nint)Gst.gst_event_new_flush_start(), GstEventType.FlushStart),
            ((nint)Gst.gst_event_new_flush_stop(1), GstEventType.FlushStop),
            ((nint)Gst.gst_event_new_eos(), GstEventType.Eos),
        };

        try
        {
            if (events.Any(e => e.Item1 == 0))
            {
                return -1;
            }

            // The scan starts past the GType that opens every GstMiniObject.
            for (var offset = 8; offset + 4 <= searchLimit; offset += 4)
            {
                if (events.All(e => *(int*)((byte*)e.Item1 + offset) == (int)e.Item2))
                {
                    return offset;
                }
            }

            return -1;
        }
        finally
        {
            foreach (var (handle, _) in events)
            {
                if (handle != 0)
                {
                    Gst.gst_mini_object_unref((void*)handle);
                }
            }
        }
    }

    /// <summary>
    /// Confirms the class pointer by looking up, through it, a property the
    /// <c>identity</c> element has and one no element has.
    /// </summary>
    private static bool ProbeInstanceClass()
    {
        Span<byte> factoryScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> knownScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> unknownScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var factory = new Utf8Scoped("identity", factoryScratch);
        using var known = new Utf8Scoped("silent", knownScratch);
        using var unknown = new Utf8Scoped("mediatoolkitnet-probe", unknownScratch);

        var element = Gst.gst_element_factory_make(factory.Pointer, null);
        if (element is null)
        {
            return false;
        }

        Gst.gst_object_ref_sink(element);
        try
        {
            var type = ClassOf(element);
            return type is not null
                   && Gst.g_object_class_find_property(type, known.Pointer) is not null
                   && Gst.g_object_class_find_property(type, unknown.Pointer) is null;
        }
        finally
        {
            Gst.gst_object_unref(element);
        }
    }

    /// <summary>
    /// Finds <c>GstBuffer::pts</c> in a buffer that has just been allocated.
    /// </summary>
    /// <remarks>
    /// A fresh buffer has pts, dts and duration set to GST_CLOCK_TIME_NONE and
    /// offset and offset_end set to GST_BUFFER_OFFSET_NONE, which is the same
    /// all-ones value: five in a row, which nothing else in the struct matches.
    /// </remarks>
    private static int ProbeBufferPts()
    {
        const int searchLimit = 128;
        const int run = 5;

        var buffer = Gst.gst_buffer_new_allocate(null, 64, null);
        if (buffer is null)
        {
            return -1;
        }

        try
        {
            var raw = (byte*)buffer;
            for (var offset = 8; offset + (run * 8) <= searchLimit; offset += 8)
            {
                var matched = true;
                for (var i = 0; i < run && matched; i++)
                {
                    matched = *(ulong*)(raw + offset + (i * 8)) == GstConstants.ClockTimeNone;
                }

                if (matched)
                {
                    return offset;
                }
            }

            return -1;
        }
        finally
        {
            Gst.gst_mini_object_unref(buffer);
        }
    }

    /// <summary>Confirms the <c>GList</c> links against a list of one known element.</summary>
    private static bool ProbeList()
    {
        var marker = (void*)0x1234;
        var list = Gst.g_list_append(null, marker);
        if (list is null)
        {
            return false;
        }

        try
        {
            return DataOfListNode(list) == marker && NextListNode(list) is null;
        }
        finally
        {
            Gst.g_list_free(list);
        }
    }

    /// <summary>
    /// Confirms <c>GstMapInfo</c> by mapping a buffer of a size chosen here: if
    /// the fields were in the wrong place the size would not read back.
    /// </summary>
    private static bool ProbeMapInfo()
    {
        const nuint size = 4096;

        var buffer = Gst.gst_buffer_new_allocate(null, size, null);
        if (buffer is null)
        {
            return false;
        }

        try
        {
            var info = stackalloc byte[MapInfoSize];
            new Span<byte>(info, MapInfoSize).Clear();
            if (Gst.gst_buffer_map(buffer, info, (int)GstMapFlags.Read) == 0)
            {
                return false;
            }

            try
            {
                return *(nuint*)(info + MapInfoSizeField) == size
                       && *(byte**)(info + MapInfoData) is not null;
            }
            finally
            {
                Gst.gst_buffer_unmap(buffer, info);
            }
        }
        finally
        {
            Gst.gst_mini_object_unref(buffer);
        }
    }

    /// <summary>Confirms <c>GError</c> against one built here.</summary>
    private static bool ProbeError()
    {
        const int code = 0x5A5A;
        const string text = "mediatoolkitnet-probe";

        Span<byte> domainScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> textScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var domain = new Utf8Scoped("mediatoolkitnet", domainScratch);
        using var message = new Utf8Scoped(text, textScratch);

        var error = Gst.g_error_new_literal(Gst.g_quark_from_string(domain.Pointer), code, message.Pointer);
        if (error is null)
        {
            return false;
        }

        try
        {
            return *(int*)((byte*)error + ErrorCode) == code && MessageOfError(error) == text;
        }
        finally
        {
            Gst.g_error_free(error);
        }
    }
}
