#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Recording;

namespace MediaToolkitNet.Abstractions.Transcoding;

/// <summary>
/// Picks streams out of an input. Resolved against what the input was probed to
/// contain, so a selector that matches nothing is reported before anything runs.
/// </summary>
public sealed record StreamSource
{
    private StreamSource(int input, int? index, MediaStreamKind? kind, int? kindIndex, string? language, bool all)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(input);
        Input = input;
        Index = index;
        Kind = kind;
        KindIndex = kindIndex;
        Language = language;
        MatchesAll = all;
    }

    /// <summary>Which of the request's inputs to take the stream from.</summary>
    public int Input { get; }

    /// <summary>A container stream index, when the stream is picked by position.</summary>
    public int? Index { get; }

    /// <summary>The kind of stream wanted, when it is picked by kind.</summary>
    public MediaStreamKind? Kind { get; }

    /// <summary>Which stream of that kind, counting from 0, when one is picked by its place among its kind.</summary>
    public int? KindIndex { get; }

    /// <summary>A language the stream must be tagged with, when one is required.</summary>
    public string? Language { get; }

    /// <summary>True when every matching stream is taken rather than the first.</summary>
    public bool MatchesAll { get; }

    /// <summary>The stream at a container index, whatever it carries.</summary>
    public static StreamSource At(int index, int input = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return new StreamSource(input, index, null, null, null, all: false);
    }

    /// <summary>
    /// The first stream of a kind, optionally in a language. For video, still
    /// pictures such as cover art are passed over.
    /// </summary>
    public static StreamSource First(MediaStreamKind kind, string? language = null, int input = 0) =>
        new(input, null, kind, null, language, all: false);

    /// <summary>The n-th stream of a kind, counting from 0, the way <c>0:a:1</c> does on the ffmpeg command line.</summary>
    public static StreamSource Nth(MediaStreamKind kind, int n, int input = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        return new StreamSource(input, null, kind, n, null, all: false);
    }

    /// <summary>Every stream of a kind, optionally only those in a language.</summary>
    public static StreamSource All(MediaStreamKind kind, string? language = null, int input = 0) =>
        new(input, null, kind, null, language, all: true);

    /// <inheritdoc />
    public override string ToString()
    {
        var prefix = Input == 0 ? string.Empty : $"input {Input} ";
        if (Index is { } index)
        {
            return $"{prefix}stream #{index}";
        }

        var language = Language is null ? string.Empty : $" [{Language}]";
        return MatchesAll ? $"{prefix}all {Kind} streams{language}"
            : KindIndex is { } n ? $"{prefix}{Kind} stream {n}"
            : $"{prefix}first {Kind} stream{language}";
    }
}

/// <summary>
/// One thing the output should contain, and where it comes from. Created
/// through the static factories: <see cref="Copy"/>, <see cref="Drop"/>,
/// <see cref="Video"/>, <see cref="Audio"/>, <see cref="Subtitles"/> and
/// <see cref="BurnIn"/>.
/// </summary>
public abstract record OutputStream
{
    private protected OutputStream(StreamSource source) =>
        Source = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>The input stream or streams this takes.</summary>
    public StreamSource Source { get; }

    /// <summary>Language to tag the output stream with, instead of the input's.</summary>
    public string? Language { get; init; }

    /// <summary>Title to give the output stream, instead of the input's.</summary>
    public string? Title { get; init; }

    /// <summary>Whether to mark the output stream as the default of its kind, instead of copying the input's flag.</summary>
    public bool? IsDefault { get; init; }

    /// <summary>Whether to mark the output stream as forced, instead of copying the input's flag.</summary>
    public bool? IsForced { get; init; }

    /// <summary>Copies the streams as they are: no decoding, no loss, and only as fast as the disk.</summary>
    public static CopyStream Copy(StreamSource source) => new(source);

    /// <summary>
    /// Leaves the streams out. Useful beside an <see cref="StreamSource.All"/>
    /// selector, which then passes over them.
    /// </summary>
    public static DropStream Drop(StreamSource source) => new(source);

    /// <summary>Re-encodes video.</summary>
    public static EncodeVideo Video(StreamSource source, VideoOutputSettings settings) => new(source, settings);

    /// <summary>Re-encodes audio.</summary>
    public static EncodeAudio Audio(StreamSource source, AudioOutputSettings settings) => new(source, settings);

