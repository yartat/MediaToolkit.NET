#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Playback;
using MediaToolkitNet.Interop;
using MediaToolkitNet.Mpv.Native;

namespace MediaToolkitNet.Mpv;

/// <summary>
/// Player backed by libmpv.
/// </summary>
/// <remarks>
/// <para>
/// mpv owns the whole pipeline: it demuxes, decodes, renders video into a window
/// it creates itself and pushes audio to the system output. That makes it the
/// backend to reach for when you want playback rather than frames.
/// </para>
/// <para>
/// Set <see cref="Options"/> before <see cref="Open(string)"/> to configure mpv, e.g.
/// <c>wid</c> to embed the video into an existing window handle, or <c>vo=null</c>
/// to play audio only. The full property and option set is reachable through
/// <see cref="SetProperty(string,string)"/> and <see cref="GetPropertyString"/>.
/// </para>
/// </remarks>
public sealed unsafe class MpvPlayer : IMediaPlayer
{
    private readonly Lock _gate = new();

    private void* _handle;
    private Thread? _events;
    private CancellationTokenSource? _cancellation;
    private PlaybackState _state = PlaybackState.Closed;
    private long _positionTicks;
    private long _durationTicks;
    private double _volume = 1.0;
    private bool _seekable;
    private bool _disposed;
    private readonly ReaderWriterLockSlim _life = new();
    private MpvFilterChain? _videoFilters;
    private MpvFilterChain? _audioFilters;
    private readonly List<MpvStreamProtocol> _protocols = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, MpvHookHandler> _hooks = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, PendingCommand> _pending = new();
    private long _nextRequest;
    private readonly Dictionary<string, (Stream Stream, bool LeaveOpen)> _lent = [];
    private bool _lendingRegistered;

    /// <summary>
    /// Options applied to the mpv instance before it is initialised. The
    /// defaults keep mpv from touching the terminal and from quitting the
    /// process on its own.
    /// </summary>
    public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal)
    {
        ["terminal"] = "no",
        ["input-default-bindings"] = "no",
        ["input-vo-keyboard"] = "no",
        ["osc"] = "no",
        ["idle"] = "yes",
        // mpv would otherwise run yt-dlp for a URL and load user scripts.
        ["ytdl"] = "no",
        ["load-scripts"] = "no",
    };

    /// <summary>
    /// mpv's video filter chain. Filters survive a reload, so setting one before
    /// <see cref="Open(string)"/> applies it to everything played afterwards.
    /// </summary>
    public MpvFilterChain VideoFilters => _videoFilters ??= new MpvFilterChain(this, "vf");

    /// <summary>mpv's audio filter chain.</summary>
    public MpvFilterChain AudioFilters => _audioFilters ??= new MpvFilterChain(this, "af");

    /// <inheritdoc />
    public string Backend => Native.Mpv.BackendName;

    /// <inheritdoc />
    public PlaybackState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public TimeSpan Duration => new(Interlocked.Read(ref _durationTicks));

    /// <inheritdoc />
    public TimeSpan Position => new(Interlocked.Read(ref _positionTicks));

    /// <inheritdoc />
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0d, 1d);
            if (_handle is not null)
            {
                try
                {
                    // mpv expresses volume as a percentage.
                    SetProperty("volume", _volume * 100d);
                }
                catch (ObjectDisposedException)
                {
                    // Disposed while this was being set; nothing left to set it on.
                }
            }
        }
    }

    /// <inheritdoc />
    public bool CanSeek => _seekable;

    /// <inheritdoc />
    public event EventHandler<PlaybackStateChange>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<MediaToolkitNetException>? Failed;

    // mpv renders on its own, so no frames ever reach the caller. The events are
    // part of IMediaPlayer and are declared here to satisfy the contract.
#pragma warning disable CS0067
    /// <inheritdoc />
    public event VideoFrameHandler? VideoFrameDecoded;

    /// <inheritdoc />
    public event AudioFrameHandler? AudioFrameDecoded;
