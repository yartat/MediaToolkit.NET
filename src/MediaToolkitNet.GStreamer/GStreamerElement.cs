#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer;

/// <summary>
/// One element inside a <see cref="GStreamerPipeline"/>.
/// </summary>
/// <remarks>
/// The wrapper owns one reference to the underlying <c>GstElement</c>. Elements
/// handed out by the pipeline are owned by it and released with it.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerElement : IDisposable
{
    private readonly List<GStreamerSignalConnection> _connections = [];
    private void* _element;
    private bool _disposed;

    internal GStreamerElement(void* element, string name)
    {
        _element = element;
        Name = name;
    }

    /// <summary>Raw <c>GstElement</c> pointer.</summary>
    public void* Handle => _element;

    /// <summary>The name the element carries inside the pipeline.</summary>
    public string Name { get; }

    /// <summary>The element's current state.</summary>
    public GstState State
    {
        get
        {
            EnsureAlive();
            int current;
            int pending;
            Gst.gst_element_get_state(_element, &current, &pending, 0);
            return (GstState)current;
        }
    }

    /// <summary>
    /// Sets one property from its string form, the way a <c>gst-launch</c>
    /// description does: GObject converts the text to whatever the property is.
    /// </summary>
    public void Set(string property, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(property);
        ArgumentNullException.ThrowIfNull(value);
        EnsureAlive();
        Gst.SetProperty(_element, property, value);
    }

    /// <summary>
    /// True when the element has a property of that name. <see cref="Set"/>
    /// on a missing property only logs a GLib warning, so this is how a caller
    /// finds out beforehand.
    /// </summary>
    public bool HasProperty(string property)
    {
        ArgumentException.ThrowIfNullOrEmpty(property);
        EnsureAlive();
        GstLayout.Require();

        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(property, scratch);
        return Gst.g_object_class_find_property(GstLayout.ClassOf(_element), utf8.Pointer) is not null;
    }

    /// <summary>
    /// Sets a property from a managed value of the type the property has: <see cref="bool"/>, an integer, a
    /// <see cref="float"/> or <see cref="double"/>, a <see cref="string"/>, or for an enum or flags property the
    /// integer value (or its text, as <see cref="Set"/> takes it).
    /// </summary>
    /// <exception cref="MediaToolkitNetException">
    /// The element has no such property, the value cannot be converted to its type, or the property holds an object or
    /// a structure, which this call does not handle. The property is left as it was.
    /// </exception>
    public void SetValue(string property, object value)
    {
        ArgumentException.ThrowIfNullOrEmpty(property);
        ArgumentNullException.ThrowIfNull(value);
        EnsureAlive();

        var type = PropertyType(property);
        var storage = stackalloc byte[GstSignals.ValueSize];
        new Span<byte>(storage, GstSignals.ValueSize).Clear();
        Gst.g_value_init(storage, type);
        try
        {
            if (value is string text && (uint)Gst.g_type_fundamental(type) is GstSignals.TypeEnum or GstSignals.TypeFlags)
            {
                // Enums and flags are named by text more often than by number; GStreamer parses either.
                Gst.SetProperty(_element, property, text);
                return;
            }

            try
            {
                GstSignals.Store(storage, type, value);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or NotSupportedException)
            {
                throw new MediaToolkitNetException(
                    GstConstants.BackendName,
                    $"the property {property} of {Name} is a {Utf8.ToManagedOrEmpty(Gst.g_type_name(type))}, and {value} ({value.GetType().Name}) does not fit: {ex.Message}",
                    0);
            }

            Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var utf8 = new Utf8Scoped(property, scratch);
            Gst.g_object_set_property(_element, utf8.Pointer, storage);
        }
        finally
        {
            Gst.g_value_unset(storage);
        }
    }

    /// <summary>
    /// Reads a property as a managed value: <see cref="bool"/>, <see cref="int"/>, <see cref="uint"/>, <see cref="long"/>,
    /// <see cref="ulong"/>, <see cref="float"/>, <see cref="double"/> or <see cref="string"/>; an enum is its
    /// <see cref="int"/> value and flags are a <see cref="uint"/>.
    /// </summary>
    /// <exception cref="MediaToolkitNetException">
    /// The element has no such property, or it holds an object or a structure: a reference read here could be
    /// released by the element before the caller used it.
    /// </exception>
    public object? GetValue(string property)
    {
        ArgumentException.ThrowIfNullOrEmpty(property);
        EnsureAlive();

        var type = PropertyType(property);
        var fundamental = (uint)Gst.g_type_fundamental(type);
        if (fundamental is GstSignals.TypeObject or GstSignals.TypeBoxed or GstSignals.TypeParam)
        {
            throw new MediaToolkitNetException(
                GstConstants.BackendName,
                $"the property {property} of {Name} holds a {Utf8.ToManagedOrEmpty(Gst.g_type_name(type))}, which GetValue does not read.",
                0);
        }

        var storage = stackalloc byte[GstSignals.ValueSize];
        new Span<byte>(storage, GstSignals.ValueSize).Clear();
        Gst.g_value_init(storage, type);
        try
        {
            Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var utf8 = new Utf8Scoped(property, scratch);
            Gst.g_object_get_property(_element, utf8.Pointer, storage);
            return GstSignals.ToManaged(storage);
        }
        finally
        {
            Gst.g_value_unset(storage);
        }
    }

    /// <summary>Sets a property that holds an object, such as <c>video-sink</c> of <c>playbin</c>; the property takes its own reference.</summary>
    internal void SetObject(string property, void* value)
    {
        EnsureAlive();
        var type = PropertyType(property);
        var storage = stackalloc byte[GstSignals.ValueSize];
        new Span<byte>(storage, GstSignals.ValueSize).Clear();
        Gst.g_value_init(storage, type);
        try
        {
            Gst.g_value_set_object(storage, value);
            Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var utf8 = new Utf8Scoped(property, scratch);
            Gst.g_object_set_property(_element, utf8.Pointer, storage);
        }
        finally
        {
            Gst.g_value_unset(storage);
        }
    }

    /// <summary><see cref="GetValue"/> cast to <typeparamref name="T"/>.</summary>
    public T Get<T>(string property) => (T)GetValue(property)!;

    // The type of a property is the type of its default value; GParamSpec::value_type has no accessor.
    private nuint PropertyType(string property)
    {
        GstLayout.Require();
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(property, scratch);
        var spec = Gst.g_object_class_find_property(GstLayout.ClassOf(_element), utf8.Pointer);
        if (spec is null)
        {
            throw new MediaToolkitNetException(GstConstants.BackendName, $"the element {Name} has no property named {property}.", 0);
        }

        return *(nuint*)Gst.g_param_spec_get_default_value(spec);
    }

    /// <summary>Raised when a signal handler throws, which must not end the process (it runs on a thread GStreamer owns).</summary>
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <summary>
    /// Connects a handler to one of the element's signals, such as <c>pad-added</c> on a demuxer, <c>handoff</c> on
    /// <c>fakesink</c>, <c>need-data</c> on <c>appsrc</c> or <c>notify::volume</c> for the change of a property.
    /// </summary>
    /// <param name="signal">The signal's name, with <c>::detail</c> if it takes one.</param>
    /// <param name="handler">Called on the thread that emits the signal.</param>
    /// <param name="after">Whether to run after the element's own handler rather than before.</param>
    /// <returns>The connection; dispose it to disconnect. Disposing the element or its pipeline disconnects it too.</returns>
    /// <exception cref="MediaToolkitNetException">The element has no such signal.</exception>
    public GStreamerSignalConnection Connect(string signal, GStreamerSignalHandler handler, bool after = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(signal);
        ArgumentNullException.ThrowIfNull(handler);
        EnsureAlive();

        // g_signal_connect on a name the type lacks only logs a critical warning, so the type is asked first.
        var separator = signal.IndexOf("::", StringComparison.Ordinal);
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var plain = new Utf8Scoped(separator < 0 ? signal : signal[..separator], scratch);
        var type = Gst.g_type_from_name(Gst.g_type_name_from_instance(_element));
        if (Gst.g_signal_lookup(plain.Pointer, type) == 0)
        {
            throw new MediaToolkitNetException(GstConstants.BackendName, $"the element {Name} has no signal named {signal}.", 0);
        }

        var connection = new GStreamerSignalConnection(_element, signal, handler, ex => SafeEvents.RaiseFailed(
            Failed, this, new MediaToolkitNetException(GstConstants.BackendName, $"a handler of {signal} on {Name} threw: {ex.Message}", 0)));
        connection.Connect(after);
        lock (_connections)
        {
            _connections.Add(connection);
        }

        return connection;
    }

    /// <summary>A static pad of the element, as a reference the caller owns, or null.</summary>
    internal void* GetStaticPad(string name)
    {
        EnsureAlive();
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(name, scratch);
        return Gst.gst_element_get_static_pad(_element, utf8.Pointer);
    }

    /// <summary>True when the element is an instance of a GType, an interface included.</summary>
    internal bool Is(nuint type)
    {
        EnsureAlive();
        return Gst.g_type_check_instance_is_a(_element, type) != 0;
    }

    /// <inheritdoc />
    public override string ToString() => Name;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Handlers go before the element does: a connection holds no reference of its own to what it is connected to.
        lock (_connections)
        {
            foreach (var connection in _connections)
            {
                connection.Detach();
            }

            _connections.Clear();
        }

        if (_element is not null)
        {
            Gst.gst_object_unref(_element);
            _element = null;
        }
    }

    private void EnsureAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
}
