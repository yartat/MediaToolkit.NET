using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.FFmpeg.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg.Filtering;

/// <summary>
/// A libavfilter processing graph: frames go in one end, filtered frames come
/// out the other.
/// </summary>
/// <remarks>
/// <para>
/// This is the portable counterpart of the DirectShow filter graph. The chain
/// is written in FFmpeg's own syntax, the same string the <c>ffmpeg</c> command
/// line takes after <c>-vf</c> or <c>-af</c>, for example
/// <c>scale=640:-1,hflip</c> or <c>volume=0.5,aformat=sample_fmts=s16</c>. The
/// buffer source and sink at either end are added here, so the description
/// holds only the filters in between.
/// </para>
/// <para>
/// Timestamps travel in <see cref="TimeSpan"/> ticks: the source is configured
/// with a time base of 1/10000000 so that a tick is one unit, and the sink's own
/// time base converts the result back.
/// </para>
/// <para>
/// Frames handed to a handler borrow libavfilter's memory and are valid only
/// until the handler returns, like every other frame in this library.
/// </para>
/// </remarks>
public sealed unsafe class FFmpegFilterGraph : IDisposable
{
    /// <summary>AV_BUFFERSRC_FLAG_KEEP_REF: the caller keeps its frame.</summary>
    private const int KeepReference = 8;

    /// <summary>Ticks per second, which is also the time base this graph uses.</summary>
    private const int TickRate = 10_000_000;

    private void* _graph;
    private void* _source;
    private void* _sink;
    private AVFrameHead* _input;
    private AVFrameHead* _output;
    private bool _disposed;

    private FFmpegFilterGraph(bool video)
    {
        IsVideo = video;
        _graph = FFmpegError.CheckAlloc(AV.avfilter_graph_alloc(), "avfilter_graph_alloc");
        _input = (AVFrameHead*)FFmpegError.CheckAlloc(AV.av_frame_alloc(), "av_frame_alloc");
        _output = (AVFrameHead*)FFmpegError.CheckAlloc(AV.av_frame_alloc(), "av_frame_alloc");
    }

    /// <summary>
    /// True when libavfilter was found beside the other FFmpeg libraries and its
    /// struct layout was confirmed.
    /// </summary>
    public static bool IsAvailable => FFmpegLibraries.IsAvailable && AbiLayout.FilterLayoutVerified;

    /// <summary>Whether this graph carries video or audio.</summary>
    public bool IsVideo { get; }

    /// <summary>The format the graph produces, once it has been configured.</summary>
    public VideoFormat OutputVideoFormat { get; private set; }

    /// <summary>The format the graph produces, once it has been configured.</summary>
    public AudioFormat OutputAudioFormat { get; private set; }

    /// <summary>
    /// Builds a video graph fed with frames of <paramref name="input"/>.
    /// </summary>
    /// <param name="input">Geometry and pixel layout of the frames that will be pushed.</param>
    /// <param name="description">
    /// The filter chain, in FFmpeg syntax. Empty means pass the frames through,
    /// which is still useful when the chain's only job is a format change made
    /// by the sink.
    /// </param>
    public static FFmpegFilterGraph ForVideo(VideoFormat input, string description)
    {
        EnsureAvailable();
        ArgumentNullException.ThrowIfNull(description);
        if (!input.IsValid)
        {
            throw new ArgumentException("The input video format is incomplete.", nameof(input));
        }

        var graph = new FFmpegFilterGraph(video: true);
        try
        {
            var rate = input.FrameRate;
            var arguments =
                $"video_size={input.Width}x{input.Height}" +
                $":pix_fmt={(int)FFmpegFormatMap.ToAV(input.PixelFormat)}" +
                $":time_base=1/{TickRate}" +
                $":pixel_aspect=1/1" +
                (rate.Numerator > 0 && rate.Denominator > 0 ? $":frame_rate={rate.Numerator}/{rate.Denominator}" : string.Empty);

            graph.Build("buffer", arguments, "buffersink", description, "null");
            graph.ReadOutputVideoFormat();
            return graph;
        }
        catch
        {
            graph.Dispose();
            throw;
        }
    }