#pragma warning restore CS0067

    /// <summary>Raised for every mpv log message once <see cref="RequestLogMessages"/> has been called.</summary>
    public event EventHandler<string>? LogMessage;

    /// <inheritdoc />
    public void Open(string uri)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        EnsureHandle();
        Command("loadfile", uri, "replace");
        TransitionTo(PlaybackState.Stopped);
    }

    /// <summary>
    /// Plays a stream the host holds: a file being downloaded, an archive entry, memory. mpv reads it on its own
    /// thread, from the start, and seeks it when it is seekable; the stream must not be used elsewhere until the
    /// player is done with it. A non-seekable stream plays from start to end.
    /// </summary>
    /// <param name="stream">A readable stream.</param>
    /// <param name="leaveOpen">
    /// Leave <paramref name="stream"/> open when mpv is done with it. By default mpv closes it, which happens when
    /// something else is loaded or the player is disposed.
    /// </param>
    /// <exception cref="NotSupportedException">The libmpv in use has no stream callbacks.</exception>
    public void Open(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream cannot be read.", nameof(stream));
        }

        const string protocol = "mtnstream";
        RequireStreamCallbacks();
        EnsureHandle();

        var id = Guid.NewGuid().ToString("N");
        lock (_protocols)
        {
            if (!_lendingRegistered)
            {
                RegisterProtocol(protocol, uri =>
                {
                    lock (_protocols)
                    {
                        return _lent.Remove(uri[(protocol.Length + 3)..], out var lent) ? (lent.Stream, !lent.LeaveOpen) : (null, false);
                    }
                });
                _lendingRegistered = true;
            }

            _lent[id] = (stream, leaveOpen);
        }

        try
        {
            Open($"{protocol}://{id}");
        }
        catch
        {
            lock (_protocols)
            {
                _lent.Remove(id);
            }

            throw;
        }
    }

    /// <summary>
    /// Teaches mpv a protocol of your own: <c>Open("myproto://thing")</c> then calls <paramref name="open"/> with the
    /// URI, and mpv reads the stream it returns, from any thread it likes, and disposes it when it is done. Register
    /// before the first <c>Open</c> of that protocol; a protocol stays until the player is disposed.
    /// </summary>
    /// <param name="protocol">The scheme, without <c>://</c>: letters, digits, <c>+</c>, <c>-</c> and <c>.</c>.</param>
    /// <param name="open">Opens the stream a URI names, or returns <see langword="null"/> for none.</param>
    /// <exception cref="NotSupportedException">The libmpv in use has no stream callbacks.</exception>
    public void RegisterStreamProtocol(string protocol, MpvStreamOpener open)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentNullException.ThrowIfNull(open);
        if (!protocol.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
        {
            throw new ArgumentException("A protocol is made of letters, digits, '+', '-' and '.'.", nameof(protocol));
        }

        RequireStreamCallbacks();
        EnsureHandle();
        lock (_protocols)
        {
            RegisterProtocol(protocol, uri => (open(uri), true));
        }
    }

    private void RegisterProtocol(string protocol, Func<string, (Stream? Stream, bool Own)> open)
    {
        var registration = new MpvStreamProtocol(open);
        registration.Handle = System.Runtime.InteropServices.GCHandle.Alloc(registration);
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var name = new Utf8Scoped(protocol, scratch);
        int result;
        using (var guard = EnterHandle())
        {
            result = Native.Mpv.mpv_stream_cb_add_ro(
                guard.Handle,
                name.Pointer,
                (void*)System.Runtime.InteropServices.GCHandle.ToIntPtr(registration.Handle),
                MpvStreamCallbacks.OpenFunction);
        }

        if (result < 0)
        {
            registration.Handle.Free();
            Native.Mpv.Check(result, $"mpv_stream_cb_add_ro({protocol})");
        }

        _protocols.Add(registration);
    }

    private static void RequireStreamCallbacks()
    {
        if (!Native.Mpv.HasStreamCallbacks)
        {
            throw new NotSupportedException("this libmpv has no stream callbacks (mpv_stream_cb_add_ro)");
        }
    }

    /// <summary>Asks mpv to deliver log messages of at least the given level, e.g. <c>warn</c> or <c>info</c>.</summary>
    public void RequestLogMessages(string minimumLevel)
    {
        EnsureHandle();
        Span<byte> scratch = stackalloc byte[64];
        using var level = new Utf8Scoped(minimumLevel, scratch);
        using var guard = EnterHandle();
        Native.Mpv.Check(Native.Mpv.mpv_request_log_messages(guard.Handle, level.Pointer), "mpv_request_log_messages");
    }

    /// <inheritdoc />
    public void Play()
    {
        EnsureHandle();
        SetProperty("pause", false);
        TransitionTo(PlaybackState.Playing);
    }

    /// <inheritdoc />
    public void Pause()
    {
        if (_handle is null)
        {
            return;
        }

        try
        {
            SetProperty("pause", true);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        TransitionTo(PlaybackState.Paused);
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_handle is null)
        {
            return;
        }

        try
        {
            Command("stop");
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        Interlocked.Exchange(ref _positionTicks, 0);
        TransitionTo(PlaybackState.Stopped);
    }

    /// <inheritdoc />
    public void Seek(TimeSpan position)
    {
        EnsureHandle();
        if (!_seekable)
        {
            throw new InvalidOperationException("The current source does not support seeking.");
        }

        Command("seek", position.TotalSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture), "absolute");
    }

    // -------------------------------------------------------------- properties

    /// <summary>Sets a string property.</summary>
    public void SetProperty(string name, string value)
    {
        EnsureHandle();
        Span<byte> nameScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> valueScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, nameScratch);
        using var valueUtf8 = new Utf8Scoped(value, valueScratch);
        using var guard = EnterHandle();
        Native.Mpv.Check(
            Native.Mpv.mpv_set_property_string(guard.Handle, nameUtf8.Pointer, valueUtf8.Pointer), $"mpv_set_property({name})");
    }

    /// <summary>Sets a boolean property.</summary>
    public void SetProperty(string name, bool value)
    {
        EnsureHandle();
        var flag = value ? 1 : 0;
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, scratch);
        using var guard = EnterHandle();
        Native.Mpv.Check(
            Native.Mpv.mpv_set_property(guard.Handle, nameUtf8.Pointer, (int)MpvFormat.Flag, &flag), $"mpv_set_property({name})");
    }

    /// <summary>Sets a floating point property.</summary>
    public void SetProperty(string name, double value)
    {
        EnsureHandle();
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, scratch);
        using var guard = EnterHandle();
        Native.Mpv.Check(
            Native.Mpv.mpv_set_property(guard.Handle, nameUtf8.Pointer, (int)MpvFormat.Double, &value), $"mpv_set_property({name})");
    }

    /// <summary>Reads a property as a string, or <see langword="null"/> when it is unavailable.</summary>
    public string? GetPropertyString(string name)
    {
        EnsureHandle();
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, scratch);
        using var guard = EnterHandle();
        var raw = Native.Mpv.mpv_get_property_string(guard.Handle, nameUtf8.Pointer);
        if (raw is null)
        {
            return null;
        }

        try
        {
            return Utf8.ToManaged(raw);
        }
        finally
        {
            Native.Mpv.mpv_free(raw);
        }
    }

    /// <summary>Reads a floating point property, returning <paramref name="fallback"/> when it is unavailable.</summary>
    public double GetPropertyDouble(string name, double fallback = 0d)
    {
        EnsureHandle();
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, scratch);
        double value;
        using var guard = EnterHandle();
        return Native.Mpv.mpv_get_property(guard.Handle, nameUtf8.Pointer, (int)MpvFormat.Double, &value) < 0
            ? fallback
            : value;
    }

    // ---------------------------------------------------------------- nodes

    /// <summary>
    /// Reads a property as a node: the structured value mpv has for <c>track-list</c>, <c>chapter-list</c>,
    /// <c>metadata</c>, <c>command-list</c> and many more, where <see cref="GetPropertyString"/> would give JSON text
    /// at best.
    /// </summary>
    /// <returns>Returns the value, or <see langword="null"/> when the property is unavailable.</returns>
    /// <exception cref="NotSupportedException">The libmpv in use has no node API.</exception>
    public MpvNode? GetPropertyNode(string name)
    {
        RequireNodeApi();
        EnsureHandle();
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, scratch);
        var node = stackalloc byte[MpvNode.NativeSize];
        new Span<byte>(node, MpvNode.NativeSize).Clear();
        using var guard = EnterHandle();
        if (Native.Mpv.mpv_get_property(guard.Handle, nameUtf8.Pointer, (int)MpvFormat.Node, node) < 0)
        {
            return null;
        }

        try
        {
            return MpvNode.Read(node);
        }
        finally
        {
            Native.Mpv.mpv_free_node_contents(node);
        }
    }

    /// <summary>Sets a property to a structured value.</summary>
    /// <exception cref="NotSupportedException">The libmpv in use has no node API.</exception>
    public void SetPropertyNode(string name, MpvNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RequireNodeApi();
        EnsureHandle();
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, scratch);
        using var arena = new MpvNode.Arena();
        var native = arena.NewNode(value);
        using var guard = EnterHandle();
        Native.Mpv.Check(
            Native.Mpv.mpv_set_property(guard.Handle, nameUtf8.Pointer, (int)MpvFormat.Node, native), $"mpv_set_property({name})");
    }

    /// <summary>
    /// Runs a command given as a node, which is how a command takes named arguments
    /// (<see cref="MpvNode.Command"/>) and how it hands back data, such as the pixels of <c>screenshot-raw</c>.
    /// Not every command accepts named arguments; give those an array whose first element is the command name.
    /// </summary>
    /// <returns>Returns what the command returned, or <see cref="MpvNode.None"/> when it returns nothing.</returns>
    /// <exception cref="NotSupportedException">The libmpv in use has no node API.</exception>
    public MpvNode CommandNode(MpvNode arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        RequireNodeApi();
        EnsureHandle();
        using var arena = new MpvNode.Arena();
        var native = arena.NewNode(arguments);
        var result = stackalloc byte[MpvNode.NativeSize];
        new Span<byte>(result, MpvNode.NativeSize).Clear();
        using var guard = EnterHandle();
        Native.Mpv.Check(Native.Mpv.mpv_command_node(guard.Handle, native, result), "mpv_command_node");
        try
        {
            return MpvNode.Read(result);
        }
        finally
        {
            Native.Mpv.mpv_free_node_contents(result);
        }
    }

    /// <summary>The tracks of the file that is loaded, as mpv lists them. Empty until a file has loaded.</summary>
    public IReadOnlyList<MpvTrack> GetTracks() =>
        GetPropertyNode("track-list") is { Kind: MpvNodeKind.Array } list ? [.. list.Items.Select(MpvTrack.From)] : [];

    /// <summary>The chapters of the file that is loaded. Empty when it has none.</summary>
    public IReadOnlyList<MpvChapter> GetChapters() =>
        GetPropertyNode("chapter-list") is { Kind: MpvNodeKind.Array } list
            ? [.. list.Items.Select(c => new MpvChapter(c["title"]?.AsString() ?? string.Empty, TimeSpan.FromSeconds(c["time"]?.AsDouble() ?? 0)))]
            : [];

    /// <summary>The tags of the file that is loaded (<c>title</c>, <c>artist</c>...), with the container's own spelling of the keys.</summary>
    public IReadOnlyDictionary<string, string> GetMetadata()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (GetPropertyNode("metadata") is { Kind: MpvNodeKind.Map } map)
        {
            for (var i = 0; i < map.Keys.Count; i++)
            {
                if (map.Items[i].AsString() is { } value)
                {
                    result[map.Keys[i]] = value;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Takes the picture on screen into memory with mpv's <c>screenshot-raw</c>: the frame after mpv's own filters,
    /// scaling, colour conversion and subtitles, as the decoder alone could not give it. Pausing first makes the
    /// frame the one you want.
    /// </summary>
    /// <param name="content">What to draw into the picture.</param>
    /// <param name="format">The pixel layout to return.</param>
    /// <exception cref="NotSupportedException">The libmpv in use has no node API.</exception>
    /// <exception cref="MediaToolkitNetException">There is no frame to take, for instance before a file has loaded.</exception>
    public MpvScreenshot TakeScreenshot(
        MpvScreenshotContent content = MpvScreenshotContent.Subtitles,
        MpvScreenshotFormat format = MpvScreenshotFormat.Bgra) =>
        MpvScreenshot.From(
            CommandNode(MpvNode.Command(
                "screenshot-raw",
                ("flags", MpvScreenshot.Name(content)),
                ("format", MpvScreenshot.Name(format)))),
            format);

    // ------------------------------------------------------------------ hooks

    /// <summary>
    /// Asks mpv to wait for <paramref name="handler"/> at a hook: <c>on_load</c> (before a file is opened: read and
    /// rewrite <c>stream-open-filename</c>, set <c>file-local-options/...</c>), <c>on_load_fail</c>,
    /// <c>on_preloaded</c>, <c>on_loaded</c> (0.42), <c>on_unload</c>, <c>on_before_start_file</c> and
    /// <c>on_after_end_file</c>. mpv does nothing more with the file until every handler has let it go, so a handler
    /// that never does makes the player freeze.
    /// </summary>
    /// <param name="name">The hook's name.</param>
    /// <param name="handler">Runs on the player's event thread; see <see cref="MpvHookHandler"/>.</param>
    /// <param name="priority">Handlers with a lower number run first.</param>
    /// <exception cref="NotSupportedException">The libmpv in use has no hooks.</exception>
    public void AddHook(string name, MpvHookHandler handler, int priority = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        if (!Native.Mpv.HasHooks)
        {
            throw new NotSupportedException("this libmpv has no hooks (mpv_hook_add)");
        }

        EnsureHandle();
        var id = (ulong)Interlocked.Increment(ref _nextRequest);
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, scratch);
        _hooks[id] = handler;
        int result;
        using (var guard = EnterHandle())
        {
            result = Native.Mpv.mpv_hook_add(guard.Handle, id, nameUtf8.Pointer, priority);
        }

        if (result < 0)
        {
            _hooks.TryRemove(id, out _);
            Native.Mpv.Check(result, $"mpv_hook_add({name})");
        }
    }

    internal void ContinueHook(ulong id)
    {
        try
        {
            using var guard = EnterHandle();
            Native.Mpv.mpv_hook_continue(guard.Handle, id);
        }
        catch (ObjectDisposedException)
        {
            // The player is gone, and so is whatever was waiting.
        }
    }

    private void RunHook(MpvEvent* evt)
    {
        if (evt->Data is null)
        {
            return;
        }

        // mpv_event_hook: { const char *name; uint64_t id; }
        var hook = new MpvHook(this, Utf8.ToManagedOrEmpty(*(byte**)evt->Data), *(ulong*)((byte*)evt->Data + 8));
        try
        {
            if (_hooks.TryGetValue(evt->ReplyUserData, out var handler))
            {
                handler(hook);
            }
        }
        catch (Exception ex)
        {
            // A handler that throws must not leave mpv waiting, nor stop the loop.
            SafeEvents.RaiseFailed(Failed, this, new MediaToolkitNetException(Backend, $"the {hook.Name} hook handler threw: {ex.Message}", 0));
        }
        finally
        {
            if (!hook.Deferred)
            {
                hook.Continue();
            }
        }
    }

    // ------------------------------------------------------ asynchronous commands

    private sealed record PendingCommand(TaskCompletionSource<MpvNode> Source, CancellationToken Token);

    /// <summary>
    /// Runs a command without waiting for it (<c>mpv_command_node_async</c>): the task finishes when mpv has, with what
    /// the command returned. Takes arguments as <see cref="CommandNode"/> does.
    /// </summary>
    /// <param name="arguments">The command, by name with named arguments or as an array.</param>
    /// <param name="cancellationToken">
    /// Asks mpv to stop the command (<c>mpv_abort_async_command</c>), which only some commands honour, such as
    /// <c>subprocess</c>. The task then ends as cancelled, whatever the command went on to return.
    /// </param>
    /// <exception cref="NotSupportedException">The libmpv in use has no asynchronous commands.</exception>
    /// <exception cref="ObjectDisposedException">The task: the player was disposed before the command finished.</exception>
    public Task<MpvNode> CommandNodeAsync(MpvNode arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!Native.Mpv.HasAsyncCommands)
        {
            throw new NotSupportedException("this libmpv has no asynchronous commands (mpv_command_node_async)");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<MpvNode>(cancellationToken);
        }

        EnsureHandle();
        var id = (ulong)Interlocked.Increment(ref _nextRequest);
        var source = new TaskCompletionSource<MpvNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new PendingCommand(source, cancellationToken);

        try
        {
            using var arena = new MpvNode.Arena();
            var native = arena.NewNode(arguments);
            using var guard = EnterHandle();
            Native.Mpv.Check(Native.Mpv.mpv_command_node_async(guard.Handle, id, native), "mpv_command_node_async");
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(() => AbortCommand(id));
            source.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        }

        return source.Task;
    }

    /// <summary>Runs a command without waiting for it. See <see cref="CommandNodeAsync"/>.</summary>
    /// <param name="arguments">The command name, then its arguments.</param>
    public Task CommandAsync(params string[] arguments)
    {
        ArgumentOutOfRangeException.ThrowIfZero(arguments.Length);
        return CommandNodeAsync(MpvNode.Array(arguments.Select(MpvNode.From)));
    }

    private void AbortCommand(ulong id)
    {
        try
        {
            using var guard = EnterHandle();
            Native.Mpv.mpv_abort_async_command(guard.Handle, id);
        }
        catch (ObjectDisposedException)
        {
            // Disposal fails what is pending.
        }
    }

    private void CompleteCommand(MpvEvent* evt)
    {
        if (!_pending.TryRemove(evt->ReplyUserData, out var pending))
        {
            return;
        }

        if (pending.Token.IsCancellationRequested)
        {
            pending.Source.TrySetCanceled(pending.Token);
        }
        else if (evt->Error < 0)
        {
            pending.Source.TrySetException(new MediaToolkitNetException(
                Backend, $"mpv command: {Native.Mpv.Describe(evt->Error)}", evt->Error));
        }
        else
        {
            // mpv_event_command: { mpv_node result; }
            pending.Source.TrySetResult(evt->Data is null ? MpvNode.None : MpvNode.Read((byte*)evt->Data));
        }
    }

    private void FailPending()
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.Source.TrySetException(new ObjectDisposedException(nameof(MpvPlayer), "the player ended before the command did"));
            }
        }
    }

    private static void RequireNodeApi()
    {
        if (!Native.Mpv.HasNodeApi)
        {
            throw new NotSupportedException("this libmpv has no node API (mpv_command_node, mpv_free_node_contents)");
        }
    }

    /// <summary>Runs an mpv command, e.g. <c>Command("loadfile", path, "append-play")</c>.</summary>
    public void Command(params string[] arguments)
    {
        EnsureHandle();
        ArgumentOutOfRangeException.ThrowIfZero(arguments.Length);

        // Sized by the caller's arguments, so not stackalloc; zeroed, so a failure part
        // way through the loop leaves only entries that were set to free.
        var native = (byte**)System.Runtime.InteropServices.NativeMemory.AllocZeroed(
            (nuint)(arguments.Length + 1), (nuint)sizeof(byte*));
        try
        {
            for (var i = 0; i < arguments.Length; i++)
            {
                native[i] = Utf8.Allocate(arguments[i]);
            }

            native[arguments.Length] = null;
            using var guard = EnterHandle();
            Native.Mpv.Check(Native.Mpv.mpv_command(guard.Handle, native), $"mpv_command({arguments[0]})");
        }
        finally
        {
            for (var i = 0; i < arguments.Length; i++)
            {
                Utf8.Free(native[i]);
            }

            System.Runtime.InteropServices.NativeMemory.Free(native);
        }
    }

    // ------------------------------------------------------------- event loop

    /// <summary>
    /// Holds the native handle alive for one call: <see cref="Dispose"/> destroys it
    /// only once every call in flight has returned.
    /// </summary>
    private ReadGuard EnterHandle()
    {
        _life.EnterReadLock();
        var handle = _handle;
        if (_disposed || handle is null)
        {
            _life.ExitReadLock();
            throw new ObjectDisposedException(nameof(MpvPlayer));
        }

        return new ReadGuard(_life, handle);
    }

    private readonly struct ReadGuard(ReaderWriterLockSlim life, void* handle) : IDisposable
    {
        public void* Handle { get; } = handle;

        public void Dispose() => life.ExitReadLock();
    }

    private void EnsureHandle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle is not null)
        {
            return;
        }

        lock (_gate)
        {
            if (_handle is not null)
            {
                return;
            }

            Native.Mpv.EnsureLoaded();

            var handle = Native.Mpv.mpv_create();
            if (handle is null)
            {
                throw new MediaToolkitNetException(Backend, "mpv_create returned NULL", 0);
            }

            try
            {
                Span<byte> keyScratch = stackalloc byte[Utf8Scoped.StackThreshold];
                Span<byte> valueScratch = stackalloc byte[Utf8Scoped.StackThreshold];
                foreach (var (key, value) in Options)
                {
                    using var keyUtf8 = new Utf8Scoped(key, keyScratch);
                    using var valueUtf8 = new Utf8Scoped(value, valueScratch);
                    Native.Mpv.Check(
                        Native.Mpv.mpv_set_option_string(handle, keyUtf8.Pointer, valueUtf8.Pointer),
                        $"mpv_set_option_string({key})");
                }

                Native.Mpv.Check(Native.Mpv.mpv_initialize(handle), "mpv_initialize");
            }
            catch
            {
                Native.Mpv.mpv_terminate_destroy(handle);
                throw;
            }

            _handle = handle;
            Observe("time-pos", MpvFormat.Double);
            Observe("duration", MpvFormat.Double);
            Observe("pause", MpvFormat.Flag);
            Observe("seekable", MpvFormat.Flag);
            Observe("eof-reached", MpvFormat.Flag);

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            var handleValue = (nint)handle;
            _events = new Thread(() => EventLoop(handleValue, token))
            {
                IsBackground = true,
                Name = "MediaToolkitNet.Mpv events",
            };
            _events.Start();
        }
    }

    private void Observe(string name, MpvFormat format)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, scratch);
        Native.Mpv.Check(
            Native.Mpv.mpv_observe_property(_handle, 0, nameUtf8.Pointer, (int)format), $"mpv_observe_property({name})");
    }

    /// <remarks>
    /// The handle is passed in rather than read from the field so that disposal,
    /// which clears the field, cannot race the loop into dereferencing null.
    /// </remarks>
    private void EventLoop(nint handleValue, CancellationToken token)
    {
        var handle = (void*)handleValue;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var evt = Native.Mpv.mpv_wait_event(handle, 0.1);
                if (evt is null || evt->EventId == MpvEventId.None)
                {
                    continue;
                }

                switch (evt->EventId)
                {
                    case MpvEventId.Shutdown:
                        FailPending();
                        TransitionTo(PlaybackState.Closed);
                        return;

                    case MpvEventId.EndFile:
                        TransitionTo(PlaybackState.Ended);
                        break;

                    case MpvEventId.FileLoaded:
                        TransitionTo(PlaybackState.Playing);
                        break;

                    case MpvEventId.LogMessage:
                        RaiseLogMessage(evt);
                        break;

                    case MpvEventId.PropertyChange:
                        HandlePropertyChange((MpvEventProperty*)evt->Data);
                        break;

                    case MpvEventId.CommandReply:
                        CompleteCommand(evt);
                        break;

                    case MpvEventId.Hook:
                        RunHook(evt);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            var error = ex as MediaToolkitNetException ?? new MediaToolkitNetException("mpv event loop error.", ex);
            TransitionTo(PlaybackState.Faulted);
            SafeEvents.RaiseFailed(Failed, this, error);
        }
    }

    private void RaiseLogMessage(MpvEvent* evt)
    {
        var handler = LogMessage;
        if (handler is null || evt->Data is null)
        {
            return;
        }

        // mpv_event_log_message: { const char *prefix; const char *level; const char *text; int log_level; }
        var raw = (byte**)evt->Data;
        var prefix = Utf8.ToManagedOrEmpty(raw[0]);
        var level = Utf8.ToManagedOrEmpty(raw[1]);
        var text = Utf8.ToManagedOrEmpty(raw[2]).TrimEnd('\n');
        handler(this, $"[{level}] {prefix}: {text}");
    }

    private void HandlePropertyChange(MpvEventProperty* property)
    {
        if (property is null || property->Data is null)
        {
            return;
        }

        var name = Utf8.ToManagedOrEmpty(property->Name);
        switch (name)
        {
            case "time-pos" when property->Format == MpvFormat.Double:
                Interlocked.Exchange(ref _positionTicks, TimeSpan.FromSeconds(*(double*)property->Data).Ticks);
                break;

            case "duration" when property->Format == MpvFormat.Double:
                Interlocked.Exchange(ref _durationTicks, TimeSpan.FromSeconds(*(double*)property->Data).Ticks);
                break;

            case "seekable" when property->Format == MpvFormat.Flag:
                _seekable = *(int*)property->Data != 0;
                break;

            case "pause" when property->Format == MpvFormat.Flag:
                TransitionTo(*(int*)property->Data != 0 ? PlaybackState.Paused : PlaybackState.Playing);
                break;

            case "eof-reached" when property->Format == MpvFormat.Flag && *(int*)property->Data != 0:
                TransitionTo(PlaybackState.Ended);
                break;
        }
    }

    private void TransitionTo(PlaybackState next)
    {
        PlaybackState previous;
        lock (_gate)
        {
            if (_state == next)
            {
                return;
            }

            previous = _state;
            _state = next;
        }

        StateChanged?.Invoke(this, new PlaybackStateChange(previous, next));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Thread? events;
        void* handle;
        lock (_gate)
        {
            _cancellation?.Cancel();
            events = _events;
            handle = _handle;
            _events = null;
            _handle = null;
        }

        if (handle is not null)
        {
            // Wake the event thread so it notices the cancellation promptly.
            Native.Mpv.mpv_wakeup(handle);
        }

        events?.Join(TimeSpan.FromSeconds(2));

        if (handle is not null)
        {
            // Calls already inside mpv finish first; later ones find the player disposed.
            _life.EnterWriteLock();
            try
            {
                Native.Mpv.mpv_terminate_destroy(handle);
            }
            finally
            {
                _life.ExitWriteLock();
            }
        }

        lock (_gate)
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }

        FailPending();

        // The core is gone, so no stream opens or reads any more; streams it had open are closed.
        lock (_protocols)
        {
            foreach (var protocol in _protocols)
            {
                protocol.Handle.Free();
            }

            _protocols.Clear();
            foreach (var (stream, leaveOpen) in _lent.Values)
            {
                if (!leaveOpen)
                {
                    stream.Dispose();
                }
            }

            _lent.Clear();
        }
    }
}
