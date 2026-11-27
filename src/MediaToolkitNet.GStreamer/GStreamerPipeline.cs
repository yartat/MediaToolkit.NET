#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer;

/// <summary>How late the data of a pipeline is.</summary>
/// <param name="IsLive">
/// GStreamer's live flag of the answer. A playing sink that keeps to the clock (<c>sync=true</c>) sets it whatever its
/// source is, so it does not tell whether the source captures in real time.
/// </param>
/// <param name="Minimum">The least the sinks have to wait.</param>
/// <param name="Maximum">The most that can be waited before data is lost, or null for no limit.</param>
public readonly record struct GStreamerLatency(bool IsLive, TimeSpan Minimum, TimeSpan? Maximum);

/// <summary>Where a stream can seek.</summary>
/// <param name="CanSeek">Whether it can at all.</param>
/// <param name="Start">The first position.</param>
/// <param name="End">The last, or null when it is not known.</param>
public readonly record struct GStreamerSeekRange(bool CanSeek, TimeSpan Start, TimeSpan? End);

/// <summary>The state of the buffering elements.</summary>
/// <param name="IsBuffering">True while they are filling and playback should wait.</param>
/// <param name="Percent">How full, 0 to 100.</param>
public readonly record struct GStreamerBuffering(bool IsBuffering, int Percent);

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

    /// <summary>The description this pipeline was built from; empty for one built with <see cref="Create"/>.</summary>
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

    /// <summary>
    /// An empty pipeline to build by hand: <see cref="Add(string, string?)"/> its elements, then join them with
    /// <see cref="GStreamerElement.Link"/>.
    /// </summary>
    public static GStreamerPipeline Create()
    {
        Gst.EnsureLoaded();
        GstLayout.Require();

        var pipeline = Gst.gst_pipeline_new(null);
        if (pipeline is null)
        {
            throw new MediaToolkitNetException(GstConstants.BackendName, "gst_pipeline_new failed.", 0);
        }

        Gst.gst_object_ref_sink(pipeline);
        return new GStreamerPipeline(pipeline, string.Empty);
    }

    /// <summary>
    /// Makes an element and puts it in the pipeline. One added to a running pipeline stays stopped until it is linked
    /// and <see cref="GStreamerElement.SyncStateWithPipeline"/> is called.
    /// </summary>
    /// <param name="factory">The element's kind, such as <c>videoconvert</c>.</param>
    /// <param name="name">Its name in the pipeline; GStreamer makes one up without it.</param>
    /// <remarks>The pipeline owns the result and disposes it with itself.</remarks>
    /// <exception cref="MediaToolkitNetException">
    /// No installed plugin provides the element, or the pipeline already has an element of that name.
    /// </exception>
    public GStreamerElement Add(string factory, string? name = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(factory);
        EnsureAlive();

        Span<byte> factoryScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> nameScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var factoryUtf8 = new Utf8Scoped(factory, factoryScratch);
        using var nameUtf8 = new Utf8Scoped(name, nameScratch);

        var element = Gst.gst_element_factory_make(factoryUtf8.Pointer, nameUtf8.Pointer);
        if (element is null)
        {
            throw new MediaToolkitNetException(
                GstConstants.BackendName, $"no element {factory} could be made; is its plugin installed?", 0);
        }

        // Sunk first, so this reference is ours whether or not the pipeline takes the element.
        Gst.gst_object_ref_sink(element);
        return Take(element, name ?? factory);
    }

    /// <summary>Makes the element that captures from or plays to a device, and puts it in the pipeline.</summary>
    /// <param name="device">From a <see cref="GStreamerDeviceMonitor"/>.</param>
    /// <param name="name">Its name in the pipeline; GStreamer makes one up without it.</param>
    /// <exception cref="MediaToolkitNetException">The device makes no element, or the name is taken.</exception>
    public GStreamerElement Add(GStreamerDevice device, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        EnsureAlive();

        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(name, scratch);
        var element = Gst.gst_device_create_element(device.Handle, utf8.Pointer);
        if (element is null)
        {
            throw new MediaToolkitNetException(GstConstants.BackendName, $"the device {device.DisplayName} made no element.", 0);
        }

        Gst.gst_object_ref_sink(element);
        return Take(element, name ?? device.DisplayName);
    }

    /// <summary>
    /// Builds a bin from a part of a <c>gst-launch</c> description and puts it in the pipeline. The bin links like one
    /// element: the input and the output its description leaves open become its pads.
    /// </summary>
    /// <param name="description">What is inside, for example <c>videoconvert ! videoscale</c>.</param>
    /// <param name="name">The bin's name in the pipeline; GStreamer makes one up without it.</param>
    /// <remarks>
    /// The pipeline owns the result and disposes it with itself. The elements inside are found by
    /// <see cref="FindElement"/> like any other.
    /// </remarks>
    /// <exception cref="MediaToolkitNetException">
    /// The description cannot be built in full, or the pipeline already has an element of that name.
    /// </exception>
    public GStreamerElement AddBin(string description, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        EnsureAlive();

        Span<byte> descriptionScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> nameScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var descriptionUtf8 = new Utf8Scoped(description, descriptionScratch);
        using var nameUtf8 = new Utf8Scoped(name, nameScratch);

        void* error = null;
        var bin = Gst.gst_parse_bin_from_description(descriptionUtf8.Pointer, 1, &error);
        if (bin is not null)
        {
            Gst.gst_object_ref_sink(bin);
        }

        // The parser can return a bin that lacks what it could not make, and say so only in the error.
        if (bin is null || error is not null)
        {
            var reason = error is null ? "no reason given" : Gst.TakeError(error);
            if (bin is not null)
            {
                Gst.gst_object_unref(bin);
            }

            throw new MediaToolkitNetException(GstConstants.BackendName, $"the bin could not be built: {reason}", 0);
        }

        if (nameUtf8.Pointer is not null)
        {
            Gst.gst_object_set_name(bin, nameUtf8.Pointer);
        }

        return Take(bin, name ?? description);
    }

    /// <summary>
    /// Takes an element or a bin out of the pipeline, unlinking it from its neighbours, and stops it. In a playing
    /// pipeline unlink it from an idle probe first, and do not call this from one of the element's own threads.
    /// </summary>
    /// <param name="element">One that this pipeline holds directly, not inside a bin of it.</param>
    /// <remarks>The wrapper stays usable until the pipeline is disposed, but the element is no longer part of it.</remarks>
    /// <exception cref="MediaToolkitNetException">The pipeline does not hold the element.</exception>
    public void Remove(GStreamerElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        EnsureAlive();

        if (Gst.gst_bin_remove(_pipeline, element.Handle) == 0)
        {
            throw new MediaToolkitNetException(
                GstConstants.BackendName, $"the pipeline does not hold the element {element.Name}.", 0);
        }

        Gst.gst_element_set_state(element.Handle, (int)GstState.Null);
    }

    // Puts a sunk element into the pipeline; the reference goes to the wrapper, or is dropped on refusal.
    private GStreamerElement Take(void* element, string label)
    {
        // A pipeline on its way to a state sets every child to it when it gets there, from a thread of its own, which
        // would start an element nobody has linked or configured yet. Locked, it waits for SyncStateWithPipeline.
        int current;
        int next;
        Gst.gst_element_get_state(_pipeline, &current, &next, 0);
        if (current > (int)GstState.Null || next > (int)GstState.Null)
        {
            Gst.gst_element_set_locked_state(element, 1);
        }

        if (Gst.gst_bin_add(_pipeline, element) == 0)
        {
            Gst.gst_object_unref(element);
            throw new MediaToolkitNetException(
                GstConstants.BackendName, $"the pipeline refused the element {label}; is the name taken?", 0);
        }

        var wrapper = new GStreamerElement(element, Gst.TakeString(Gst.gst_object_get_name(element)));
        _tracked.Add(wrapper);
        return wrapper;
    }

    /// <summary>The element of that name, or <see langword="null"/>.</summary>
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

    /// <summary>
    /// The pipeline's graph in Graphviz <c>dot</c> text: every element, bins with what is inside them, the links between them and,
    /// as <paramref name="details"/> asks, the negotiated caps, the properties that were changed and the states. Render it with
    /// <c>dot -Tpng graph.dot -o graph.png</c>. Caps appear once the pipeline has negotiated, from PAUSED onwards.
    /// </summary>
    /// <param name="details">What to show; <see cref="GstGraphDetails.All"/> by default.</param>
    public string ToDot(GstGraphDetails details = GstGraphDetails.All)
    {
        EnsureAlive();
        return Gst.TakeString(Gst.gst_debug_bin_to_dot_data(_pipeline, (int)details));
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
    /// Seeks to a range, at a rate, the way GStreamer's seek event does it. At a negative rate the range is played from
    /// <paramref name="stop"/> back to <paramref name="start"/>.
    /// </summary>
    /// <param name="start">Where the range begins.</param>
    /// <param name="stop">Where it ends, or null for the end of the stream.</param>
    /// <param name="rate">1 is normal speed, 2 twice as fast, 0.5 half, -1 backwards.</param>
    /// <param name="flags">
    /// How to seek. With <see cref="GstSeekFlags.Segment"/> the end of the range is a
    /// <see cref="GstMessageType.SegmentDone"/> message on the bus rather than the end of the stream; seeking again
    /// without <see cref="GstSeekFlags.Flush"/> then loops it without a gap.
    /// </param>
    /// <returns>False when the pipeline cannot do it. Not every demuxer and decoder can play backwards.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rate is zero or not a number.</exception>
    /// <exception cref="ArgumentException">The range ends before it starts.</exception>
    public bool Seek(TimeSpan start, TimeSpan? stop, double rate = 1.0, GstSeekFlags flags = GstSeekFlags.Flush | GstSeekFlags.Accurate)
    {
        if (rate == 0 || !double.IsFinite(rate))
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "The rate must be a finite number other than zero.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(start, TimeSpan.Zero);
        if (stop < start)
        {
            throw new ArgumentException("The range ends before it starts.", nameof(stop));
        }

        EnsureAlive();
        var seek = Gst.gst_event_new_seek(
            rate,
            (int)GstFormat.Time,
            (int)flags,
            (int)GstSeekType.Set,
            start.Ticks * GstConstants.NanosecondsPerTick,
            (int)(stop is null ? GstSeekType.None : GstSeekType.Set),
            stop is { } end ? end.Ticks * GstConstants.NanosecondsPerTick : -1);
        return Gst.gst_element_send_event(_pipeline, seek) != 0;
    }

    /// <summary>
    /// Moves a paused pipeline forward by frames: each step shows the frame after those stepped over.
    /// </summary>
    /// <param name="frames">How many buffers to advance; one video frame each.</param>
    /// <returns>False when no element took the step. Wait with <see cref="WaitForState"/> for it to be done.</returns>
    public bool Step(ulong frames = 1)
    {
        ArgumentOutOfRangeException.ThrowIfZero(frames);
        EnsureAlive();
        var step = Gst.gst_event_new_step((int)GstFormat.Buffers, frames, 1.0, 1, 0);
        return Gst.gst_element_send_event(_pipeline, step) != 0;
    }

    /// <summary>The time of the pipeline's clock, or null before it has chosen one, which it does on the way to playing.</summary>
    public TimeSpan? ClockTime
    {
        get
        {
            EnsureAlive();
            var clock = Gst.gst_element_get_clock(_pipeline);
            if (clock is null)
            {
                return null;
            }

            try
            {
                return Time(Gst.gst_clock_get_time(clock));
            }
            finally
            {
                Gst.gst_object_unref(clock);
            }
        }
    }

    /// <summary>The clock time at which the pipeline's running time was zero.</summary>
    public TimeSpan BaseTime
    {
        get
        {
            EnsureAlive();
            return Time(Gst.gst_element_get_base_time(_pipeline)) ?? TimeSpan.Zero;
        }
    }

    /// <summary>How long the pipeline has been playing by its clock, or null when it is not playing.</summary>
    public TimeSpan? RunningTime => State == GstState.Playing && ClockTime is { } now ? now - BaseTime : null;

    /// <summary>
    /// Makes this pipeline run on the clock and base time of another that is already playing, so that equal running
    /// times mean the same moment in both. Call it before this pipeline starts.
    /// </summary>
    /// <param name="other">The pipeline to follow.</param>
    /// <exception cref="InvalidOperationException">The other pipeline has no clock yet.</exception>
    public void SynchronizeWith(GStreamerPipeline other)
    {
        ArgumentNullException.ThrowIfNull(other);
        EnsureAlive();
        other.EnsureAlive();

        var clock = Gst.gst_element_get_clock(other._pipeline);
        if (clock is null)
        {
            throw new InvalidOperationException("The other pipeline has no clock yet; start it first.");
        }

        try
        {
            Gst.gst_pipeline_use_clock(_pipeline, clock);

            // Without a start time of its own the pipeline keeps the base time it is given.
            Gst.gst_element_set_start_time(_pipeline, GstConstants.ClockTimeNone);
            Gst.gst_element_set_base_time(_pipeline, Gst.gst_element_get_base_time(other._pipeline));
        }
        finally
        {
            Gst.gst_object_unref(clock);
        }
    }

    /// <summary>
    /// Fixes how long after their time the sinks of a live pipeline render, or with null lets the pipeline work it out
    /// from its elements, which is the default.
    /// </summary>
    /// <param name="latency">The latency; one shorter than the elements need makes buffers arrive late.</param>
    public void SetLatency(TimeSpan? latency)
    {
        if (latency is { } value)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
        }

        EnsureAlive();
        Gst.gst_pipeline_set_latency(
            _pipeline, latency is { } set ? (ulong)(set.Ticks * GstConstants.NanosecondsPerTick) : GstConstants.ClockTimeNone);
    }

    /// <summary>Works the latency out again, which a <see cref="GstMessageType.Latency"/> message asks for.</summary>
    /// <returns>False when it could not be done.</returns>
    public bool RecalculateLatency()
    {
        EnsureAlive();
        return Gst.gst_bin_recalculate_latency(_pipeline) != 0;
    }

    /// <summary>
    /// The latency the elements report, or null when the pipeline cannot say. Ask once it is playing: before that the
    /// figures of a live source are not there yet.
    /// </summary>
    public GStreamerLatency? QueryLatency()
    {
        EnsureAlive();
        var query = Gst.gst_query_new_latency();
        try
        {
            if (Gst.gst_element_query(_pipeline, query) == 0)
            {
                return null;
            }

            var live = 0;
            ulong minimum = 0;
            ulong maximum = 0;
            Gst.gst_query_parse_latency(query, &live, &minimum, &maximum);
            return new GStreamerLatency(live != 0, Time(minimum) ?? TimeSpan.Zero, Time(maximum));
        }
        finally
        {
            Gst.gst_mini_object_unref(query);
        }
    }

    /// <summary>Whether and where the stream can seek in time, or null when the pipeline cannot say.</summary>
    public GStreamerSeekRange? QuerySeeking()
    {
        EnsureAlive();
        var query = Gst.gst_query_new_seeking((int)GstFormat.Time);
        try
        {
            if (Gst.gst_element_query(_pipeline, query) == 0)
            {
                return null;
            }

            var format = 0;
            var seekable = 0;
            long start = 0;
            long end = 0;
            Gst.gst_query_parse_seeking(query, &format, &seekable, &start, &end);
            return new GStreamerSeekRange(
                seekable != 0,
                start < 0 ? TimeSpan.Zero : new TimeSpan(start / GstConstants.NanosecondsPerTick),
                end < 0 ? null : new TimeSpan(end / GstConstants.NanosecondsPerTick));
        }
        finally
        {
            Gst.gst_mini_object_unref(query);
        }
    }

    /// <summary>
    /// How full the pipeline's buffers are, or null when nothing answers. Sources answer too, so a pipeline without a
    /// buffering element says it is not buffering.
    /// </summary>
    public GStreamerBuffering? QueryBuffering()
    {
        EnsureAlive();
        var query = Gst.gst_query_new_buffering((int)GstFormat.Percent);
        try
        {
            if (Gst.gst_element_query(_pipeline, query) == 0)
            {
                return null;
            }

            var busy = 0;
            var percent = 0;
            Gst.gst_query_parse_buffering_percent(query, &busy, &percent);
            return new GStreamerBuffering(busy != 0, percent);
        }
        finally
        {
            Gst.gst_mini_object_unref(query);
        }
    }

    private static TimeSpan? Time(ulong nanoseconds) =>
        nanoseconds == GstConstants.ClockTimeNone ? null : new TimeSpan((long)(nanoseconds / GstConstants.NanosecondsPerTick));

    /// <summary>
    /// Tells the sources to end the stream, so that what is already in the pipeline is processed and muxers finish their
    /// files. A live pipeline never ends by itself, and stopping it without this leaves an MP4 that cannot be opened.
    /// </summary>
    /// <remarks>
    /// This only asks: <see cref="WaitForCompletion"/> says when the end has reached every sink. The pipeline has to be
    /// playing, because a paused one does not move the end through.
    /// </remarks>
    /// <returns>False when no element took the event.</returns>
    public bool EndOfStream()
    {
        EnsureAlive();
        return Gst.gst_element_send_event(_pipeline, Gst.gst_event_new_eos()) != 0;
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
