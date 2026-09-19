using MediaToolkitNet.FFmpeg.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg.Filtering;

/// <summary>One filter libavfilter offers.</summary>
/// <param name="Name">The name used in a chain description, for example <c>scale</c>.</param>
/// <param name="Description">The one-line description libavfilter carries.</param>
public readonly record struct FFmpegFilterInfo(string Name, string Description)
{
    /// <inheritdoc />
    public override string ToString() =>
        string.IsNullOrEmpty(Description) ? Name : $"{Name} — {Description}";
}

/// <summary>
/// The filters the loaded libavfilter was built with.
/// </summary>
/// <remarks>
/// Unlike DirectShow, where the built-in filters are a fixed list of class ids,
/// libavfilter carries its own registry and which filters exist depends on how
/// the library was configured. So the list is read from the library rather than
/// written down here.
/// </remarks>
public static unsafe class FFmpegFilters
{
    /// <summary>Every filter the loaded libavfilter registered, sorted by name.</summary>
    public static IReadOnlyList<FFmpegFilterInfo> All()
    {
        if (!FFmpegFilterGraph.IsAvailable)
        {
            return [];
        }

        var result = new List<FFmpegFilterInfo>();
        void* state = null;
        while (true)
        {
            var filter = AV.av_filter_iterate(&state);
            if (filter is null)
            {
                break;
            }

            result.Add(new FFmpegFilterInfo(
                Utf8.ToManagedOrEmpty(AbiLayout.NameOfFilter(filter)),
                Utf8.ToManagedOrEmpty(AbiLayout.DescriptionOfFilter(filter))));
        }

        result.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return result;
    }

    /// <summary>True when the loaded libavfilter has a filter of that name.</summary>
    public static bool Exists(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!FFmpegFilterGraph.IsAvailable)
        {
            return false;
        }

        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(name, scratch);
        return AV.avfilter_get_by_name(utf8.Pointer) is not null;
    }
}
