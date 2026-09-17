using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Playback;
using MediaToolkitNet.MacOS.Native;

namespace MediaToolkitNet.MacOS;

/// <summary>
/// Player backed by <c>AVPlayer</c>.
/// </summary>
/// <remarks>
/// <para>
/// AVPlayer decodes and renders on its own, so like the mpv backend it never
/// raises <see cref="VideoFrameDecoded"/>. Without an <c>AVPlayerLayer</c>
/// attached to a view it plays audio only, which is what a console process
/// gets; hosts with a window can read <see cref="Handle"/> and attach a layer
/// themselves.
/// </para>
/// <para>
/// State is polled rather than observed: wiring up KVO would mean building
/// another Objective-C class at runtime for a single notification.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed unsafe class AVPlayerPlayer : IMediaPlayer
{
    private readonly Lock _gate = new();

    private nint _player;
    private PlaybackState _state = PlaybackState.Closed;
    private double _volume = 1.0;
    private bool _disposed;

    /// <summary>Raw <c>AVPlayer</c> pointer, for hosts that want to attach an <c>AVPlayerLayer</c>.</summary>
    public nint Handle => _player;

    /// <inheritdoc />
    public string Backend => MacDeviceEnumerator.BackendName;

    /// <inheritdoc />
    public PlaybackState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public TimeSpan Duration
    {
        get
        {
            if (_player == 0)
            {
                return TimeSpan.Zero;
            }

            var item = ObjC.Send(_player, ObjC.Sel("currentItem"));
            return item == 0 ? TimeSpan.Zero : SendCMTime(item, ObjC.Sel("duration")).ToTimeSpan();
        }
    }

    /// <inheritdoc />
    public TimeSpan Position =>
        _player == 0 ? TimeSpan.Zero : SendCMTime(_player, ObjC.Sel("currentTime")).ToTimeSpan();

    /// <inheritdoc />
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0d, 1d);
            if (_player != 0)
            {
                ObjC.SendSetFloat(_player, ObjC.Sel("setVolume:"), (float)_volume);
            }
        }
    }

    /// <inheritdoc />
    public bool CanSeek => _player != 0;

    /// <inheritdoc />
    public event EventHandler<PlaybackStateChange>? StateChanged;

    // AVPlayer reports failures through KVO on its status property, which this
    // backend does not observe; the event is part of the contract.
#pragma warning disable CS0067
    /// <inheritdoc />
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <inheritdoc />
    public event VideoFrameHandler? VideoFrameDecoded;

    /// <inheritdoc />
    public event AudioFrameHandler? AudioFrameDecoded;
#pragma warning restore CS0067

    /// <inheritdoc />
    public void Open(string uri)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        if (!AVFoundation.IsAvailable)
        {
            throw new MediaBackendUnavailableException(Backend, "AVFoundation is unavailable in this process.");
        }

        Release();

        var url = ObjC.NSUrl(uri);
        var playerClass = ObjC.objc_getClass("AVPlayer");
        if (playerClass == 0)
        {
            throw new MediaBackendUnavailableException(Backend, "the AVPlayer class was not found.");
        }

        var player = ObjC.Send(ObjC.Send(playerClass, Selectors.Alloc), ObjC.Sel("initWithURL:"), url);
        if (player == 0)
        {
            throw new MediaToolkitNetException(Backend, $"AVPlayer could not open {uri}", 0);
        }

        _player = player;
        ObjC.SendSetFloat(_player, ObjC.Sel("setVolume:"), (float)_volume);
        TransitionTo(PlaybackState.Stopped);
    }

    /// <inheritdoc />
    public void Play()
    {
        RequirePlayer();
        ObjC.Send(_player, ObjC.Sel("play"));
        TransitionTo(PlaybackState.Playing);
    }

    /// <inheritdoc />
    public void Pause()
    {
        if (_player == 0)
        {
            return;
        }

        ObjC.Send(_player, ObjC.Sel("pause"));
        TransitionTo(PlaybackState.Paused);
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_player == 0)
        {
            return;
        }

        ObjC.Send(_player, ObjC.Sel("pause"));
        Seek(TimeSpan.Zero);
        TransitionTo(PlaybackState.Stopped);
    }

    /// <inheritdoc />
    public void Seek(TimeSpan position)
    {
        RequirePlayer();

        var time = CMTime.FromSeconds(Math.Max(position.TotalSeconds, 0d));
        ((delegate* unmanaged[Cdecl]<nint, nint, CMTime, void>)ObjC.MsgSend)(
            _player, ObjC.Sel("seekToTime:"), time);
    }

    /// <summary>
    /// Sends a message whose return value is a <see cref="CMTime"/>.
    /// </summary>
    /// <remarks>
    /// On x86-64 a struct larger than 16 bytes comes back through a hidden
    /// pointer, which is what <c>objc_msgSend_stret</c> exists for. On arm64 the
    /// ABI handles it inside the normal entry point.
    /// </remarks>
    private static CMTime SendCMTime(nint receiver, nint selector)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64 && ObjC.MsgSendStret is not null)
        {
            CMTime result;
            ((delegate* unmanaged[Cdecl]<CMTime*, nint, nint, void>)ObjC.MsgSendStret)(&result, receiver, selector);
            return result;
        }

        return ((delegate* unmanaged[Cdecl]<nint, nint, CMTime>)ObjC.MsgSend)(receiver, selector);
    }

    private void RequirePlayer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_player == 0)
        {
            throw new InvalidOperationException("Open the media first.");
        }
    }

    private void TransitionTo(PlaybackState next)
    {
        PlaybackState previous;
        lock (_gate)
        {
            if (_state == next)
            {
                return;
            }

            previous = _state;
            _state = next;
        }

        StateChanged?.Invoke(this, new PlaybackStateChange(previous, next));
    }

    private void Release()
    {
        if (_player == 0)
        {
            return;
        }

        ObjC.Send(_player, ObjC.Sel("pause"));
        ObjC.Send(_player, Selectors.Release);
        _player = 0;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release();
        TransitionTo(PlaybackState.Closed);
    }
}
