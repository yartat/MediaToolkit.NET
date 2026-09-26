#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Playback;

namespace MediaToolkitNet.Mpv;

/// <summary>
/// libmpv entry point. mpv is a complete player: this backend offers
/// playback, probing, and transcoding through mpv's encoding mode, but no
/// capture, no recorder and no device enumeration.
/// </summary>
public sealed class MpvBackend : MediaBackendBase
{
    /// <summary>Shared instance.</summary>
    public static MpvBackend Instance { get; } = new();

    /// <inheritdoc />
    public override string Name => Native.Mpv.BackendName;

    /// <inheritdoc />
    public override BackendCapabilities Capabilities =>
        BackendCapabilities.Playback | BackendCapabilities.Probing | BackendCapabilities.Transcoding;

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

    /// <inheritdoc />
    public override Abstractions.Transcoding.IMediaProber CreateProber()
    {
        EnsureAvailable();
        return new Transcoding.MpvProber();
    }

    /// <inheritdoc />
    public override Abstractions.Transcoding.IMediaTranscoder CreateTranscoder()
    {
        EnsureAvailable();
        return new Transcoding.MpvTranscoder();
    }
}
