#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer;

/// <summary>A device a GStreamer device provider knows: a microphone, a sound output, a camera.</summary>
/// <remarks>
/// It holds a reference to the native device until it is disposed, which the monitor that gave it does with itself.
/// Add it to a pipeline with <see cref="GStreamerPipeline.Add(GStreamerDevice, string?)"/>.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerDevice : IDisposable
{
    private void* _device;

    // Takes over the caller's reference.
    internal GStreamerDevice(void* device)
    {
        _device = device;
        DisplayName = Gst.TakeString(Gst.gst_device_get_display_name(device));
        DeviceClass = Gst.TakeString(Gst.gst_device_get_device_class(device));

        var caps = Gst.gst_device_get_caps(device);
        if (caps is not null)
        {
            Caps = GStreamerCaps.From(caps);
            Gst.gst_mini_object_unref(caps);
        }

        var properties = Gst.gst_device_get_properties(device);
        if (properties is not null)
        {
            Properties = GStreamerStructure.From(properties);
            Gst.gst_structure_free(properties);
        }
    }

    /// <summary>The name to show a person.</summary>
    public string DisplayName { get; }

    /// <summary>What it is, such as <c>Audio/Source</c>, <c>Audio/Sink</c> or <c>Video/Source</c>.</summary>
    public string DeviceClass { get; }

    /// <summary>The formats it takes or gives, or null.</summary>
    public GStreamerCaps? Caps { get; }

    /// <summary>What its provider says about it, or null.</summary>
    public GStreamerStructure? Properties { get; }

    internal void* Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_device is null, this);
            return _device;
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"{DisplayName} ({DeviceClass})";

    /// <inheritdoc />
    public void Dispose()
    {
        if (_device is not null)
        {
            Gst.gst_object_unref(_device);
            _device = null;
        }
    }
}

/// <summary>
/// Lists the devices GStreamer's providers know and says when one comes or goes, with <c>GstDeviceMonitor</c>.
/// </summary>
/// <remarks>
/// The events are raised on a thread of the monitor's, between <see cref="Start"/> and <see cref="Stop"/>; a handler
/// that throws is reported through <see cref="Failed"/>. Devices handed out are disposed with the monitor.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerDeviceMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly List<GStreamerDevice> _handedOut = [];
    private void* _monitor;
    private void* _bus;
    private Thread? _pump;
    private volatile bool _stopping;

    /// <summary>Creates a monitor of every device, or of those of some classes.</summary>
    /// <param name="classes">Such as <c>Audio/Source</c> or <c>Video</c>; null for all.</param>
    /// <param name="caps">Only devices that can do this, or null.</param>
    public GStreamerDeviceMonitor(string? classes = null, GStreamerCaps? caps = null)
    {
        Gst.EnsureLoaded();
        _monitor = Gst.gst_device_monitor_new();
        // Providers are only taken in by a filter, so there is always one, if only one that lets everything through.
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(classes, scratch);
        var native = caps is null ? null : GStreamerCaps.Create(caps.ToString());
        Gst.gst_device_monitor_add_filter(_monitor, utf8.Pointer, native);
        if (native is not null)
        {
            Gst.gst_mini_object_unref(native);
        }
    }

    /// <summary>Raised when a device appears.</summary>
    public event EventHandler<GStreamerDevice>? DeviceAdded;

    /// <summary>Raised when a device goes away.</summary>
    public event EventHandler<GStreamerDevice>? DeviceRemoved;

    /// <summary>Raised when a handler throws.</summary>
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <summary>The devices there are now.</summary>
    public IReadOnlyList<GStreamerDevice> GetDevices()
    {
        ObjectDisposedException.ThrowIf(_monitor is null, this);
        var list = Gst.gst_device_monitor_get_devices(_monitor);
        var devices = new List<GStreamerDevice>();
        for (var node = list; node is not null; node = GstLayout.NextListNode(node))
        {
            devices.Add(Keep(new GStreamerDevice(GstLayout.DataOfListNode(node))));
        }

        if (list is not null)
        {
            Gst.g_list_free(list);
        }

        return devices;
    }

    /// <summary>Starts watching for devices to come and go.</summary>
    /// <returns>False when no provider could be started.</returns>
    public bool Start()
    {
        ObjectDisposedException.ThrowIf(_monitor is null, this);
        if (_pump is not null)
        {
            return true;
        }

        _bus = Gst.gst_device_monitor_get_bus(_monitor);
        if (Gst.gst_device_monitor_start(_monitor) == 0)
        {
            Gst.gst_object_unref(_bus);
            _bus = null;
            return false;
        }

        _stopping = false;
        _pump = new Thread(Pump) { IsBackground = true, Name = "MediaToolkitNet.GStreamer devices" };
        _pump.Start();
        return true;
    }

    /// <summary>Stops watching.</summary>
    public void Stop()
    {
        if (_pump is not { } pump)
        {
            return;
        }

        _stopping = true;
        if (pump != Thread.CurrentThread)
        {
            pump.Join();
        }

        _pump = null;
        Gst.gst_device_monitor_stop(_monitor);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_monitor is null)
        {
            return;
        }

        Stop();
        lock (_handedOut)
        {
            foreach (var device in _handedOut)
            {
                device.Dispose();
            }

            _handedOut.Clear();
        }

        Gst.gst_object_unref(_monitor);
        _monitor = null;
    }

    private GStreamerDevice Keep(GStreamerDevice device)
    {
        lock (_handedOut)
        {
            _handedOut.Add(device);
        }

        return device;
    }

    // The bus is the thread's to release, so that a Stop from a handler does not take it away while it is read.
    private void Pump()
    {
        var bus = _bus;
        while (!_stopping)
        {
            var message = Gst.gst_bus_timed_pop(bus, (ulong)(PollInterval.Ticks * GstConstants.NanosecondsPerTick));
            if (message is null)
            {
                continue;
            }

            try
            {
                var type = GstLayout.TypeOfMessage(message);
                void* device = null;
                if (type == GstMessageType.DeviceAdded)
                {
                    Gst.gst_message_parse_device_added(message, &device);
                    if (device is not null)
                    {
                        DeviceAdded?.Invoke(this, Keep(new GStreamerDevice(device)));
                    }
                }
                else if (type == GstMessageType.DeviceRemoved)
                {
                    Gst.gst_message_parse_device_removed(message, &device);
                    if (device is not null)
                    {
                        DeviceRemoved?.Invoke(this, Keep(new GStreamerDevice(device)));
                    }
                }
            }
            catch (Exception ex)
            {
                SafeEvents.RaiseFailed(Failed, this, new MediaToolkitNetException(GstConstants.BackendName, $"a device handler threw: {ex.Message}", 0));
            }
            finally
            {
                Gst.gst_mini_object_unref(message);
            }
        }

        Gst.gst_object_unref(bus);
    }
}
