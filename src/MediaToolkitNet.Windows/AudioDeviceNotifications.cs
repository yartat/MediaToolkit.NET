#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>An audio endpoint that appeared, went away, changed state or became the default.</summary>
/// <param name="DeviceId">The endpoint's id, as <c>MediaDevice.Id</c> carries it; null for "no default any more".</param>
/// <param name="Flow">For a default change: whether it is the default for playback or for recording.</param>
/// <param name="Role">For a default change: console, multimedia or communications.</param>
/// <param name="State">For a state change: the <c>DEVICE_STATE_*</c> value (1 active, 2 disabled, 4 not present, 8 unplugged).</param>
public readonly record struct AudioDeviceEvent(string? DeviceId, DataFlow Flow, DeviceRole Role, uint State);

/// <summary>
/// Tells you when audio devices come and go: a headset plugged in, a USB microphone pulled out, the default speakers
/// switched. Windows calls on its own threads, so a handler must not wait for anything slow.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class AudioDeviceNotifications : IDisposable
{
    private ComPtr _enumerator;
    private AudioCallbacks.CallbackObject* _callback;
    private bool _disposed;

    /// <summary>Starts listening.</summary>
    public AudioDeviceNotifications()
    {
        Ole32.Initialize();
        try
        {
            _enumerator = Wasapi.CreateDeviceEnumerator();
            _callback = AudioCallbacks.NewNotificationClient(this);
            HResult.ThrowIfFailed(
                ((delegate* unmanaged[Stdcall]<void*, void*, int>)Com.Slot(_enumerator.Pointer, AudioSlots.EnumeratorRegisterNotification))(
                    _enumerator.Pointer, _callback),
                "IMMDeviceEnumerator::RegisterEndpointNotificationCallback");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>An endpoint was added.</summary>
    public event EventHandler<AudioDeviceEvent>? DeviceAdded;

    /// <summary>An endpoint was removed.</summary>
    public event EventHandler<AudioDeviceEvent>? DeviceRemoved;

    /// <summary>An endpoint's state changed: it was unplugged, disabled or enabled.</summary>
    public event EventHandler<AudioDeviceEvent>? DeviceStateChanged;

    /// <summary>The default endpoint for a flow and role changed.</summary>
    public event EventHandler<AudioDeviceEvent>? DefaultDeviceChanged;

    /// <summary>Raised when a handler throws, which must not end the process.</summary>
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_callback is not null)
        {
            if (!_enumerator.IsNull)
            {
                ((delegate* unmanaged[Stdcall]<void*, void*, int>)Com.Slot(_enumerator.Pointer, AudioSlots.EnumeratorUnregisterNotification))(
                    _enumerator.Pointer, _callback);
            }

            AudioCallbacks.ReleaseOwn(_callback);
            _callback = null;
        }

        _enumerator.Dispose();
    }

    internal void Added(string id) => Raise(DeviceAdded, new AudioDeviceEvent(id, default, default, 0));

    internal void Removed(string id) => Raise(DeviceRemoved, new AudioDeviceEvent(id, default, default, 0));

    internal void StateChanged(string id, uint state) => Raise(DeviceStateChanged, new AudioDeviceEvent(id, default, default, state));

    internal void DefaultChanged(DataFlow flow, DeviceRole role, string? id) =>
        Raise(DefaultDeviceChanged, new AudioDeviceEvent(id, flow, role, 0));

    private void Raise(EventHandler<AudioDeviceEvent>? handler, AudioDeviceEvent change)
    {
        try
        {
            handler?.Invoke(this, change);
        }
        catch (Exception ex)
        {
            SafeEvents.RaiseFailed(Failed, this, new MediaToolkitNetException(WasapiDeviceEnumerator.BackendName, $"a device notification handler threw: {ex.Message}", 0));
        }
    }
}
