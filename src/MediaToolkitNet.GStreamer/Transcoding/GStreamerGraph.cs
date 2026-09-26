#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;

namespace MediaToolkitNet.GStreamer.Transcoding;

/// <summary>
/// A <see cref="TranscodePlan"/> turned into one <c>gst-launch</c> description,
/// plus what cannot be written into one: property values set after parsing,
/// the gates a trim needs and the pads the seek goes to.
/// </summary>
/// <remarks>
/// <para>
/// Each input is <c>filesrc ! parsebin</c>, and each output stream is a branch
/// from one of the parser's pads, <c>p0.src_2</c>, into a named pad of the
/// muxer. <c>parsebin</c> names its pads in the order the demuxer exposes
/// streams, which is container order, so a stream index from the prober is
/// also a pad name. Pads nothing asks for stay unlinked; linking them to a
/// <c>fakesink</c> instead stalls preroll.
/// </para>
/// <para>
/// Encoded branches decode with <c>decodebin</c>, not <c>decodebin3</c>: after
/// <c>parsebin</c> the latter selects streams itself, picks the default audio
/// track, and leaves a branch fed with any other one not-linked. Subtitles are
/// burned in with <c>textoverlay</c> fed straight from the subtitle pad;
/// <c>subtitleoverlay</c> accepted the same stream and drew nothing.
/// </para>
/// <para>
/// File names and other free text never go into the description: every such
/// value is a property set by name after parsing, so no quoting is involved.
/// <see cref="Render"/> can still write them in, quoted, for display.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class GStreamerGraph
{
    /// <summary>Enough queueing that one branch waiting on an encoder does not starve another of the same demuxer.</summary>
    private const string Queue = "queue max-size-buffers=0 max-size-bytes=0 max-size-time=10000000000";

    private readonly List<List<Node>> _chains = [];
    private readonly List<TranscodeIssue> _issues = [];
    private bool _trimmed;

    private GStreamerGraph(TranscodePlan plan) => Plan = plan;

    /// <summary>The plan this graph carries out.</summary>
    public TranscodePlan Plan { get; }

    /// <summary>Property values to set once the description is parsed, by element name.</summary>
    public List<(string Element, string Property, string Value)> Properties { get; } = [];

    /// <summary>
    /// The first element of every branch, straight after the parser pad, whose
    /// source pad is gated while a trim seeks.
    /// </summary>
    /// <remarks>
    /// The gate sits in front of the decoders and encoders rather than the
    /// muxer so that none of them ever sees data from before the seek or the
    /// flush that discards it: <c>flacenc</c> flushed in mid-stream fails a
    /// <c>g_assert</c> in <c>gst_audio_encoder_finish_frame</c>, which ends the
    /// process.
    /// </remarks>
    public List<string> Gates { get; } = [];

    /// <summary>
    /// One parser pad per input, where the seek of a trim is sent, and whether
    /// the seek lands on the key frame before the start rather than exactly on it.
    /// </summary>
    public List<(string Parser, string Pad, bool KeyFrame)> Seeks { get; } = [];

    /// <summary>The name of the muxer element, or null when the encoder writes the file directly.</summary>
    public string? Muxer { get; private set; }

    /// <summary>What building the graph found it could not do as asked.</summary>
    public IReadOnlyList<TranscodeIssue> Issues => _issues;

    /// <summary>Builds the graph for a plan that has passed <see cref="GStreamerTranscoder.Check"/>.</summary>
    public static GStreamerGraph Build(TranscodePlan plan)
    {
        var graph = new GStreamerGraph(plan);
        graph.BuildAll();
        return graph;
    }

    /// <summary>
    /// The description. With <paramref name="inline"/> the values in
    /// <see cref="Properties"/> are written into it, quoted, which is what a
    /// person runs by hand; without, they are left out and set after parsing.
    /// </summary>
    public string Render(bool inline)
    {
        var builder = new StringBuilder();
        foreach (var chain in _chains)
        {
            if (builder.Length > 0)
            {
                builder.Append("  ");
            }

            for (var i = 0; i < chain.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(" ! ");
                }

                builder.Append(chain[i].Render(inline ? Properties : null));
            }
        }

        return builder.ToString();
    }

    private void BuildAll()
    {
        var request = Plan.Request;
        _trimmed = request.Start is not null || request.End is not null;

        var used = Plan.Streams.Select(s => s.Input)
            .Concat(Plan.Streams.SelectMany(s => s.BurnIn).Select(b => b.Input))
            .Distinct()
            .Order()
            .ToList();

        foreach (var input in used)
        {
            var path = request.Inputs[input];
            var isUri = path.Contains("://", StringComparison.Ordinal);
            var source = Element(isUri ? "urisourcebin" : "filesrc", $"in{input}");
            Properties.Add((source.Name, isUri ? "uri" : "location", path));
            _chains.Add([source, Element("parsebin", Parser(input))]);
        }

        var muxer = MuxerFor(Plan.Container);
        if (muxer is not null)
        {
            Muxer = "mux";
            var mux = Element(muxer, Muxer);
            foreach (var (name, value) in request.ContainerOptions ?? new Dictionary<string, string>())
            {
                Properties.Add((Muxer, name, value));
            }

            var sink = Element("filesink", "out");
            Properties.Add(("out", "location", request.Output));
            _chains.Add([mux, sink]);
        }

        var videoPads = 0;
        var audioPads = 0;
        foreach (var stream in Plan.Streams)
        {
            var chain = Branch(stream.Input, stream.Source.Index, $"gate{stream.OutputIndex}");
            switch (stream.Action)
            {
                case PlannedAction.Copy:
                    chain.Add(new Text(CopyParser(stream.Source.Codec) ?? "identity"));
                    break;
                case PlannedAction.EncodeVideo:
                    AddVideo(stream, chain);
                    break;
                case PlannedAction.EncodeAudio:
                    AddAudio(stream, chain);
                    break;
            }

            if (StreamTags(stream) is { } tags)
            {
                var inject = Element("taginject", $"tags{stream.OutputIndex}");
                Properties.Add((inject.Name, "tags", tags));
                chain.Add(inject);
            }

            if (Muxer is null)
            {
                chain.Add(Element("filesink", "out"));
                Properties.Add(("out", "location", request.Output));
            }
            else
            {
                var isVideo = stream.Source.Kind == MediaStreamKind.Video;
                chain.Add(new Text($"{Muxer}.{MuxerPad(muxer!, isVideo, isVideo ? videoPads : audioPads, stream.OutputIndex)}"));
                if (isVideo)
                {
                    videoPads++;
                }
                else
                {
                    audioPads++;
                }
            }

            _chains.Add(chain);
        }

        if (_trimmed)
        {
            foreach (var input in used)
            {
                var first = Plan.Streams.FirstOrDefault(s => s.Input == input)?.Source.Index
                    ?? Plan.Streams.SelectMany(s => s.BurnIn).First(b => b.Input == input).Subtitles.Index;
                // A copied video stream can only start on a key frame, and the
                // muxers drop what falls before the segment, so an exact seek
                // would leave it without one.
                var keyFrame = Plan.Streams.Any(s => s.Input == input && s.Action == PlannedAction.Copy && s.Source.Kind == MediaStreamKind.Video);
                Seeks.Add((Parser(input), $"src_{first}", keyFrame));
            }
        }
    }

    /// <summary>The start of a branch: the parser pad, the gate when the job trims, and a queue.</summary>
    private List<Node> Branch(int input, int index, string gateName)
    {
        var branch = new List<Node> { new Text($"{Parser(input)}.src_{index}") };
        if (_trimmed)
        {
            var gate = Element("identity", gateName);
            Gates.Add(gate.Name);
            branch.Add(gate);
        }

        branch.Add(new Text(Queue));
        return branch;
    }

    private void AddVideo(PlannedStream stream, List<Node> chain)
    {
        var settings = stream.VideoSettings!;
        chain.Add(new Text("decodebin"));
        chain.Add(new Text("videoconvert"));

        var size = stream.Source.Video is { Width: > 0, Height: > 0 } v ? (v.Width, v.Height) : ((int, int)?)null;
        foreach (var filter in settings.Filters)
        {
            chain.AddRange(VideoFilterElements(filter, ref size).Select(t => (Node)new Text(t)));
        }

        if (settings.Width is not null || settings.Height is not null)
        {
            chain.AddRange(VideoFilterElements(new VideoFilter.Scale(settings.Width ?? -1, settings.Height ?? -1), ref size).Select(t => (Node)new Text(t)));
        }

        if (settings.FrameRate is { } rate)
        {
            chain.AddRange(VideoFilterElements(new VideoFilter.FrameRate(rate), ref size).Select(t => (Node)new Text(t)));
        }

        foreach (var burn in stream.BurnIn)
        {
            // textoverlay's own default is too small to read below 640 pixels
            // wide; this size, scaled with the width as textoverlay does, is
            // close to what libass gives SRT by default.
            var overlay = $"ovl{stream.OutputIndex}";
            chain.Add(Element("textoverlay", overlay));
            Properties.Add((overlay, "font-desc", "Sans 24"));
            var text = Branch(burn.Input, burn.Subtitles.Index, $"gate{stream.OutputIndex}t");
            text.Add(new Text($"{overlay}.text_sink"));
            _chains.Add(text);
        }

        chain.Add(new Text("videoconvert"));
        if (settings.PixelFormat is { } pixel)
        {
            chain.Add(new Text($"video/x-raw,format={PixelFormatName(pixel)}"));
        }

        var encoder = GStreamerCodecs.VideoEncoder(settings)!;
        var element = Element(encoder, $"enc{stream.OutputIndex}");
        chain.Add(element);
        foreach (var (name, value) in GStreamerCodecs.VideoEncoderProperties(encoder, settings, _issues))
        {
            Properties.Add((element.Name, name, value));
        }

        if (GStreamerCodecs.EncodedParser(encoder) is { } parser)
        {
            chain.Add(new Text(parser));
        }
    }

    private void AddAudio(PlannedStream stream, List<Node> chain)
    {
        var settings = stream.AudioSettings!;
        chain.Add(new Text("decodebin"));
        chain.Add(new Text("audioconvert"));
        chain.Add(new Text("audioresample"));

        foreach (var filter in settings.Filters)
        {
            chain.Add(new Text(filter switch
            {
                AudioFilter.Volume volume => $"volume volume={Number(volume.Gain)}",
                AudioFilter.Custom custom => custom.Description,
                _ => throw new ArgumentOutOfRangeException(nameof(stream), filter, "Unknown audio filter."),
            }));
        }

        chain.Add(new Text("audioconvert"));
        chain.Add(new Text("audioresample"));

        var caps = new List<string>();
        if (settings.SampleRate is { } rate)
        {
            caps.Add($"rate={rate}");
        }

        if (settings.Channels is not null || settings.ChannelMask is not null)
        {
            caps.Add($"channels={settings.Channels ?? System.Numerics.BitOperations.PopCount(settings.ChannelMask!.Value)}");
        }

        if (settings.ChannelMask is { } mask)
        {
            caps.Add($"channel-mask=(bitmask)0x{mask:x}");
        }

        var format = settings.SampleFormat ?? GStreamerCodecs.RawFormatOf(settings.Codec);
        if (format is { } sample)
        {
            caps.Add($"format={SampleFormatName(sample)}");
            caps.Add($"layout={(IsPlanar(sample) ? "non-interleaved" : "interleaved")}");
        }

        if (caps.Count > 0)
        {
            chain.Add(new Text("audio/x-raw," + string.Join(',', caps)));
        }

        // Raw PCM needs no encoder: the caps above are the encoding.
        if (GStreamerCodecs.AudioEncoder(settings) is not { } encoder)
        {
            return;
        }

        var element = Element(encoder, $"enc{stream.OutputIndex}");
        chain.Add(element);
        foreach (var (name, value) in GStreamerCodecs.AudioEncoderProperties(encoder, settings, _issues))
        {
            Properties.Add((element.Name, name, value));
        }

        if (GStreamerCodecs.EncodedParser(encoder) is { } parser)
        {
            chain.Add(new Text(parser));
        }
    }

    /// <summary>The elements that carry out one video filter, tracking the picture size for the crops that need it.</summary>
    private IEnumerable<string> VideoFilterElements(VideoFilter filter, ref (int Width, int Height)? size)
    {
        switch (filter)
        {
            case VideoFilter.Scale scale when scale.Width <= 0 && scale.Height <= 0:
                return [];

            case VideoFilter.Scale scale:
                // Left to itself, videoscale meets a free side by changing the
                // pixel aspect ratio rather than the size, so the missing side
                // is worked out here the way FFmpeg's -2 does: aspect kept,
                // rounded to even. Without a known size, square pixels make
                // videoscale scale the other side instead.
                if (size is { } before && (scale.Width <= 0 || scale.Height <= 0))
                {
                    size = scale.Width > 0
                        ? (scale.Width, Even((double)before.Height * scale.Width / before.Width))
                        : (Even((double)before.Width * scale.Height / before.Height), scale.Height);
                }
                else if (scale.Width > 0 && scale.Height > 0)
                {
                    size = (scale.Width, scale.Height);
                }
                else
                {
                    size = null;
                    return ["videoscale", $"video/x-raw,{(scale.Width > 0 ? $"width={scale.Width}" : $"height={scale.Height}")},pixel-aspect-ratio=1/1"];
                }

                return ["videoscale", $"video/x-raw,width={size.Value.Width},height={size.Value.Height}"];

            case VideoFilter.Crop crop:
                if (size is not { } current)
                {
                    _issues.Add(new TranscodeIssue(TranscodeIssueSeverity.Error, "cropping with GStreamer needs the picture size, which the prober did not report"));
                    return [];
                }

                var right = current.Width - crop.X - crop.Width;
                var bottom = current.Height - crop.Y - crop.Height;
                if (crop.X < 0 || crop.Y < 0 || right < 0 || bottom < 0)
                {
                    _issues.Add(new TranscodeIssue(TranscodeIssueSeverity.Error, $"the crop {crop.Width}x{crop.Height}+{crop.X}+{crop.Y} does not fit a {current.Width}x{current.Height} picture"));
                    return [];
                }

                size = (crop.Width, crop.Height);
                return [$"videocrop left={crop.X} top={crop.Y} right={right} bottom={bottom}"];

            case VideoFilter.FrameRate rate:
                return ["videorate", $"video/x-raw,framerate={rate.Rate.Numerator}/{rate.Rate.Denominator}"];

            case VideoFilter.Rotate rotate:
                if (rotate.Degrees is 90 or 270 && size is { } turned)
                {
                    size = (turned.Height, turned.Width);
                }

                return [rotate.Degrees switch
                {
                    90 => "videoflip method=clockwise",
                    270 => "videoflip method=counterclockwise",
                    _ => "videoflip method=rotate-180",
                }];

            case VideoFilter.Flip { Horizontal: true, Vertical: true }:
                return ["videoflip method=rotate-180"];
            case VideoFilter.Flip { Horizontal: true }:
                return ["videoflip method=horizontal-flip"];
            case VideoFilter.Flip { Vertical: true }:
                return ["videoflip method=vertical-flip"];
            case VideoFilter.Flip:
                return [];

            case VideoFilter.Deinterlace:
                return ["deinterlace"];

            case VideoFilter.Custom custom:
                return [custom.Description];

            default:
                throw new ArgumentOutOfRangeException(nameof(filter), filter, "Unknown video filter.");
        }
    }

    /// <summary>
    /// The <c>taginject</c> value for the language and title the request sets.
    /// Tags the input already carries flow through on their own.
    /// </summary>
    private static string? StreamTags(PlannedStream stream)
    {
        var tags = new List<string>();
        if (stream.Request.Language is { } language)
        {
            tags.Add($"language-code={Quoted(language)}");
        }

        if (stream.Request.Title is { } title)
        {
            tags.Add($"title={Quoted(title)}");
        }

        return tags.Count == 0 ? null : string.Join(',', tags);
    }

    private static string? MuxerFor(MediaContainer? container) => container switch
    {
        MediaContainer.Mp4 => "mp4mux",
        MediaContainer.Mov => "qtmux",
        MediaContainer.Matroska => "matroskamux",
        MediaContainer.WebM => "webmmux",
        MediaContainer.MpegTs => "mpegtsmux",
        MediaContainer.Ogg => "oggmux",
        MediaContainer.Avi => "avimux",
        MediaContainer.Wav => "wavenc",
        _ => null,
    };

    /// <summary>
    /// The request pad a branch links to. <c>mpegtsmux</c> numbers its pads by
    /// PID, and the PIDs below 0x40 are reserved, so its streams start at 0x100.
    /// </summary>
    private static string MuxerPad(string muxer, bool isVideo, int kindIndex, int outputIndex) => muxer switch
    {
        "mpegtsmux" => $"sink_{0x100 + outputIndex}",
        "oggmux" => $"sink_{outputIndex}",
        "wavenc" => "sink",
        _ => $"{(isVideo ? "video" : "audio")}_{kindIndex}",
    };

    /// <summary>
    /// The parser a copied stream goes through, which also converts between
    /// the forms containers want, such as H.264 in AVC or Annex B layout.
    /// </summary>
    private static string? CopyParser(MediaCodec? codec) => codec switch
    {
        MediaCodec.H264 => "h264parse",
        MediaCodec.Hevc => "h265parse",
        MediaCodec.Vp9 => "vp9parse",
        MediaCodec.Av1 => "av1parse",
        MediaCodec.Mjpeg => "jpegparse",
        MediaCodec.Aac => "aacparse",
        MediaCodec.Mp3 or MediaCodec.Mp2 => "mpegaudioparse",
        MediaCodec.Opus => "opusparse",
        MediaCodec.Flac => "flacparse",
        MediaCodec.Vorbis => "vorbisparse",
        MediaCodec.Ac3 => "ac3parse",
        _ => null,
    };

    private static string PixelFormatName(PixelFormat format) => format switch
    {
        PixelFormat.Yuv420P => "I420",
        PixelFormat.Yuv422P => "Y42B",
        PixelFormat.Yuv444P => "Y444",
        PixelFormat.Nv12 => "NV12",
        PixelFormat.Yuyv422 => "YUY2",
        PixelFormat.Uyvy422 => "UYVY",
        PixelFormat.Rgb24 => "RGB",
        PixelFormat.Bgr24 => "BGR",
        PixelFormat.Rgba32 => "RGBA",
        PixelFormat.Bgra32 => "BGRA",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "The pixel format has no GStreamer name."),
    };

    private static string SampleFormatName(SampleFormat format) => format switch
    {
        SampleFormat.U8 or SampleFormat.U8Planar => "U8",
        SampleFormat.S16 or SampleFormat.S16Planar => "S16LE",
        SampleFormat.S32 or SampleFormat.S32Planar => "S32LE",
        SampleFormat.F32 or SampleFormat.F32Planar => "F32LE",
        SampleFormat.F64 or SampleFormat.F64Planar => "F64LE",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "The sample format has no GStreamer name."),
    };

    private static bool IsPlanar(SampleFormat format) =>
        format is SampleFormat.U8Planar or SampleFormat.S16Planar or SampleFormat.S32Planar
            or SampleFormat.F32Planar or SampleFormat.F64Planar;

    private static string Parser(int input) => $"p{input}";

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>A GstStructure string value: double quotes, with backslash and quote escaped.</summary>
    private static string Quoted(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static ElementNode Element(string factory, string name) => new(factory, name);

    private abstract record Node
    {
        public abstract string Render(List<(string Element, string Property, string Value)>? properties);
    }

    private sealed record Text(string Value) : Node
    {
        public override string Render(List<(string Element, string Property, string Value)>? properties) => Value;
    }

    private sealed record ElementNode(string Factory, string Name) : Node
    {
        public override string Render(List<(string Element, string Property, string Value)>? properties)
        {
            var builder = new StringBuilder(Factory).Append(" name=").Append(Name);
            if (properties is not null)
            {
                foreach (var (_, property, value) in properties.Where(p => p.Element == Name))
                {
                    builder.Append(' ').Append(property).Append('=').Append(Quoted(value));
                }
            }

            return builder.ToString();
        }
    }
}
