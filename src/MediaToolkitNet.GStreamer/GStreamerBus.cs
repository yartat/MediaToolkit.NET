#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer;

/// <summary>
/// The message bus of a pipeline: where its elements report errors and warnings, tags, buffering, state changes and
/// the end of the stream. Subscribing to <see cref="MessageReceived"/> starts a thread that delivers them.
/// </summary>
/// <remarks>
/// <para>
/// The bus has one reader. While the thread runs, it is that reader: it keeps the end of the stream and the last error
/// for <see cref="GStreamerPipeline.WaitForCompletion"/> and the pipeline's own error reporting, which would otherwise
/// find the bus empty. A message of a type outside <see cref="Mask"/> is dropped, not queued.
/// </para>
/// <para>
/// Handlers run on the bus thread, one message at a time, in the order the messages were posted. A handler that
/// throws is reported through <see cref="Failed"/> and the thread goes on.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerBus : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ErrorGrace = TimeSpan.FromMilliseconds(300);

    private readonly Lock _gate = new();
    private readonly void* _pipeline;
    private readonly ManualResetEventSlim _completed = new();
    private readonly ManualResetEventSlim _errorSeen = new();
    private void* _bus;
    private EventHandler<GStreamerMessage>? _handlers;
    private Thread? _pump;
    private CancellationTokenSource? _stop;
    private string? _pendingError;
    private string? _completionError;
    private bool _disposed;

    internal GStreamerBus(void* bus, void* pipeline)
    {
        _bus = bus;
        _pipeline = pipeline;
    }

    /// <summary>
    /// The kinds of message that are delivered. The default is every kind that says what is happening, which leaves out
    /// the very frequent ones (quality-of-service, progress, stream status and the like).
    /// </summary>
    public GstMessageType Mask { get; set; } =
        GstMessageType.Error | GstMessageType.Warning | GstMessageType.Info | GstMessageType.Eos | GstMessageType.Tag |
        GstMessageType.Buffering | GstMessageType.StateChanged | GstMessageType.Application | GstMessageType.Element |
        GstMessageType.AsyncDone | GstMessageType.StreamStart | GstMessageType.DurationChanged | GstMessageType.Latency;

    /// <summary>
    /// Whether a state change is reported only when the pipeline itself made it (the default), or for every element.
    /// </summary>
    public bool PipelineStateOnly { get; set; } = true;

    /// <summary>Raised for each message in <see cref="Mask"/>, on the bus thread. The first subscriber starts that thread.</summary>
    public event EventHandler<GStreamerMessage>? MessageReceived
    {
        add
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_gate)
            {
                _handlers += value;
                StartPump();
            }
        }

        remove
        {
            lock (_gate)
            {
                _handlers -= value;
            }
        }
    }

    /// <summary>Raised when a handler of <see cref="MessageReceived"/> throws, which must not end the process.</summary>
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <summary>True while the thread that delivers messages is running.</summary>
    public bool IsDelivering
    {
        get
        {
            lock (_gate)
            {
                return _pump is not null;
            }
        }
    }

    /// <summary>
    /// Posts an application message with an empty structure of that name, as from the pipeline. What an application
    /// does to tell its own bus handler something, and how a test sends a message with no media.
    /// </summary>
    /// <param name="structureName">The name of the message's structure.</param>
    public void PostApplication(string structureName)
    {
        ArgumentException.ThrowIfNullOrEmpty(structureName);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var name = new Utf8Scoped(structureName, scratch);
        var structure = Gst.gst_structure_new_empty(name.Pointer);
        var message = structure is null ? null : Gst.gst_message_new_application(_pipeline, structure);
        if (message is null || Gst.gst_bus_post(_bus, message) == 0)
        {
            throw new MediaToolkitNetException(GstConstants.BackendName, "the bus did not accept the message", 0);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Thread? pump;
        lock (_gate)
        {
            pump = _pump;
            _pump = null;
            _stop?.Cancel();
        }

        // Only a thread that has stopped is done with the bus; one stuck in a handler leaves it to the process.
        if (pump is not null && !pump.Join(TimeSpan.FromSeconds(3)))
        {
            return;
        }

        _stop?.Dispose();
        _completed.Dispose();
        _errorSeen.Dispose();
        if (_bus is not null)
        {
            Gst.gst_object_unref(_bus);
            _bus = null;
        }
    }

    // ------------------------------------------------------- the pipeline's view

    // Waits for the stream to end or for an error: from what the thread saw when it is running, from the bus when not.
    internal bool WaitForCompletion(TimeSpan timeout)
    {
        if (IsDelivering)
        {
            if (!_completed.Wait(timeout))
            {
                return false;
            }

            if (_completionError is { } error)
            {
                throw new MediaToolkitNetException(GstConstants.BackendName, error, 0);
            }

            return true;
        }

        var message = Gst.gst_bus_timed_pop_filtered(_bus, Nanoseconds(timeout), (uint)(GstMessageType.Eos | GstMessageType.Error));
        if (message is null)
        {
            return false;
        }

        try
        {
            if (GstLayout.TypeOfMessage(message) != GstMessageType.Error)
            {
                return true;
            }

            throw new MediaToolkitNetException(GstConstants.BackendName, ErrorOf(message), 0);
        }
        finally
        {
            Gst.gst_mini_object_unref(message);
        }
    }

    // The error the pipeline posted, if it did. While the thread runs it may not have read the message yet, so a
    // caller that knows an error is coming says how long to give it.
    internal string? TakeError(bool expected)
    {
        if (IsDelivering)
        {
            if (expected && _pendingError is null)
            {
                _errorSeen.Wait(ErrorGrace);
            }

            var error = _pendingError;
            _pendingError = null;
            _errorSeen.Reset();
            return error;
        }

        var message = Gst.gst_bus_timed_pop_filtered(_bus, 0, (uint)GstMessageType.Error);
        if (message is null)
        {
            return null;
        }

        try
        {
            return ErrorOf(message);
        }
        finally
        {
            Gst.gst_mini_object_unref(message);
        }
    }

    // ------------------------------------------------------------------- thread

    private void StartPump()
    {
        if (_pump is not null)
        {
            return;
        }

        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _pump = new Thread(() => Pump(token))
        {
            IsBackground = true,
            Name = "MediaToolkitNet.GStreamer bus",
        };
        _pump.Start();
    }

    private void Pump(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var message = Gst.gst_bus_timed_pop(_bus, Nanoseconds(PollInterval));
            if (message is null)
            {
                continue;
            }

            try
            {
                Handle(message);
            }
            catch (Exception ex)
            {
                SafeEvents.RaiseFailed(Failed, this, new MediaToolkitNetException(GstConstants.BackendName, $"a bus message could not be handled: {ex.Message}", 0));
            }
            finally
            {
                Gst.gst_mini_object_unref(message);
            }
        }
    }

    private void Handle(void* message)
    {
        var type = GstLayout.TypeOfMessage(message);
        var source = GstLayout.SourceOfMessage(message);
        var fromPipeline = source == _pipeline;

        // What the pipeline's waiters need is kept whether or not anyone asked for the message.
        string? error = null;
        if (type == GstMessageType.Error)
        {
            error = ErrorOf(message);
            _pendingError = error;
            _completionError = error;
            _errorSeen.Set();
            _completed.Set();
        }
        else if (type == GstMessageType.Eos)
        {
            _completed.Set();
        }

        var parsed = Parse(message, type, source, fromPipeline, error);
        if (type == GstMessageType.StateChanged && fromPipeline && parsed.NewState is GstState.Null or GstState.Ready)
        {
            // The pipeline is being used again: what it finished or failed with before is history.
            _completed.Reset();
            _completionError = null;
        }

        if ((type & Mask) == 0 || (type == GstMessageType.StateChanged && PipelineStateOnly && !fromPipeline))
        {
            return;
        }

        EventHandler<GStreamerMessage>? handlers;
        lock (_gate)
        {
            handlers = _handlers;
        }

        if (handlers is null)
        {
            return;
        }

        try
        {
            handlers(this, parsed);
        }
        catch (Exception ex)
        {
            SafeEvents.RaiseFailed(Failed, this, new MediaToolkitNetException(GstConstants.BackendName, $"a bus message handler threw: {ex.Message}", 0));
        }
    }

    private static GStreamerMessage Parse(void* message, GstMessageType type, void* source, bool fromPipeline, string? error)
    {
        string? name = null;
        if (source is not null)
        {
            name = Gst.TakeString(Gst.gst_object_get_name(source));
        }

        var result = new GStreamerMessage(type, name, fromPipeline);
        switch (type)
        {
            case GstMessageType.Error:
                return result with { Text = TextOf(message, GstMessageType.Error, out var errorDebug), Debug = errorDebug };
            case GstMessageType.Warning:
            case GstMessageType.Info:
                return result with { Text = TextOf(message, type, out var debug), Debug = debug };
            case GstMessageType.StateChanged:
                int old, current, pending;
                Gst.gst_message_parse_state_changed(message, &old, &current, &pending);
                return result with { OldState = (GstState)old, NewState = (GstState)current, PendingState = (GstState)pending };
            case GstMessageType.Buffering:
                int percent;
                Gst.gst_message_parse_buffering(message, &percent);
                return result with { Percent = percent };
            case GstMessageType.Tag:
                return result with { Tags = TagsOf(message) };
            default:
                var structure = Gst.gst_message_get_structure(message);
                return structure is null
                    ? result
                    : result with
                    {
                        StructureName = Utf8.ToManagedOrEmpty(Gst.gst_structure_get_name(structure)),
                        Structure = Gst.TakeString(Gst.gst_structure_to_string(structure)),
                    };
        }
    }

    private static string TextOf(void* message, GstMessageType type, out string? debug)
    {
        void* error = null;
        byte* detail = null;
        if (type == GstMessageType.Error)
        {
            Gst.gst_message_parse_error(message, &error, &detail);
        }
        else if (type == GstMessageType.Warning)
        {
            Gst.gst_message_parse_warning(message, &error, &detail);
        }
        else
        {
            Gst.gst_message_parse_info(message, &error, &detail);
        }

        debug = detail is null ? null : Gst.TakeString(detail);
        return Gst.TakeError(error);
    }

    private static string ErrorOf(void* message)
    {
        var text = TextOf(message, GstMessageType.Error, out var debug);
        return string.IsNullOrEmpty(debug) ? text : $"{text} ({debug})";
    }

    private static Dictionary<string, string> TagsOf(void* message)
    {
        var result = new Dictionary<string, string>();
        void* list = null;
        Gst.gst_message_parse_tag(message, &list);
        if (list is null)
        {
            return result;
        }

        try
        {
            var count = Gst.gst_tag_list_n_tags(list);
            for (var i = 0u; i < count; i++)
            {
                var tag = Gst.gst_tag_list_nth_tag_name(list, i);
                byte* value = null;

                // Only tags with a text value come out; a number or a date has no string form to take here.
                if (Gst.gst_tag_list_get_string(list, tag, &value) != 0 && value is not null)
                {
                    result[Utf8.ToManagedOrEmpty(tag)] = Gst.TakeString(value);
                }
            }
        }
        finally
        {
            Gst.gst_mini_object_unref(list);
        }

        return result;
    }

    private static ulong Nanoseconds(TimeSpan timeout) =>
        timeout < TimeSpan.Zero
            ? GstConstants.ClockTimeNone
            : (ulong)(timeout.Ticks * GstConstants.NanosecondsPerTick);
}
