using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.GStreamer.Native;

namespace MediaToolkitNet.GStreamer;

/// <summary>
/// GStreamer entry point.
/// </summary>
/// <remarks>
/// <para>
/// The backend does not implement the capture and playback interfaces: what it
/// offers is the pipeline itself, through <see cref="GStreamerPipeline"/>, which
/// covers all of them and more in GStreamer's own terms. It registers so that a
/// program can see whether GStreamer is present and which version.
/// </para>
/// <para>
/// <see cref="IsAvailable"/> is false on every platform other than Linux and
/// whenever the libraries are missing, and it never throws, because every
/// registered backend is probed.
/// </para>
/// </remarks>
public sealed class GStreamerBackend : MediaBackendBase
{
    /// <summary>Shared instance.</summary>
    public static GStreamerBackend Instance { get; } = new();

    /// <inheritdoc />
    public override string Name => GstConstants.BackendName;

    /// <inheritdoc />
    public override BackendCapabilities Capabilities => BackendCapabilities.None;

    /// <inheritdoc />
    public override bool IsAvailable => OperatingSystem.IsLinux() && Gst.IsAvailable;

    /// <inheritdoc />
    public override string? NativeVersion => IsAvailable ? Version : null;

    /// <summary>
    /// The version string GStreamer reports, or <see langword="null"/> when it
    /// is not available. Safe to read on any platform.
    /// </summary>
    public static string? Version => Gst.IsAvailable ? Gst.VersionString : null;

    /// <summary>
    /// Builds a pipeline from a <c>gst-launch</c> description.
    /// </summary>
    /// <param name="description">The pipeline description.</param>
    /// <exception cref="MediaBackendUnavailableException">GStreamer is not usable here.</exception>
    [SupportedOSPlatform("linux")]
    public GStreamerPipeline CreatePipeline(string description)
    {
        EnsureAvailable();
        return GStreamerPipeline.Parse(description);
    }
}
