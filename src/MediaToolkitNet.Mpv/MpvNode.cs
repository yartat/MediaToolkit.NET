#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using MediaToolkitNet.Interop;
using MediaToolkitNet.Mpv.Native;

namespace MediaToolkitNet.Mpv;

/// <summary>What an <see cref="MpvNode"/> holds.</summary>
public enum MpvNodeKind
{
    /// <summary>Nothing: a command that returns no data, or a property with no value.</summary>
    None,

    /// <summary>A string.</summary>
    String,

    /// <summary>A boolean.</summary>
    Flag,

    /// <summary>A 64-bit integer.</summary>
    Int64,

    /// <summary>A double.</summary>
    Double,

    /// <summary>An ordered list of nodes.</summary>
    Array,

    /// <summary>String keys to nodes, in the order mpv gave them.</summary>
    Map,

    /// <summary>Raw bytes, such as the pixels <c>screenshot-raw</c> returns.</summary>
    Bytes,
}

/// <summary>
/// mpv's <c>mpv_node</c> as a managed tree: how libmpv returns structured data (<c>track-list</c>,
/// <c>chapter-list</c>, <c>metadata</c>, the result of a command) and takes it (named command arguments).
/// A node is immutable and owns copies of everything it holds, so it stays valid after libmpv frees its own.
/// </summary>
public sealed class MpvNode
{
    private const int MaxDepth = 64;

    private static readonly MpvNode Empty = new(MpvNodeKind.None);

    private readonly string? _string;
    private readonly long _integer;
    private readonly double _number;
    private readonly MpvNode[]? _items;
    private readonly string[]? _keys;
    private readonly byte[]? _bytes;

    private MpvNode(
        MpvNodeKind kind,
        string? text = null,
        long integer = 0,
        double number = 0,
        MpvNode[]? items = null,
        string[]? keys = null,
        byte[]? bytes = null)
    {
        Kind = kind;
        _string = text;
        _integer = integer;
        _number = number;
        _items = items;
        _keys = keys;
        _bytes = bytes;
    }

    /// <summary>What the node holds.</summary>
    public MpvNodeKind Kind { get; }

    /// <summary>A node with no value.</summary>
    public static MpvNode None => Empty;

