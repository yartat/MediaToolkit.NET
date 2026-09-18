using System.Runtime.Versioning;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows.DirectShow;

/// <summary>
/// One pin of a DirectShow filter.
/// </summary>
/// <remarks>
/// A pin wrapper owns one reference to the underlying <c>IPin</c>. Wrappers
/// handed out by <see cref="DirectShowFilter"/> are owned by the graph and are
/// released when it is disposed, so ordinary code does not have to track them.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class DirectShowPin : IDisposable
{
    private ComPtr _pin;
    private bool _disposed;

    internal DirectShowPin(void* pin, DirectShowGraph? owner)
    {
        _pin = new ComPtr(pin);
        owner?.Track(this);

        Direction = DsNative.QueryDirection(pin, out var direction) == HResult.Ok
            ? direction
            : PinDirection.Input;

        Id = DsNative.QueryId(pin) ?? string.Empty;

        if (DsNative.QueryPinInfo(pin, out var info) == HResult.Ok)
        {
            Name = DsNative.ReadFixedString(info.Name, 128);

            // QueryPinInfo hands back a reference to the owning filter.
            if (info.Filter is not null)
            {
                Com.Release(info.Filter);
            }
        }
        else
        {
            Name = Id;
        }
    }

    /// <summary>Raw <c>IPin</c> pointer.</summary>
    public void* Handle => _pin.Pointer;

    /// <summary>The pin's name, as its filter reports it.</summary>
    public string Name { get; }

    /// <summary>The pin's identifier, which survives across graph rebuilds.</summary>
    public string Id { get; }

    /// <summary>Whether the pin accepts or produces data.</summary>
    public PinDirection Direction { get; }

    /// <summary>True when the pin is connected to another pin.</summary>
    public bool IsConnected
    {
        get
        {
            var hr = DsNative.ConnectedTo(Handle, out var other);
            if (HResult.Failed(hr))
            {
                return false;
            }

            Com.Release(other);
            return true;
        }
    }

    /// <summary>
    /// The pin this one is connected to, or <see langword="null"/> when it is free.
    /// </summary>
    public DirectShowPin? ConnectedTo(DirectShowGraph? owner = null)
    {
        var hr = DsNative.ConnectedTo(Handle, out var other);
        return HResult.Failed(hr) ? null : new DirectShowPin(other, owner);
    }

    /// <summary>The media type of the current connection, or <see langword="null"/> when free.</summary>
    public DsMediaType? ConnectionMediaType
    {
        get
        {
            if (HResult.Failed(DsNative.ConnectionMediaType(Handle, out var native)))
            {
                return null;
            }

            try
            {
                return DsMediaType.From(native);
            }
            finally
            {
                DsNative.FreeMediaTypeContents(&native);
            }
        }
    }

    /// <summary>
    /// The media types the pin offers. A pin may report none and still connect,
    /// because the list is only a hint.
    /// </summary>
    public IReadOnlyList<DsMediaType> MediaTypes
    {
        get
        {
            var result = new List<DsMediaType>();
            if (HResult.Failed(DsNative.EnumMediaTypes(Handle, out var enumerator)))
            {
                return result;
            }

            try
            {
                while (DsNative.EnumeratorNext(enumerator, out var item) == HResult.Ok && item is not null)
                {
                    var native = (AmMediaType*)item;
                    try
                    {
                        result.Add(DsMediaType.From(*native));
                    }
                    finally
                    {
                        // The enumerator allocates each media type on the task heap.
                        DsNative.FreeMediaType(native);
                    }
                }
            }
            finally
            {
                Com.Release(enumerator);
            }

            return result;
        }
    }

    /// <summary>Breaks this pin's connection.</summary>
    public void Disconnect() =>
        HResult.ThrowIfFailed(DsNative.DisconnectPin(Handle), "IPin::Disconnect");

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Direction})";

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pin.Dispose();
    }
}
