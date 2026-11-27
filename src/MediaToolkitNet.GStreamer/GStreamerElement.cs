#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.InteropServices;
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
    private readonly List<GStreamerProbe> _probes = [];
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
    /// Links this element's output to <paramref name="downstream"/>'s input. Without pad names GStreamer picks a
    /// compatible pair, requesting one from an element such as <c>tee</c> that makes them on demand. Name the pad to link
    /// one that <c>pad-added</c> announced.
    /// </summary>
    /// <param name="downstream">The element to feed, in the same pipeline.</param>
    /// <param name="pad">The output pad of this element, or null.</param>
    /// <param name="downstreamPad">The input pad of <paramref name="downstream"/>, or null.</param>
    /// <exception cref="MediaToolkitNetException">
    /// The two cannot be linked: no such pad, a pad already linked, or no format both accept.
    /// </exception>
    public void Link(GStreamerElement downstream, string? pad = null, string? downstreamPad = null)
    {
        ArgumentNullException.ThrowIfNull(downstream);
        EnsureAlive();
        downstream.EnsureAlive();

        Span<byte> padScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> downstreamScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var padUtf8 = new Utf8Scoped(pad, padScratch);
        using var downstreamUtf8 = new Utf8Scoped(downstreamPad, downstreamScratch);

        if (Gst.gst_element_link_pads(_element, padUtf8.Pointer, downstream._element, downstreamUtf8.Pointer) == 0)
        {
            throw new MediaToolkitNetException(
                GstConstants.BackendName,
                $"could not link {Name}{(pad is null ? "" : ":" + pad)} to {downstream.Name}{(downstreamPad is null ? "" : ":" + downstreamPad)}.",
                0);
        }
    }

    /// <summary>
    /// Undoes every link from this element to <paramref name="downstream"/>; elements that are not linked are left as
    /// they are. A pad requested from an element such as <c>tee</c> stays on it until <see cref="ReleasePad"/>. In a
    /// playing pipeline do it from an idle probe on the pad (<see cref="AddProbe"/>).
    /// </summary>
    /// <param name="downstream">The element this one feeds.</param>
    public void Unlink(GStreamerElement downstream)
    {
        ArgumentNullException.ThrowIfNull(downstream);
        EnsureAlive();
        downstream.EnsureAlive();
        Gst.gst_element_unlink(_element, downstream._element);
    }

    /// <summary>
    /// Watches, drops or blocks what passes one of the element's pads. With <see cref="GstPadProbeType.Idle"/> the handler
    /// runs as soon as nothing is passing, which is when a pad of a playing pipeline can be unlinked safely; it can then
    /// run before this call returns.
    /// </summary>
    /// <param name="pad">The pad's name; a request pad has to exist already.</param>
    /// <param name="mask">What to be called for.</param>
    /// <param name="handler">Called on the thread that carries the data; a handler that throws is reported through <see cref="Failed"/>.</param>
    /// <returns>The probe; dispose it to take it off. Disposing the element or its pipeline does so too.</returns>
    /// <exception cref="MediaToolkitNetException">The element has no such pad.</exception>
    public GStreamerProbe AddProbe(string pad, GstPadProbeType mask, GStreamerProbeHandler handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(pad);
        ArgumentNullException.ThrowIfNull(handler);
        var handle = RequirePad(pad);
        var probe = GStreamerProbe.Install(handle, mask, handler, ex => SafeEvents.RaiseFailed(
            Failed, this, new MediaToolkitNetException(GstConstants.BackendName, $"a probe on {Name}:{pad} threw: {ex.Message}", 0)));
        lock (_probes)
        {
            _probes.Add(probe);
        }

        return probe;
    }

    /// <summary>
    /// Probes the buffers passing one pad and reads their bytes: the handler may let a buffer go on, drop it, or take
    /// the probe off. The bytes cannot be changed.
    /// </summary>
    /// <param name="pad">The pad's name.</param>
    /// <param name="handler">Called on the streaming thread for each buffer.</param>
    /// <returns>The probe; dispose it to take it off.</returns>
    /// <exception cref="MediaToolkitNetException">The element has no such pad.</exception>
    public GStreamerProbe AddDataProbe(string pad, GStreamerDataProbeHandler handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(pad);
        ArgumentNullException.ThrowIfNull(handler);
        var probe = GStreamerProbe.Install(RequirePad(pad), handler, ex => SafeEvents.RaiseFailed(
            Failed, this, new MediaToolkitNetException(GstConstants.BackendName, $"a probe on {Name}:{pad} threw: {ex.Message}", 0)));
        lock (_probes)
        {
            _probes.Add(probe);
        }

        return probe;
    }

    /// <summary>
    /// Sends an event of the application's own through the pipeline, starting at this element. A probe on a pad it
    /// passes sees the fields in <see cref="GStreamerProbeInfo.Details"/>.
    /// </summary>
    /// <param name="data">What the event carries.</param>
    /// <param name="type">One of the custom kinds, which says which way it goes.</param>
    /// <returns>False when no element said it handled it; the event may have travelled all the same.</returns>
    /// <exception cref="ArgumentException">The type is not a custom one.</exception>
    public bool SendEvent(GStreamerStructure data, GstEventType type = GstEventType.CustomDownstream)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (type is not (GstEventType.CustomUpstream or GstEventType.CustomDownstream
            or GstEventType.CustomDownstreamOutOfBand or GstEventType.CustomBoth))
        {
            throw new ArgumentException($"{type} is not a custom event.", nameof(type));
        }

        EnsureAlive();
        return Send(data.ToString(), type);
    }

    /// <summary>
    /// Asks the encoder at or before this element to make the next frame a key frame, with its headers, as for a viewer
    /// who has just joined a stream.
    /// </summary>
    /// <returns>False when nothing took the request.</returns>
    public bool ForceKeyFrame()
    {
        EnsureAlive();

        // What gst_video_event_new_upstream_force_key_unit builds; no running time means as soon as possible.
        return Send(
            "GstForceKeyUnit, running-time=(guint64)18446744073709551615, all-headers=(boolean)true, count=(uint)0",
            GstEventType.CustomUpstream);
    }

    private bool Send(string structure, GstEventType type)
    {
        var native = GStreamerStructure.Create(structure);
        return Gst.gst_element_send_event(_element, Gst.gst_event_new_custom((int)type, native)) != 0;
    }

    /// <summary>
    /// Makes a numeric property follow values over stream time: a fade of <c>volume</c> on the <c>volume</c> element, for
    /// one. It takes the place of an earlier animation of the same property.
    /// </summary>
    /// <param name="property">A property the element marks controllable.</param>
    /// <param name="points">The values at their stream times, in the property's own units.</param>
    /// <param name="interpolation">How it moves between them.</param>
    /// <exception cref="MediaToolkitNetException">There is no such property, or it cannot be animated.</exception>
    /// <exception cref="NotSupportedException"><c>libgstcontroller</c> is not installed.</exception>
    public void Animate(string property, IReadOnlyList<(TimeSpan Time, double Value)> points, GstInterpolation interpolation = GstInterpolation.Linear)
    {
        EnsureAlive();
        Animate(_element, Name, property, points, interpolation);
    }

    /// <summary><see cref="Animate(string, IReadOnlyList{ValueTuple{TimeSpan, double}}, GstInterpolation)"/> for a property of a pad, such as <c>alpha</c> of a <c>compositor</c> input.</summary>
    public void AnimatePad(string pad, string property, IReadOnlyList<(TimeSpan Time, double Value)> points, GstInterpolation interpolation = GstInterpolation.Linear)
    {
        ArgumentException.ThrowIfNullOrEmpty(pad);
        var handle = RequirePad(pad);
        try
        {
            Animate(handle, $"{Name}:{pad}", property, points, interpolation);
        }
        finally
        {
            Gst.gst_object_unref(handle);
        }
    }

    /// <summary>Stops animating a property, which keeps the value it has then.</summary>
    /// <returns>False when it was not animated.</returns>
    public bool ClearAnimation(string property)
    {
        ArgumentException.ThrowIfNullOrEmpty(property);
        EnsureAlive();
        return Unbind(_element, property);
    }

    /// <summary>Chooses which input a selecting element such as <c>input-selector</c> passes on.</summary>
    /// <param name="pad">The input pad's name; it has to exist.</param>
    /// <exception cref="MediaToolkitNetException">There is no such pad, or the element selects none.</exception>
    public void SetActivePad(string pad)
    {
        ArgumentException.ThrowIfNullOrEmpty(pad);
        var handle = RequirePad(pad);
        try
        {
            SetObject("active-pad", handle);
        }
        finally
        {
            Gst.gst_object_unref(handle);
        }
    }

    private static void Animate(
        void* target, string label, string property, IReadOnlyList<(TimeSpan Time, double Value)> points, GstInterpolation interpolation)
    {
        ArgumentException.ThrowIfNullOrEmpty(property);
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfZero(points.Count, nameof(points));
        GstLayout.Require();

        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(property, scratch);
        var spec = Gst.g_object_class_find_property(GstLayout.ClassOf(target), utf8.Pointer);
        if (spec is null || (*(uint*)((byte*)spec + GstLayout.ParamSpecFlags) & GstLayout.ParamControllable) == 0)
        {
            throw Refused($"{label} has no property named {property} that can be animated.");
        }

        Unbind(target, property);
        var mode = interpolation switch
        {
            GstInterpolation.Step => "none",
            GstInterpolation.Linear => "linear",
            GstInterpolation.Cubic => "cubic",
            _ => "cubic-monotonic",
        };
        Gst.BindControl(target, property, mode, points);
    }

    private static bool Unbind(void* target, string property)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(property, scratch);
        var binding = Gst.gst_object_get_control_binding(target, utf8.Pointer);
        if (binding is null)
        {
            return false;
        }

        Gst.gst_object_remove_control_binding(target, binding);
        Gst.gst_object_unref(binding);
        return true;
    }

    /// <summary>
    /// Sends the end of the stream into one input pad, so that what follows it finishes its work while the rest of the
    /// pipeline goes on: how a recording branch is closed. Unlink the pad first.
    /// </summary>
    /// <param name="pad">The input pad.</param>
    /// <returns>False when the pad did not take it.</returns>
    /// <exception cref="MediaToolkitNetException">The element has no such pad.</exception>
    public bool EndOfStream(string pad)
    {
        ArgumentException.ThrowIfNullOrEmpty(pad);
        var handle = RequirePad(pad);
        try
        {
            return Gst.gst_pad_send_event(handle, Gst.gst_event_new_eos()) != 0;
        }
        finally
        {
            Gst.gst_object_unref(handle);
        }
    }

    /// <summary>
    /// Gives back a pad requested from an element such as <c>tee</c>. Name the pad when linking (<c>src_1</c>) to know
    /// which to give back.
    /// </summary>
    /// <param name="pad">The request pad's name.</param>
    /// <exception cref="MediaToolkitNetException">The element has no such pad.</exception>
    public void ReleasePad(string pad)
    {
        ArgumentException.ThrowIfNullOrEmpty(pad);
        var handle = RequirePad(pad);
        try
        {
            Gst.gst_element_release_request_pad(_element, handle);
        }
        finally
        {
            Gst.gst_object_unref(handle);
        }
    }

    /// <summary>
    /// Brings an element added to a running pipeline to the pipeline's state. Link it first, and bring up a new branch
    /// from its sink backwards; give a new sink <c>async=false</c> so that the pipeline does not wait for it to preroll.
    /// </summary>
    /// <exception cref="MediaToolkitNetException">The element refused the state.</exception>
    public void SyncStateWithPipeline()
    {
        EnsureAlive();
        Gst.gst_element_set_locked_state(_element, 0);
        if (Gst.gst_element_sync_state_with_parent(_element) == 0)
        {
            throw Refused($"the element {Name} could not be brought to the pipeline's state.");
        }
    }

    // A static or already requested pad, as a reference the caller owns.
    private void* RequirePad(string pad)
    {
        var handle = GetStaticPad(pad);
        return handle is not null ? handle : throw Refused($"the element {Name} has no pad named {pad}.");
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
    /// <see cref="float"/> or <see cref="double"/>, a <see cref="string"/>, <see cref="GStreamerCaps"/> or
    /// <see cref="GStreamerStructure"/>, or for an enum or flags property the
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
        SetOn(_element, Name, property, value);
    }

    /// <summary>
    /// Sets a property of one of the element's pads, such as <c>xpos</c> of a <c>compositor</c> input or <c>volume</c> of an
    /// <c>audiomixer</c> input, with the same values as <see cref="SetValue"/>.
    /// </summary>
    /// <param name="pad">The pad's name (<c>sink_0</c>); a request pad has to exist already.</param>
    /// <param name="property">The pad's property.</param>
    /// <param name="value">The value, of the type the property has.</param>
    /// <exception cref="MediaToolkitNetException">There is no such pad or property, or the value does not fit.</exception>
    public void SetPadValue(string pad, string property, object value)
    {
        ArgumentException.ThrowIfNullOrEmpty(pad);
        ArgumentException.ThrowIfNullOrEmpty(property);
        ArgumentNullException.ThrowIfNull(value);
        EnsureAlive();
        var handle = GetStaticPad(pad);
        if (handle is null)
        {
            throw new MediaToolkitNetException(GstConstants.BackendName, $"the element {Name} has no pad named {pad}.", 0);
        }

        try
        {
            SetOn(handle, $"{Name}:{pad}", property, value);
        }
        finally
        {
            Gst.gst_object_unref(handle);
        }
    }

    // Sets a property of any GObject: the element or one of its pads. `label` names it in a message.
    private static void SetOn(void* target, string label, string property, object value)
    {
        var type = PropertyTypeOf(target, label, property);
        var storage = stackalloc byte[GstSignals.ValueSize];
        new Span<byte>(storage, GstSignals.ValueSize).Clear();
        Gst.g_value_init(storage, type);
        try
        {
            if (value is string text && (uint)Gst.g_type_fundamental(type) is GstSignals.TypeEnum or GstSignals.TypeFlags)
            {
                // Enums and flags are named by text more often than by number; GStreamer parses either.
                Gst.SetProperty(target, property, text);
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
                    $"the property {property} of {label} is a {Utf8.ToManagedOrEmpty(Gst.g_type_name(type))}, and {value} ({value.GetType().Name}) does not fit: {ex.Message}",
                    0);
            }

            Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var utf8 = new Utf8Scoped(property, scratch);
            Gst.g_object_set_property(target, utf8.Pointer, storage);
        }
        finally
        {
            Gst.g_value_unset(storage);
        }
    }

    /// <summary>
    /// Reads a property as a managed value: <see cref="bool"/>, <see cref="int"/>, <see cref="uint"/>, <see cref="long"/>,
    /// <see cref="ulong"/>, <see cref="float"/>, <see cref="double"/> or <see cref="string"/>; an enum is its
    /// <see cref="int"/> value and flags are a <see cref="uint"/>. Caps are <see cref="GStreamerCaps"/> and a structure
    /// is a <see cref="GStreamerStructure"/>, copied; <see langword="null"/> when the property holds none.
    /// </summary>
    /// <exception cref="MediaToolkitNetException">
    /// The element has no such property, or it holds an object or another boxed value: a reference read here could be
    /// released by the element before the caller used it.
    /// </exception>
    public object? GetValue(string property)
    {
        ArgumentException.ThrowIfNullOrEmpty(property);
        EnsureAlive();

        return Read(_element, Name, property);
    }

    /// <summary>A property of one of the element's pads, with the values <see cref="GetValue"/> gives.</summary>
    /// <param name="pad">The pad's name; a request pad has to exist already.</param>
    /// <param name="property">The pad's property.</param>
    /// <exception cref="MediaToolkitNetException">There is no such pad or property, or it cannot be read.</exception>
    public object? GetPadValue(string pad, string property)
    {
        ArgumentException.ThrowIfNullOrEmpty(pad);
        ArgumentException.ThrowIfNullOrEmpty(property);
        var handle = RequirePad(pad);
        try
        {
            return Read(handle, $"{Name}:{pad}", property);
        }
        finally
        {
            Gst.gst_object_unref(handle);
        }
    }

    /// <summary>The element a property holds, such as <c>video-sink</c> of <c>playbin3</c>, or null.</summary>
    /// <remarks>The caller owns the result and disposes it.</remarks>
    /// <exception cref="MediaToolkitNetException">There is no such property, or it does not hold an element.</exception>
    public GStreamerElement? GetElement(string property)
    {
        ArgumentException.ThrowIfNullOrEmpty(property);
        EnsureAlive();

        var type = PropertyType(property);
        if ((uint)Gst.g_type_fundamental(type) != GstSignals.TypeObject)
        {
            throw Refused($"the property {property} of {Name} does not hold an element.");
        }

        var storage = stackalloc byte[GstSignals.ValueSize];
        new Span<byte>(storage, GstSignals.ValueSize).Clear();
        Gst.g_value_init(storage, type);
        try
        {
            Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var utf8 = new Utf8Scoped(property, scratch);
            Gst.g_object_get_property(_element, utf8.Pointer, storage);
            var held = Gst.g_value_get_object(storage);
            if (held is null)
            {
                return null;
            }

            if (Gst.g_type_check_instance_is_a(held, Gst.gst_element_get_type()) == 0)
            {
                throw Refused($"the property {property} of {Name} does not hold an element.");
            }

            // A reference of the wrapper's own, which outlives the value that carried the object here.
            Gst.gst_object_ref(held);
            return new GStreamerElement(held, Gst.TakeString(Gst.gst_object_get_name(held)));
        }
        finally
        {
            Gst.g_value_unset(storage);
        }
    }

    private static object? Read(void* target, string label, string property)
    {
        var type = PropertyTypeOf(target, label, property);
        var fundamental = (uint)Gst.g_type_fundamental(type);
        var copied = type == Gst.gst_caps_get_type() || type == Gst.gst_structure_get_type() || type == Gst.g_value_array_get_type();
        if (fundamental is GstSignals.TypeObject or GstSignals.TypeParam || (fundamental == GstSignals.TypeBoxed && !copied))
        {
            throw new MediaToolkitNetException(
                GstConstants.BackendName,
                $"the property {property} of {label} holds a {Utf8.ToManagedOrEmpty(Gst.g_type_name(type))}, which GetValue does not read.",
                0);
        }

        var storage = stackalloc byte[GstSignals.ValueSize];
        new Span<byte>(storage, GstSignals.ValueSize).Clear();
        Gst.g_value_init(storage, type);
        try
        {
            Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var utf8 = new Utf8Scoped(property, scratch);
            Gst.g_object_get_property(target, utf8.Pointer, storage);
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
    private nuint PropertyType(string property) => PropertyTypeOf(_element, Name, property);

    private static nuint PropertyTypeOf(void* target, string label, string property)
    {
        GstLayout.Require();
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(property, scratch);
        var spec = Gst.g_object_class_find_property(GstLayout.ClassOf(target), utf8.Pointer);
        if (spec is null)
        {
            throw new MediaToolkitNetException(GstConstants.BackendName, $"the element {label} has no property named {property}.", 0);
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

    /// <summary>
    /// Emits one of the element's action signals, such as <c>split-now</c> of <c>splitmuxsink</c> or <c>get-stats</c> of
    /// <c>multiudpsink</c>, and returns what it answers.
    /// </summary>
    /// <param name="signal">The signal's name.</param>
    /// <param name="arguments">
    /// One value for each parameter, of the types <see cref="SetValue"/> takes; for an object a
    /// <see cref="GStreamerElement"/> or its pointer, for another boxed value its pointer, and null for none.
    /// </param>
    /// <returns>
    /// Null for a signal that returns nothing; otherwise a value as <see cref="GetValue"/> gives it, an enum such as
    /// <c>GstFlowReturn</c> being its number.
    /// </returns>
    /// <exception cref="MediaToolkitNetException">
    /// There is no such action signal, the arguments do not fit it, or it returns a reference (a sample, a pad), which
    /// this call cannot hand over safely.
    /// </exception>
    public object? Emit(string signal, params object?[] arguments) => Emit(signal, arguments, discard: false);

    // For a signal answering with a reference nobody here wants, such as add-transceiver: it is released at once.
    internal void EmitDiscardingResult(string signal, params object?[] arguments) => Emit(signal, arguments, discard: true);

    private object? Emit(string signal, object?[] arguments, bool discard)
    {
        ArgumentException.ThrowIfNullOrEmpty(signal);
        ArgumentNullException.ThrowIfNull(arguments);
        EnsureAlive();

        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(signal, scratch);
        var instanceType = Gst.g_type_from_name(Gst.g_type_name_from_instance(_element));
        var id = Gst.g_signal_lookup(utf8.Pointer, instanceType);
        if (id == 0)
        {
            throw Refused($"the element {Name} has no signal named {signal}.");
        }

        var query = stackalloc byte[GstLayout.SignalQuerySize];
        new Span<byte>(query, GstLayout.SignalQuerySize).Clear();
        Gst.g_signal_query(id, query);
        if (*(uint*)query != id)
        {
            throw Refused("GSignalQuery is not laid out as expected.");
        }

        if ((*(uint*)(query + GstLayout.SignalQueryFlags) & GstLayout.SignalActionFlag) == 0)
        {
            throw Refused($"{signal} on {Name} is not an action signal: the element emits it, and Connect listens to it.");
        }

        var count = (int)*(uint*)(query + GstLayout.SignalQueryParameterCount);
        if (arguments.Length != count)
        {
            throw Refused($"{signal} on {Name} takes {count} arguments, not {arguments.Length}.");
        }

        // The low bit of a type here is G_SIGNAL_TYPE_STATIC_SCOPE, not part of the type.
        var types = *(nuint**)(query + GstLayout.SignalQueryParameterTypes);
        var returnType = *(nuint*)(query + GstLayout.SignalQueryReturnType) & ~(nuint)1;
        var answers = returnType != GstLayout.TypeNone;
        if (answers && !discard)
        {
            var fundamental = (uint)Gst.g_type_fundamental(returnType);
            var copied = returnType == Gst.gst_caps_get_type() || returnType == Gst.gst_structure_get_type();
            if (fundamental is GstSignals.TypeObject or GstSignals.TypeParam or GstSignals.TypePointer ||
                (fundamental == GstSignals.TypeBoxed && !copied))
            {
                throw Refused($"{signal} on {Name} returns a {Utf8.ToManagedOrEmpty(Gst.g_type_name(returnType))}, which Emit does not hand over.");
            }
        }

        // The instance, the arguments, and last the return value.
        var values = (byte*)NativeMemory.AllocZeroed((nuint)(count + 2), GstSignals.ValueSize);
        var initialized = 0;
        try
        {
            Gst.g_value_init(values, instanceType);
            initialized = 1;
            Gst.g_value_set_object(values, _element);

            for (var i = 0; i < count; i++)
            {
                var value = values + ((i + 1) * GstSignals.ValueSize);
                Gst.g_value_init(value, types[i] & ~(nuint)1);
                initialized++;
                Fill(value, types[i] & ~(nuint)1, arguments[i], signal, i);
            }

            var result = values + ((count + 1) * GstSignals.ValueSize);
            if (answers)
            {
                Gst.g_value_init(result, returnType);
            }

            Gst.g_signal_emitv(values, id, 0, answers ? result : null);

            if (!answers)
            {
                return null;
            }

            try
            {
                return discard ? null : GstSignals.ToManaged(result);
            }
            finally
            {
                Gst.g_value_unset(result);
            }
        }
        finally
        {
            for (var i = 0; i < initialized; i++)
            {
                Gst.g_value_unset(values + (i * GstSignals.ValueSize));
            }

            NativeMemory.Free(values);
        }
    }

    private void Fill(byte* value, nuint type, object? argument, string signal, int index)
    {
        if (argument is null)
        {
            return;
        }

        var fundamental = (uint)Gst.g_type_fundamental(type);
        if (argument is GStreamerElement element && fundamental == GstSignals.TypeObject)
        {
            Gst.g_value_set_object(value, element.Handle);
        }
        else if (argument is nint pointer && fundamental == GstSignals.TypeObject)
        {
            Gst.g_value_set_object(value, (void*)pointer);
        }
        else if (argument is nint boxed && fundamental == GstSignals.TypeBoxed)
        {
            Gst.g_value_set_boxed(value, (void*)boxed);
        }
        else
        {
            try
            {
                GstSignals.Store(value, type, argument);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or NotSupportedException)
            {
                throw Refused(
                    $"argument {index} of {signal} on {Name} is a {Utf8.ToManagedOrEmpty(Gst.g_type_name(type))}, and {argument} ({argument.GetType().Name}) does not fit: {ex.Message}");
            }
        }
    }

    private static MediaToolkitNetException Refused(string message) => new(GstConstants.BackendName, message, 0);

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

        lock (_probes)
        {
            foreach (var probe in _probes)
            {
                probe.Dispose();
            }

            _probes.Clear();
        }

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
