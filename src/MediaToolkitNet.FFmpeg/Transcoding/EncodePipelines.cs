#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Globalization;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg.Filtering;
using MediaToolkitNet.FFmpeg.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg.Transcoding;

/// <summary>
/// Decode, filter, encode: the part video and audio share. Every conversion
/// happens in the filter graph, which ends in exactly the format the encoder
/// was opened with, so frames go from decoder to encoder without a copy of
/// their own.
/// </summary>
internal abstract unsafe class EncodePipeline : StreamPipeline
{
    private AVPacketNative* _packet;

    protected EncodePipeline(PlannedStream plan, FFmpegDemuxer demuxer, TimeWindow window, int threads)
        : base(plan, demuxer, window) =>
        Threads = threads;

    /// <summary>The encoder that was opened, for messages.</summary>
    public string EncoderName { get; protected set; } = string.Empty;

    protected int Threads { get; }

    protected FFmpegDecoder Decoder { get; set; } = null!;

    protected FFmpegFilterGraph Graph { get; set; } = null!;

    protected void* Encoder { get; set; }

    protected AVRationalNative EncoderTimeBase { get; set; }

    protected AVFrameHead* Decoded { get; private set; }

    protected AVFrameHead* Filtered { get; private set; }

    public override void Process(AVPacketNative* packet)
    {
        // The decoder refuses a packet while it holds frames nobody has taken.
        while (!Decoder.SendPacket(packet))
        {
            DrainDecoder();
        }

        DrainDecoder();
    }

    public override void Flush()
    {
        while (!Decoder.SendPacket(null))
        {
            DrainDecoder();
        }

        DrainDecoder();

        // A graph a trim has already ended answers false; what it still holds
        // comes out either way.
        Graph.TryPush(null);
        DrainGraph();
        Encode(null);
    }

    public override void Dispose()
    {
        Graph?.Dispose();
        Decoder?.Dispose();

        if (Encoder is not null)
        {
            var encoder = Encoder;
            AV.avcodec_free_context(&encoder);
            Encoder = null;
        }

        if (Decoded is not null)
        {
            var frame = Decoded;
            AV.av_frame_free(&frame);
            Decoded = null;
        }

        if (Filtered is not null)
        {
            var frame = Filtered;
            AV.av_frame_free(&frame);
            Filtered = null;
        }

        if (_packet is not null)
        {
            fixed (AVPacketNative** packet = &_packet)
            {
                AV.av_packet_free(packet);
            }
        }
    }

    /// <summary>Allocates the frames and the packet the pipeline reuses.</summary>
    protected void AllocateBuffers()
    {
        Decoded = (AVFrameHead*)FFmpegError.CheckAlloc(AV.av_frame_alloc(), "av_frame_alloc");
        Filtered = (AVFrameHead*)FFmpegError.CheckAlloc(AV.av_frame_alloc(), "av_frame_alloc");
        _packet = (AVPacketNative*)FFmpegError.CheckAlloc(AV.av_packet_alloc(), "av_packet_alloc");
    }

    /// <summary>Checks a decoded frame against what the graph was built for.</summary>
    protected abstract void Check(AVFrameHead* frame);

    /// <summary>Moves a filtered frame's timestamp into the encoder time base; false drops the frame.</summary>
    protected abstract bool Retime(AVFrameHead* frame);

    private void DrainDecoder()
    {
        while (Decoder.ReceiveFrame(Decoded) == FFmpegDecoder.ReceiveResult.Frame)
        {
            Check(Decoded);
            if (Window.To is { } to && Decoded->Pts != AVConstants.NoPtsValue
                && FFmpegStreamFormats.ToTimeSpan(Decoded->Pts, InputTimeBase) >= to)
            {
                // The trim filter drops it anyway; this only stops the reading.
                Done = true;
            }

            // A graph that has passed a trim's end takes nothing more; that is
            // the end of this stream, not a failure.
            var accepted = Graph.TryPush(Decoded);
            AV.av_frame_unref(Decoded);
            DrainGraph();
            if (!accepted)
            {
                Done = true;
                return;
            }
        }
    }

    private void DrainGraph()
    {
        while (Graph.TryReceive(Filtered))
        {
            Encode(Filtered);
            AV.av_frame_unref(Filtered);
        }
    }