    /// <summary>
    /// Converts text subtitles to another format, as moving SRT into MP4 needs:
    /// MP4 accepts only <see cref="MediaCodec.MovText"/>.
    /// </summary>
    public static ConvertSubtitles Subtitles(StreamSource source, MediaCodec codec) => new(source, codec);

    /// <summary>
    /// Renders subtitles into the picture of a video stream that is being
    /// re-encoded by an <see cref="EncodeVideo"/> in the same request. Produces no
    /// output stream of its own.
    /// </summary>
    public static BurnSubtitles BurnIn(StreamSource subtitles, StreamSource video) => new(subtitles, video);
}

/// <summary>Copies streams without decoding them.</summary>
public sealed record CopyStream : OutputStream
{
    internal CopyStream(StreamSource source)
        : base(source)
    {
    }
}

/// <summary>Leaves streams out of the output.</summary>
public sealed record DropStream : OutputStream
{
    internal DropStream(StreamSource source)
        : base(source)
    {
    }
}

/// <summary>Re-encodes video streams.</summary>
public sealed record EncodeVideo : OutputStream
{
    internal EncodeVideo(StreamSource source, VideoOutputSettings settings)
        : base(source) =>
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>What to encode to.</summary>
    public VideoOutputSettings Settings { get; }
}

/// <summary>Re-encodes audio streams.</summary>
public sealed record EncodeAudio : OutputStream
{
    internal EncodeAudio(StreamSource source, AudioOutputSettings settings)
        : base(source) =>
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>What to encode to.</summary>
    public AudioOutputSettings Settings { get; }
}

/// <summary>Converts text subtitles to another format.</summary>
public sealed record ConvertSubtitles : OutputStream
{
    internal ConvertSubtitles(StreamSource source, MediaCodec codec)
        : base(source)
    {
        if (!codec.IsTextSubtitle())
        {
            throw new ArgumentException(
                $"{codec} is not a text subtitle format; subtitles can be converted to SubRip, Ass, WebVtt or MovText.",
                nameof(codec));
        }

        Codec = codec;
    }

    /// <summary>The format to convert to.</summary>
    public MediaCodec Codec { get; }
}

/// <summary>Renders subtitles into a re-encoded video stream.</summary>
public sealed record BurnSubtitles : OutputStream
{
    internal BurnSubtitles(StreamSource subtitles, StreamSource video)
        : base(subtitles) =>
        Target = video ?? throw new ArgumentNullException(nameof(video));

    /// <summary>The video to render them into.</summary>
    public StreamSource Target { get; }
}

/// <summary>How hard the encoder works for each bit it saves, from fastest to smallest.</summary>
/// <remarks>
/// The names are x264's presets, which most encoders map onto: x265 takes the
/// same names, and the backends translate them for libvpx, SVT-AV1 and the
/// GStreamer encoders. An encoder with no such setting ignores it.
/// </remarks>
public enum EncoderSpeed
{
    /// <summary>Fastest, largest.</summary>
    UltraFast,

    /// <summary>x264 <c>superfast</c>.</summary>
    SuperFast,

    /// <summary>x264 <c>veryfast</c>.</summary>
    VeryFast,

    /// <summary>x264 <c>faster</c>.</summary>
    Faster,

    /// <summary>x264 <c>fast</c>.</summary>
    Fast,

    /// <summary>x264 <c>medium</c>, the usual default.</summary>
    Medium,

    /// <summary>x264 <c>slow</c>.</summary>
    Slow,

    /// <summary>x264 <c>slower</c>.</summary>
    Slower,

    /// <summary>Slowest, smallest.</summary>
    VerySlow,
}

/// <summary>What to re-encode a video stream to. Anything left unset keeps the input's value.</summary>
/// <param name="Codec">The codec to encode with.</param>
public sealed record VideoOutputSettings(MediaCodec Codec = MediaCodec.H264)
{
    /// <summary>Output width. Give only one of width and height, or -1 for the other, to keep the aspect ratio.</summary>
    public int? Width { get; init; }

    /// <summary>Output height. Give only one of width and height, or -1 for the other, to keep the aspect ratio.</summary>
    public int? Height { get; init; }

    /// <summary>Output frame rate. Frames are duplicated or dropped to reach it.</summary>
    public Rational? FrameRate { get; init; }

    /// <summary>Pixel layout to encode. Nothing lets the backend pick one the encoder accepts.</summary>
    public PixelFormat? PixelFormat { get; init; }

    /// <summary>
    /// Constant quality instead of a bit rate, on the encoder's own scale: CRF for
    /// x264 and x265, CQ for libvpx and SVT-AV1. Lower is better.
    /// </summary>
    public double? Quality { get; init; }

