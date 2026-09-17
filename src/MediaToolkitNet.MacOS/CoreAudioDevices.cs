using System.Runtime.Versioning;
using MediaToolkitNet.Core.Devices;
using MediaToolkitNet.MacOS.Native;

namespace MediaToolkitNet.MacOS;

/// <summary>
/// Lists audio endpoints through the CoreAudio AudioObject property API and
/// video devices through AVFoundation.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacDeviceEnumerator : IMediaDeviceEnumerator
{
    /// <summary>Backend name reported on the devices this enumerator returns.</summary>
    public const string BackendName = "macos";

    /// <inheritdoc />
    public IReadOnlyList<MediaDevice> Enumerate(MediaDeviceKind kind = MediaDeviceKind.All)
    {
        var result = new List<MediaDevice>();

        if ((kind & (MediaDeviceKind.AudioCapture | MediaDeviceKind.AudioRender)) != 0)
        {
            CollectAudio(kind, result);
        }

        if ((kind & MediaDeviceKind.VideoCapture) != 0)
        {
            result.AddRange(AVFoundation.EnumerateVideoDevices());
        }

        return result;
    }

    /// <inheritdoc />
    public MediaDevice? GetDefault(MediaDeviceKind kind)
    {
        if (kind == MediaDeviceKind.VideoCapture)
        {
            return AVFoundation.EnumerateVideoDevices().FirstOrDefault();
        }

        var selector = kind == MediaDeviceKind.AudioRender
            ? CoreAudio.PropertyDefaultOutput
            : CoreAudio.PropertyDefaultInput;

        if (!CoreAudio.TryGetProperty<uint>(CoreAudio.SystemObject, selector, CoreAudio.ScopeGlobal, out var deviceId)
            || deviceId == 0)
        {
            return null;
        }

        return Describe(deviceId, kind, isDefault: true);
    }

    /// <summary>
    /// Resolves a device id from a <see cref="MediaDevice"/>, falling back to the
    /// system default for the given direction.
    /// </summary>
    public static uint ResolveDeviceId(MediaDevice? device, bool forOutput)
    {
        if (device is not null && uint.TryParse(device.Id, out var parsed))
        {
            return parsed;
        }

        var selector = forOutput ? CoreAudio.PropertyDefaultOutput : CoreAudio.PropertyDefaultInput;
        return CoreAudio.TryGetProperty<uint>(CoreAudio.SystemObject, selector, CoreAudio.ScopeGlobal, out var id)
            ? id
            : 0;
    }

    private static void CollectAudio(MediaDeviceKind kind, List<MediaDevice> result)
    {
        var defaultInput = CoreAudio.TryGetProperty<uint>(
            CoreAudio.SystemObject, CoreAudio.PropertyDefaultInput, CoreAudio.ScopeGlobal, out var input) ? input : 0;
        var defaultOutput = CoreAudio.TryGetProperty<uint>(
            CoreAudio.SystemObject, CoreAudio.PropertyDefaultOutput, CoreAudio.ScopeGlobal, out var output) ? output : 0;

        foreach (var deviceId in CoreAudio.ListDevices())
        {
            // A device is an input, an output, or both, depending on which
            // scopes actually carry channels.
            if ((kind & MediaDeviceKind.AudioCapture) != 0 && CoreAudio.CountChannels(deviceId, CoreAudio.ScopeInput) > 0)
            {
                result.Add(Describe(deviceId, MediaDeviceKind.AudioCapture, deviceId == defaultInput));
            }

            if ((kind & MediaDeviceKind.AudioRender) != 0 && CoreAudio.CountChannels(deviceId, CoreAudio.ScopeOutput) > 0)
            {
                result.Add(Describe(deviceId, MediaDeviceKind.AudioRender, deviceId == defaultOutput));
            }
        }
    }

    private static MediaDevice Describe(uint deviceId, MediaDeviceKind kind, bool isDefault)
    {
        var name = CoreAudio.GetStringProperty(deviceId, CoreAudio.PropertyName, CoreAudio.ScopeGlobal);
        return new MediaDevice(
            deviceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(name) ? $"Device {deviceId}" : name!,
            kind,
            BackendName,
            isDefault);
    }
}