    private void Encode(AVFrameHead* frame)
    {
        if (frame is not null && !Retime(frame))
        {
            return;
        }

        var sent = AV.avcodec_send_frame(Encoder, frame);
        if (sent != AVConstants.ErrorEof)
        {
            FFmpegError.Check(sent, $"avcodec_send_frame({EncoderName})");
        }

        while (true)
        {
            var received = AV.avcodec_receive_packet(Encoder, _packet);
            if (received is AVConstants.ErrorAgain or AVConstants.ErrorEof)
            {
                return;
            }

            FFmpegError.Check(received, $"avcodec_receive_packet({EncoderName})");
            AV.av_packet_rescale_ts(_packet, EncoderTimeBase, OutputTimeBase);
            Write(_packet);
        }
    }

    /// <summary>Trim and timeline shift for the front of a chain, in the input's own timestamps.</summary>
    protected string TrimFilters(bool audio)
    {
        var prefix = audio ? "a" : string.Empty;
        var parts = new List<string>();
        if (Window.Start > TimeSpan.Zero || Window.End is not null)
        {
            var end = Window.To is { } to ? $":end={FilterChains.Seconds(to)}" : string.Empty;
            parts.Add($"{prefix}trim=start={FilterChains.Seconds(Window.From)}{end}");
        }

        return FilterChains.Join(parts);
    }

    /// <summary>The timeline shift that makes the output start at zero.</summary>
    protected string ShiftFilter(bool audio, TimeSpan by) =>
        by == TimeSpan.Zero ? string.Empty : $"{(audio ? "a" : string.Empty)}setpts=PTS-{FilterChains.Seconds(by)}/TB";

    protected static string NameOf(AVRationalNative rational) => $"{rational.Num}/{rational.Den}";
}

/// <summary>Decodes, filters and re-encodes a video stream.</summary>
internal sealed unsafe class VideoPipeline : EncodePipeline
{
    private readonly VideoOutputSettings _settings;
    private readonly IReadOnlyList<(string Path, int SubtitleIndex, bool SameInput)> _burnIn;
    private int _width;
    private int _height;
    private int _format;
    private long _lastPts = long.MinValue;
    private AVRationalNative _sinkTimeBase;

    public VideoPipeline(
        PlannedStream plan,
        FFmpegDemuxer demuxer,
        TimeWindow window,
        int threads,
        IReadOnlyList<(string Path, int SubtitleIndex, bool SameInput)> burnIn)
        : base(plan, demuxer, window, threads)
    {
        _settings = plan.VideoSettings!;
        _burnIn = burnIn;
    }

    /// <summary>
    /// The chain before the final format conversion: trim, burned-in
    /// subtitles, the shift to zero, then the requested filters, size and rate.
    /// </summary>
    /// <remarks>
    /// Subtitles from the same file are timed like its video, so they are
    /// rendered before the shift; an external file's are timed from zero, so
    /// the video is moved to zero first. Either way the picture and the text
    /// meet at the same instant.
    /// </remarks>
    public string Chain()
    {
        var parts = new List<string?> { TrimFilters(audio: false) };
        var shifted = TimeSpan.Zero;
        foreach (var (path, index, sameInput) in _burnIn)
        {
            if (!sameInput && shifted == TimeSpan.Zero)
            {
                parts.Add(ShiftFilter(audio: false, Window.Origin));
                shifted = Window.Origin;
            }

            parts.Add($"subtitles=filename={FilterChains.EscapeOptionValue(path)}:si={index}");
        }

        parts.Add(ShiftFilter(audio: false, Window.From - shifted));
        parts.AddRange(FilterChains.VideoSettings(_settings));
        return FilterChains.Join(parts);
    }

