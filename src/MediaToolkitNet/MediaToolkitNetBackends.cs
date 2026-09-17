using MediaToolkitNet.Core;
using MediaToolkitNet.Core.Capture;
using MediaToolkitNet.Core.Devices;
using MediaToolkitNet.Core.Playback;
using MediaToolkitNet.Core.Recording;

namespace MediaToolkitNet;

/// <summary>
/// Registry over every backend in this repository.
/// </summary>
/// <remarks>
/// <para>
/// Backends are listed in the order they should be preferred on the current
/// operating system: the native stack first for capture and playback, FFmpeg
/// wherever files have to be decoded or written. Nothing is loaded until a
/// backend is actually asked for, and probing one that is unavailable never
/// throws.
/// </para>
/// <para>
/// Reach past this class whenever you need something a backend offers and the
/// common abstraction does not, such as WASAPI loopback capture or mpv
/// properties.
/// </para>
/// </remarks>
public static class MediaToolkitNetBackends
{
    private static readonly Lazy<IEnumerable<IMediaBackend>> All = new(BuildRegistry);

    /// <summary>Every backend that compiles into this build, in preference order.</summary>
    public static IEnumerable<IMediaBackend> Registered => All.Value;

    /// <summary>Backends whose native libraries are present on this machine.</summary>
    public static IReadOnlyList<IMediaBackend> Available =>
        [.. Registered.Where(backend => backend.IsAvailable)];

    /// <summary>Finds a backend by its <see cref="IMediaBackend.Name"/>.</summary>
    public static IMediaBackend? Find(string name) =>
        Registered.FirstOrDefault(backend => string.Equals(backend.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the first available backend offering <paramref name="capability"/>.
    /// </summary>
    /// <exception cref="MediaBackendUnavailableException">Nothing on this machine can do it.</exception>
    public static IMediaBackend Require(BackendCapabilities capability)
    {
        var backend = Select(capability);
        return backend ?? throw new MediaBackendUnavailableException(
            "mediatoolkitnet",
            $"this system does not support {capability}. " +
            $"Available backends: {string.Join(", ", Registered.Select(b => b.Name))}.");
    }

    /// <summary>Returns the first available backend offering <paramref name="capability"/>, or null.</summary>
    public static IMediaBackend? Select(BackendCapabilities capability) =>
        Registered.FirstOrDefault(backend => (backend.Capabilities & capability) == capability && backend.IsAvailable);

    /// <summary>Creates a player using the best backend available.</summary>
    public static IMediaPlayer CreatePlayer() => Require(BackendCapabilities.Playback).CreatePlayer();

    /// <summary>Creates a recorder using the best backend available.</summary>
    public static IMediaRecorder CreateRecorder(string outputPath) =>
        Require(BackendCapabilities.Recording).CreateRecorder(outputPath);

    /// <summary>Opens an audio input endpoint using the best backend available.</summary>
    public static IAudioCapture CreateAudioCapture(AudioCaptureSettings settings) =>
        Require(BackendCapabilities.AudioCapture).CreateAudioCapture(settings);

    /// <summary>Opens an audio output endpoint using the best backend available.</summary>
    public static IAudioRenderer CreateAudioRenderer(AudioCaptureSettings settings) =>
        Require(BackendCapabilities.AudioRender).CreateAudioRenderer(settings);

    /// <summary>Opens a video input device using the best backend available.</summary>
    public static IVideoCapture CreateVideoCapture(VideoCaptureSettings settings) =>
        Require(BackendCapabilities.VideoCapture).CreateVideoCapture(settings);

    /// <summary>Lists devices from every available backend that can enumerate.</summary>
    public static IReadOnlyList<MediaDevice> EnumerateDevices(MediaDeviceKind kind = MediaDeviceKind.All)
    {
        var result = new List<MediaDevice>();
        foreach (var backend in Registered)
        {
            if ((backend.Capabilities & BackendCapabilities.DeviceEnumeration) == 0 || !backend.IsAvailable)
            {
                continue;
            }

            try
            {
                result.AddRange(backend.CreateDeviceEnumerator().Enumerate(kind));
            }
            catch (MediaToolkitNetException)
            {
                // One broken backend must not hide the devices of the others.
            }
        }

        return result;
    }

    private static IEnumerable<IMediaBackend> BuildRegistry()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Windows.WindowsBackend.Instance;
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return Linux.LinuxBackend.Instance;
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return MacOS.MacOSBackend.Instance;
        }

        // mpv renders playback itself, so it comes before FFmpeg for that role;
        // FFmpeg remains the only portable decoder and recorder.
        yield return Mpv.MpvBackend.Instance;
        yield return FFmpeg.FFmpegBackend.Instance;
    }
}