    /// <summary>Builds an audio graph fed with frames of <paramref name="input"/>.</summary>
    /// <param name="input">Rate, channels and sample layout of the frames that will be pushed.</param>
    /// <param name="description">The filter chain, in FFmpeg syntax; empty passes the frames through.</param>
    public static FFmpegFilterGraph ForAudio(AudioFormat input, string description)
    {
        EnsureAvailable();
        ArgumentNullException.ThrowIfNull(description);
        if (input.SampleRate <= 0 || input.Channels <= 0)
        {
            throw new ArgumentException("The input audio format is incomplete.", nameof(input));
        }

        var graph = new FFmpegFilterGraph(video: false);
        try
        {
            var arguments =
                $"time_base=1/{TickRate}" +
                $":sample_rate={input.SampleRate}" +
                $":sample_fmt={(int)FFmpegFormatMap.ToAV(input.SampleFormat)}" +
                $":ch_layout={FFmpegFormatMap.ChannelLayoutDescription(input.Channels, input.ChannelMask)}";

            graph.Build("abuffer", arguments, "abuffersink", description, "anull");
            graph.ReadOutputAudioFormat();
            return graph;
        }
        catch
        {
            graph.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Pushes a frame libavcodec produced. The graph takes its own reference, so
    /// the caller keeps ownership of <paramref name="frame"/>.
    /// </summary>
    public void Push(AVFrameHead* frame)
    {
        EnsureAlive();
        FFmpegError.Check(
            AV.av_buffersrc_add_frame_flags(_source, frame, KeepReference), "av_buffersrc_add_frame_flags");
    }

    /// <summary>Pushes a borrowed video frame, which libavfilter copies as it takes it.</summary>
    public void Push(in VideoFrame frame)
    {
        EnsureAlive();
        if (!IsVideo)
        {
            throw new InvalidOperationException("This is an audio graph.");
        }

        AV.av_frame_unref(_input);
        _input->Width = frame.Format.Width;
        _input->Height = frame.Format.Height;
        _input->Format = (int)FFmpegFormatMap.ToAV(frame.Format.PixelFormat);
        _input->Pts = frame.Timestamp.Ticks;

        for (var plane = 0; plane < frame.PlaneCount; plane++)
        {
            _input->Data[plane] = frame.PlanePointer(plane);
            _input->Linesize[plane] = frame.Stride(plane);
        }

        Push(_input);
    }

    /// <summary>Pushes a borrowed audio frame, which libavfilter copies as it takes it.</summary>
    public void Push(in AudioFrame frame)
    {
        EnsureAlive();
        if (IsVideo)
        {
            throw new InvalidOperationException("This is a video graph.");
        }

        AV.av_frame_unref(_input);
        AbiLayout.PrepareAudioFrame(
            _input,
            frame.Format.SampleRate,
            frame.Format.Channels,
            frame.Format.ChannelMask,
            (int)FFmpegFormatMap.ToAV(frame.Format.SampleFormat),
            frame.SampleCount);
        _input->Pts = frame.Timestamp.Ticks;

        for (var plane = 0; plane < frame.PlaneCount; plane++)
        {
            _input->Data[plane] = frame.PlanePointer(plane);
        }

        _input->Linesize[0] = frame.GetPlane(0).Length;
        Push(_input);
    }

    /// <summary>
    /// Tells the graph that no more frames are coming, so filters that buffer
    /// can emit what they are holding.
    /// </summary>
    public void Flush()
    {
        EnsureAlive();
        FFmpegError.Check(AV.av_buffersrc_add_frame_flags(_source, null, 0), "av_buffersrc_add_frame_flags(null)");
    }

    /// <summary>
    /// Takes one filtered video frame, when the graph has one ready.
    /// </summary>
    /// <param name="handler">Receives the frame, which is valid only for the call.</param>
    /// <returns>False when the graph needs more input or has reached its end.</returns>
    public bool TryReceive(VideoFrameHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!TryPull())
        {
            return false;
        }

        var format = OutputVideoFormat;
        var planeCount = Math.Min(format.PixelFormat.PlaneCount(), AVConstants.NumDataPointers);
        Span<nint> planes = stackalloc nint[MediaPlanes.MaxPlanes];
        Span<int> strides = stackalloc int[MediaPlanes.MaxPlanes];
        for (var plane = 0; plane < planeCount; plane++)
        {
            planes[plane] = (nint)_output->Data[plane];
            strides[plane] = _output->Linesize[plane];
        }

        var frame = new VideoFrame(format, TimestampOfOutput(), planes, strides, Math.Max(planeCount, 1));
        handler(in frame);
        return true;
    }

    /// <summary>Takes one filtered audio frame, when the graph has one ready.</summary>
    /// <param name="handler">Receives the frame, which is valid only for the call.</param>
    /// <returns>False when the graph needs more input or has reached its end.</returns>
    public bool TryReceive(AudioFrameHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!TryPull())
        {
            return false;
        }

        var format = OutputAudioFormat;
        var planeCount = format.SampleFormat.IsPlanar()
            ? Math.Min(format.Channels, AVConstants.NumDataPointers)
            : 1;

        Span<nint> planes = stackalloc nint[MediaPlanes.MaxPlanes];
        for (var plane = 0; plane < planeCount; plane++)
        {
            planes[plane] = (nint)_output->Data[plane];
        }

        var frame = new AudioFrame(format, TimestampOfOutput(), _output->NbSamples, planes, planeCount);
        handler(in frame);
        return true;
    }

