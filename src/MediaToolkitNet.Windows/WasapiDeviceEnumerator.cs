#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions.Devices;
using MediaToolkitNet.Abstractions.Formats;
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

    /// <summary>The format the Windows audio engine mixes in for an endpoint.</summary>
    /// <param name="device">The endpoint, or <see langword="null"/> for the default one.</param>
    /// <param name="flow">Render or capture.</param>
    public static AudioFormat GetMixFormat(MediaDevice? device, DataFlow flow)
    {
        Ole32.Initialize();
        using var endpoint = OpenDevice(device, flow);
        HResult.ThrowIfFailed(Wasapi.Activate(endpoint.Pointer, WinGuids.IAudioClient, out var raw), "IMMDevice::Activate(IAudioClient)");
        using var client = new ComPtr(raw);
        HResult.ThrowIfFailed(Wasapi.GetMixFormat(client.Pointer, out var mix), "IAudioClient::GetMixFormat");
        try
        {
            return Wasapi.ReadFormat(mix);
        }
        finally
        {
            Ole32.CoTaskMemFree(mix);
        }
    }

    /// <summary>
    /// The periods <see cref="WasapiMode.LowLatencyShared"/> can use on an endpoint. Needs Windows 10.
    /// </summary>
    /// <param name="device">The endpoint, or <see langword="null"/> for the default one.</param>
    /// <param name="flow">Render or capture.</param>
    public static AudioEnginePeriods GetEnginePeriods(MediaDevice? device, DataFlow flow)
    {
        Ole32.Initialize();
        using var endpoint = OpenDevice(device, flow);
        HResult.ThrowIfFailed(Wasapi.Activate(endpoint.Pointer, WinGuids.IAudioClient, out var raw), "IMMDevice::Activate(IAudioClient)");
        using var client = new ComPtr(raw);
        HResult.ThrowIfFailed(Wasapi.GetMixFormat(client.Pointer, out var mix), "IAudioClient::GetMixFormat");
        try
        {
            return WasapiStreams.EnginePeriods(client.Pointer, mix);
        }
        finally
        {
            Ole32.CoTaskMemFree(mix);
        }
    }

    /// <summary>Whether an endpoint takes a format in exclusive mode, as it is and without conversion.</summary>
    /// <param name="device">The endpoint, or <see langword="null"/> for the default one.</param>
    /// <param name="flow">Render or capture.</param>
    /// <param name="format">The format asked about.</param>
    /// <remarks>This is the driver's answer; opening the stream may still refuse a format it said yes to.</remarks>
    public static bool SupportsExclusive(MediaDevice? device, DataFlow flow, AudioFormat format)
    {
        Ole32.Initialize();
        using var endpoint = OpenDevice(device, flow);
        HResult.ThrowIfFailed(Wasapi.Activate(endpoint.Pointer, WinGuids.IAudioClient, out var raw), "IMMDevice::Activate(IAudioClient)");
        using var client = new ComPtr(raw);
        return WasapiStreams.TryExclusiveFormat(client.Pointer, format, out _);
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
