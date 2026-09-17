using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions.Devices;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>
/// Enumerates capture devices through <c>MFEnumDeviceSources</c>.
/// </summary>
/// <remarks>
/// The <see cref="MediaDevice.Id"/> of a video device is its symbolic link and
/// of an audio device its endpoint id; both can be passed straight back to
/// <see cref="MediaFoundationVideoCapture"/> or to WASAPI.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class MediaFoundationDeviceEnumerator : IMediaDeviceEnumerator
{
    /// <summary>Backend name reported on the devices this enumerator returns.</summary>
    public const string BackendName = "mediafoundation";

    /// <inheritdoc />
    public IReadOnlyList<MediaDevice> Enumerate(MediaDeviceKind kind = MediaDeviceKind.All)
    {
        MfApi.Startup();

        var result = new List<MediaDevice>();
        if ((kind & MediaDeviceKind.VideoCapture) != 0)
        {
            Collect(WinGuids.DevSourceVideoCapture, MediaDeviceKind.VideoCapture, result);
        }

        if ((kind & MediaDeviceKind.AudioCapture) != 0)
        {
            Collect(WinGuids.DevSourceAudioCapture, MediaDeviceKind.AudioCapture, result);
        }

        return result;
    }

    /// <summary>
    /// Media Foundation does not mark a default capture device, so this returns
    /// the first one found.
    /// </summary>
    public MediaDevice? GetDefault(MediaDeviceKind kind) => Enumerate(kind).FirstOrDefault();

    /// <summary>
    /// Creates an <c>IMFMediaSource</c> for the given device and returns an
    /// owning handle. Pass <see langword="null"/> to take the first device.
    /// </summary>
    public static ComPtr ActivateSource(MediaDevice? device, MediaDeviceKind kind)
    {
        MfApi.Startup();

        var sourceType = kind == MediaDeviceKind.AudioCapture
            ? WinGuids.DevSourceAudioCapture
            : WinGuids.DevSourceVideoCapture;
        var idKey = kind == MediaDeviceKind.AudioCapture
            ? WinGuids.DevSourceAudioEndpointId
            : WinGuids.DevSourceVideoSymbolicLink;

        void* attributes;
        HResult.ThrowIfFailed(MfApi.MFCreateAttributes(&attributes, 2), "MFCreateAttributes");

        try
        {
            HResult.ThrowIfFailed(
                Mf.SetGuid(attributes, WinGuids.DevSourceAttributeSourceType, sourceType),
                "IMFAttributes::SetGUID(SOURCE_TYPE)");

            void** sources;
            uint count;
            HResult.ThrowIfFailed(MfApi.MFEnumDeviceSources(attributes, &sources, &count), "MFEnumDeviceSources");

            try
            {
                for (var i = 0u; i < count; i++)
                {
                    var activate = sources[i];
                    if (device is not null && Mf.GetString(activate, idKey) != device.Id)
                    {
                        continue;
                    }

                    HResult.ThrowIfFailed(
                        Mf.ActivateObject(activate, WinGuids.IMFMediaSource, out var source),
                        "IMFActivate::ActivateObject");
                    return new ComPtr(source);
                }
            }
            finally
            {
                for (var i = 0u; i < count; i++)
                {
                    Com.Release(sources[i]);
                }

                Ole32.CoTaskMemFree(sources);
            }
        }
        finally
        {
            Com.Release(attributes);
        }

        throw new Abstractions.MediaToolkitNetException(
            BackendName,
            device is null
                ? "no capture devices found"
                : $"device \"{device.Name}\" not found",
            0);
    }

    private static void Collect(Guid sourceType, MediaDeviceKind kind, List<MediaDevice> result)
    {
        void* attributes;
        HResult.ThrowIfFailed(MfApi.MFCreateAttributes(&attributes, 1), "MFCreateAttributes");

        try
        {
            HResult.ThrowIfFailed(
                Mf.SetGuid(attributes, WinGuids.DevSourceAttributeSourceType, sourceType),
                "IMFAttributes::SetGUID(SOURCE_TYPE)");

            void** sources;
            uint count;
            HResult.ThrowIfFailed(MfApi.MFEnumDeviceSources(attributes, &sources, &count), "MFEnumDeviceSources");

            try
            {
                var idKey = kind == MediaDeviceKind.AudioCapture
                    ? WinGuids.DevSourceAudioEndpointId
                    : WinGuids.DevSourceVideoSymbolicLink;

                for (var i = 0u; i < count; i++)
                {
                    var activate = sources[i];
                    var name = Mf.GetString(activate, WinGuids.DevSourceFriendlyName) ?? $"Device {i}";
                    var id = Mf.GetString(activate, idKey) ?? name;
                    result.Add(new MediaDevice(id, name, kind, BackendName));
                }
            }
            finally
            {
                for (var i = 0u; i < count; i++)
                {
                    Com.Release(sources[i]);
                }

                Ole32.CoTaskMemFree(sources);
            }
        }
        finally
        {
            Com.Release(attributes);
        }
    }
}
