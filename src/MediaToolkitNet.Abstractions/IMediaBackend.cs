using MediaToolkitNet.Abstractions.Capture;
using MediaToolkitNet.Abstractions.Devices;
using MediaToolkitNet.Abstractions.Playback;
using MediaToolkitNet.Abstractions.Recording;

namespace MediaToolkitNet.Abstractions;

/// <summary>What a backend is able to provide.</summary>
[Flags]
public enum BackendCapabilities
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Can enumerate hardware endpoints.</summary>
    DeviceEnumeration = 1 << 0,

    /// <summary>Can create an <see cref="IMediaPlayer"/>.</summary>
    Playback = 1 << 1,

    /// <summary>Can create an <see cref="IAudioCapture"/>.</summary>
    AudioCapture = 1 << 2,

    /// <summary>Can create an <see cref="IAudioRenderer"/>.</summary>
    AudioRender = 1 << 3,

    /// <summary>Can create an <see cref="IVideoCapture"/>.</summary>
    VideoCapture = 1 << 4,

    /// <summary>Can create an <see cref="IMediaRecorder"/>.</summary>
    Recording = 1 << 5,
}

/// <summary>
/// Entry point of one native media stack. Every backend package exposes exactly
/// one implementation of this interface.
/// </summary>
public interface IMediaBackend
{
    /// <summary>Stable short name, e.g. "ffmpeg", "mpv", "mediafoundation".</summary>
    string Name { get; }

    /// <summary>What this backend can do on the current machine.</summary>
    BackendCapabilities Capabilities { get; }

    /// <summary>
    /// True when the native libraries this backend needs were found and loaded.
    /// Checking this never throws, so it is safe to call during backend probing.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Version string of the loaded native stack, or <see langword="null"/> when unavailable.</summary>
    string? NativeVersion { get; }

    /// <summary>Creates the device enumerator.</summary>
    /// <exception cref="NotSupportedException">The backend does not support enumeration.</exception>
    IMediaDeviceEnumerator CreateDeviceEnumerator();

    /// <summary>Creates a player.</summary>
    /// <exception cref="NotSupportedException">The backend does not support playback.</exception>
    IMediaPlayer CreatePlayer();

    /// <summary>Opens an audio input endpoint.</summary>
    /// <exception cref="NotSupportedException">The backend does not support audio capture.</exception>
    IAudioCapture CreateAudioCapture(AudioCaptureSettings settings);

    /// <summary>Opens an audio output endpoint.</summary>
    /// <exception cref="NotSupportedException">The backend does not support audio render.</exception>
    IAudioRenderer CreateAudioRenderer(AudioCaptureSettings settings);

    /// <summary>Opens a video input device.</summary>
    /// <exception cref="NotSupportedException">The backend does not support video capture.</exception>
    IVideoCapture CreateVideoCapture(VideoCaptureSettings settings);

    /// <summary>Creates a recorder writing to <paramref name="outputPath"/>.</summary>
    /// <exception cref="NotSupportedException">The backend does not support recording.</exception>
    IMediaRecorder CreateRecorder(string outputPath);
}

/// <summary>
/// Default <see cref="IMediaBackend"/> implementation that throws
/// <see cref="NotSupportedException"/> for everything. Backends override only
/// the factory methods matching their <see cref="IMediaBackend.Capabilities"/>.
/// </summary>
public abstract class MediaBackendBase : IMediaBackend
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract BackendCapabilities Capabilities { get; }

    /// <inheritdoc />
    public abstract bool IsAvailable { get; }

    /// <inheritdoc />
    public virtual string? NativeVersion => null;

    /// <inheritdoc />
    public virtual IMediaDeviceEnumerator CreateDeviceEnumerator() => throw Unsupported(nameof(CreateDeviceEnumerator));

    /// <inheritdoc />
    public virtual IMediaPlayer CreatePlayer() => throw Unsupported(nameof(CreatePlayer));

    /// <inheritdoc />
    public virtual IAudioCapture CreateAudioCapture(AudioCaptureSettings settings) => throw Unsupported(nameof(CreateAudioCapture));

    /// <inheritdoc />
    public virtual IAudioRenderer CreateAudioRenderer(AudioCaptureSettings settings) => throw Unsupported(nameof(CreateAudioRenderer));

    /// <inheritdoc />
    public virtual IVideoCapture CreateVideoCapture(VideoCaptureSettings settings) => throw Unsupported(nameof(CreateVideoCapture));

    /// <inheritdoc />
    public virtual IMediaRecorder CreateRecorder(string outputPath) => throw Unsupported(nameof(CreateRecorder));

    /// <summary>Throws when the backend is not usable on this machine.</summary>
    protected void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new MediaBackendUnavailableException(Name, "backend unavailable: the native libraries were not found, or the platform is not supported.");
        }
    }

    private NotSupportedException Unsupported(string member) =>
        new($"Backend \"{Name}\" does not support {member}.");
}
