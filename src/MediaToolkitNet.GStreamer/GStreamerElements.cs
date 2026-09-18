using System.Runtime.Versioning;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer;

/// <summary>One element factory the installed plugins provide.</summary>
/// <param name="Name">The name a pipeline description uses, for example <c>videoconvert</c>.</param>
/// <param name="LongName">The readable name, for example <c>Colorspace converter</c>.</param>
/// <param name="Classification">
/// The <c>klass</c> metadata, a slash-separated path such as
/// <c>Filter/Converter/Video</c>, which is how GStreamer groups elements.
/// </param>
/// <param name="Description">The one-line description the plugin carries.</param>
[SupportedOSPlatform("linux")]
public readonly record struct GStreamerElementInfo(
    string Name,
    string LongName,
    string Classification,
    string Description)
{
    /// <inheritdoc />
    public override string ToString() => $"{Name} — {LongName}";
}

/// <summary>
/// The element factories the installed GStreamer plugins register.
/// </summary>
/// <remarks>
/// This is the counterpart of the DirectShow filter catalogue, except that
/// nothing can be written down in advance: which elements exist depends on
/// which of gst-plugins-base, -good, -bad, -ugly and libav are installed, so the
/// list is read from the registry. An element a pipeline needs but does not
/// find is the usual reason <see cref="GStreamerPipeline.Parse"/> fails.
/// </remarks>
[SupportedOSPlatform("linux")]
public static unsafe class GStreamerElements
{
    /// <summary>Every registered element factory, sorted by name.</summary>
    public static IReadOnlyList<GStreamerElementInfo> All()
    {
        if (!Gst.IsAvailable || !GstLayout.Verified)
        {
            return [];
        }

        var registry = Gst.gst_registry_get();
        if (registry is null)
        {
            return [];
        }

        var list = Gst.gst_registry_get_feature_list(registry, Gst.gst_element_factory_get_type());
        if (list is null)
        {
            return [];
        }

        try
        {
            var result = new List<GStreamerElementInfo>();
            for (var node = list; node is not null; node = GstLayout.NextListNode(node))
            {
                var factory = GstLayout.DataOfListNode(node);
                if (factory is null)
                {
                    continue;
                }

                result.Add(new GStreamerElementInfo(
                    Utf8.ToManagedOrEmpty(Gst.gst_plugin_feature_get_name(factory)),
                    Metadata(factory, "long-name"),
                    Metadata(factory, "klass"),
                    Metadata(factory, "description")));
            }

            result.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
            return result;
        }
        finally
        {
            Gst.gst_plugin_feature_list_free(list);
        }
    }

    /// <summary>True when an element of that name can be created.</summary>
    public static bool Exists(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!Gst.IsAvailable)
        {
            return false;
        }

        Span<byte> nameScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(name, nameScratch);

        var element = Gst.gst_element_factory_make(utf8.Pointer, null);
        if (element is null)
        {
            return false;
        }

        // A freshly made element carries a floating reference; sinking it first
        // makes the unref that follows the one that frees it.
        Gst.gst_object_ref_sink(element);
        Gst.gst_object_unref(element);
        return true;
    }

    private static string Metadata(void* factory, string key)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(key, scratch);

        // The metadata belongs to the factory and must not be freed.
        return Utf8.ToManagedOrEmpty(Gst.gst_element_factory_get_metadata(factory, utf8.Pointer));
    }
}
