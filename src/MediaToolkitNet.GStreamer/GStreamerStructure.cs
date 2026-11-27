#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer;

/// <summary>
/// A <c>GstStructure</c> copied into managed values: a name and typed fields, as statistics, element messages and caps
/// are made of. To make one or change one, write its text and <see cref="Parse"/> it.
/// </summary>
/// <remarks>
/// A field is a <see cref="bool"/>, <see cref="int"/>, <see cref="uint"/>, <see cref="long"/>, <see cref="ulong"/>,
/// <see cref="float"/>, <see cref="double"/>, <see cref="string"/>, <see cref="Rational"/> for a fraction,
/// <see cref="GStreamerStructure"/>, <see cref="GStreamerCaps"/>, or an array of these for a list or an array. An enum is
/// its <see cref="int"/> value and flags a <see cref="uint"/>. Anything else, a range for one, is its text.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerStructure
{
    private readonly string _description;

    private GStreamerStructure(string name, IReadOnlyDictionary<string, object?> fields, string description)
    {
        Name = name;
        Fields = fields;
        _description = description;
    }

    /// <summary>The structure's name, such as <c>video/x-raw</c>.</summary>
    public string Name { get; }

    /// <summary>The fields by name, in the structure's order.</summary>
    public IReadOnlyDictionary<string, object?> Fields { get; }

    /// <summary>The field <paramref name="field"/> as a <typeparamref name="T"/>.</summary>
    /// <exception cref="KeyNotFoundException">There is no such field.</exception>
    public T Get<T>(string field) => (T)Fields[field]!;

    /// <summary>A structure from its text, such as <c>stats, count=(int)3</c>.</summary>
    /// <exception cref="MediaToolkitNetException">The text is not a structure.</exception>
    public static GStreamerStructure Parse(string text)
    {
        var pointer = Create(text);
        try
        {
            return From(pointer);
        }
        finally
        {
            Gst.gst_structure_free(pointer);
        }
    }

    /// <inheritdoc />
    public override string ToString() => _description;

    // A native structure from text, which the caller frees.
    internal static void* Create(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        Gst.EnsureLoaded();

        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(text, scratch);
        byte* end = null;
        var pointer = Gst.gst_structure_from_string(utf8.Pointer, &end);
        if (pointer is not null && end is not null && !string.IsNullOrWhiteSpace(Utf8.ToManagedOrEmpty(end)))
        {
            Gst.gst_structure_free(pointer);
            pointer = null;
        }

        return pointer is not null
            ? pointer
            : throw new MediaToolkitNetException(GstConstants.BackendName, $"this is not a structure: {text}", 0);
    }

    // A copy of a structure somebody else owns.
    internal static GStreamerStructure From(void* structure)
    {
        var count = Gst.gst_structure_n_fields(structure);
        var fields = new Dictionary<string, object?>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var name = Gst.gst_structure_nth_field_name(structure, (uint)i);
            fields[Utf8.ToManagedOrEmpty(name)] = Convert(Gst.gst_structure_get_value(structure, name));
        }

        return new GStreamerStructure(
            Utf8.ToManagedOrEmpty(Gst.gst_structure_get_name(structure)),
            fields,
            Gst.TakeString(Gst.gst_structure_to_string(structure)));
    }

    // A value of a structure or of a list. A pointer, an object or a boxed value of another kind would outlive what
    // owns it: those, and what has no managed counterpart, are given as text.
    internal static object? Convert(byte* value)
    {
        var converted = GstSignals.ToManaged(value);
        var isText = converted is null && (uint)Gst.g_type_fundamental(*(nuint*)value) != GstSignals.TypeString;
        return isText || converted is nint ? Gst.TakeString(Gst.gst_value_serialize(value)) : converted;
    }
}

/// <summary>
/// <c>GstCaps</c> copied into managed values: the formats a link can carry, each a <see cref="GStreamerStructure"/>.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerCaps
{
    private readonly string _description;

    private GStreamerCaps(IReadOnlyList<GStreamerStructure> structures, string description)
    {
        Structures = structures;
        _description = description;
    }

    /// <summary>The formats, in order of preference; empty for <c>ANY</c> and <c>EMPTY</c>.</summary>
    public IReadOnlyList<GStreamerStructure> Structures { get; }

    /// <summary>Caps from their text, such as <c>video/x-raw,width=640</c>.</summary>
    /// <exception cref="MediaToolkitNetException">The text is not caps.</exception>
    public static GStreamerCaps Parse(string text)
    {
        var pointer = Create(text);
        try
        {
            return From(pointer);
        }
        finally
        {
            Gst.gst_mini_object_unref(pointer);
        }
    }

    /// <inheritdoc />
    public override string ToString() => _description;

    // Native caps from text, a reference the caller releases.
    internal static void* Create(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        Gst.EnsureLoaded();

        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(text, scratch);
        var pointer = Gst.gst_caps_from_string(utf8.Pointer);
        return pointer is not null
            ? pointer
            : throw new MediaToolkitNetException(GstConstants.BackendName, $"these are not caps: {text}", 0);
    }

    internal static GStreamerCaps From(void* caps)
    {
        var structures = new GStreamerStructure[Gst.gst_caps_get_size(caps)];
        for (var i = 0; i < structures.Length; i++)
        {
            structures[i] = GStreamerStructure.From(Gst.gst_caps_get_structure(caps, (uint)i));
        }

        return new GStreamerCaps(structures, Gst.TakeString(Gst.gst_caps_to_string(caps)));
    }
}
