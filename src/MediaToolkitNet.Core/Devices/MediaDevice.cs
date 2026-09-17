namespace MediaToolkitNet.Core.Devices;

/// <summary>Direction and media type of a hardware endpoint.</summary>
[Flags]
public enum MediaDeviceKind
{
    /// <summary>Unspecified.</summary>
    None = 0,

    /// <summary>Audio input, e.g. a microphone or a loopback endpoint.</summary>
    AudioCapture = 1 << 0,

    /// <summary>Audio output, e.g. speakers or headphones.</summary>
    AudioRender = 1 << 1,

    /// <summary>Video input, e.g. a webcam or a capture card.</summary>
    VideoCapture = 1 << 2,

    /// <summary>Every kind.</summary>
    All = AudioCapture | AudioRender | VideoCapture,
}

/// <summary>
/// A hardware endpoint reported by a backend.
/// </summary>
/// <param name="Id">
/// Backend-specific identifier. This is the value to pass back to the backend
/// when opening the device: an MMDevice endpoint id on WASAPI, a symbolic link
/// on Media Foundation, an ALSA PCM name, a /dev/videoN path on V4L2 or a
/// CoreAudio unique id on macOS.
/// </param>
/// <param name="Name">Human-readable name suitable for a device picker.</param>
/// <param name="Kind">What the device can do.</param>
/// <param name="Backend">Name of the backend that reported the device.</param>
/// <param name="IsDefault">True when the platform marks this device as the default for its kind.</param>
public sealed record MediaDevice(
    string Id,
    string Name,
    MediaDeviceKind Kind,
    string Backend,
    bool IsDefault = false)
{
    /// <inheritdoc />
    public override string ToString() => IsDefault ? $"{Name} [{Kind}, default]" : $"{Name} [{Kind}]";
}

/// <summary>Enumerates the hardware endpoints a backend can see.</summary>
public interface IMediaDeviceEnumerator
{
    /// <summary>Lists every device of the requested kinds.</summary>
    IReadOnlyList<MediaDevice> Enumerate(MediaDeviceKind kind = MediaDeviceKind.All);

    /// <summary>
    /// Returns the platform default device of a single kind, or <see langword="null"/>
    /// when the platform exposes no default.
    /// </summary>
    MediaDevice? GetDefault(MediaDeviceKind kind);
}
