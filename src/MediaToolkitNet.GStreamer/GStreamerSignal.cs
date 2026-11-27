#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer;

/// <summary>Handles one emission of a signal. It runs on the thread that emitted it, often a streaming thread.</summary>
/// <param name="args">What the signal was emitted with, and where a return value is set.</param>
public delegate void GStreamerSignalHandler(GStreamerSignalArgs args);

/// <summary>
/// What a signal was emitted with: the values after the instance, converted, and the return value the handler may set.
/// </summary>
/// <remarks>
/// Values are <see cref="bool"/>, <see cref="int"/>, <see cref="uint"/>, <see cref="long"/>, <see cref="ulong"/>,
/// <see cref="float"/>, <see cref="double"/> and <see cref="string"/> as they are; enums are <see cref="int"/> and flags
/// <see cref="uint"/>; caps are <see cref="GStreamerCaps"/> and a structure is a <see cref="GStreamerStructure"/>, copies
/// to keep; objects, pointers and other boxed values (a pad, a buffer, a sample) are <see cref="nint"/>
/// and belong to the emitter for the length of the call. Anything a handler wants to keep it must copy or take its own
/// reference to.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerSignalArgs
{
    private readonly nuint[] _types;

    internal GStreamerSignalArgs(string signal, nint instance, IReadOnlyList<object?> values, nuint[] types)
    {
        Signal = signal;
        Instance = instance;
        Values = values;
        _types = types;
    }

    /// <summary>The signal, with its detail if the connection named one.</summary>
    public string Signal { get; }

    /// <summary>The object that emitted it.</summary>
    public nint Instance { get; }

    /// <summary>The values the signal carries after its instance.</summary>
    public IReadOnlyList<object?> Values { get; }

    /// <summary>
    /// What the handler answers, for a signal that returns something: a <see cref="bool"/>, an <see cref="int"/> for an
    /// integer or an enum such as <c>GstFlowReturn</c>, or a <see cref="long"/>. Left unset, the signal returns its
    /// default (false, 0, which is <c>GST_FLOW_OK</c>).
    /// </summary>
    public object? ReturnValue { get; set; }

    /// <summary>The value at <paramref name="index"/> as a <typeparamref name="T"/>.</summary>
    public T Get<T>(int index) => (T)Values[index]!;

    /// <summary>
    /// The name of the object or property at <paramref name="index"/>: the name of a pad, element or bin, or of the
    /// property a <c>notify</c> signal is about. <see langword="null"/> for a value that has no name.
    /// </summary>
    public string? NameOf(int index)
    {
        if (Values[index] is not nint pointer || pointer == 0)
        {
            return null;
        }

        var fundamental = Gst.g_type_fundamental(_types[index]);
        if (fundamental == GstSignals.TypeParam)
        {
            return Utf8.ToManagedOrEmpty(Gst.g_param_spec_get_name((void*)pointer));
        }

        return fundamental == GstSignals.TypeObject && Gst.g_type_check_instance_is_a((void*)pointer, Gst.gst_object_get_type()) != 0
            ? Gst.TakeString(Gst.gst_object_get_name((void*)pointer))
            : null;
    }
}

