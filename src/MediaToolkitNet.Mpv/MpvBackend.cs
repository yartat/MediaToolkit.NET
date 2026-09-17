using MediaToolkitNet.Core;
using MediaToolkitNet.Core.Playback;

namespace MediaToolkitNet.Mpv;

/// <summary>
/// libmpv entry point. mpv is a complete player, so this backend offers
/// playback only: it has no capture, no recorder and no device enumeration.
/// </summary>
public sealed class MpvBackend : MediaBackendBase
{
    /// <summary>Shared instance.</summary>
    public static MpvBackend Instance { get; } = new();

    /// <inheritdoc />
    public override string Name => Native.Mpv.BackendName;

    /// <inheritdoc />
    public override BackendCapabilities Capabilities => BackendCapabilities.Playback;

    /// <inheritdoc />
    public override bool IsAvailable => Native.Mpv.IsAvailable;

    /// <inheritdoc />
    public override string? NativeVersion => Native.Mpv.IsAvailable ? Native.Mpv.VersionString : null;

    /// <inheritdoc />
    public override IMediaPlayer CreatePlayer()
    {
        EnsureAvailable();
        return new MpvPlayer();
    }
}
