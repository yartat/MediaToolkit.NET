using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Devices;
using MediaToolkitNet.Abstractions.Playback;
using MediaToolkitNet.Abstractions.Recording;
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

    /// <summary>
    /// Sets the libavutil log level, e.g. <see cref="AVConstants.LogError"/> or
    /// <see cref="AVConstants.LogInfo"/>.
    /// </summary>
    /// <param name="level">The level, as <see cref="AVConstants"/> names them.</param>
    /// <remarks>
    /// The default is <see cref="AVConstants.LogError"/>, and FFmpeg writes at
    /// that level for things a caller may well be doing on purpose: the recorder
    /// asks an encoder about one sample format after another until one is
    /// accepted, and every refusal along the way goes to standard error. An
    /// application that reports its own failures usually wants
    /// <see cref="AVConstants.LogFatal"/> or <see cref="AVConstants.LogQuiet"/>.
    /// This is process-wide.
    /// </remarks>
    public static unsafe void SetLogLevel(int level)
    {
        FFmpegLibraries.EnsureLoaded();
        AV.av_log_set_level(level);
    }

    /// <summary>Reads the libavutil log level.</summary>
    /// <returns>Returns the level, as <see cref="AVConstants"/> names them.</returns>
    public static unsafe int GetLogLevel()
    {
        FFmpegLibraries.EnsureLoaded();
        return AV.av_log_get_level();
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