/// <summary>A handler connected to a signal of an element. Disposing it disconnects the handler.</summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerSignalConnection : IDisposable
{
    private readonly GStreamerSignalHandler _handler;
    private readonly Action<Exception> _failed;
    private readonly Lock _gate = new();
    private void* _instance;
    private nuint _id;

    internal GStreamerSignalConnection(void* instance, string signal, GStreamerSignalHandler handler, Action<Exception> failed)
    {
        _instance = instance;
        _handler = handler;
        _failed = failed;
        Signal = signal;
    }

    /// <summary>The signal, with its detail if one was named.</summary>
    public string Signal { get; }

    /// <summary>True until the handler is disconnected.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _instance is not null;
            }
        }
    }

    // A closure with no callback of its own: GLib hands every emission to the meta marshaller, which converts the
    // GValues and calls the managed handler. The GCHandle travels as the marshal data and is freed when the closure is.
    internal void Connect(bool after)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var detailed = new Utf8Scoped(Signal, scratch);

        // g_cclosure_new refuses a null callback; this one is never called, the meta marshaller takes every emission.
        var closure = Gst.g_cclosure_new((delegate* unmanaged[Cdecl]<void*, void*, void>)&GstSignals.Finalized, null, null);
        var handle = GCHandle.Alloc(this);
        var data = (void*)GCHandle.ToIntPtr(handle);
        Gst.g_closure_set_meta_marshal(closure, data, (delegate* unmanaged[Cdecl]<void*, byte*, uint, byte*, void*, void*, void>)&GstSignals.Marshal);
        Gst.g_closure_add_finalize_notifier(closure, data, (delegate* unmanaged[Cdecl]<void*, void*, void>)&GstSignals.Finalized);
        _id = Gst.g_signal_connect_closure(_instance, detailed.Pointer, closure, after ? 1 : 0);
        if (_id == 0)
        {
            // The closure was sunk by the failed connect and is gone; so is its handle, through the notifier.
            _instance = null;
            throw new MediaToolkitNetException(GstConstants.BackendName, $"the signal {Signal} could not be connected", 0);
        }
    }

    internal void Invoke(byte* returnValue, uint count, byte* values)
    {
        // The first value is the instance; the handler sees the rest.
        var converted = new List<object?>((int)Math.Max(count - 1, 0));
        var types = new nuint[Math.Max((int)count - 1, 0)];
        for (var i = 1; i < count; i++)
        {
            var value = values + (i * GstSignals.ValueSize);
            types[i - 1] = *(nuint*)value;
            converted.Add(GstSignals.ToManaged(value));
        }

        var args = new GStreamerSignalArgs(Signal, count > 0 ? GstSignals.InstanceOf(values) : 0, converted, types);
        lock (_gate)
        {
            if (_instance is null)
            {
                return;
            }
        }

        try
        {
            _handler(args);
        }
        catch (Exception ex)
        {
            _failed(ex);
        }

        if (returnValue is not null && args.ReturnValue is { } answer)
        {
            GstSignals.SetReturn(returnValue, answer);
        }
    }

    // Called by the owner when the object the signal belongs to is going away.
    internal void Detach()
    {
        lock (_gate)
        {
            if (_instance is null)
            {
                return;
            }

            Gst.g_signal_handler_disconnect(_instance, _id);
            _instance = null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Detach();
}

/// <summary>The GObject value types and the marshaller behind <see cref="GStreamerElement.Connect"/>.</summary>
[SupportedOSPlatform("linux")]
internal static unsafe class GstSignals
{
    // sizeof(GValue): GType, then two eight-byte union slots.
    internal const int ValueSize = 24;

    // G_TYPE_MAKE_FUNDAMENTAL(n) is n << 2.
    internal const uint TypeBoolean = 5 << 2;
    internal const uint TypeInt = 6 << 2;
    internal const uint TypeUint = 7 << 2;
    internal const uint TypeLong = 8 << 2;
    internal const uint TypeUlong = 9 << 2;
    internal const uint TypeInt64 = 10 << 2;
    internal const uint TypeUint64 = 11 << 2;
    internal const uint TypeEnum = 12 << 2;
    internal const uint TypeFlags = 13 << 2;
    internal const uint TypeFloat = 14 << 2;
    internal const uint TypeDouble = 15 << 2;
    internal const uint TypeString = 16 << 2;
    internal const uint TypePointer = 17 << 2;
    internal const uint TypeBoxed = 18 << 2;
    internal const uint TypeParam = 19 << 2;
    internal const uint TypeObject = 20 << 2;

    // The instance is the first of the values; GObject keeps it in the union slot of a G_TYPE_OBJECT value.
    internal static nint InstanceOf(byte* values) => (nint)Gst.g_value_get_object(values);

    internal static object? ToManaged(byte* value)
    {
        var type = *(nuint*)value;
        if (type == Gst.gst_fraction_get_type())
        {
            return new Rational(Gst.gst_value_get_fraction_numerator(value), Gst.gst_value_get_fraction_denominator(value));
        }

        if (type == Gst.gst_value_list_get_type())
        {
            var items = new object?[Gst.gst_value_list_get_size(value)];
            for (var i = 0; i < items.Length; i++)
            {
                items[i] = GStreamerStructure.Convert(Gst.gst_value_list_get_value(value, (uint)i));
            }

            return items;
        }

        if (type == Gst.gst_value_array_get_type())
        {
            var items = new object?[Gst.gst_value_array_get_size(value)];
            for (var i = 0; i < items.Length; i++)
            {
                items[i] = GStreamerStructure.Convert(Gst.gst_value_array_get_value(value, (uint)i));
            }

            return items;
        }

        if (type == Gst.g_value_array_get_type())
        {
            // The older GValueArray, which the level element still posts.
            var array = (byte*)Gst.g_value_get_boxed(value);
            var items = new object?[array is null ? 0 : *(uint*)array];
            for (var i = 0; i < items.Length; i++)
            {
                items[i] = GStreamerStructure.Convert(*(byte**)(array + GstLayout.ValueArrayValues) + (i * ValueSize));
            }

            return items;
        }

        var fundamental = Gst.g_type_fundamental(type);
        return (uint)fundamental switch
        {
            TypeBoolean => Gst.g_value_get_boolean(value) != 0,
            TypeInt => Gst.g_value_get_int(value),
            TypeUint => Gst.g_value_get_uint(value),
            TypeLong => (long)Gst.g_value_get_long(value),
            TypeUlong => (ulong)Gst.g_value_get_ulong(value),
            TypeInt64 => Gst.g_value_get_int64(value),
            TypeUint64 => Gst.g_value_get_uint64(value),
            TypeEnum => Gst.g_value_get_enum(value),
            TypeFlags => Gst.g_value_get_flags(value),
            TypeFloat => Gst.g_value_get_float(value),
            TypeDouble => Gst.g_value_get_double(value),
            TypeString => Gst.g_value_get_string(value) is var text && text is not null ? Utf8.ToManagedOrEmpty(text) : null,
            TypeObject => (nint)Gst.g_value_get_object(value),
            TypeParam => (nint)Gst.g_value_get_param(value),
            TypePointer => (nint)Gst.g_value_get_pointer(value),
            TypeBoxed => Boxed(*(nuint*)value, Gst.g_value_get_boxed(value)),
            _ => null,
        };
    }

    // Caps and structures are copied into managed values; other boxed values stay the pointer they are.
    private static object? Boxed(nuint type, void* boxed)
    {
        if (type == Gst.gst_caps_get_type())
        {
            return boxed is null ? null : GStreamerCaps.From(boxed);
        }

        if (type == Gst.gst_structure_get_type())
        {
            return boxed is null ? null : GStreamerStructure.From(boxed);
        }

        return (nint)boxed;
    }

    // Writes a managed value into a GValue already initialised to `type`, converting as the type needs; what cannot
    // be converted throws FormatException, InvalidCastException or OverflowException for the caller to name.
    internal static void Store(byte* value, nuint type, object managed)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (type == Gst.gst_fraction_get_type() && managed is Rational fraction)
        {
            Gst.gst_value_set_fraction(value, fraction.Numerator, fraction.Denominator);
            return;
        }

        if ((uint)Gst.g_type_fundamental(type) == TypeObject && managed is GStreamerElement element)
        {
            Gst.g_value_set_object(value, element.Handle);
            return;
        }

        switch ((uint)Gst.g_type_fundamental(type))
        {
            case TypeBoolean:
                Gst.g_value_set_boolean(value, Convert.ToBoolean(managed, culture) ? 1 : 0);
                break;
            case TypeInt:
                Gst.g_value_set_int(value, Convert.ToInt32(managed, culture));
                break;
            case TypeUint:
                Gst.g_value_set_uint(value, Convert.ToUInt32(managed, culture));
                break;
            case TypeInt64:
                Gst.g_value_set_int64(value, Convert.ToInt64(managed, culture));
                break;
            case TypeUint64:
                Gst.g_value_set_uint64(value, Convert.ToUInt64(managed, culture));
                break;
            case TypeFloat:
                Gst.g_value_set_float(value, Convert.ToSingle(managed, culture));
                break;
            case TypeDouble:
                Gst.g_value_set_double(value, Convert.ToDouble(managed, culture));
                break;
            case TypeEnum:
                Gst.g_value_set_enum(value, Convert.ToInt32(managed, culture));
                break;
            case TypeFlags:
                Gst.g_value_set_flags(value, Convert.ToUInt32(managed, culture));
                break;
            case TypeString:
                {
                    Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
                    using var text = new Utf8Scoped(Convert.ToString(managed, culture)!, scratch);
                    Gst.g_value_set_string(value, text.Pointer);
                    break;
                }

            case TypeBoxed when type == Gst.gst_caps_get_type() && managed is GStreamerCaps caps:
                {
                    var native = GStreamerCaps.Create(caps.ToString());
                    Gst.g_value_set_boxed(value, native);
                    Gst.gst_mini_object_unref(native);
                    break;
                }

            case TypeBoxed when type == Gst.gst_structure_get_type() && managed is GStreamerStructure structure:
                {
                    var native = GStreamerStructure.Create(structure.ToString());
                    Gst.g_value_set_boxed(value, native);
                    Gst.gst_structure_free(native);
                    break;
                }

            default:
                throw new NotSupportedException("this kind of property cannot be set from a managed value");
        }
    }

    internal static void SetReturn(byte* returnValue, object answer)
    {
        var type = *(nuint*)returnValue;
        if (type == 0)
        {
            return;
        }

        switch ((uint)Gst.g_type_fundamental(type))
        {
            case TypeBoolean:
                Gst.g_value_set_boolean(returnValue, Convert.ToBoolean(answer) ? 1 : 0);
                break;
            case TypeInt:
                Gst.g_value_set_int(returnValue, Convert.ToInt32(answer));
                break;
            case TypeInt64:
                Gst.g_value_set_int64(returnValue, Convert.ToInt64(answer));
                break;
            case TypeEnum:
                Gst.g_value_set_enum(returnValue, Convert.ToInt32(answer));
                break;
        }
    }

    // GClosureMarshal: (GClosure *, GValue *return_value, guint n_param_values, const GValue *param_values,
    // gpointer invocation_hint, gpointer marshal_data), which a meta marshaller is given in place of the closure's own.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void Marshal(void* closure, byte* returnValue, uint count, byte* values, void* hint, void* data)
    {
        try
        {
            ((GStreamerSignalConnection)GCHandle.FromIntPtr((nint)data).Target!).Invoke(returnValue, count, values);
        }
        catch
        {
            // Nothing may throw into GLib.
        }
    }

    // GClosureNotify: the closure is gone, so the connection's handle can be too.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void Finalized(void* data, void* closure)
    {
        try
        {
            GCHandle.FromIntPtr((nint)data).Free();
        }
        catch
        {
            // A handle freed twice has nothing left to free.
        }
    }
}
