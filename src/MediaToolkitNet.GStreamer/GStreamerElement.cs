using System.Runtime.Versioning;
using MediaToolkitNet.GStreamer.Native;

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
