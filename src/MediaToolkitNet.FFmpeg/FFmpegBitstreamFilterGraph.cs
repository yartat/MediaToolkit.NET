#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.FFmpeg.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg;

/// <summary>
/// A graph of bitstream filters over compressed packets, through the API FFmpeg 9.1
/// added (<c>av_bsf_graph_*</c>): sources that packets are fed into, filters that
/// may take several inputs, and sinks that filtered packets are read from.
/// </summary>
/// <remarks>
/// <para>
/// Needs <see cref="FFmpegFeatures.BitstreamFilterGraphs"/>. The API is marked
/// experimental by FFmpeg, and only filters written for it can be in a graph: in
/// 9.1 that is <c>lcevc_merge</c>, which joins the base layer of an LCEVC stream
/// with its enhancement layer. The classic filters (<c>h264_mp4toannexb</c> and
/// the rest) work on one stream through <c>av_bsf_send_packet</c> and cannot be
/// placed in a graph; FFmpeg answers "not supported" and so does <see cref="AddFilter"/>.
/// </para>
/// <para>
/// Nodes are numbered in the order they were added. Packets are the demuxer's own
/// <see cref="AVPacketNative"/>: <see cref="TrySend"/> takes the reference a packet
/// holds, and <see cref="TryReceive"/> fills one the caller owns and must unreference.
/// </para>
/// </remarks>
public sealed unsafe class FFmpegBitstreamFilterGraph : IDisposable
{
    private enum NodeKind
    {
        Source,
        Filter,
        Sink,
    }

    private readonly List<(nint Context, NodeKind Kind)> _nodes = [];
    private void* _graph;
    private bool _configured;

    private FFmpegBitstreamFilterGraph()
    {
    }

    /// <summary>True when the loaded libraries have the bitstream filter graph API.</summary>
    public static bool IsAvailable => FFmpegFeatures.BitstreamFilterGraphs;

    /// <summary>Creates an empty graph.</summary>
    /// <exception cref="NotSupportedException">The libraries are older than FFmpeg 9.1.</exception>
    public static FFmpegBitstreamFilterGraph Create()
    {
        if (!IsAvailable)
        {
            throw new NotSupportedException(
                "bitstream filter graphs need av_bsf_graph_*, which FFmpeg has from 9.1 on; " +
                "check FFmpegFeatures.BitstreamFilterGraphs first");
        }

        var graph = new FFmpegBitstreamFilterGraph { _graph = AVModern.av_bsf_graph_alloc() };
        if (graph._graph is null)
        {
            throw new Abstractions.MediaToolkitNetException(
                FFmpegLibraries.BackendName, "av_bsf_graph_alloc returned null", AVConstants.ErrorInvalid);
        }

        return graph;
    }

    /// <summary>Adds a source for one stream of an open input, carrying its parameters and time base.</summary>
    /// <returns>Returns the node number.</returns>
    public int AddSource(FFmpegDemuxer demuxer, int streamIndex)
    {
        ArgumentNullException.ThrowIfNull(demuxer);
        EnsureOpenForNodes();

        void* source;
        FFmpegError.Check(AllocFilter(&source, "source", $"source{_nodes.Count}"), "av_bsf_graph_alloc_filter(source)");

        var timeBase = AbiLayout.TimeBaseOf(demuxer.StreamHandle(streamIndex));
        Span<byte> nameScratch = stackalloc byte[16];
        Span<byte> valueScratch = stackalloc byte[32];
        using (var key = new Utf8Scoped("time_base", nameScratch))
        using (var text = new Utf8Scoped($"{timeBase.Num}/{timeBase.Den}", valueScratch))
        {
            // The option belongs to the filter's private context, which a search of the children reaches.
            FFmpegError.Check(AV.av_opt_set(source, key.Pointer, text.Pointer, 1), "av_opt_set(time_base)");
        }

        FFmpegError.Check(
            AVModern.av_bsf_source_parameters_set(source, demuxer.CodecParameters(streamIndex)),
            "av_bsf_source_parameters_set");

        // A filter has to be initialised before it can be linked.
        FFmpegError.Check(AVModern.av_bsf_init_str(source, null), "av_bsf_init_str(source)");

        _nodes.Add(((nint)source, NodeKind.Source));
        return _nodes.Count - 1;
    }