    /// <summary>
    /// The graph as libavfilter itself prints it: every filter, its pads and
    /// what each pad is linked to.
    /// </summary>
    public string Describe()
    {
        EnsureAlive();
        var text = AV.avfilter_graph_dump(_graph, null);
        if (text is null)
        {
            return string.Empty;
        }

        try
        {
            return Utf8.ToManagedOrEmpty(text);
        }
        finally
        {
            AV.av_freep(&text);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_input is not null)
        {
            fixed (AVFrameHead** frame = &_input)
            {
                AV.av_frame_free(frame);
            }
        }

        if (_output is not null)
        {
            fixed (AVFrameHead** frame = &_output)
            {
                AV.av_frame_free(frame);
            }
        }

        if (_graph is not null)
        {
            // Frees every filter in it, including the source and the sink.
            fixed (void** graph = &_graph)
            {
                AV.avfilter_graph_free(graph);
            }

            _source = null;
            _sink = null;
        }
    }

    /// <summary>
    /// Creates the buffer source and sink, splices the caller's chain between
    /// them and configures the result.
    /// </summary>
    /// <param name="sourceName">buffer or abuffer.</param>
    /// <param name="sourceArguments">The source's options, which describe the input format.</param>
    /// <param name="sinkName">buffersink or abuffersink.</param>
    /// <param name="description">The caller's chain.</param>
    /// <param name="passthrough">The filter to stand in for an empty chain.</param>
    private void Build(
        string sourceName, string sourceArguments, string sinkName, string description, string passthrough)
    {
        _source = CreateFilter(sourceName, sourceArguments);
        _sink = CreateFilter(sinkName, null);

        // parse_ptr needs a filter on both sides of the chain, so an empty
        // description becomes the pass-through filter rather than a special case.
        var chain = string.IsNullOrWhiteSpace(description) ? passthrough : description;

        var outputs = FFmpegError.CheckAlloc(AV.avfilter_inout_alloc(), "avfilter_inout_alloc");
        var inputs = FFmpegError.CheckAlloc(AV.avfilter_inout_alloc(), "avfilter_inout_alloc");
        try
        {
            // The names are what the chain's open ends are labelled with; the
            // list called "outputs" is what feeds the chain, which reads
            // backwards but is how libavfilter defines it.
            AbiLayout.FillFilterInOut(outputs, Duplicate("in"), _source, 0);
            AbiLayout.FillFilterInOut(inputs, Duplicate("out"), _sink, 0);

            Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var chainUtf8 = new Utf8Scoped($"[in]{chain}[out]", scratch);
            FFmpegError.Check(
                AV.avfilter_graph_parse_ptr(_graph, chainUtf8.Pointer, &inputs, &outputs, null),
                $"avfilter_graph_parse_ptr({chain})");
        }
        finally
        {
            AV.avfilter_inout_free(&inputs);
            AV.avfilter_inout_free(&outputs);
        }

        FFmpegError.Check(AV.avfilter_graph_config(_graph, null), "avfilter_graph_config");
    }