    protected override int OpenCore(FFmpegMuxer muxer)
    {
        Decoder = FFmpegDecoder.Open(Demuxer, SourceIndex, Threads);

        var parameters = Demuxer.CodecParameters(SourceIndex);
        var stream = Demuxer.StreamHandle(SourceIndex);
        _width = AbiLayout.WidthOf(parameters);
        _height = AbiLayout.HeightOf(parameters);
        _format = AbiLayout.FormatOf(parameters);
        if (_width <= 0 || _height <= 0 || _format < 0)
        {
            throw new MediaToolkitNetException(
                FFmpegLibraries.BackendName,
                $"the size or pixel format of input stream #{SourceIndex} is not known, so it cannot be re-encoded",
                AVConstants.ErrorInvalid);
        }

        var frameRate = AbiLayout.AvgFrameRateOf(stream);
        var aspect = AbiLayout.SampleAspectRatioOf(stream);
        var chain = Chain();

        // Read through the decoder's AVOptions; 2 and 0 mean unspecified.
        var colorSpace = (int)AV.GetOption(Decoder.Handle, "colorspace", 2);
        var colorRange = (int)AV.GetOption(Decoder.Handle, "color_range", 0);
        int? space = colorSpace == 2 ? null : colorSpace;
        int? range = colorRange == 0 ? null : colorRange;

        // A first graph answers what the chain produces: size, rate and pixel
        // format. The encoder is opened for that, and the real graph then ends
        // in the one pixel format the encoder accepted.
        int width, height, produced;
        AVRationalNative rate, sinkTimeBase, sinkAspect;
        using (var probe = FFmpegFilterGraph.ForVideo(_width, _height, _format, InputTimeBase, frameRate, aspect, chain, space, range))
        {
            width = probe.OutputVideoFormat.Width;
            height = probe.OutputVideoFormat.Height;
            produced = probe.OutputPixelFormat;
            rate = probe.OutputFrameRate;
            sinkTimeBase = probe.OutputTimeBase;
            sinkAspect = probe.OutputSampleAspectRatio;
        }

        var codec = EncoderFactory.Find(_settings.Codec, MediaCodec.H264, _settings.EncoderName, out var encoderName);
        EncoderName = encoderName;

        // The setpts a trim adds reports the graph's rate as unknown (1/0), so
        // then the input's rate is the best statement of it.
        var nominalRate = rate.Num > 0 && rate.Den > 0 ? rate : frameRate;

        // A known rate makes it the time base, as the ffmpeg program does: rate
        // control reads it, and Media Foundation's encoders take their frame
        // rate from it and refuse the 1000 fps a Matroska time base would give.
        // Without one the graph's time base keeps every timestamp exact.
        EncoderTimeBase = nominalRate.Num > 0 && nominalRate.Den > 0
            ? new AVRationalNative(nominalRate.Den, nominalRate.Num)
            : sinkTimeBase;

        var options = FFmpegFormatMap.VideoEncoderOptions(encoderName, _settings);
        var qscale = _settings.Quality is not null && !FFmpegFormatMap.UsesConstantQuality(encoderName);
        var candidates = _settings.PixelFormat is not null
            ? Candidates(produced)
            : EncoderFactory.Prefer(Candidates(produced), EncoderFactory.SupportedPixelFormats(codec));
        foreach (var candidate in candidates)
        {
            Encoder = EncoderFactory.TryOpen(
                codec,
                AVMediaType.Video,
                candidate,
                _settings.BitrateBitsPerSecond,
                width,
                height,
                EncoderTimeBase,
                muxer.WantsGlobalHeader,
                configure: context =>
                {
                    if (_settings.KeyFrameInterval > 0)
                    {
                        AV.SetOption((void*)context, "g", _settings.KeyFrameInterval);
                    }

                    if (sinkAspect.Num > 0 && sinkAspect.Den > 0 && sinkAspect.Num != sinkAspect.Den)
                    {
                        AV.SetOption((void*)context, "aspect", NameOf(sinkAspect));
                    }

                    if (qscale)
                    {
                        AV.SetOption(
                            (void*)context,
                            "global_quality",
                            (long)Math.Round(_settings.Quality!.Value * AVConstants.QualityToLambda));
                    }

                    if (nominalRate.Num > 0 && nominalRate.Den > 0)
                    {
                        AV.SetOption((void*)context, "framerate", NameOf(nominalRate));
                    }
                },
                extraFlags: qscale ? AVConstants.CodecFlagQScale : 0,
                streamOptions: options);

            if (Encoder is not null)
            {
                Graph = FFmpegFilterGraph.ForVideo(
                    _width, _height, _format, InputTimeBase, frameRate, aspect,
                    FilterChains.Join([chain, $"format=pix_fmts={Utf8.ToManagedOrEmpty(AV.av_get_pix_fmt_name(candidate))}"]),
                    space,
                    range);
                _sinkTimeBase = Graph.OutputTimeBase;
                AllocateBuffers();
                return muxer.AddEncodedStream(Encoder, EncoderTimeBase);
            }
        }

        throw new MediaToolkitNetException(
            FFmpegLibraries.BackendName,
            $"could not open the video encoder {encoderName} at {width}x{height}; " +
            "it accepted none of the pixel formats tried, or refused an option",
            AVConstants.ErrorInvalid);
    }

