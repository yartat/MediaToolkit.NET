using System.Runtime.Versioning;
using MediaToolkitNet.Core;
using MediaToolkitNet.Core.Capture;
using MediaToolkitNet.Core.Devices;
using MediaToolkitNet.Core.Recording;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>
/// Windows entry point. Device enumeration, video capture and recording go
/// through Media Foundation; audio capture and playback go through WASAPI.
/// </summary>
/// <remarks>
/// The DirectShow enumerator is reachable through
/// <see cref="CreateDirectShowEnumerator"/> rather than
/// <see cref="CreateDeviceEnumerator"/>: it exists for devices that Media
/// Foundation does not list, and it enumerates only.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsBackend : MediaBackendBase
{
    /// <summary>Shared instance.</summary>
    public static WindowsBackend Instance { get; } = new();

    /// <inheritdoc />
    public override string Name => "windows";

    /// <inheritdoc />
    public override BackendCapabilities Capabilities =>
        BackendCapabilities.DeviceEnumeration |
        BackendCapabilities.AudioCapture |
        BackendCapabilities.AudioRender |
        BackendCapabilities.VideoCapture |
        BackendCapabilities.Recording;

    /// <inheritdoc />
    public override bool IsAvailable => MfApi.TryStartup();

    /// <inheritdoc />
    public override string? NativeVersion => OperatingSystem.IsWindows() ? Environment.OSVersion.VersionString : null;

    /// <summary>
    /// Returns an enumerator that merges the Media Foundation capture devices
    /// with the WASAPI render endpoints, which is what most callers want.
    /// </summary>
    public override IMediaDeviceEnumerator CreateDeviceEnumerator()
    {
        EnsureAvailable();
        return new CompositeEnumerator();
    }

    /// <summary>Returns the DirectShow enumerator, for devices Media Foundation does not list.</summary>
    public static IMediaDeviceEnumerator CreateDirectShowEnumerator() => new DirectShowDeviceEnumerator();

    /// <summary>Returns the WASAPI enumerator on its own.</summary>
    public static IMediaDeviceEnumerator CreateWasapiEnumerator() => new WasapiDeviceEnumerator();

    /// <summary>Returns the Media Foundation enumerator on its own.</summary>
    public static IMediaDeviceEnumerator CreateMediaFoundationEnumerator() => new MediaFoundationDeviceEnumerator();

    /// <inheritdoc />
    public override IVideoCapture CreateVideoCapture(VideoCaptureSettings settings)
    {
        EnsureAvailable();
        return new MediaFoundationVideoCapture(settings);
    }

    /// <inheritdoc />
    public override IAudioCapture CreateAudioCapture(AudioCaptureSettings settings)
    {
        EnsureAvailable();
        return new WasapiCapture(settings);
    }

    /// <summary>Opens a render endpoint in loopback mode, capturing what is being played.</summary>
    public static IAudioCapture CreateLoopbackCapture(AudioCaptureSettings settings) => new WasapiCapture(settings, loopback: true);

    /// <inheritdoc />
    public override IAudioRenderer CreateAudioRenderer(AudioCaptureSettings settings)
    {
        EnsureAvailable();
        return new WasapiRenderer(settings);
    }

    /// <inheritdoc />
    public override IMediaRecorder CreateRecorder(string outputPath)
    {
        EnsureAvailable();
        return new MediaFoundationRecorder(outputPath);
    }

    /// <summary>Presents the Media Foundation and WASAPI enumerators as one list.</summary>
    private sealed class CompositeEnumerator : IMediaDeviceEnumerator
    {
        private readonly MediaFoundationDeviceEnumerator _mediaFoundation = new();
        private readonly WasapiDeviceEnumerator _wasapi = new();

        public IReadOnlyList<MediaDevice> Enumerate(MediaDeviceKind kind = MediaDeviceKind.All)
        {
            var result = new List<MediaDevice>();

            if ((kind & MediaDeviceKind.VideoCapture) != 0)
            {
                result.AddRange(_mediaFoundation.Enumerate(MediaDeviceKind.VideoCapture));
            }

            // WASAPI reports endpoint ids and default flags, which Media
            // Foundation does not, so it wins for audio.
            var audioKinds = kind & (MediaDeviceKind.AudioCapture | MediaDeviceKind.AudioRender);
            if (audioKinds != 0)
            {
                result.AddRange(_wasapi.Enumerate(audioKinds));
            }

            return result;
        }

        public MediaDevice? GetDefault(MediaDeviceKind kind) => kind switch
        {
            MediaDeviceKind.AudioCapture or MediaDeviceKind.AudioRender => _wasapi.GetDefault(kind),
            _ => _mediaFoundation.GetDefault(kind),
        };
    }
}