    private void* CreateFilter(string name, string? arguments)
    {
        Span<byte> nameScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, nameScratch);

        var filter = AV.avfilter_get_by_name(nameUtf8.Pointer);
        if (filter is null)
        {
            throw new MediaToolkitNetException(
                FFmpegLibraries.BackendName, $"libavfilter has no filter named {name}.", AVConstants.ErrorInvalid);
        }

        // The instance names are the labels used in the chain description.
        var instance = arguments is null ? "out" : "in";
        Span<byte> instanceScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var instanceUtf8 = new Utf8Scoped(instance, instanceScratch);

        var context = FFmpegError.CheckAlloc(
            AV.avfilter_graph_alloc_filter(_graph, filter, instanceUtf8.Pointer),
            $"avfilter_graph_alloc_filter({name})");

        if (arguments is null)
        {
            FFmpegError.Check(AV.avfilter_init_str(context, null), $"avfilter_init_str({name})");
            return context;
        }

        Span<byte> argumentScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var argumentsUtf8 = new Utf8Scoped(arguments, argumentScratch);
        FFmpegError.Check(
            AV.avfilter_init_str(context, argumentsUtf8.Pointer), $"avfilter_init_str({name}={arguments})");
        return context;
    }

    /// <summary>
    /// Copies a string into memory libavutil allocated, because
    /// <c>avfilter_inout_free</c> frees the name it is given.
    /// </summary>
    private static byte* Duplicate(string text)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(text, scratch);
        return (byte*)FFmpegError.CheckAlloc(AV.av_strdup(utf8.Pointer), $"av_strdup({text})");
    }

    private void ReadOutputVideoFormat()
    {
        var rate = AV.av_buffersink_get_frame_rate(_sink);
        OutputVideoFormat = new VideoFormat(
            AV.av_buffersink_get_w(_sink),
            AV.av_buffersink_get_h(_sink),
            FFmpegFormatMap.FromAV((AVPixelFormat)AV.av_buffersink_get_format(_sink)),
            rate.Den > 0 ? new Rational(rate.Num, rate.Den) : Rational.Zero);
    }

    private void ReadOutputAudioFormat()
    {
        AVChannelLayoutNative layout;
        var channels = AV.av_buffersink_get_ch_layout(_sink, &layout) < 0 ? 0 : layout.NbChannels;
        OutputAudioFormat = new AudioFormat(
            AV.av_buffersink_get_sample_rate(_sink),
            channels,
            FFmpegFormatMap.FromAV((AVSampleFormat)AV.av_buffersink_get_format(_sink)),
            layout.Mask);
    }

    private bool TryPull()
    {
        EnsureAlive();
        AV.av_frame_unref(_output);

        var result = AV.av_buffersink_get_frame(_sink, _output);
        if (result is AVConstants.ErrorAgain or AVConstants.ErrorEof)
        {
            return false;
        }

        FFmpegError.Check(result, "av_buffersink_get_frame");
        return true;
    }

    /// <summary>
    /// Converts the output frame's timestamp, which is in the sink's time base
    /// and not necessarily the one the source was given.
    /// </summary>
    private TimeSpan TimestampOfOutput()
    {
        if (_output->Pts == AVConstants.NoPtsValue)
        {
            return TimeSpan.Zero;
        }

        var timeBase = AV.av_buffersink_get_time_base(_sink);
        return timeBase.Den <= 0
            ? TimeSpan.Zero
            : new TimeSpan(AV.av_rescale_q(_output->Pts, timeBase, new AVRationalNative(1, TickRate)));
    }

    private static void EnsureAvailable()
    {
        FFmpegLibraries.EnsureLoaded();
        AbiLayout.RequireFilterLayout();
    }

    private void EnsureAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
}
