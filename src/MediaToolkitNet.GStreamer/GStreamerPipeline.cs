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
/// A GStreamer pipeline, built from a <c>gst-launch</c> description.
/// </summary>
/// <remarks>
/// <para>
/// This is what GStreamer has in place of the DirectShow filter graph, and it
/// is assembled the same way: elements are created, linked and driven through
/// states. The difference is that GStreamer's parser does all of that from one
/// string, so
/// <c>filesrc location=a.mp4 ! decodebin ! videoconvert ! appsink name=sink</c>
/// is the whole graph, and <c>decodebin</c> is the counterpart of
/// <c>RenderFile</c>: it works out which demuxer and decoder the stream needs.
/// </para>
/// <para>
/// Frames come out through <see cref="GStreamerSink"/>, which wraps the
/// <c>appsink</c> the description names.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerPipeline : IDisposable
{
    private readonly List<IDisposable> _tracked = [];
    private void* _pipeline;
    private bool _disposed;

    private GStreamerPipeline(void* pipeline, string description)
    {
        _pipeline = pipeline;
        Description = description;
        Bus = new GStreamerBus(Gst.gst_element_get_bus(pipeline), pipeline);
    }

    /// <summary>The description this pipeline was built from.</summary>
    public string Description { get; }

    /// <summary>
    /// The pipeline's message bus: subscribe to its <see cref="GStreamerBus.MessageReceived"/> to hear about errors,
    /// tags, buffering and state changes as they happen.
    /// </summary>
    public GStreamerBus Bus { get; }

    /// <summary>The raw <c>GstElement</c> of the pipeline itself.</summary>
    public void* Handle => _pipeline;

    /// <summary>
    /// Builds a pipeline from a <c>gst-launch</c> description.
    /// </summary>
    /// <param name="description">
    /// The pipeline, for example
    /// <c>videotestsrc ! videoconvert ! autovideosink</c>.
    /// </param>
    /// <exception cref="MediaToolkitNetException">
    /// The description could not be parsed, usually because a plugin providing
    /// one of the elements is not installed. GStreamer names the element.
    /// </exception>
    public static GStreamerPipeline Parse(string description) => Parse(description, strict: false);

    /// <summary>
    /// Builds a pipeline, optionally refusing one the parser could only build in
    /// part. Without <paramref name="strict"/> a link that fails is reported and
    /// dropped, and the pipeline comes back without it.
    /// </summary>
    internal static GStreamerPipeline Parse(string description, bool strict)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Gst.EnsureLoaded();
        GstLayout.Require();

        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(description, scratch);

        void* error = null;
        var pipeline = strict
            ? Gst.gst_parse_launch_full(utf8.Pointer, null, GstTypes.ParseFatalErrors, &error)
            : Gst.gst_parse_launch(utf8.Pointer, &error);
        if (pipeline is null)
        {
            throw new MediaToolkitNetException(
                GstConstants.BackendName, $"gst_parse_launch failed: {Gst.TakeError(error)}", 0);
        }

        // A parsed pipeline arrives with a floating reference; sinking it makes
        // this object the owner, so Dispose is the only thing that frees it.
        Gst.gst_object_ref_sink(pipeline);

        // The parser can succeed and still have something to say.
        if (error is not null)
        {
            Gst.g_error_free(error);
        }

        return new GStreamerPipeline(pipeline, description);
    }

    /// <summary>The element the description gave that name, or <see langword="null"/>.</summary>
    /// <remarks>The pipeline owns the result and disposes it with itself.</remarks>
    public GStreamerElement? FindElement(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        EnsureAlive();

        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(name, scratch);

        var element = Gst.gst_bin_get_by_name(_pipeline, utf8.Pointer);
        if (element is null)
        {
            return null;
        }

        var wrapper = new GStreamerElement(element, name);
        _tracked.Add(wrapper);
        return wrapper;
    }

    /// <summary>The pipeline itself as an element, for the properties and signals of a bin such as <c>playbin3</c>.</summary>
    /// <remarks>The pipeline owns the result and disposes it with itself.</remarks>
    public GStreamerElement AsElement()
    {
        EnsureAlive();
        var wrapper = new GStreamerElement(Gst.gst_object_ref(_pipeline), "pipeline");
        _tracked.Add(wrapper);
        return wrapper;
    }

    /// <summary>
    /// Wraps the <c>appsink</c> the description named, for reading frames.
    /// </summary>
    /// <param name="name">The name given to the appsink in the description.</param>
    /// <exception cref="MediaToolkitNetException">The pipeline has no element of that name.</exception>
    public GStreamerSink GetSink(string name)
    {
        var element = FindElement(name)
            ?? throw new MediaToolkitNetException(
                GstConstants.BackendName, $"the pipeline has no element named {name}.", 0);

        var sink = new GStreamerSink(element);
        _tracked.Add(sink);
        return sink;
    }

    /// <summary>The <c>appsrc</c> the description gave that name, as the way to push frames into the pipeline.</summary>
    /// <exception cref="MediaToolkitNetException">There is no such element, or it is not an appsrc.</exception>
    public GStreamerSource GetSource(string name)
    {
        var element = FindElement(name)
            ?? throw new MediaToolkitNetException(
                GstConstants.BackendName, $"the pipeline has no element named {name}.", 0);

        if (Utf8.ToManagedOrEmpty(Gst.g_type_name_from_instance(element.Handle)) != "GstAppSrc")
        {
            element.Dispose();
            throw new MediaToolkitNetException(GstConstants.BackendName, $"the element {name} is not an appsrc.", 0);
        }

        var source = new GStreamerSource(element);
        _tracked.Add(source);
        return source;
    }

    /// <summary>Moves the pipeline to PLAYING.</summary>
    public void Play() => SetState(GstState.Playing);

    /// <summary>Moves the pipeline to PAUSED, which is also where it prerolls.</summary>
    public void Pause() => SetState(GstState.Paused);

    /// <summary>Moves the pipeline to NULL, releasing every resource it holds.</summary>
    public void Stop() => SetState(GstState.Null);

    /// <summary>
    /// Requests a state and waits for the pipeline to reach it when the change
    /// is asynchronous, which it is for anything with a real source.
    /// </summary>
    /// <param name="state">The state to move to.</param>
    /// <param name="timeout">How long to wait; the default gives up after five seconds.</param>
    /// <exception cref="MediaToolkitNetException">The pipeline refused the change.</exception>
    public void SetState(GstState state, TimeSpan? timeout = null)
    {
        EnsureAlive();

        var result = (GstStateChangeReturn)Gst.gst_element_set_state(_pipeline, (int)state);
        if (result == GstStateChangeReturn.Failure)
        {
            throw new MediaToolkitNetException(
                GstConstants.BackendName, $"the pipeline refused to go to {state}: {Bus.TakeError(expected: true) ?? "no reason given"}", 0);
        }

        if (result != GstStateChangeReturn.Async)
        {
            return;
        }

        // A pipeline fed by an appsrc prerolls when its first buffer arrives, and the caller pushes that after this
        // returns: waiting for it here would only run the timeout out. A refusal that is already known still shows.
        var waited = WaitForState(_tracked.Exists(t => t is GStreamerSource) ? TimeSpan.Zero : timeout ?? TimeSpan.FromSeconds(5));
        if (waited == GstStateChangeReturn.Failure)
        {
            throw new MediaToolkitNetException(
                GstConstants.BackendName, $"the pipeline failed on the way to {state}: {Bus.TakeError(expected: true) ?? "no reason given"}", 0);
        }
    }

    /// <summary>The state the pipeline is in right now, without waiting.</summary>
    public GstState State
    {
        get
        {
            EnsureAlive();
            int current;
            int pending;
            Gst.gst_element_get_state(_pipeline, &current, &pending, 0);
            return (GstState)current;
        }
    }

    /// <summary>Waits for a pending state change to finish.</summary>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>Returns what the state change ended up doing.</returns>
    public GstStateChangeReturn WaitForState(TimeSpan timeout)
    {
        EnsureAlive();
        int current;
        int pending;
        return (GstStateChangeReturn)Gst.gst_element_get_state(
            _pipeline, &current, &pending, Nanoseconds(timeout));
    }

    /// <summary>The stream duration, or <see cref="TimeSpan.Zero"/> when it is not known yet.</summary>
    /// <remarks>A duration is only available once the pipeline has prerolled, so from PAUSED onwards.</remarks>
    public TimeSpan Duration
    {
        get
        {
            EnsureAlive();
            long value;
            return Gst.gst_element_query_duration(_pipeline, (int)GstFormat.Time, &value) == 0 || value < 0
                ? TimeSpan.Zero
                : new TimeSpan(value / GstConstants.NanosecondsPerTick);
        }
    }

    /// <summary>The current position, or <see cref="TimeSpan.Zero"/> when it is not known.</summary>
    public TimeSpan Position
    {
        get
        {
            EnsureAlive();
            long value;
            return Gst.gst_element_query_position(_pipeline, (int)GstFormat.Time, &value) == 0 || value < 0
                ? TimeSpan.Zero
                : new TimeSpan(value / GstConstants.NanosecondsPerTick);
        }
    }

    /// <summary>Seeks to a position, flushing what is already buffered.</summary>
    /// <returns>False when the pipeline cannot seek.</returns>
    public bool Seek(TimeSpan position)
    {
        EnsureAlive();
        return Gst.gst_element_seek_simple(
            _pipeline,
            (int)GstFormat.Time,
            (int)(GstSeekFlags.Flush | GstSeekFlags.KeyUnit),
            position.Ticks * GstConstants.NanosecondsPerTick) != 0;
    }

    /// <summary>
    /// Waits for the pipeline to finish or to fail, whichever comes first.
    /// </summary>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>True when the stream ended, false on a timeout.</returns>
    /// <exception cref="MediaToolkitNetException">The pipeline reported an error.</exception>
    public bool WaitForCompletion(TimeSpan timeout)
    {
        EnsureAlive();
        return Bus.WaitForCompletion(timeout);
    }

    /// <summary>
    /// Takes the error the pipeline posted, when it posted one, without waiting.
    /// </summary>
    public string? LastError() => Bus.TakeError(expected: false);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var tracked in _tracked)
        {
            tracked.Dispose();
        }

        _tracked.Clear();

        if (_pipeline is not null)
        {
            Gst.gst_element_set_state(_pipeline, (int)GstState.Null);
        }

        Bus.Dispose();

        if (_pipeline is not null)
        {
            Gst.gst_object_unref(_pipeline);
            _pipeline = null;
        }
    }

    private static ulong Nanoseconds(TimeSpan timeout) =>
        timeout < TimeSpan.Zero
            ? GstConstants.ClockTimeNone
            : (ulong)(timeout.Ticks * GstConstants.NanosecondsPerTick);

    private void EnsureAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
}