    /// <summary>A string node.</summary>
    public static MpvNode From(string value) =>
        new(MpvNodeKind.String, text: value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>A boolean node.</summary>
    public static MpvNode From(bool value) => new(MpvNodeKind.Flag, integer: value ? 1 : 0);

    /// <summary>An integer node.</summary>
    public static MpvNode From(long value) => new(MpvNodeKind.Int64, integer: value);

    /// <summary>A floating point node.</summary>
    public static MpvNode From(double value) => new(MpvNodeKind.Double, number: value);

    /// <summary>A byte array node, copying the bytes.</summary>
    public static MpvNode From(ReadOnlySpan<byte> value) => new(MpvNodeKind.Bytes, bytes: value.ToArray());

    /// <summary>An array node.</summary>
    public static MpvNode Array(IEnumerable<MpvNode> items) =>
        new(MpvNodeKind.Array, items: [.. items ?? throw new ArgumentNullException(nameof(items))]);

    /// <summary>An array node.</summary>
    public static MpvNode Array(params MpvNode[] items) => Array((IEnumerable<MpvNode>)items);

    /// <summary>A map node. Keys keep the order given.</summary>
    public static MpvNode Map(IEnumerable<KeyValuePair<string, MpvNode>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var pairs = entries.ToArray();
        return new MpvNode(MpvNodeKind.Map, items: [.. pairs.Select(p => p.Value)], keys: [.. pairs.Select(p => p.Key)]);
    }

    /// <summary>
    /// Named arguments for <see cref="MpvPlayer.CommandNode"/>: the command goes in under <c>name</c>, the rest as
    /// given, for example <c>Command("screenshot-raw", ("flags", "video"))</c>. <c>name</c> is what every libmpv
    /// takes; one that is 0.42 or newer also takes <c>_name</c>, which a command that has an argument called
    /// <c>name</c> needs, and which this method cannot use because older libraries refuse it. Build that node with
    /// <see cref="Map"/>.
    /// </summary>
    public static MpvNode Command(string name, params (string Key, MpvNode Value)[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Map([new("name", From(name)), .. arguments.Select(a => new KeyValuePair<string, MpvNode>(a.Key, a.Value))]);
    }

    /// <summary>Converts a string.</summary>
    public static implicit operator MpvNode(string value) => From(value);

    /// <summary>Converts a boolean.</summary>
    public static implicit operator MpvNode(bool value) => From(value);

    /// <summary>Converts an integer.</summary>
    public static implicit operator MpvNode(long value) => From(value);

    /// <summary>Converts a floating point number.</summary>
    public static implicit operator MpvNode(double value) => From(value);

    /// <summary>The string, or <see langword="null"/> when the node is not a string.</summary>
    public string? AsString() => Kind == MpvNodeKind.String ? _string : null;

    /// <summary>The boolean, or <see langword="null"/> when the node is not a flag.</summary>
    public bool? AsFlag() => Kind == MpvNodeKind.Flag ? _integer != 0 : null;

    /// <summary>The integer, or <see langword="null"/> when the node is not an integer.</summary>
    public long? AsInt64() => Kind == MpvNodeKind.Int64 ? _integer : null;

    /// <summary>The number of an integer or a floating point node, or <see langword="null"/>.</summary>
    public double? AsDouble() => Kind switch
    {
        MpvNodeKind.Double => _number,
        MpvNodeKind.Int64 => _integer,
        _ => null,
    };

    /// <summary>The bytes, or <see langword="null"/> when the node is not a byte array.</summary>
    public ReadOnlyMemory<byte>? AsBytes() => Kind == MpvNodeKind.Bytes ? _bytes : null;

    /// <summary>The elements of an array node, or the values of a map node; empty for any other kind.</summary>
    public IReadOnlyList<MpvNode> Items => _items ?? [];

    /// <summary>The keys of a map node, in order; empty for any other kind.</summary>
    public IReadOnlyList<string> Keys => _keys ?? [];

    /// <summary>The value under a key of a map node, or <see langword="null"/> when there is none.</summary>
    public MpvNode? this[string key]
    {
        get
        {
            if (_keys is null)
            {
                return null;
            }

            var at = System.Array.IndexOf(_keys, key);
            return at < 0 ? null : _items![at];
        }
    }

    /// <summary>The element of an array node at an index, or <see langword="null"/> when out of range.</summary>
    public MpvNode? this[int index] =>
        Kind == MpvNodeKind.Array && index >= 0 && index < _items!.Length ? _items[index] : null;

    /// <summary>The node as JSON-like text, for logs and tests. Bytes show only their length.</summary>
    public override string ToString()
    {
        var builder = new StringBuilder();
        Write(builder);
        return builder.ToString();
    }

    private void Write(StringBuilder builder)
    {
        switch (Kind)
        {
            case MpvNodeKind.String:
                builder.Append('"').Append(_string!.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
                break;
            case MpvNodeKind.Flag:
                builder.Append(_integer != 0 ? "true" : "false");
                break;
            case MpvNodeKind.Int64:
                builder.Append(_integer.ToString(CultureInfo.InvariantCulture));
                break;
            case MpvNodeKind.Double:
                builder.Append(_number.ToString("R", CultureInfo.InvariantCulture));
                break;
            case MpvNodeKind.Bytes:
                builder.Append("<").Append(_bytes!.Length).Append(" bytes>");
                break;
            case MpvNodeKind.Array:
                builder.Append('[');
                for (var i = 0; i < _items!.Length; i++)
                {
                    builder.Append(i > 0 ? "," : string.Empty);
                    _items[i].Write(builder);
                }

                builder.Append(']');
                break;
            case MpvNodeKind.Map:
                builder.Append('{');
                for (var i = 0; i < _items!.Length; i++)
                {
                    builder.Append(i > 0 ? "," : string.Empty).Append('"').Append(_keys![i]).Append("\":");
                    _items[i].Write(builder);
                }

                builder.Append('}');
                break;
            default:
                builder.Append("null");
                break;
        }
    }

    // ----------------------------------------------------------------- native

    // mpv_node: a union of 8 bytes (string, flag, int64, double, or a pointer to a list or byte array), then the
    // mpv_format at offset 8; 16 bytes. mpv_node_list: int num at 0, mpv_node *values at 8, char **keys at 16
    // (null for an array). mpv_byte_array: void *data at 0, size_t size at 8. All of it is public C API.
    internal const int NativeSize = 16;

    internal static unsafe MpvNode Read(byte* node, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException("an mpv node is nested too deeply");
        }

        switch ((MpvFormat)(*(int*)(node + 8)))
        {
            case MpvFormat.String:
            case MpvFormat.OsdString:
                return From(Utf8.ToManagedOrEmpty(*(byte**)node));
            case MpvFormat.Flag:
                return From(*(int*)node != 0);
            case MpvFormat.Int64:
                return From(*(long*)node);
            case MpvFormat.Double:
                return From(*(double*)node);
            case MpvFormat.NodeArray:
            case MpvFormat.NodeMap:
            {
                var map = *(int*)(node + 8) == (int)MpvFormat.NodeMap;
                var list = *(byte**)node;
                var count = list is null ? 0 : Math.Max(0, *(int*)list);
                var values = list is null ? null : *(byte**)(list + 8);
                var keys = list is null ? null : *(byte***)(list + 16);
                var items = new MpvNode[count];
                var names = map ? new string[count] : null;
                for (var i = 0; i < count; i++)
                {
                    items[i] = Read(values + (i * NativeSize), depth + 1);
                    if (names is not null)
                    {
                        names[i] = keys is null ? string.Empty : Utf8.ToManagedOrEmpty(keys[i]);
                    }
                }

                return new MpvNode(map ? MpvNodeKind.Map : MpvNodeKind.Array, items: items, keys: names);
            }

            case MpvFormat.ByteArray:
            {
                var array = *(byte**)node;
                var size = array is null ? 0 : (int)Math.Min(*(nuint*)(array + 8), int.MaxValue);
                return From(size == 0 ? default : new ReadOnlySpan<byte>(*(byte**)array, size));
            }

            default:
                return Empty;
        }
    }

    /// <summary>Everything native a node needed to be handed to libmpv; freed together.</summary>
    internal sealed unsafe class Arena : IDisposable
    {
        private readonly List<nint> _blocks = [];

        public byte* NewNode(MpvNode node)
        {
            var slot = Allocate(NativeSize);
            Fill(node, slot, 0);
            return slot;
        }

        public void Dispose()
        {
            foreach (var block in _blocks)
            {
                NativeMemory.Free((void*)block);
            }

            _blocks.Clear();
        }

        private byte* Allocate(int size)
        {
            var block = (byte*)NativeMemory.AllocZeroed((nuint)Math.Max(size, 1));
            _blocks.Add((nint)block);
            return block;
        }

        private byte* Text(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var block = Allocate(bytes.Length + 1);
            bytes.CopyTo(new Span<byte>(block, bytes.Length));
            return block;
        }

        private void Fill(MpvNode node, byte* slot, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new ArgumentException("The node is nested too deeply.", nameof(node));
            }

            switch (node.Kind)
            {
                case MpvNodeKind.String:
                    *(byte**)slot = Text(node._string!);
                    *(int*)(slot + 8) = (int)MpvFormat.String;
                    break;
                case MpvNodeKind.Flag:
                    *(int*)slot = node._integer != 0 ? 1 : 0;
                    *(int*)(slot + 8) = (int)MpvFormat.Flag;
                    break;
                case MpvNodeKind.Int64:
                    *(long*)slot = node._integer;
                    *(int*)(slot + 8) = (int)MpvFormat.Int64;
                    break;
                case MpvNodeKind.Double:
                    *(double*)slot = node._number;
                    *(int*)(slot + 8) = (int)MpvFormat.Double;
                    break;
                case MpvNodeKind.Array:
                case MpvNodeKind.Map:
                {
                    var map = node.Kind == MpvNodeKind.Map;
                    var count = node._items!.Length;
                    var list = Allocate(24);
                    var values = Allocate(count * NativeSize);
                    *(int*)list = count;
                    *(byte**)(list + 8) = values;
                    if (map)
                    {
                        var keys = (byte**)Allocate(count * sizeof(byte*));
                        for (var i = 0; i < count; i++)
                        {
                            keys[i] = Text(node._keys![i]);
                        }

                        *(byte***)(list + 16) = keys;
                    }

                    for (var i = 0; i < count; i++)
                    {
                        Fill(node._items[i], values + (i * NativeSize), depth + 1);
                    }

                    *(byte**)slot = list;
                    *(int*)(slot + 8) = (int)(map ? MpvFormat.NodeMap : MpvFormat.NodeArray);
                    break;
                }

                case MpvNodeKind.Bytes:
                {
                    var array = Allocate(16);
                    var data = Allocate(node._bytes!.Length);
                    node._bytes.CopyTo(new Span<byte>(data, node._bytes.Length));
                    *(byte**)array = data;
                    *(nuint*)(array + 8) = (nuint)node._bytes.Length;
                    *(byte**)slot = array;
                    *(int*)(slot + 8) = (int)MpvFormat.ByteArray;
                    break;
                }

                default:
                    *(int*)(slot + 8) = (int)MpvFormat.None;
                    break;
            }
        }
    }
}
