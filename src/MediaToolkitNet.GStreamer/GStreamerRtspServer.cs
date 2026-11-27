#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer;

/// <summary>
/// The pipeline an RTSP server has just built for a stream, before it runs: where the application gets at the elements
/// of the description, an <c>appsrc</c> to feed for one.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerRtspMedia
{
    private readonly List<IDisposable> _tracked = [];
    private void* _bin;

    // Takes over the caller's reference to the media's element.
    internal GStreamerRtspMedia(void* bin) => _bin = bin;

    /// <summary>The element of that name in the stream's description, or null.</summary>
    /// <remarks>The media owns the result.</remarks>
    public GStreamerElement? FindElement(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObjectDisposedException.ThrowIf(_bin is null, this);

        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(name, scratch);
        var element = Gst.gst_bin_get_by_name(_bin, utf8.Pointer);
        if (element is null)
        {
            return null;
        }

        var wrapper = new GStreamerElement(element, name);
        lock (_tracked)
        {
            _tracked.Add(wrapper);
        }

        return wrapper;
    }

    /// <summary>The <c>appsrc</c> of that name, as the way to push frames into the stream.</summary>
    /// <exception cref="MediaToolkitNetException">There is no such element, or it is not an appsrc.</exception>
    public GStreamerSource GetSource(string name)
    {
        var element = FindElement(name);
        if (element is null || Utf8.ToManagedOrEmpty(Gst.g_type_name_from_instance(element.Handle)) != "GstAppSrc")
        {
            throw new MediaToolkitNetException(GstConstants.BackendName, $"the stream has no appsrc named {name}.", 0);
        }

        var source = new GStreamerSource(element);
        lock (_tracked)
        {
            _tracked.Add(source);
        }

        return source;
    }

    internal void Release()
    {
        lock (_tracked)
        {
            foreach (var tracked in _tracked)
            {
                tracked.Dispose();
            }

            _tracked.Clear();
        }

        if (_bin is not null)
        {
            Gst.gst_object_unref(_bin);
            _bin = null;
        }
    }
}

