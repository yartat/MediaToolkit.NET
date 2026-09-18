using System.Runtime.Versioning;
using System.Text;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows.DirectShow;

/// <summary>
/// A DirectShow filter graph: filters, the connections between their pins, and
/// the controls that run the result.
/// </summary>
/// <remarks>
/// <para>
/// Two ways of building a graph are exposed and they mix freely.
/// <see cref="RenderFile"/> and <see cref="Render"/> let DirectShow pick filters
/// itself, which is the "intelligent connect" every player uses. Adding filters
/// from <see cref="DsFilter"/> and joining their pins by hand gives the exact
/// graph instead, which is what you want when a specific splitter or decoder has
/// to be used.
/// </para>
/// <para>
/// The graph owns every filter and pin wrapper it hands out, so a caller does
/// not have to track them; disposing the graph releases them all. COM is
/// initialised on the calling thread and left initialised, because the graph
/// outlives the call.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class DirectShowGraph : IDisposable
{
    private readonly List<IDisposable> _tracked = [];

    private ComPtr _graph;
    private ComPtr _control;
    private ComPtr _mediaEvent;
    private ComPtr _seeking;
    private bool _disposed;

    private DirectShowGraph(void* graph)
    {
        _graph = new ComPtr(graph);

        // The control and event interfaces always exist on the standard graph;
        // seeking may not, so it is optional.
        HResult.ThrowIfFailed(
            Com.QueryInterface(graph, DsGuids.IMediaControl, out var control),
            "IFilterGraph::QueryInterface(IMediaControl)");
        _control = new ComPtr(control);

        HResult.ThrowIfFailed(
            Com.QueryInterface(graph, DsGuids.IMediaEvent, out var mediaEvent),
            "IFilterGraph::QueryInterface(IMediaEvent)");
        _mediaEvent = new ComPtr(mediaEvent);

        if (HResult.Succeeded(Com.QueryInterface(graph, DsGuids.IMediaSeeking, out var seeking)))
        {
            _seeking = new ComPtr(seeking);
        }
    }

    /// <summary>Creates an empty graph.</summary>
    public static DirectShowGraph Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new MediaBackendUnavailableException("directshow", "DirectShow is only available on Windows.");
        }

        // The graph and its filters outlive this call, so the apartment stays up.
        Ole32.Initialize();

        var graph = Ole32.CreateInstance(DsGuids.FilterGraph, DsGuids.IGraphBuilder);
        try
        {
            return new DirectShowGraph(graph.Pointer);
        }
        catch
        {
            graph.Dispose();
            throw;
        }
    }

    /// <summary>Raw <c>IGraphBuilder</c> pointer, for calls this wrapper does not cover.</summary>
    public void* Handle => _graph.Pointer;

    /// <summary>True when the graph exposes <c>IMediaSeeking</c>.</summary>
    public bool CanSeek => !_seeking.IsNull;

    internal void Track(IDisposable item) => _tracked.Add(item);

    // ------------------------------------------------------------- building

    /// <summary>Adds one of the filters that ship with Windows.</summary>
    /// <param name="filter">The filter to create.</param>
    /// <param name="name">Name inside the graph; the registered name is used when omitted.</param>
    public DirectShowFilter AddFilter(DsFilter filter, string? name = null) =>
        AddFilterCore(DsFilters.ClassId(filter), name ?? DsFilters.DisplayName(filter), filter);

    /// <summary>Adds a filter by class id, which also covers third-party filters.</summary>
    public DirectShowFilter AddFilter(Guid classId, string? name = null) =>
        AddFilterCore(classId, name, null);

    private DirectShowFilter AddFilterCore(Guid classId, string? name, DsFilter? known)
    {
        EnsureAlive();

        using var instance = Ole32.CreateInstance(classId, DsGuids.IBaseFilter);
        HResult.ThrowIfFailed(
            DsNative.AddFilter(Handle, instance.Pointer, name),
            $"IFilterGraph::AddFilter({name ?? classId.ToString("B")})");

        // The graph took its own reference in AddFilter; the wrapper needs one
        // of its own because the `using` above releases ours.
        Com.AddRef(instance.Pointer);
        return new DirectShowFilter(instance.Pointer, this, known);
    }

    /// <summary>
    /// Adds a source filter for a file, letting DirectShow choose which source
    /// filter suits it.
    /// </summary>
    public DirectShowFilter AddSourceFilter(string path, string? name = null)
    {
        EnsureAlive();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        HResult.ThrowIfFailed(
            DsNative.AddSourceFilter(Handle, path, name, out var filter),
            $"IGraphBuilder::AddSourceFilter({path})");

        return new DirectShowFilter(filter, this);
    }

    /// <summary>
    /// Builds a complete graph for a file, choosing every filter automatically.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when only some streams could be rendered, which
    /// DirectShow reports as a success code rather than an error.
    /// </returns>
    public bool RenderFile(string path)
    {
        EnsureAlive();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var hr = DsNative.RenderFile(Handle, path);
        HResult.ThrowIfFailed(hr, $"IGraphBuilder::RenderFile({path})");
        return !DsResults.IsPartial(hr);
    }

    /// <summary>Builds everything downstream of an output pin.</summary>
    /// <returns><see langword="false"/> when the render was only partial.</returns>
    public bool Render(DirectShowPin outputPin)
    {
        EnsureAlive();
        ArgumentNullException.ThrowIfNull(outputPin);

        var hr = DsNative.Render(Handle, outputPin.Handle);
        HResult.ThrowIfFailed(hr, $"IGraphBuilder::Render({outputPin.Name})");
        return !DsResults.IsPartial(hr);
    }

    /// <summary>
    /// Connects two pins, letting DirectShow insert whatever filters the
    /// connection needs.
    /// </summary>
    public void Connect(DirectShowPin output, DirectShowPin input)
    {
        EnsureAlive();
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(input);

        HResult.ThrowIfFailed(
            DsNative.Connect(Handle, output.Handle, input.Handle),
            $"IGraphBuilder::Connect({output.Name} -> {input.Name})");
    }

    /// <summary>
    /// Connects two pins without inserting anything in between. Fails unless the
    /// two already agree on a media type.
    /// </summary>
    public void ConnectDirect(DirectShowPin output, DirectShowPin input)
    {
        EnsureAlive();
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(input);

        HResult.ThrowIfFailed(
            DsNative.ConnectDirect(Handle, output.Handle, input.Handle, null),
            $"IFilterGraph::ConnectDirect({output.Name} -> {input.Name})");
    }

    /// <summary>Connects the first free output of one filter to the first free input of another.</summary>
    public void Connect(DirectShowFilter upstream, DirectShowFilter downstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(downstream);

        var output = upstream.FirstFreeOutput
                     ?? throw new MediaToolkitNetException(
                         "directshow", $"filter \"{upstream.Name}\" has no free output pin", 0);
        var input = downstream.FirstFreeInput
                    ?? throw new MediaToolkitNetException(
                        "directshow", $"filter \"{downstream.Name}\" has no free input pin", 0);

        Connect(output, input);
    }

    /// <summary>Breaks the connection a pin is part of.</summary>
    public void Disconnect(DirectShowPin pin)
    {
        EnsureAlive();
        ArgumentNullException.ThrowIfNull(pin);
        HResult.ThrowIfFailed(DsNative.Disconnect(Handle, pin.Handle), "IFilterGraph::Disconnect");
    }

    /// <summary>Removes a filter from the graph.</summary>
    public void RemoveFilter(DirectShowFilter filter)
    {
        EnsureAlive();
        ArgumentNullException.ThrowIfNull(filter);
        HResult.ThrowIfFailed(
            DsNative.RemoveFilter(Handle, filter.Handle), $"IFilterGraph::RemoveFilter({filter.Name})");
    }

    /// <summary>Every filter currently in the graph.</summary>
    public IReadOnlyList<DirectShowFilter> Filters
    {
        get
        {
            EnsureAlive();
            var result = new List<DirectShowFilter>();
            if (HResult.Failed(DsNative.EnumFilters(Handle, out var enumerator)))
            {
                return result;
            }

            try
            {
                while (DsNative.EnumeratorNext(enumerator, out var filter) == HResult.Ok && filter is not null)
                {
                    result.Add(new DirectShowFilter(filter, this));
                }
            }
            finally
            {
                Com.Release(enumerator);
            }

            return result;
        }
    }

    /// <summary>Finds a filter by the name it carries inside the graph.</summary>
    public DirectShowFilter? FindFilter(string name)
    {
        EnsureAlive();
        ArgumentException.ThrowIfNullOrEmpty(name);

        return DsNative.FindFilterByName(Handle, name, out var filter) == HResult.Ok && filter is not null
            ? new DirectShowFilter(filter, this)
            : null;
    }

    // -------------------------------------------------------------- running

    /// <summary>Runs the graph.</summary>
    public void Run()
    {
        EnsureAlive();
        HResult.ThrowIfFailed(DsNative.Run(_control.Pointer), "IMediaControl::Run");
    }

    /// <summary>Pauses the graph, which also primes the renderers.</summary>
    public void Pause()
    {
        EnsureAlive();
        HResult.ThrowIfFailed(DsNative.Pause(_control.Pointer), "IMediaControl::Pause");
    }

    /// <summary>Stops the graph and rewinds it.</summary>
    public void Stop()
    {
        EnsureAlive();
        HResult.ThrowIfFailed(DsNative.Stop(_control.Pointer), "IMediaControl::Stop");
    }

    /// <summary>Stops after the data already queued has been delivered.</summary>
    public void StopWhenReady()
    {
        EnsureAlive();
        HResult.ThrowIfFailed(DsNative.StopWhenReady(_control.Pointer), "IMediaControl::StopWhenReady");
    }

    /// <summary>
    /// The graph's state. A state change is asynchronous, so this may report the
    /// previous state briefly after a transition.
    /// </summary>
    public FilterState State
    {
        get
        {
            EnsureAlive();
            var hr = DsNative.GetState(_control.Pointer, 0, out var state);
            return HResult.Failed(hr) ? FilterState.Stopped : (FilterState)state;
        }
    }

    /// <summary>Blocks until the state transition finishes or the timeout expires.</summary>
    /// <returns><see langword="false"/> when the transition is still in progress.</returns>
    public bool WaitForState(TimeSpan timeout)
    {
        EnsureAlive();
        var hr = DsNative.GetState(_control.Pointer, (int)timeout.TotalMilliseconds, out _);
        return hr == HResult.Ok;
    }

    /// <summary>
    /// Waits until every stream has finished.
    /// </summary>
    /// <returns><see langword="false"/> when the timeout expired first.</returns>
    public bool WaitForCompletion(TimeSpan timeout)
    {
        EnsureAlive();
        var hr = DsNative.WaitForCompletion(_mediaEvent.Pointer, (int)timeout.TotalMilliseconds, out _);
        return hr == HResult.Ok;
    }

    /// <summary>
    /// Takes the next event off the graph's queue, if one is waiting.
    /// </summary>
    /// <remarks>
    /// Events must be drained or the graph's queue grows without bound. The
    /// parameters are freed here, so a caller only sees the code.
    /// </remarks>
    public bool TryGetEvent(out int eventCode)
    {
        EnsureAlive();

        var hr = DsNative.GetEvent(_mediaEvent.Pointer, out eventCode, out var p1, out var p2, 0);
        if (HResult.Failed(hr))
        {
            eventCode = 0;
            return false;
        }

        DsNative.FreeEventParams(_mediaEvent.Pointer, eventCode, p1, p2);
        return true;
    }

    // -------------------------------------------------------------- seeking

    /// <summary>Total duration, or <see cref="TimeSpan.Zero"/> when the graph cannot report one.</summary>
    public TimeSpan Duration
    {
        get
        {
            if (!CanSeek || HResult.Failed(DsNative.GetDuration(_seeking.Pointer, out var duration)))
            {
                return TimeSpan.Zero;
            }

            // DirectShow reference time is in 100-nanosecond units, one tick.
            return new TimeSpan(duration);
        }
    }

    /// <summary>Current position.</summary>
    public TimeSpan Position
    {
        get
        {
            if (!CanSeek || HResult.Failed(DsNative.GetCurrentPosition(_seeking.Pointer, out var position)))
            {
                return TimeSpan.Zero;
            }

            return new TimeSpan(position);
        }
        set
        {
            if (!CanSeek)
            {
                throw new InvalidOperationException("This graph does not support seeking.");
            }

            var current = value.Ticks;
            var stop = 0L;
            HResult.ThrowIfFailed(
                DsNative.SetPositions(_seeking.Pointer, ref current, DsResults.SeekAbsolute, ref stop, DsResults.SeekNone),
                "IMediaSeeking::SetPositions");
        }
    }

    /// <summary>Playback rate, where 1.0 is normal speed.</summary>
    public double Rate
    {
        get => CanSeek && HResult.Succeeded(DsNative.GetRate(_seeking.Pointer, out var rate)) ? rate : 1.0;
        set
        {
            if (!CanSeek)
            {
                throw new InvalidOperationException("This graph does not support seeking.");
            }

            HResult.ThrowIfFailed(DsNative.SetRate(_seeking.Pointer, value), "IMediaSeeking::SetRate");
        }
    }

    // ---------------------------------------------------------- diagnostics

    /// <summary>
    /// Renders the graph as text: every filter, every pin, and what each pin is
    /// connected to. Intended for diagnosing a graph that did not build the way
    /// it was expected to.
    /// </summary>
    public string Describe()
    {
        EnsureAlive();

        var text = new StringBuilder();
        foreach (var filter in Filters)
        {
            text.Append(filter.Name);
            if (filter.Known is { } known)
            {
                text.Append(" [").Append(known).Append(']');
            }

            text.AppendLine();

            foreach (var pin in filter.Pins)
            {
                text.Append("    ").Append(pin.Direction == PinDirection.Output ? "out " : "in  ").Append(pin.Name);

                var peer = pin.ConnectedTo(this);
                if (peer is null)
                {
                    text.AppendLine("  (not connected)");
                    continue;
                }

                text.Append("  -> ").Append(peer.Name);
                if (pin.ConnectionMediaType is { } type)
                {
                    text.Append("  [").Append(type).Append(']');
                }

                text.AppendLine();
            }
        }

        return text.ToString();
    }

    private void EnsureAlive() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_control.IsNull)
        {
            // Stopping first lets the filters release their buffers cleanly.
            DsNative.Stop(_control.Pointer);
        }

        foreach (var tracked in _tracked)
        {
            tracked.Dispose();
        }

        _tracked.Clear();

        _seeking.Dispose();
        _mediaEvent.Dispose();
        _control.Dispose();
        _graph.Dispose();
    }
}