    /// <summary>Adds a filter written for graphs, given as <c>name</c> or <c>name=key=value:key=value</c>.</summary>
    /// <returns>Returns the node number.</returns>
    /// <exception cref="Abstractions.MediaToolkitNetException">There is no such filter, it cannot be in a graph, or it refuses its options.</exception>
    public int AddFilter(string spec)
    {
        EnsureOpenForNodes();
        var (name, options) = Split(spec);

        Span<byte> nameScratch = stackalloc byte[64];
        using var utf8 = new Utf8Scoped(name, nameScratch);
        var filter = AVModern.av_bsf_get_by_name(utf8.Pointer);
        if (filter is null)
        {
            throw new Abstractions.MediaToolkitNetException(
                FFmpegLibraries.BackendName, $"there is no bitstream filter named {name}", AVConstants.ErrorInvalid);
        }

        void* dictionary = null;
        void* context = null;
        try
        {
            foreach (var (key, value) in options)
            {
                AV.DictionarySet(&dictionary, key, value);
            }

            var result = AVModern.av_bsf_graph_create_filter(&context, filter, utf8.Pointer, &dictionary, _graph);
            if (result < 0)
            {
                // Classic filters have no pads and no activate function, which the graph answers with ENOTSUP.
                if (IsNotSupported(result))
                {
                    throw new Abstractions.MediaToolkitNetException(
                        FFmpegLibraries.BackendName,
                        $"{name} is a classic bitstream filter, which works on one stream and cannot be placed in a graph; " +
                        "only filters written for graphs can, such as lcevc_merge",
                        result);
                }

                FFmpegError.Check(result, $"av_bsf_graph_create_filter({name})");
            }
        }
        finally
        {
            if (dictionary is not null)
            {
                AV.av_dict_free(&dictionary);
            }
        }

        _nodes.Add(((nint)context, NodeKind.Filter));
        return _nodes.Count - 1;
    }

    /// <summary>Adds a sink, which filtered packets are read from.</summary>
    /// <returns>Returns the node number.</returns>
    public int AddSink()
    {
        EnsureOpenForNodes();
        void* sink;
        FFmpegError.Check(AllocFilter(&sink, "sink", $"sink{_nodes.Count}"), "av_bsf_graph_alloc_filter(sink)");
        FFmpegError.Check(AVModern.av_bsf_init_str(sink, null), "av_bsf_init_str(sink)");
        _nodes.Add(((nint)sink, NodeKind.Sink));
        return _nodes.Count - 1;
    }

    /// <summary>Connects an output pad of one node to an input pad of another.</summary>
    /// <param name="from">The node whose output is used.</param>
    /// <param name="to">The node that takes it.</param>
    /// <param name="fromPad">Index of the output pad of <paramref name="from"/>.</param>
    /// <param name="toPad">Index of the input pad of <paramref name="to"/>.</param>
    public void Link(int from, int to, int fromPad = 0, int toPad = 0)
    {
        EnsureOpenForNodes();
        ArgumentOutOfRangeException.ThrowIfNegative(fromPad);
        ArgumentOutOfRangeException.ThrowIfNegative(toPad);
        FFmpegError.Check(
            AVModern.av_bsf_link((void*)Node(from).Context, (uint)fromPad, (void*)Node(to).Context, (uint)toPad),
            "av_bsf_link");
    }

    /// <summary>Checks the links and configures the graph; no more nodes can be added after it.</summary>
    public void Configure()
    {
        ObjectDisposedException.ThrowIf(_graph is null, this);
        FFmpegError.Check(AVModern.av_bsf_graph_config(_graph, null), "av_bsf_graph_config");
        _configured = true;
    }

