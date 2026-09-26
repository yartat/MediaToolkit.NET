#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.GStreamer.Transcoding;

namespace MediaToolkitNet.GStreamer;

/// <summary>
/// GStreamer entry point.
/// </summary>
/// <remarks>
/// <para>
/// The backend does not implement the capture and playback interfaces: what it
/// offers is the pipeline itself, through <see cref="GStreamerPipeline"/>, which
/// covers all of them and more in GStreamer's own terms. Of the shared roles it
/// takes probing, through <c>GstDiscoverer</c>, and transcoding, which it runs
/// as one pipeline built from the request.
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
    public override BackendCapabilities Capabilities => BackendCapabilities.Probing | BackendCapabilities.Transcoding;

    /// <inheritdoc />
    public override bool IsAvailable => OperatingSystem.IsLinux() && Gst.IsAvailable;

    /// <inheritdoc />
    public override string? NativeVersion => IsAvailable ? Version : null;

    /// <summary>
    /// The version string GStreamer reports, or <see langword="null"/> when it
    /// is not available. Safe to read on any platform.
    /// </summary>
    public static string? Version => Gst.IsAvailable ? Gst.VersionString : null;

    /// <inheritdoc />
    public override IMediaProber CreateProber()
    {
        EnsureAvailable();
        return OperatingSystem.IsLinux() ? new GStreamerProber() : throw new PlatformNotSupportedException();
    }

    /// <inheritdoc />
    public override IMediaTranscoder CreateTranscoder()
    {
        EnsureAvailable();
        return OperatingSystem.IsLinux() ? new GStreamerTranscoder() : throw new PlatformNotSupportedException();
    }

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
