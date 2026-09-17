using MediaToolkitNet.Core.Devices;
using MediaToolkitNet.Core.Formats;
using MediaToolkitNet.Core.Frames;

namespace MediaToolkitNet.Core.Capture;

/// <summary>
/// Settings for opening an audio capture or render endpoint.
/// </summary>
/// <param name="Device">Device to open, or <see langword="null"/> for the platform default.</param>
/// <param name="Format">Requested format. Backends may negotiate a different one; check the actual format after opening.</param>
/// <param name="BufferDuration">Requested endpoint buffer size. Smaller means lower latency and more callbacks.</param>
public readonly record struct AudioCaptureSettings(
    MediaDevice? Device,
    AudioFormat Format,
    TimeSpan BufferDuration)
{
    /// <summary>48 kHz stereo 16-bit from the default device with a 20 ms buffer.</summary>
    public static AudioCaptureSettings Default =>
        new(null, AudioFormat.Cd48Stereo, TimeSpan.FromMilliseconds(20));
}

/// <summary>
/// Settings for opening a video capture device.
/// </summary>
/// <param name="Device">Device to open, or <see langword="null"/> for the first device found.</param>
/// <param name="Format">Requested format. Backends pick the closest supported mode.</param>
/// <param name="BufferCount">Number of driver-side buffers to queue, where the backend exposes that knob.</param>
public readonly record struct VideoCaptureSettings(
    MediaDevice? Device,
    VideoFormat Format,
    int BufferCount = 4)
{
    /// <summary>640x480 NV12 at 30 fps from the first device found.</summary>
    public static VideoCaptureSettings Default =>
        new(null, new VideoFormat(640, 480, PixelFormat.Nv12, 30));
}

/// <summary>Common lifecycle of a capture source.</summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>Name of the backend implementing this source.</summary>
    string Backend { get; }

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    bool IsRunning { get; }

    /// <summary>Raised when the backend fails asynchronously, e.g. on its capture thread.</summary>
    event EventHandler<MediaToolkitNetException>? Failed;

    /// <summary>Begins delivering frames.</summary>
    void Start();

    /// <summary>Stops delivering frames. The source can be started again.</summary>
    void Stop();
}

/// <summary>Captures PCM audio from an input endpoint.</summary>
public interface IAudioCapture : ICaptureSource
{
    /// <summary>Format the endpoint was actually opened with.</summary>
    AudioFormat Format { get; }

    /// <summary>Raised for each captured block, on the backend capture thread.</summary>
    event AudioFrameHandler? FrameCaptured;
}

/// <summary>Captures raw frames from a video input device.</summary>
public interface IVideoCapture : ICaptureSource
{
    /// <summary>Format the device was actually opened with.</summary>
    VideoFormat Format { get; }

    /// <summary>Lists the modes the open device advertises.</summary>
    IReadOnlyList<VideoFormat> SupportedFormats { get; }

    /// <summary>Raised for each captured frame, on the backend capture thread.</summary>
    event VideoFrameHandler? FrameCaptured;
}

/// <summary>Plays PCM audio through an output endpoint.</summary>
public interface IAudioRenderer : IDisposable
{
    /// <summary>Name of the backend implementing this renderer.</summary>
    string Backend { get; }

    /// <summary>Format the endpoint was actually opened with.</summary>
    AudioFormat Format { get; }

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    bool IsRunning { get; }

    /// <summary>Starts the output stream.</summary>
    void Start();

    /// <summary>Stops the output stream and discards anything still queued.</summary>
    void Stop();

    /// <summary>
    /// Queues interleaved PCM for playback, blocking while the endpoint buffer
    /// is full.
    /// </summary>
    /// <param name="interleaved">Samples in <see cref="Format"/>.</param>
    /// <returns>Number of bytes accepted.</returns>
    int Write(ReadOnlySpan<byte> interleaved);

    /// <summary>Blocks until everything already queued has been played.</summary>
    void Drain();
}
