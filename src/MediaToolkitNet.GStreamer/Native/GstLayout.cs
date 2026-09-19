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
/// <c>GST_MESSAGE_TYPE</c>, <c>GST_BUFFER_PTS</c>, the contents of
/// <c>GstMapInfo</c> and the links of a <c>GList</c>. Those four are the whole
/// list, and none of them is hard-coded: each is found by building an object
/// whose contents are known and looking for the value in it.
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

    /// <summary>Offset of <c>GstBuffer::pts</c>, found by <see cref="Validate"/>.</summary>
    public static int BufferPts { get; private set; } = -1;

    /// <summary>True once every layout below was confirmed against a real object.</summary>
    public static bool Verified { get; private set; }

    /// <summary>Reads <c>GST_MESSAGE_TYPE</c>.</summary>
    public static GstMessageType TypeOfMessage(void* message) =>
        (GstMessageType)(*(uint*)((byte*)message + MessageType));

    /// <summary>Reads <c>GST_BUFFER_PTS</c>, in nanoseconds.</summary>
    public static ulong PtsOfBuffer(void* buffer) => *(ulong*)((byte*)buffer + BufferPts);

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
                "the GStreamer struct layout could not be confirmed against this build, so the backend is disabled.");
        }
    }

    /// <summary>
    /// Runs every probe. Called once, after <c>gst_init_check</c> has succeeded.
    /// </summary>
    internal static void Validate()
    {
        MessageType = ProbeMessageType();
        BufferPts = ProbeBufferPts();
        Verified = MessageType >= 0 && BufferPts >= 0 && ProbeList() && ProbeMapInfo() && ProbeError();
    }

    /// <summary>
    /// Finds <c>GstMessage::type</c> by posting a message of a type no element
    /// ever posts.
    /// </summary>
    /// <remarks>
    /// GST_MESSAGE_APPLICATION is 1&lt;&lt;22, a value that does not occur in the
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
