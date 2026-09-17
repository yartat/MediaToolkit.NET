using MediaToolkitNet.Core.Frames;

namespace MediaToolkitNet.Core.Playback;

/// <summary>Lifecycle state of an <see cref="IMediaPlayer"/>.</summary>
public enum PlaybackState
{
    /// <summary>No media is open.</summary>
    Closed = 0,

    /// <summary>Media is open and ready, but not advancing.</summary>
    Stopped,

    /// <summary>Media is advancing.</summary>
    Playing,

    /// <summary>Media is open and positioned, but not advancing.</summary>
    Paused,

    /// <summary>Playback reached the end of the stream.</summary>
    Ended,

    /// <summary>The backend reported an unrecoverable error.</summary>
    Faulted,
}

/// <summary>Arguments of <see cref="IMediaPlayer.StateChanged"/>.</summary>
/// <param name="Previous">State before the transition.</param>
/// <param name="Current">State after the transition.</param>
public readonly record struct PlaybackStateChange(PlaybackState Previous, PlaybackState Current);

/// <summary>
/// Plays a media source. Backends differ in how much they do for you: mpv
/// renders to its own window, Media Foundation and FFmpeg only decode and hand
/// the frames back through <see cref="VideoFrameDecoded"/> and
/// <see cref="AudioFrameDecoded"/>.
/// </summary>
public interface IMediaPlayer : IDisposable
{
    /// <summary>Name of the backend implementing this player.</summary>
    string Backend { get; }

    /// <summary>Current lifecycle state.</summary>
    PlaybackState State { get; }

    /// <summary>Total duration of the open media, or <see cref="TimeSpan.Zero"/> when unknown or live.</summary>
    TimeSpan Duration { get; }

    /// <summary>Current playback position.</summary>
    TimeSpan Position { get; }

    /// <summary>Linear output volume in the range 0.0 to 1.0.</summary>
    double Volume { get; set; }

    /// <summary>True when the player can honour <see cref="Seek"/>.</summary>
    bool CanSeek { get; }

    /// <summary>Raised on every lifecycle transition.</summary>
    event EventHandler<PlaybackStateChange>? StateChanged;

    /// <summary>Raised when the backend fails asynchronously, e.g. on its decode thread.</summary>
    event EventHandler<MediaToolkitNetException>? Failed;

    /// <summary>
    /// Raised for each decoded video frame, on the backend decode thread. The
    /// frame is only valid for the duration of the call. Backends that render
    /// on their own (mpv) never raise it.
    /// </summary>
    event VideoFrameHandler? VideoFrameDecoded;

    /// <summary>
    /// Raised for each decoded audio frame, on the backend decode thread. The
    /// frame is only valid for the duration of the call.
    /// </summary>
    event AudioFrameHandler? AudioFrameDecoded;

    /// <summary>Opens a file path or a URL. Any previously open media is closed first.</summary>
    void Open(string uri);

    /// <summary>Starts or resumes playback.</summary>
    void Play();

    /// <summary>Suspends playback, keeping the current position.</summary>
    void Pause();

    /// <summary>Stops playback and rewinds to the beginning.</summary>
    void Stop();

    /// <summary>Moves the playback position. Throws when <see cref="CanSeek"/> is false.</summary>
    void Seek(TimeSpan position);
}
