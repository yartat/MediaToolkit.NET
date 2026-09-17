using MediaToolkitNet.Core;
using MediaToolkitNet.Core.Devices;
using MediaToolkitNet.Core.Playback;
using MediaToolkitNet.Core.Recording;
using MediaToolkitNet.FFmpeg.Native;

namespace MediaToolkitNet.FFmpeg;

/// <summary>
/// FFmpeg entry point: demuxing, decoding, encoding, muxing and device
/// enumeration through libavdevice.
/// </summary>
/// <remarks>
/// FFmpeg owns no output hardware, so this backend advertises neither audio
/// render nor a self-rendering player: <see cref="CreatePlayer"/> returns a
/// decode-only <see cref="FFmpegPlayer"/>.
/// </remarks>
public sealed class FFmpegBackend : MediaBackendBase
{
    /// <summary>Shared instance; the underlying libraries are global anyway.</summary>
    public static FFmpegBackend Instance { get; } = new();

    /// <inheritdoc />
    public override string Name => FFmpegLibraries.BackendName;

    /// <inheritdoc />
    public override BackendCapabilities Capabilities =>
        BackendCapabilities.DeviceEnumeration |
        BackendCapabilities.Playback |
        BackendCapabilities.Recording;

    /// <inheritdoc />
    public override bool IsAvailable => FFmpegLibraries.IsAvailable;

    /// <inheritdoc />
    public override string? NativeVersion => FFmpegLibraries.IsAvailable ? FFmpegLibraries.VersionString : null;

    /// <summary>Sets the libavutil log level, e.g. 16 for AV_LOG_ERROR or 32 for AV_LOG_INFO.</summary>
    public static unsafe void SetLogLevel(int level)
    {
        FFmpegLibraries.EnsureLoaded();
        AV.av_log_set_level(level);
    }

    /// <inheritdoc />
    public override IMediaDeviceEnumerator CreateDeviceEnumerator()
    {
        EnsureAvailable();
        return new FFmpegDeviceEnumerator();
    }

    /// <inheritdoc />
    public override IMediaPlayer CreatePlayer()
    {
        EnsureAvailable();
        return new FFmpegPlayer();
    }

    /// <inheritdoc />
    public override IMediaRecorder CreateRecorder(string outputPath)
    {
        EnsureAvailable();
        return new FFmpegRecorder(outputPath);
    }
}