/// <summary>
/// Serves pipelines over RTSP with <c>gst-rtsp-server</c>: each mount path is a description that ends in RTP
/// payloaders, which a viewer's player asks for by URL.
/// </summary>
/// <remarks>
/// <para>
/// There is no authentication and no encryption: whoever reaches the port can watch. The server therefore listens on
/// the loopback only unless another address is given.
/// </para>
/// <para>
/// A description is run as it is written, like any pipeline description, so do not build one from what a user typed.
/// The configure callbacks run on a thread of the server's, one new media at a time.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerRtspServer : IDisposable
{
    private const int FilterRemove = 1;

    private readonly string _address;
    private readonly int _requestedPort;
    private readonly List<GStreamerSignalConnection> _connections = [];
    private readonly Dictionary<nint, (GStreamerRtspMedia Media, GStreamerSignalConnection Ended)> _media = [];
    private readonly List<nint> _factories = [];
    private void* _server;
    private void* _mounts;
    private void* _context;
    private void* _loop;
    private uint _source;
    private Thread? _thread;

    /// <summary>Creates a server; <see cref="Start"/> makes it listen.</summary>
    /// <param name="port">The port, 8554 by custom; 0 for one that is free.</param>
    /// <param name="address">
    /// The address to listen on. The default is reachable from this machine only; <c>0.0.0.0</c> opens it to the network.
    /// </param>
    /// <exception cref="NotSupportedException"><c>gst-rtsp-server</c> is not installed.</exception>
    public GStreamerRtspServer(int port = 8554, string address = "127.0.0.1")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentException.ThrowIfNullOrEmpty(address);
        Gst.EnsureLoaded();
        GstRtspServer.Require();

        _address = address;
        _requestedPort = port;
        _server = GstRtspServer.ServerNew();
        _mounts = GstRtspServer.GetMountPoints(_server);

        Span<byte> addressScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> portScratch = stackalloc byte[16];
        using var addressUtf8 = new Utf8Scoped(address, addressScratch);
        using var portUtf8 = new Utf8Scoped(port.ToString(System.Globalization.CultureInfo.InvariantCulture), portScratch);
        GstRtspServer.SetAddress(_server, addressUtf8.Pointer);
        GstRtspServer.SetService(_server, portUtf8.Pointer);
    }

    /// <summary>True when <c>gst-rtsp-server</c> is installed.</summary>
    public static bool IsAvailable => GstRtspServer.IsAvailable;

    /// <summary>Raised when a configure callback throws.</summary>
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <summary>The port it listens on, known once started; before that the one asked for.</summary>
    public int Port => _thread is null ? _requestedPort : GstRtspServer.GetBoundPort(_server);

    /// <summary>How many clients are connected.</summary>
    public int ClientCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(_server is null, this);
            var list = GstRtspServer.ClientFilter(_server, null, null);
            var count = 0;
            for (var node = list; node is not null; node = GstLayout.NextListNode(node))
            {
                Gst.g_object_unref(GstLayout.DataOfListNode(node));
                count++;
            }

            if (list is not null)
            {
                Gst.g_list_free(list);
            }

            return count;
        }
    }

    /// <summary>The URL of a stream. A server listening on every address is named by the loopback here.</summary>
    public string Url(string path)
    {
        CheckPath(path);
        var host = _address == "0.0.0.0" ? "127.0.0.1" : _address;
        return $"rtsp://{host}:{Port}{path}";
    }

    /// <summary>Serves a stream at a path, replacing one already there.</summary>
    /// <param name="path">Such as <c>/camera</c>.</param>
    /// <param name="description">
    /// A <c>gst-launch</c> description in parentheses whose payloaders are named <c>pay0</c>, <c>pay1</c> and so on, for
    /// example <c>( videotestsrc ! x264enc ! rtph264pay name=pay0 pt=96 )</c>. One that cannot be built is only found out
    /// when a client asks for it.
    /// </param>
    /// <param name="configure">Called with each media as it is built, before it runs.</param>
    /// <param name="shared">Whether all clients watch one pipeline, rather than each having its own.</param>
    public void AddStream(string path, string description, Action<GStreamerRtspMedia>? configure = null, bool shared = true)
    {
        CheckPath(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ObjectDisposedException.ThrowIf(_server is null, this);

        var factory = GstRtspServer.FactoryNew();
        Span<byte> descriptionScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> pathScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var descriptionUtf8 = new Utf8Scoped(description, descriptionScratch);
        using var pathUtf8 = new Utf8Scoped(path, pathScratch);
        GstRtspServer.SetLaunch(factory, descriptionUtf8.Pointer);
        GstRtspServer.SetShared(factory, shared ? 1 : 0);

        if (configure is not null)
        {
            var connection = new GStreamerSignalConnection(factory, "media-configure", args => Configure(args, configure), Raise);
            connection.Connect(after: false);
            lock (_connections)
            {
                _connections.Add(connection);
            }
        }

        // One reference is the server's, to disconnect from the factory later; the mount points take the other.
        Gst.g_object_ref(factory);
        _factories.Add((nint)factory);
        GstRtspServer.AddFactory(_mounts, pathUtf8.Pointer, factory);
    }

    /// <summary>Stops offering a path. Those already watching it go on.</summary>
    public void RemoveStream(string path)
    {
        CheckPath(path);
        ObjectDisposedException.ThrowIf(_server is null, this);
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(path, scratch);
        GstRtspServer.RemoveFactory(_mounts, utf8.Pointer);
    }

    /// <summary>Starts listening, on a thread of the server's own.</summary>
    /// <exception cref="MediaToolkitNetException">The address and port cannot be listened on.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_server is null, this);
        if (_thread is not null)
        {
            return;
        }

        _context = GstRtspServer.ContextNew();
        _source = GstRtspServer.Attach(_server, _context);
        if (_source == 0)
        {
            GstRtspServer.ContextUnref(_context);
            _context = null;
            throw new MediaToolkitNetException(
                GstConstants.BackendName, $"could not listen on {_address}:{_requestedPort}; is the port in use?", 0);
        }

        _loop = GstRtspServer.LoopNew(_context, 0);
        var loop = (nint)_loop;
        _thread = new Thread(() => GstRtspServer.LoopRun((void*)loop)) { IsBackground = true, Name = "MediaToolkitNet.GStreamer RTSP" };
        _thread.Start();

        // A quit that came before the loop was running would be lost, and Dispose would wait for ever.
        while (GstRtspServer.LoopIsRunning(_loop) == 0 && _thread.IsAlive)
        {
            Thread.Sleep(1);
        }
    }

    /// <summary>Disconnects the clients and stops listening.</summary>
    public void Dispose()
    {
        if (_server is null)
        {
            return;
        }

        var list = GstRtspServer.ClientFilter(_server, &RemoveClient, null);
        if (list is not null)
        {
            Gst.g_list_free(list);
        }

        if (_thread is { } thread)
        {
            var source = GstRtspServer.ContextFindSource(_context, _source);
            if (source is not null)
            {
                GstRtspServer.SourceDestroy(source);
            }

            GstRtspServer.LoopQuit(_loop);
            thread.Join();
            _thread = null;
            GstRtspServer.LoopUnref(_loop);
            GstRtspServer.ContextUnref(_context);
        }

        lock (_connections)
        {
            foreach (var connection in _connections)
            {
                connection.Dispose();
            }

            _connections.Clear();
        }

        nint[] remaining;
        lock (_media)
        {
            remaining = [.. _media.Keys];
        }

        foreach (var media in remaining)
        {
            Forget(media);
        }

        foreach (var factory in _factories)
        {
            Gst.g_object_unref((void*)factory);
        }

        _factories.Clear();
        Gst.g_object_unref(_mounts);
        Gst.g_object_unref(_server);
        _server = null;
    }

    private static void CheckPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path[0] != '/')
        {
            throw new ArgumentException("A stream's path begins with a slash.", nameof(path));
        }
    }

    private void Configure(GStreamerSignalArgs args, Action<GStreamerRtspMedia> configure)
    {
        var native = (nint)args.Values[0]!;
        var media = new GStreamerRtspMedia(GstRtspServer.MediaGetElement((void*)native));

        // A media is let go when its last client has gone; what was taken from it goes then, not with the server.
        Gst.g_object_ref((void*)native);
        var ended = new GStreamerSignalConnection((void*)native, "unprepared", _ => Forget(native), Raise);
        lock (_media)
        {
            _media[native] = (media, ended);
        }

        ended.Connect(after: false);
        configure(media);
    }

    private void Forget(nint native)
    {
        (GStreamerRtspMedia Media, GStreamerSignalConnection Ended) entry;
        lock (_media)
        {
            if (!_media.Remove(native, out entry))
            {
                return;
            }
        }

        entry.Ended.Dispose();
        entry.Media.Release();
        Gst.g_object_unref((void*)native);
    }

    private void Raise(Exception ex) => SafeEvents.RaiseFailed(
        Failed, this, new MediaToolkitNetException(GstConstants.BackendName, $"configuring a stream threw: {ex.Message}", 0));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RemoveClient(void* server, void* client, void* data) => FilterRemove;
}