    protected override void Check(AVFrameHead* frame)
    {
        if (frame->Width != _width || frame->Height != _height || frame->Format != _format)
        {
            throw new MediaToolkitNetException(
                FFmpegLibraries.BackendName,
                $"input stream #{SourceIndex} changes from {_width}x{_height} to {frame->Width}x{frame->Height} " +
                "part way through, which the transcoder does not follow",
                AVConstants.ErrorInvalid);
        }
    }

    protected override bool Retime(AVFrameHead* frame)
    {
        if (frame->Pts == AVConstants.NoPtsValue)
        {
            frame->Pts = _lastPts == long.MinValue ? 0 : _lastPts + 1;
        }
        else
        {
            frame->Pts = AV.av_rescale_q(frame->Pts, _sinkTimeBase, EncoderTimeBase);
        }

        // Two frames rounding onto one tick of a frame-rate time base would give
        // the encoder timestamps that do not increase; the later one goes.
        if (frame->Pts <= _lastPts)
        {
            return false;
        }

        _lastPts = frame->Pts;

        // The decoder's picture types are about the input; left in place they
        // would force key frames wherever the input happened to have them.
        frame->PictType = 0;
        return true;
    }

    private IEnumerable<int> Candidates(int produced)
    {
        if (_settings.PixelFormat is { } pinned)
        {
            yield return (int)FFmpegFormatMap.ToAV(pinned);
            yield break;
        }

        // What the chain already produces costs no conversion; after that the
        // formats nearly every encoder takes, and the full-range one MJPEG wants.
        int[] fallbacks =
        [
            produced,
            (int)AVPixelFormat.Yuv420P,
            (int)AVPixelFormat.Nv12,
            (int)AVPixelFormat.Yuv422P,
            (int)AVPixelFormat.Yuv444P,
            (int)AVPixelFormat.Yuvj420P,
        ];

        foreach (var format in fallbacks.Distinct())
        {
            if (format >= 0)
            {
                yield return format;
            }
        }
    }

}

/// <summary>Decodes, filters and re-encodes an audio stream.</summary>
internal sealed unsafe class AudioPipeline : EncodePipeline
{
    private readonly AudioOutputSettings _settings;
    private int _sampleRate;
    private int _format;
    private int _channels;
    private AVRationalNative _sinkTimeBase;
    private long _lastPts = long.MinValue;

    public AudioPipeline(PlannedStream plan, FFmpegDemuxer demuxer, TimeWindow window, int threads)
        : base(plan, demuxer, window, threads) =>
        _settings = plan.AudioSettings!;

    public string Chain() => FilterChains.Join(
    [
        TrimFilters(audio: true),
        ShiftFilter(audio: true, Window.From),
        .. FilterChains.AudioSettings(_settings),
    ]);

