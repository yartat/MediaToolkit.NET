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

    /// <summary>Uncompressed 16-bit PCM audio; the same as <see cref="PcmS16"/>.</summary>
    Pcm,

    /// <summary>Dolby Digital (AC-3) audio.</summary>
    Ac3,

    /// <summary>DTS Coherent Acoustics audio. The encoder is experimental, and the backend opens it as such.</summary>
    Dts,

    /// <summary>Dolby TrueHD audio, lossless. The encoder is experimental, and the backend opens it as such.</summary>
    TrueHd,

    /// <summary>MPEG audio layer II.</summary>
    Mp2,

    /// <summary>MPEG audio layer III.</summary>
    Mp3,

    /// <summary>Vorbis audio.</summary>
    Vorbis,

    /// <summary>RealAudio 1.0, which is 14.4 kbps of mono at 8 kHz and nothing else.</summary>
    RealAudio,

    /// <summary>Uncompressed 8-bit unsigned PCM audio.</summary>
    PcmU8,

    /// <summary>Uncompressed 16-bit signed little-endian PCM audio.</summary>
    PcmS16,

    /// <summary>Uncompressed 24-bit signed little-endian PCM audio.</summary>
    PcmS24,

    /// <summary>Uncompressed 32-bit signed little-endian PCM audio.</summary>
    PcmS32,
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
    int KeyFrameInterval = 0)
{
    /// <summary>
    /// Encoder to use by name, which overrides <see cref="Codec"/> entirely,
    /// e.g. <c>libx264rgb</c>. Nothing means the backend picks the encoder it
    /// knows for the codec.
    /// </summary>
    public string? EncoderName { get; init; }

    /// <summary>
    /// Encoder settings passed straight through, e.g. <c>preset=veryfast</c> or
    /// <c>crf=23</c>. They are applied last, so they win over everything this
    /// record states.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }
}

/// <summary>
/// Description of the audio stream to write.
/// </summary>
/// <param name="Format">Format of the frames that will be pushed in.</param>
/// <param name="Codec">Codec to encode with.</param>
/// <param name="BitrateBitsPerSecond">Target bitrate; zero lets the backend decide.</param>
public readonly record struct AudioEncodingSettings(
    AudioFormat Format,
    MediaCodec Codec = MediaCodec.Aac,
    int BitrateBitsPerSecond = 0)
{
    /// <summary>
    /// Encoder to use by name, which overrides <see cref="Codec"/> entirely,
    /// e.g. <c>libfdk_aac</c> or <c>ac3_fixed</c>. Nothing means the backend
    /// picks the encoder it knows for the codec.
    /// </summary>
    public string? EncoderName { get; init; }

    /// <summary>
    /// Pins the format the encoder is opened with instead of letting the backend
    /// try the ones it knows until one is accepted. Choosing a wider one is how a
    /// lossless encoder is asked for more bits: FLAC and TrueHD write 24-bit from
    /// <see cref="Formats.SampleFormat.S32"/> and 16-bit from
    /// <see cref="Formats.SampleFormat.S16"/>.
    /// </summary>
    /// <remarks>
    /// The samples pushed in are converted to this, whatever they arrive as. An
    /// encoder that does not accept it fails to open rather than quietly using
    /// something else.
    /// </remarks>
    public SampleFormat? SampleFormat { get; init; }

    /// <summary>
    /// Asks for variable bitrate at this quality instead of a fixed rate, the
    /// way <c>-q:a</c> does on the FFmpeg command line: lower is better, and
    /// the scale belongs to the encoder. Nothing means constant bitrate.
    /// </summary>
    /// <remarks>
    /// Set this or <see cref="BitrateBitsPerSecond"/>, not both: an encoder
    /// asked for a quality ignores the rate it was also given.
    /// </remarks>
    public double? Quality { get; init; }

    /// <summary>
    /// Encoder settings passed straight through, e.g. <c>strict=-2</c>. They
    /// are applied last, so they win over everything this record states.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }
}

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