    /// <summary>Target bit rate in bits per second; 0 leaves it to the encoder, or to <see cref="Quality"/>.</summary>
    public int BitrateBitsPerSecond { get; init; }

    /// <summary>Distance between key frames in frames; 0 leaves it to the encoder.</summary>
    public int KeyFrameInterval { get; init; }

    /// <summary>Speed against size, where the encoder has such a setting.</summary>
    public EncoderSpeed? Speed { get; init; }

    /// <summary>
    /// The encoder by name, which overrides <see cref="Codec"/>: an FFmpeg encoder
    /// such as <c>libx264</c>, a GStreamer element such as <c>x264enc</c>, or what
    /// mpv's <c>--ovc</c> takes.
    /// </summary>
    public string? EncoderName { get; init; }

    /// <summary>
    /// Encoder settings passed through as they are: AVOptions for FFmpeg and mpv,
    /// element properties for GStreamer. Applied last, so they win.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }

    /// <summary>Filters applied before the size and rate above, in order.</summary>
    public IReadOnlyList<VideoFilter> Filters { get; init; } = [];
}

/// <summary>What to re-encode an audio stream to. Anything left unset keeps the input's value.</summary>
/// <param name="Codec">The codec to encode with.</param>
public sealed record AudioOutputSettings(MediaCodec Codec = MediaCodec.Aac)
{
    /// <summary>Output sample rate.</summary>
    public int? SampleRate { get; init; }

    /// <summary>Output channel count. Down- and up-mixing follow the backend's defaults.</summary>
    public int? Channels { get; init; }

    /// <summary>Output speaker layout, when the channel count alone is ambiguous.</summary>
    public ulong? ChannelMask { get; init; }

    /// <summary>Sample layout to encode; pins the width for lossless encoders.</summary>
    public SampleFormat? SampleFormat { get; init; }

    /// <summary>Variable bit rate at this quality, on the encoder's own scale, as <c>-q:a</c> does it.</summary>
    public double? Quality { get; init; }

    /// <summary>Target bit rate in bits per second; 0 leaves it to the encoder.</summary>
    public int BitrateBitsPerSecond { get; init; }

    /// <summary>The encoder by name, which overrides <see cref="Codec"/>.</summary>
    public string? EncoderName { get; init; }

    /// <summary>Encoder settings passed through as they are. Applied last, so they win.</summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }

    /// <summary>Filters applied before the rate and layout above, in order.</summary>
    public IReadOnlyList<AudioFilter> Filters { get; init; } = [];
}

/// <summary>
/// A job for a transcoder: inputs, an output, and what the output should
/// contain.
/// </summary>
/// <param name="Output">The file to write. An existing file is overwritten.</param>
public sealed record TranscodeRequest(string Output)
{
    /// <summary>
    /// The files to read. The first is the main input; the others are sources
    /// such as an external subtitle file or an alternative audio track, addressed
    /// through <see cref="StreamSource.Input"/>.
    /// </summary>
    public IReadOnlyList<string> Inputs { get; init; } = [];

    /// <summary>The container to write; <see cref="MediaContainer.Auto"/> takes it from the extension of <see cref="Output"/>.</summary>
    public MediaContainer Container { get; init; } = MediaContainer.Auto;

    /// <summary>
    /// What the output contains, in output order. Empty copies every video,
    /// audio and subtitle stream of the first input, which is a container change
    /// with no re-encoding.
    /// </summary>
    public IReadOnlyList<OutputStream> Streams { get; init; } = [];

    /// <summary>Where in the input to start. Copied streams start at the key frame at or before it.</summary>
    public TimeSpan? Start { get; init; }

    /// <summary>Where in the input to stop.</summary>
    public TimeSpan? End { get; init; }

    /// <summary>
    /// File-level tags to write. The first input's tags are copied first, and
    /// these replace them key by key; an empty value removes a tag.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Whether to carry the first input's chapters over, trimmed to <see cref="Start"/> and <see cref="End"/>.</summary>
    public bool CopyChapters { get; init; } = true;

    /// <summary>
    /// Container options passed through as they are, for example
    /// <c>movflags=+faststart</c> for FFmpeg's MP4 muxer.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ContainerOptions { get; init; }

    /// <summary>A request that copies every stream of <paramref name="input"/> into another container.</summary>
    public static TranscodeRequest Remux(string input, string output) => new(output) { Inputs = [input] };
}
