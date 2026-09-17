using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;

namespace MediaToolkitNet.Abstractions.Recording;

/// <summary>Codec requested for an encoded stream.</summary>
public enum MediaCodec
{
    /// <summary>Let the backend choose based on the container.</summary>
    Default = 0,

    /// <summary>H.264 / AVC video.</summary>
    H264,

    /// <summary>H.265 / HEVC video.</summary>
    Hevc,

    /// <summary>VP9 video.</summary>
    Vp9,

    /// <summary>AV1 video.</summary>
    Av1,

    /// <summary>Motion JPEG video.</summary>
    Mjpeg,

    /// <summary>AAC audio.</summary>
    Aac,

    /// <summary>Opus audio.</summary>
    Opus,

    /// <summary>FLAC audio.</summary>
    Flac,

    /// <summary>Uncompressed PCM audio.</summary>
    Pcm,
}

/// <summary>
/// Description of the video stream to write.
/// </summary>
/// <param name="Format">Format of the frames that will be pushed in.</param>
/// <param name="Codec">Codec to encode with.</param>
/// <param name="BitrateBitsPerSecond">Target bitrate; zero lets the backend decide.</param>
/// <param name="KeyFrameInterval">Distance between key frames in frames; zero lets the backend decide.</param>
public readonly record struct VideoEncodingSettings(
    VideoFormat Format,
    MediaCodec Codec = MediaCodec.H264,
    int BitrateBitsPerSecond = 0,
    int KeyFrameInterval = 0);

/// <summary>
/// Description of the audio stream to write.
/// </summary>
/// <param name="Format">Format of the frames that will be pushed in.</param>
/// <param name="Codec">Codec to encode with.</param>
/// <param name="BitrateBitsPerSecond">Target bitrate; zero lets the backend decide.</param>
public readonly record struct AudioEncodingSettings(
    AudioFormat Format,
    MediaCodec Codec = MediaCodec.Aac,
    int BitrateBitsPerSecond = 0);

/// <summary>
/// Writes encoded media to a container. Frames are pushed in by the caller,
/// which keeps the recorder independent of where they came from.
/// </summary>
public interface IMediaRecorder : IDisposable
{
    /// <summary>Name of the backend implementing this recorder.</summary>
    string Backend { get; }

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    bool IsRecording { get; }

    /// <summary>Wall-clock duration written so far.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>
    /// Adds a video stream. Must be called before <see cref="Start"/>.
    /// </summary>
    /// <returns>Stream index to pass to <see cref="WriteVideo"/>.</returns>
    int AddVideoStream(VideoEncodingSettings settings);

    /// <summary>
    /// Adds an audio stream. Must be called before <see cref="Start"/>.
    /// </summary>
    /// <returns>Stream index to pass to <see cref="WriteAudio"/>.</returns>
    int AddAudioStream(AudioEncodingSettings settings);

    /// <summary>Writes the container header and begins accepting frames.</summary>
    void Start();

    /// <summary>Encodes and writes one video frame.</summary>
    void WriteVideo(int streamIndex, in VideoFrame frame);

    /// <summary>Encodes and writes one block of audio.</summary>
    void WriteAudio(int streamIndex, in AudioFrame frame);

    /// <summary>Flushes the encoders, writes the trailer and closes the output.</summary>
    void Stop();
}
