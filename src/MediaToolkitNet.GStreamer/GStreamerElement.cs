#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
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
        if (_element is not null)
        {
            Gst.gst_object_unref(_element);
            _element = null;
        }
    }

    private void EnsureAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
}
