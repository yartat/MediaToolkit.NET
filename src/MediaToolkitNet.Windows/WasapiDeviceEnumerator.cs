using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions.Devices;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>Lists WASAPI endpoints through <c>IMMDeviceEnumerator</c>.</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WasapiDeviceEnumerator : IMediaDeviceEnumerator
{
    /// <summary>Backend name reported on the devices this enumerator returns.</summary>
    public const string BackendName = "wasapi";

    /// <inheritdoc />
    public IReadOnlyList<MediaDevice> Enumerate(MediaDeviceKind kind = MediaDeviceKind.All)
    {
        var result = new List<MediaDevice>();
        using var enumerator = Wasapi.CreateDeviceEnumerator();

        if ((kind & MediaDeviceKind.AudioRender) != 0)
        {
            Collect(enumerator.Pointer, DataFlow.Render, MediaDeviceKind.AudioRender, result);
        }

        if ((kind & MediaDeviceKind.AudioCapture) != 0)
        {
            Collect(enumerator.Pointer, DataFlow.Capture, MediaDeviceKind.AudioCapture, result);
        }

        return result;
    }

    /// <inheritdoc />
    public MediaDevice? GetDefault(MediaDeviceKind kind)
    {
        var flow = kind switch
        {
            MediaDeviceKind.AudioRender => DataFlow.Render,
            MediaDeviceKind.AudioCapture => DataFlow.Capture,
            _ => throw new NotSupportedException("WASAPI only works with audio devices."),
        };

        using var enumerator = Wasapi.CreateDeviceEnumerator();
        if (HResult.Failed(
                Wasapi.GetDefaultAudioEndpoint(enumerator.Pointer, flow, DeviceRole.Multimedia, out var device)))
        {
            return null;
        }

        try
        {
            return Describe(device, kind, isDefault: true);
        }
        finally
        {
            Com.Release(device);
        }
    }

    /// <summary>
    /// Opens an <c>IMMDevice</c> by id, or the platform default when
    /// <paramref name="device"/> is <see langword="null"/>.
    /// </summary>
    public static ComPtr OpenDevice(MediaDevice? device, DataFlow flow)
    {
        using var enumerator = Wasapi.CreateDeviceEnumerator();

        void* result;
        if (device is null)
        {
            HResult.ThrowIfFailed(
                Wasapi.GetDefaultAudioEndpoint(enumerator.Pointer, flow, DeviceRole.Multimedia, out result),
                "IMMDeviceEnumerator::GetDefaultAudioEndpoint");
        }
        else
        {
            HResult.ThrowIfFailed(
                Wasapi.GetDevice(enumerator.Pointer, device.Id, out result), "IMMDeviceEnumerator::GetDevice");
        }

        return new ComPtr(result);
    }

    private static void Collect(void* enumerator, DataFlow flow, MediaDeviceKind kind, List<MediaDevice> result)
    {
        HResult.ThrowIfFailed(
            Wasapi.EnumAudioEndpoints(enumerator, flow, Wasapi.DeviceStateActive, out var collection),
            "IMMDeviceEnumerator::EnumAudioEndpoints");

        try
        {
            HResult.ThrowIfFailed(Wasapi.GetCount(collection, out var count), "IMMDeviceCollection::GetCount");

            var defaultId = HResult.Succeeded(
                Wasapi.GetDefaultAudioEndpoint(enumerator, flow, DeviceRole.Multimedia, out var defaultDevice))
                ? TakeId(defaultDevice)
                : null;

            for (var i = 0u; i < count; i++)
            {
                if (HResult.Failed(Wasapi.Item(collection, i, out var device)))
                {
                    continue;
                }

                try
                {
                    var described = Describe(device, kind, isDefault: false);
                    result.Add(described with { IsDefault = described.Id == defaultId });
                }
                finally
                {
                    Com.Release(device);
                }
            }
        }
        finally
        {
            Com.Release(collection);
        }
    }

    private static string? TakeId(void* device)
    {
        try
        {
            return Wasapi.GetId(device);
        }
        finally
        {
            Com.Release(device);
        }
    }

    private static MediaDevice Describe(void* device, MediaDeviceKind kind, bool isDefault)
    {
        var id = Wasapi.GetId(device) ?? string.Empty;
        var name = Wasapi.GetFriendlyName(device) ?? id;
        return new MediaDevice(id, name, kind, BackendName, isDefault);
    }
}
