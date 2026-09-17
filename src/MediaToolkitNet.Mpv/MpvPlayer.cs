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
/// Set <see cref="Options"/> before <see cref="Open"/> to configure mpv, e.g.
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
    };

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
                // mpv expresses volume as a percentage.
                SetProperty("volume", _volume * 100d);
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

    /// <summary>Asks mpv to deliver log messages of at least the given level, e.g. <c>warn</c> or <c>info</c>.</summary>
    public void RequestLogMessages(string minimumLevel)
    {
        EnsureHandle();
        Span<byte> scratch = stackalloc byte[64];
        using var level = new Utf8Scoped(minimumLevel, scratch);
        Native.Mpv.Check(Native.Mpv.mpv_request_log_messages(_handle, level.Pointer), "mpv_request_log_messages");
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

        SetProperty("pause", true);
        TransitionTo(PlaybackState.Paused);
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_handle is null)
        {
            return;
        }

        Command("stop");
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
        Native.Mpv.Check(
            Native.Mpv.mpv_set_property_string(_handle, nameUtf8.Pointer, valueUtf8.Pointer), $"mpv_set_property({name})");
    }

    /// <summary>Sets a boolean property.</summary>
    public void SetProperty(string name, bool value)
    {
        EnsureHandle();
        var flag = value ? 1 : 0;
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, scratch);
        Native.Mpv.Check(
            Native.Mpv.mpv_set_property(_handle, nameUtf8.Pointer, (int)MpvFormat.Flag, &flag), $"mpv_set_property({name})");
    }

    /// <summary>Sets a floating point property.</summary>
    public void SetProperty(string name, double value)
    {
        EnsureHandle();
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, scratch);
        Native.Mpv.Check(
            Native.Mpv.mpv_set_property(_handle, nameUtf8.Pointer, (int)MpvFormat.Double, &value), $"mpv_set_property({name})");
    }

    /// <summary>Reads a property as a string, or <see langword="null"/> when it is unavailable.</summary>
    public string? GetPropertyString(string name)
    {
        EnsureHandle();
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, scratch);
        var raw = Native.Mpv.mpv_get_property_string(_handle, nameUtf8.Pointer);
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
        return Native.Mpv.mpv_get_property(_handle, nameUtf8.Pointer, (int)MpvFormat.Double, &value) < 0
            ? fallback
            : value;
    }

    /// <summary>Runs an mpv command, e.g. <c>Command("loadfile", path, "append-play")</c>.</summary>
    public void Command(params string[] arguments)
    {
        EnsureHandle();
        ArgumentOutOfRangeException.ThrowIfZero(arguments.Length);

        var native = stackalloc byte*[arguments.Length + 1];
        try
        {
            for (var i = 0; i < arguments.Length; i++)
            {
                native[i] = Utf8.Allocate(arguments[i]);
            }

            native[arguments.Length] = null;
            Native.Mpv.Check(Native.Mpv.mpv_command(_handle, native), $"mpv_command({arguments[0]})");
        }
        finally
        {
            for (var i = 0; i < arguments.Length; i++)
            {
                Utf8.Free(native[i]);
            }
        }
    }

    // ------------------------------------------------------------- event loop

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
                }
            }
        }
        catch (Exception ex)
        {
            var error = ex as MediaToolkitNetException ?? new MediaToolkitNetException("mpv event loop error.", ex);
            TransitionTo(PlaybackState.Faulted);
            Failed?.Invoke(this, error);
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
            Native.Mpv.mpv_terminate_destroy(handle);
        }

        lock (_gate)
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }
}
