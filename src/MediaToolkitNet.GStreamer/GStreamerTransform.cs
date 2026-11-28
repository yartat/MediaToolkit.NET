#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer;

/// <summary>
/// An element written in .NET that changes buffers where they are, built on <c>GstBaseTransform</c>. Derive from it and
/// <see cref="Register"/> it under a name; pipelines then use the name like any other element's.
/// </summary>
/// <remarks>
/// <para>
/// There is one instance for each element in each pipeline, made when it first has work and disposed when the element
/// is. Its methods run on the pipeline's streaming thread, one call at a time, and the stream waits for them.
/// </para>
/// <para>
/// The element takes and gives the same format and cannot change a buffer's size. A registration lasts as long as the
/// process: GLib has no way to take a type back.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public abstract unsafe class GStreamerTransform : IDisposable
{
    private const int FlowOk = 0;
    private const int FlowError = -5;

    // GST_BASE_TRANSFORM_FLOW_DROPPED, which is GST_FLOW_CUSTOM_SUCCESS.
    private const int FlowDropped = 100;

    private static readonly Lock Gate = new();
    private static readonly ConcurrentDictionary<nuint, Registration> Types = new();
    private static readonly ConcurrentDictionary<nint, (GStreamerTransform Instance, Registration Of)> Instances = new();

    /// <summary>Called when the format is settled, before the first buffer, and again if it changes.</summary>
    /// <param name="caps">The format of what comes in and goes out.</param>
    public virtual void SetCaps(GStreamerCaps caps)
    {
    }

    /// <summary>Changes one buffer.</summary>
    /// <param name="data">The buffer's bytes, to read and write; valid for the call only.</param>
    /// <param name="timestamp">Its presentation time, or null.</param>
    /// <returns>True to pass it on, false to drop it.</returns>
    public abstract bool Process(Span<byte> data, TimeSpan? timestamp);

    /// <summary>Called when the element goes away.</summary>
    public virtual void Dispose() => GC.SuppressFinalize(this);

    /// <summary>Makes an element of a name available to this process.</summary>
    /// <param name="name">The name pipelines use, such as <c>myfilter</c>.</param>
    /// <param name="caps">What it takes and gives, such as <c>video/x-raw,format=BGRA</c>.</param>
    /// <param name="create">Makes the instance of one element.</param>
    /// <param name="onError">
    /// Told what an instance threw. The element then fails the stream, which the pipeline reports as an error of the source.
    /// </param>
    /// <exception cref="MediaToolkitNetException">
    /// The name is taken, the caps are not caps, or GStreamer's base class is not laid out as expected.
    /// </exception>
    public static void Register(string name, string caps, Func<GStreamerTransform> create, Action<Exception>? onError = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(caps);
        ArgumentNullException.ThrowIfNull(create);
        Gst.EnsureLoaded();

        var parsed = GStreamerCaps.Parse(caps);
        lock (Gate)
        {
            if (GStreamerElements.Exists(name))
            {
                throw Refused($"there is an element named {name} already.");
            }

            var layout = BaseTransformLayout.Require();
            var typeName = "MediaToolkitNet_" + new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')]);
            Span<byte> typeScratch = stackalloc byte[Utf8Scoped.StackThreshold];
            Span<byte> nameScratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var typeUtf8 = new Utf8Scoped(typeName, typeScratch);
            using var nameUtf8 = new Utf8Scoped(name, nameScratch);

            if (Gst.g_type_from_name(typeUtf8.Pointer) != 0)
            {
                throw Refused($"a type for {name} exists already.");
            }

            var type = layout.Register(
                layout.Parent, typeUtf8.Pointer, layout.ClassSize, &ClassInit, layout.InstanceSize, null, 0);
            Types[type] = new Registration(name, parsed.ToString(), create, onError);
            if (type == 0 || layout.ElementRegister(null, nameUtf8.Pointer, 0, type) == 0)
            {
                Types.TryRemove(type, out _);
                throw Refused($"GStreamer would not register {name}.");
            }
        }
    }

    private static MediaToolkitNetException Refused(string message) => new(GstConstants.BackendName, message, 0);

    // Runs once for each registered type, when GLib builds its class: the description, the pads and the two methods.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ClassInit(void* klass, void* classData)
    {
        try
        {
            var registration = Types[*(nuint*)klass];
            var layout = BaseTransformLayout.Require();

            Span<byte> longName = stackalloc byte[Utf8Scoped.StackThreshold];
            Span<byte> classification = stackalloc byte[16];
            Span<byte> description = stackalloc byte[64];
            Span<byte> author = stackalloc byte[32];
            using var longUtf8 = new Utf8Scoped(registration.Name, longName);
            using var classUtf8 = new Utf8Scoped("Filter", classification);
            using var descriptionUtf8 = new Utf8Scoped("An element written in .NET", description);
            using var authorUtf8 = new Utf8Scoped("MediaToolkit.NET", author);
            layout.SetMetadata(klass, longUtf8.Pointer, classUtf8.Pointer, descriptionUtf8.Pointer, authorUtf8.Pointer);

            AddPad(layout, klass, "sink", GstPadDirection.Sink, registration.Caps);
            AddPad(layout, klass, "src", GstPadDirection.Source, registration.Caps);

            *(void**)((byte*)klass + layout.SetCapsSlot) = (delegate* unmanaged[Cdecl]<void*, void*, void*, int>)&OnSetCaps;
            *(void**)((byte*)klass + layout.TransformSlot) = (delegate* unmanaged[Cdecl]<void*, void*, int>)&OnTransform;
        }
        catch
        {
            // Nothing may throw into GLib; the element would then pass buffers through unchanged.
        }
    }

    private static void AddPad(BaseTransformLayout layout, void* klass, string name, GstPadDirection direction, string caps)
    {
        Span<byte> scratch = stackalloc byte[8];
        using var utf8 = new Utf8Scoped(name, scratch);
        var native = GStreamerCaps.Create(caps);
        layout.AddPadTemplate(klass, layout.PadTemplateNew(utf8.Pointer, (int)direction, (int)GstPadPresence.Always, native));
        Gst.gst_mini_object_unref(native);
    }

    private static (GStreamerTransform Instance, Registration Of) InstanceOf(void* element)
    {
        if (Instances.TryGetValue((nint)element, out var existing))
        {
            return existing;
        }

        // The class of an instance is its first field, and the type the first of the class.
        var registration = Types[**(nuint**)element];
        var entry = (registration.Create(), registration);
        Instances[(nint)element] = entry;
        BaseTransformLayout.Require().WeakRef(element, &OnFinalized, null);
        return entry;
    }

    private static void Report(void* element, Exception exception)
    {
        try
        {
            Types[**(nuint**)element].OnError?.Invoke(exception);
        }
        catch
        {
            // A handler of errors that throws has nowhere further to go.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnSetCaps(void* element, void* input, void* output)
    {
        try
        {
            InstanceOf(element).Instance.SetCaps(GStreamerCaps.From(input));
            return 1;
        }
        catch (Exception ex)
        {
            Report(element, ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnTransform(void* element, void* buffer)
    {
        try
        {
            var instance = InstanceOf(element).Instance;
            var map = stackalloc byte[GstLayout.MapInfoSize];
            new Span<byte>(map, GstLayout.MapInfoSize).Clear();
            if (Gst.gst_buffer_map(buffer, map, (int)(GstMapFlags.Read | GstMapFlags.Write)) == 0)
            {
                return FlowError;
            }

            try
            {
                var data = new Span<byte>(
                    *(byte**)(map + GstLayout.MapInfoData), checked((int)*(nuint*)(map + GstLayout.MapInfoSizeField)));
                var pts = GstLayout.PtsOfBuffer(buffer);
                TimeSpan? timestamp = pts == GstConstants.ClockTimeNone ? null : new TimeSpan((long)(pts / GstConstants.NanosecondsPerTick));
                return instance.Process(data, timestamp) ? FlowOk : FlowDropped;
            }
            finally
            {
                Gst.gst_buffer_unmap(buffer, map);
            }
        }
        catch (Exception ex)
        {
            Report(element, ex);
            return FlowError;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFinalized(void* data, void* element)
    {
        try
        {
            // The element is gone: its pointer is only a key here.
            if (Instances.TryRemove((nint)element, out var entry))
            {
                try
                {
                    entry.Instance.Dispose();
                }
                catch (Exception ex)
                {
                    entry.Of.OnError?.Invoke(ex);
                }
            }
        }
        catch
        {
            // Nothing may throw into GLib.
        }
    }

    private sealed record Registration(string Name, string Caps, Func<GStreamerTransform> Create, Action<Exception>? OnError);
}
