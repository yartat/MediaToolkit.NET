using System.Runtime.Versioning;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows.DirectShow;

/// <summary>
/// One filter inside a <see cref="DirectShowGraph"/>.
/// </summary>
/// <remarks>
/// The wrapper owns one reference to the underlying <c>IBaseFilter</c>. Filters
/// created through the graph are tracked by it and released when it is disposed.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class DirectShowFilter : IDisposable
{
    private readonly DirectShowGraph? _owner;
    private ComPtr _filter;
    private bool _disposed;

    internal DirectShowFilter(void* filter, DirectShowGraph? owner, DsFilter? known = null)
    {
        _filter = new ComPtr(filter);
        _owner = owner;
        Known = known;
        owner?.Track(this);

        ClassId = DsNative.GetClassId(filter, out var clsid) == HResult.Ok ? clsid : Guid.Empty;
        Name = DsNative.QueryFilterInfo(filter, out var info) == HResult.Ok
            ? DsNative.ReadFixedString(info.Name, 128)
            : string.Empty;

        // QueryFilterInfo returns a counted reference to the owning graph.
        if (info.Graph is not null)
        {
            Com.Release(info.Graph);
        }
    }

    /// <summary>Raw <c>IBaseFilter</c> pointer.</summary>
    public void* Handle => _filter.Pointer;

    /// <summary>The name the filter carries inside the graph.</summary>
    public string Name { get; }

    /// <summary>The filter's class id.</summary>
    public Guid ClassId { get; }

    /// <summary>Set when the filter was created from the built-in catalogue.</summary>
    public DsFilter? Known { get; }

    /// <summary>The vendor string, when the filter provides one.</summary>
    public string? VendorInfo => DsNative.QueryVendorInfo(Handle);

    /// <summary>The filter's current state.</summary>
    public FilterState State =>
        DsNative.GetFilterState(Handle, 0, out var state) == HResult.Ok ? state : FilterState.Stopped;

    /// <summary>Every pin the filter exposes.</summary>
    public IReadOnlyList<DirectShowPin> Pins
    {
        get
        {
            var result = new List<DirectShowPin>();
            if (HResult.Failed(DsNative.EnumPins(Handle, out var enumerator)))
            {
                return result;
            }

            try
            {
                while (DsNative.EnumeratorNext(enumerator, out var pin) == HResult.Ok && pin is not null)
                {
                    result.Add(new DirectShowPin(pin, _owner));
                }
            }
            finally
            {
                Com.Release(enumerator);
            }

            return result;
        }
    }

    /// <summary>The filter's input pins.</summary>
    public IReadOnlyList<DirectShowPin> InputPins =>
        [.. Pins.Where(p => p.Direction == PinDirection.Input)];

    /// <summary>The filter's output pins.</summary>
    public IReadOnlyList<DirectShowPin> OutputPins =>
        [.. Pins.Where(p => p.Direction == PinDirection.Output)];

    /// <summary>
    /// The first output pin that is not connected yet, which is what a manual
    /// graph build almost always wants next.
    /// </summary>
    public DirectShowPin? FirstFreeOutput => Pins.FirstOrDefault(
        p => p.Direction == PinDirection.Output && !p.IsConnected);

    /// <summary>The first input pin that is not connected yet.</summary>
    public DirectShowPin? FirstFreeInput => Pins.FirstOrDefault(
        p => p.Direction == PinDirection.Input && !p.IsConnected);

    /// <summary>Finds a pin by its identifier.</summary>
    public DirectShowPin? FindPin(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return DsNative.FindPin(Handle, id, out var pin) == HResult.Ok && pin is not null
            ? new DirectShowPin(pin, _owner)
            : null;
    }

    /// <summary>
    /// Queries the filter for another interface. The returned handle owns a
    /// reference and must be disposed.
    /// </summary>
    public ComPtr QueryInterface(in Guid iid)
    {
        var hr = Com.QueryInterface(Handle, iid, out var result);
        HResult.ThrowIfFailed(hr, $"IBaseFilter::QueryInterface({iid:B})");
        return new ComPtr(result);
    }

    /// <summary>
    /// Points a source filter at a file through <c>IFileSourceFilter::Load</c>.
    /// Use it after adding, say, the WAVE Parser or the DVD Navigator.
    /// </summary>
    public void LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var source = QueryInterface(DsGuids.IFileSourceFilter);
        HResult.ThrowIfFailed(
            DsNative.LoadFile(source.Pointer, path, null), $"IFileSourceFilter::Load({path})");
    }

    /// <summary>The file a source filter is currently reading, when it exposes one.</summary>
    public string? CurrentFile
    {
        get
        {
            var hr = Com.QueryInterface(Handle, DsGuids.IFileSourceFilter, out var source);
            if (HResult.Failed(hr))
            {
                return null;
            }

            try
            {
                return DsNative.GetCurrentFile(source);
            }
            finally
            {
                Com.Release(source);
            }
        }
    }

    /// <summary>Points a sink filter at a file through <c>IFileSinkFilter::SetFileName</c>.</summary>
    public void SetOutputFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var sink = QueryInterface(DsGuids.IFileSinkFilter);
        HResult.ThrowIfFailed(
            DsNative.SetFileName(sink.Pointer, path, null), $"IFileSinkFilter::SetFileName({path})");
    }

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? ClassId.ToString("B") : Name;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _filter.Dispose();
    }
}