    protected override int OpenCore(FFmpegMuxer muxer)
    {
        Decoder = FFmpegDecoder.Open(Demuxer, SourceIndex, Threads);

        var (rate, channels, mask) = FFmpegStreamFormats.ReadAudioOptions(Decoder.Handle);
        _sampleRate = rate;
        _channels = channels;
        _format = AbiLayout.FormatOf(Demuxer.CodecParameters(SourceIndex));
        if (_sampleRate <= 0 || _channels <= 0 || _format < 0)
        {
            throw new MediaToolkitNetException(
                FFmpegLibraries.BackendName,
                $"the rate, layout or sample format of input stream #{SourceIndex} is not known, so it cannot be re-encoded",
                AVConstants.ErrorInvalid);
        }

        var inputLayout = AVChannelLayoutNative.Of(_channels, mask);
        var inputLayoutName = FFmpegFilterGraph.DescribeLayout(&inputLayout);
        var chain = Chain();

        int producedRate, producedFormat;
        string producedLayout;
        using (var probe = FFmpegFilterGraph.ForAudio(_sampleRate, _format, inputLayoutName, InputTimeBase, chain))
        {
            producedRate = probe.OutputAudioFormat.SampleRate;
            producedFormat = probe.OutputSampleFormat;
            producedLayout = probe.OutputChannelLayout;
        }

        var targetRate = _settings.SampleRate ?? producedRate;
        var targetLayoutName = producedLayout;
        if (_settings.Channels is not null || _settings.ChannelMask is not null)
        {
            var targetChannels = _settings.Channels ?? System.Numerics.BitOperations.PopCount(_settings.ChannelMask!.Value);
            var targetLayout = AVChannelLayoutNative.Of(targetChannels, _settings.ChannelMask ?? ChannelLayout.Unspecified);
            targetLayoutName = FFmpegFilterGraph.DescribeLayout(&targetLayout);
        }

        var codec = EncoderFactory.Find(_settings.Codec, MediaCodec.Aac, _settings.EncoderName, out var encoderName);
        EncoderName = encoderName;
        if (_settings.SampleRate is null)
        {
            // Asked for nothing in particular, the input's rate is kept when the
            // encoder takes it and moved to the nearest one it does otherwise.
            targetRate = EncoderFactory.ChooseSampleRate(targetRate, EncoderFactory.SupportedSampleRates(codec));
        }

        EncoderTimeBase = new AVRationalNative(1, targetRate);
        var experimental = FFmpegFormatMap.IsExperimental(encoderName);
        var quality = _settings.Quality;

        var candidates = _settings.SampleFormat is not null
            ? Candidates(producedFormat)
            : EncoderFactory.Prefer(Candidates(producedFormat), EncoderFactory.SupportedSampleFormats(codec));
        foreach (var candidate in candidates)
        {
            Encoder = EncoderFactory.TryOpen(
                codec,
                AVMediaType.Audio,
                candidate,
                _settings.BitrateBitsPerSecond,
                width: 0,
                height: 0,
                EncoderTimeBase,
                muxer.WantsGlobalHeader,
                configure: context =>
                {
                    AV.SetOption((void*)context, "ar", targetRate);
                    AV.SetOption((void*)context, "ch_layout", targetLayoutName);
                    if (experimental)
                    {
                        AV.SetOption((void*)context, "strict", AVConstants.ComplianceExperimental);
                    }

                    if (quality is { } requested)
                    {
                        AV.SetOption((void*)context, "global_quality", (long)Math.Round(requested * AVConstants.QualityToLambda));
                    }
                },
                extraFlags: quality is null ? 0 : AVConstants.CodecFlagQScale,
                streamOptions: _settings.Options);

            if (Encoder is null)
            {
                continue;
            }

            var formatName = Utf8.ToManagedOrEmpty(AV.av_get_sample_fmt_name(candidate));
            Graph = FFmpegFilterGraph.ForAudio(
                _sampleRate, _format, inputLayoutName, InputTimeBase,
                FilterChains.Join(
                [
                    chain,
                    $"aformat=sample_fmts={formatName}:sample_rates={targetRate}:channel_layouts={targetLayoutName}",
                ]));

            // Most encoders take a fixed number of samples per frame; the sink
            // cuts them to size, and those that take any report zero.
            var frameSize = (int)AV.GetOption(Encoder, "frame_size");
            if (frameSize > 0)
            {
                Graph.SetOutputFrameSize(frameSize);
            }

            _sinkTimeBase = Graph.OutputTimeBase;
            AllocateBuffers();
            return muxer.AddEncodedStream(Encoder, EncoderTimeBase);
        }

        throw new MediaToolkitNetException(
            FFmpegLibraries.BackendName,
            $"could not open the audio encoder {encoderName} at {targetRate} Hz with layout {targetLayoutName}; " +
            "it accepted none of the sample formats tried, or refused an option",
            AVConstants.ErrorInvalid);
    }

    protected override void Check(AVFrameHead* frame)
    {
        if (frame->Format != _format)
        {
            throw new MediaToolkitNetException(
                FFmpegLibraries.BackendName,
                $"input stream #{SourceIndex} changes its sample format part way through, which the transcoder does not follow",
                AVConstants.ErrorInvalid);
        }
    }

    protected override bool Retime(AVFrameHead* frame)
    {
        frame->Pts = frame->Pts == AVConstants.NoPtsValue
            ? (_lastPts == long.MinValue ? 0 : _lastPts + 1)
            : AV.av_rescale_q(frame->Pts, _sinkTimeBase, EncoderTimeBase);
        _lastPts = frame->Pts;
        return true;
    }

    private IEnumerable<int> Candidates(int produced)
    {
        if (_settings.SampleFormat is { } pinned)
        {
            yield return (int)FFmpegFormatMap.ToAV(pinned);
            yield break;
        }

        int[] fallbacks =
        [
            produced,
            (int)AVSampleFormat.FltP, (int)AVSampleFormat.S16, (int)AVSampleFormat.Flt, (int)AVSampleFormat.S16P,
            (int)AVSampleFormat.S32, (int)AVSampleFormat.S32P, (int)AVSampleFormat.U8,
        ];

        foreach (var format in fallbacks.Distinct())
        {
            if (format >= 0)
            {
                yield return format;
            }
        }
    }
}
