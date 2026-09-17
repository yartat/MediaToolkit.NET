using System.Runtime.Versioning;
using MediaToolkitNet.Core;
using MediaToolkitNet.Core.Capture;
using MediaToolkitNet.Core.Devices;
using MediaToolkitNet.Linux.Native;

namespace MediaToolkitNet.Linux;

/// <summary>
/// Linux entry point. Video capture goes through V4L2; audio goes through
/// PulseAudio when a sound server is running and falls back to ALSA otherwise.
/// </summary>
/// <remarks>
/// PipeWire needs no separate backend: its PulseAudio and ALSA compatibility
/// layers are what these bindings talk to.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxBackend : MediaBackendBase
{
    /// <summary>Shared instance.</summary>
    public static LinuxBackend Instance { get; } = new();

    /// <inheritdoc />
    public override string Name => "linux";

    /// <inheritdoc />
    public override BackendCapabilities Capabilities =>
        BackendCapabilities.DeviceEnumeration |
        BackendCapabilities.AudioCapture |
        BackendCapabilities.AudioRender |
        BackendCapabilities.VideoCapture;

    /// <inheritdoc />
    public override bool IsAvailable => OperatingSystem.IsLinux() && (Pulse.IsAvailable || Alsa.IsAvailable);

    /// <inheritdoc />
    public override string? NativeVersion
    {
        get
        {
            if (!OperatingSystem.IsLinux())
            {
                return null;
            }

            var parts = new List<string>(2);
            if (Pulse.IsAvailable)
            {
                parts.Add("pulseaudio");
            }

            if (Alsa.IsAvailable)
            {
                parts.Add("alsa");
            }

            return parts.Count == 0 ? null : string.Join(" + ", parts);
        }
    }

    /// <summary>
    /// When true, audio goes through ALSA even if PulseAudio is present. Set it
    /// before creating any audio object.
    /// </summary>
    public bool PreferAlsa { get; set; }

    private bool UseAlsa => PreferAlsa || !Pulse.IsAvailable;

    /// <inheritdoc />
    public override IMediaDeviceEnumerator CreateDeviceEnumerator()
    {
        EnsureAvailable();
        return new CompositeEnumerator();
    }

    /// <inheritdoc />
    public override IVideoCapture CreateVideoCapture(VideoCaptureSettings settings)
    {
        EnsureAvailable();
        return new V4l2VideoCapture(settings);
    }

    /// <inheritdoc />
    public override IAudioCapture CreateAudioCapture(AudioCaptureSettings settings)
    {
        EnsureAvailable();
        return UseAlsa ? new AlsaCapture(settings) : new PulseCapture(settings);
    }

    /// <inheritdoc />
    public override IAudioRenderer CreateAudioRenderer(AudioCaptureSettings settings)
    {
        EnsureAvailable();
        return UseAlsa ? new AlsaRenderer(settings) : new PulseRenderer(settings);
    }

    /// <summary>Presents the V4L2 and ALSA enumerators as one list.</summary>
    private sealed class CompositeEnumerator : IMediaDeviceEnumerator
    {
        private readonly V4l2DeviceEnumerator _video = new();
        private readonly AlsaDeviceEnumerator _audio = new();

        public IReadOnlyList<MediaDevice> Enumerate(MediaDeviceKind kind = MediaDeviceKind.All)
        {
            var result = new List<MediaDevice>();

            if ((kind & MediaDeviceKind.VideoCapture) != 0)
            {
                result.AddRange(_video.Enumerate(MediaDeviceKind.VideoCapture));
            }

            // ALSA enumerates the PCM devices even when PulseAudio owns them,
            // and its names are what both backends accept.
            var audioKinds = kind & (MediaDeviceKind.AudioCapture | MediaDeviceKind.AudioRender);
            if (audioKinds != 0 && Alsa.IsAvailable)
            {
                result.AddRange(_audio.Enumerate(audioKinds));
            }

            return result;
        }

        public MediaDevice? GetDefault(MediaDeviceKind kind) => kind switch
        {
            MediaDeviceKind.VideoCapture => _video.GetDefault(kind),
            _ => Alsa.IsAvailable ? _audio.GetDefault(kind) : null,
        };
    }
}
