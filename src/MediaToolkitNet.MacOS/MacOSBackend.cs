using System.Runtime.Versioning;
using MediaToolkitNet.Core;
using MediaToolkitNet.Core.Capture;
using MediaToolkitNet.Core.Devices;
using MediaToolkitNet.Core.Playback;

namespace MediaToolkitNet.MacOS;

/// <summary>
/// macOS entry point. Video capture and playback go through AVFoundation;
/// audio capture and playback go through CoreAudio's AudioQueue.
/// </summary>
/// <remarks>
/// Recording is not implemented here: <c>AVAssetWriter</c> would need a second
/// runtime-built delegate class and a full <c>CMSampleBuffer</c> construction
/// path. Use the FFmpeg backend to write files on macOS.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacOSBackend : MediaBackendBase
{
    /// <summary>Shared instance.</summary>
    public static MacOSBackend Instance { get; } = new();

    /// <inheritdoc />
    public override string Name => MacDeviceEnumerator.BackendName;

    /// <inheritdoc />
    public override BackendCapabilities Capabilities =>
        BackendCapabilities.DeviceEnumeration |
        BackendCapabilities.Playback |
        BackendCapabilities.AudioCapture |
        BackendCapabilities.AudioRender |
        BackendCapabilities.VideoCapture;

    /// <inheritdoc />
    public override bool IsAvailable => OperatingSystem.IsMacOS();

    /// <inheritdoc />
    public override string? NativeVersion => OperatingSystem.IsMacOS() ? Environment.OSVersion.VersionString : null;

    /// <inheritdoc />
    public override IMediaDeviceEnumerator CreateDeviceEnumerator()
    {
        EnsureAvailable();
        return new MacDeviceEnumerator();
    }

    /// <inheritdoc />
    public override IMediaPlayer CreatePlayer()
    {
        EnsureAvailable();
        return new AVPlayerPlayer();
    }

    /// <inheritdoc />
    public override IVideoCapture CreateVideoCapture(VideoCaptureSettings settings)
    {
        EnsureAvailable();
        return new AVFoundationVideoCapture(settings);
    }

    /// <inheritdoc />
    public override IAudioCapture CreateAudioCapture(AudioCaptureSettings settings)
    {
        EnsureAvailable();
        return new AudioQueueCapture(settings);
    }

    /// <inheritdoc />
    public override IAudioRenderer CreateAudioRenderer(AudioCaptureSettings settings)
    {
        EnsureAvailable();
        return new AudioQueueRenderer(settings);
    }
}