    /// <summary>Hands a packet to a source, which takes the reference it holds; <see langword="null"/> marks the end of that stream.</summary>
    /// <param name="source">A node returned by <see cref="AddSource"/>.</param>
    /// <param name="packet">The packet.</param>
    /// <returns>False when the source takes no more packets.</returns>
    public bool TrySend(int source, AVPacketNative* packet)
    {
        var result = AVModern.av_bsf_source_add_packet((void*)Node(source, NodeKind.Source).Context, packet, 0);
        if (result == AVConstants.ErrorEof)
        {
            return false;
        }

        FFmpegError.Check(result, "av_bsf_source_add_packet");
        return true;
    }

    /// <summary>Takes one filtered packet out of a sink.</summary>
    /// <param name="sink">A node returned by <see cref="AddSink"/>.</param>
    /// <param name="packet">An allocated packet, which receives it.</param>
    /// <returns>False when the graph needs more input, or has ended.</returns>
    public bool TryReceive(int sink, AVPacketNative* packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var result = AVModern.av_bsf_sink_get_packet((void*)Node(sink, NodeKind.Sink).Context, packet, 0);
        if (result is AVConstants.ErrorAgain or AVConstants.ErrorEof)
        {
            return false;
        }

        FFmpegError.Check(result, "av_bsf_sink_get_packet");
        return true;
    }

    /// <summary>Time base of the packets a sink produces.</summary>
    /// <param name="sink">A node returned by <see cref="AddSink"/>.</param>
    public AVRationalNative TimeBaseOf(int sink) =>
        AVModern.av_bsf_sink_get_time_base((void*)Node(sink, NodeKind.Sink).Context);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_graph is not null)
        {
            var graph = _graph;
            AVModern.av_bsf_graph_free(&graph);
            _graph = null;
            _nodes.Clear();
        }
    }

    private void EnsureOpenForNodes()
    {
        ObjectDisposedException.ThrowIf(_graph is null, this);
        if (_configured)
        {
            throw new InvalidOperationException("The graph is configured; nodes and links cannot be added any more.");
        }
    }

    private (nint Context, NodeKind Kind) Node(int index, NodeKind? expected = null)
    {
        ObjectDisposedException.ThrowIf(_graph is null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _nodes.Count);
        var node = _nodes[index];
        if (expected is { } kind && node.Kind != kind)
        {
            throw new ArgumentException($"Node {index} is a {node.Kind.ToString().ToLowerInvariant()}, not a {kind.ToString().ToLowerInvariant()}.");
        }

        return node;
    }

    private int AllocFilter(void** context, string filterName, string instanceName)
    {
        Span<byte> scratch = stackalloc byte[32];
        Span<byte> instance = stackalloc byte[32];
        using var utf8 = new Utf8Scoped(filterName, scratch);
        using var named = new Utf8Scoped(instanceName, instance);
        var filter = AVModern.av_bsf_get_by_name(utf8.Pointer);
        if (filter is null)
        {
            throw new Abstractions.MediaToolkitNetException(
                FFmpegLibraries.BackendName, $"this FFmpeg has no \"{filterName}\" bitstream filter", AVConstants.ErrorInvalid);
        }

        return AVModern.av_bsf_graph_alloc_filter(context, filter, named.Pointer, _graph);
    }

    // AVERROR(ENOTSUP), whose number is the platform's: Linux 95, macOS 45, the Windows C runtime 129.
    private static bool IsNotSupported(int result) => result is -95 or -45 or -129;

    // "name" or "name=key=value:key=value" into the name and its options.
    private static (string Name, List<(string Key, string Value)> Options) Split(string spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        var equals = spec.IndexOf('=', StringComparison.Ordinal);
        if (equals < 0)
        {
            return (spec.Trim(), []);
        }

        var options = new List<(string, string)>();
        foreach (var pair in spec[(equals + 1)..].Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=', StringComparison.Ordinal);
            if (at <= 0)
            {
                throw new ArgumentException($"\"{pair}\" is not key=value in the options of {spec[..equals]}.", nameof(spec));
            }

            options.Add((pair[..at], pair[(at + 1)..]));
        }

        return (spec[..equals].Trim(), options);
    }
}
