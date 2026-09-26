#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg.Filtering;
using MediaToolkitNet.FFmpeg.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg.Transcoding;

/// <summary>
/// Runs <see cref="TranscodeRequest"/> jobs with libavformat, libavcodec and
/// libavfilter. It can do everything <see cref="TranscodeCapabilities"/> names.
/// </summary>
/// <remarks>
/// <para>
/// Each output stream is its own pipeline. A copied stream is packets in,
/// packets out. A re-encoded one is decoder, filter graph and encoder, where the
/// graph does every conversion, trimming included, and ends in exactly the
/// format the encoder was opened with. Text subtitles are decoded to ASS events
/// and encoded again; burned-in subtitles are rendered by libavfilter's
/// <c>subtitles</c> filter, which needs an FFmpeg built with libass.
/// </para>
/// <para>
/// Timestamps are measured from each input's own start time, so a trim means
/// the same thing for an MPEG-TS that starts at 1.4 s as for an MP4 that starts
/// at zero, and every stream is shifted by the same amount so they stay in sync.
/// </para>
/// </remarks>
public sealed unsafe class FFmpegTranscoder : IMediaTranscoder
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc />
    public string Backend => FFmpegLibraries.BackendName;

    /// <inheritdoc />
    public TranscodeCapabilities Capabilities =>
        TranscodeCapabilities.StreamCopy | TranscodeCapabilities.VideoEncode | TranscodeCapabilities.AudioEncode
        | TranscodeCapabilities.MultipleVideo | TranscodeCapabilities.MultipleAudio
        | TranscodeCapabilities.SubtitleCopy | TranscodeCapabilities.SubtitleConvert | TranscodeCapabilities.SubtitleBurnIn
        | TranscodeCapabilities.MultipleInputs | TranscodeCapabilities.Trim | TranscodeCapabilities.Filters
        | TranscodeCapabilities.Metadata | TranscodeCapabilities.Chapters;

    /// <summary>Decoder threads per stream; 0 uses as many as there are processors, up to 16.</summary>
    public int Threads { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<TranscodeIssue> Validate(TranscodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var inputs = new List<FFmpegDemuxer>();
        try
        {
            var issues = new List<TranscodeIssue>();
            if (Plan(request, inputs, issues) is { } plan)
            {
                issues.AddRange(plan.Issues);
                if (!plan.HasErrors)
                {
                    issues.AddRange(Check(plan, inputs));
                }
            }

            return issues;
        }
        finally
        {
            inputs.ForEach(d => d.Dispose());
        }
    }

    /// <inheritdoc />
    public Task<TranscodeResult> RunAsync(
        TranscodeRequest request,
        IProgress<TranscodeProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => Run(request, progress, cancellation), cancellation);
    }

    /// <summary>Runs the job on the calling thread.</summary>
    /// <exception cref="TranscodeRejectedException">The request has errors; nothing was written.</exception>
    public TranscodeResult Run(
        TranscodeRequest request,
        IProgress<TranscodeProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var clock = Stopwatch.StartNew();
        var inputs = new List<FFmpegDemuxer>();
        var pipelines = new List<StreamPipeline>();
        FFmpegMuxer? muxer = null;
        var finished = false;

        try
        {
            var issues = new List<TranscodeIssue>();
            var plan = Plan(request, inputs, issues);
            if (plan is not null)
            {
                issues.AddRange(plan.Issues);
                if (!plan.HasErrors)
                {
                    issues.AddRange(Check(plan, inputs));
                }
            }

            if (plan is null || issues.Any(i => i.Severity == TranscodeIssueSeverity.Error))
            {
                throw new TranscodeRejectedException(Backend, issues);
            }

            var windows = inputs
                .Select(d => new TimeWindow(TimeSpan.FromTicks(AbiLayout.StartTimeOfInput(d.Handle) * 10), plan.Start, request.End))
                .ToList();

            muxer = FFmpegMuxer.Create(request.Output, FFmpegFormatMap.MuxerName(plan.Container));
            foreach (var stream in plan.Streams)
            {
                var pipeline = Create(plan, stream, inputs[stream.Input], windows[stream.Input]);
                pipelines.Add(pipeline);
                pipeline.Open(muxer);
            }

            WriteTags(plan, muxer);
            if (request.CopyChapters)
            {
                CopyChapters(plan, muxer);
            }

            foreach (var unused in muxer.WriteHeader(request.ContainerOptions))
            {
                issues.Add(new TranscodeIssue(
                    TranscodeIssueSeverity.Warning, $"the {muxer.FormatName} muxer did not recognise the option {unused}"));
            }

            pipelines.ForEach(p => p.Start());
            Pump(plan, inputs, windows, pipelines, progress, clock, cancellation);
            pipelines.ForEach(p => p.Flush());
            muxer.Finish();
            finished = true;

            var written = pipelines.Count == 0 ? TimeSpan.Zero : pipelines.Max(p => p.Written);
            progress?.Report(new TranscodeProgress(written, plan.ExpectedDuration, clock.Elapsed));
            return new TranscodeResult(
                request.Output,
                written,
                clock.Elapsed,
                [.. issues.Where(i => i.Severity == TranscodeIssueSeverity.Warning)]);
        }
        finally
        {
            pipelines.ForEach(p => p.Dispose());
            muxer?.Dispose();
            inputs.ForEach(d => d.Dispose());

            // A half-written file is worse than none: it looks like a result.
            if (!finished && muxer is not null)
            {
                TryDelete(request.Output);
            }
        }
    }

    /// <summary>Opens every input and resolves the request against them.</summary>
    private TranscodePlan? Plan(TranscodeRequest request, List<FFmpegDemuxer> inputs, List<TranscodeIssue> issues)
    {
        if (!FFmpegLibraries.IsAvailable)
        {
            issues.Add(Error("FFmpeg is not available"));
            return null;
        }

        var infos = new List<MediaInfo>();
        foreach (var path in request.Inputs)
        {
            try
            {
                var demuxer = FFmpegDemuxer.Open(path);
                inputs.Add(demuxer);
                infos.Add(FFmpegProber.Describe(demuxer, path));
            }
            catch (MediaToolkitNetException ex)
            {
                issues.Add(Error($"could not open {path}: {ex.Message}"));
            }
        }

        return infos.Count == request.Inputs.Count ? TranscodePlan.Resolve(request, infos) : null;
    }

    /// <summary>What FFmpeg itself cannot do with a plan that is otherwise sound.</summary>
    private static List<TranscodeIssue> Check(TranscodePlan plan, IReadOnlyList<FFmpegDemuxer> inputs)
    {
        var issues = new List<TranscodeIssue>();
        var request = plan.Request;

        Span<byte> nameScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> pathScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var name = new Utf8Scoped(FFmpegFormatMap.MuxerName(plan.Container), nameScratch);
        using var path = new Utf8Scoped(request.Output, pathScratch);
        var format = AV.av_guess_format(name.Pointer, path.Pointer, null);
        if (format is null)
        {
            issues.Add(Error($"FFmpeg has no container for {request.Output}; name one in the request"));
            return issues;
        }

        var container = AbiLayout.NameOfFormat(format);
        foreach (var stream in plan.Streams)
        {
            var source = inputs[stream.Input].Streams[stream.Source.Index];
            switch (stream.Action)
            {
                case PlannedAction.Copy:
                    if (AV.avformat_query_codec(format, source.CodecId, AVConstants.ComplianceNormal) == 0)
                    {
                        issues.Add(Error(stream.Source.Kind == MediaStreamKind.Subtitle
                            ? $"{container} cannot hold {stream.Source.CodecName} subtitles ({stream}); convert them to {SuggestedSubtitles(plan.Container)}"
                            : $"{container} cannot hold {stream.Source.CodecName} ({stream}); re-encode it"));
                    }

                    break;

                case PlannedAction.EncodeVideo:
                    CheckEncoder(stream, stream.VideoSettings!.Codec, MediaCodec.H264, stream.VideoSettings.EncoderName, format, container, issues);
                    CheckFilters(stream, stream.VideoSettings.Filters.OfType<VideoFilter.Custom>().Select(c => c.Backend), issues);
                    foreach (var burn in stream.BurnIn)
                    {
                        if (!burn.Subtitles.IsTextSubtitle)
                        {
                            issues.Add(Error($"{burn.Subtitles} is bitmap subtitles; the ffmpeg backend burns in text subtitles only"));
                        }
                    }

                    if (stream.BurnIn.Count > 0 && !FFmpegFilters.Exists("subtitles"))
                    {
                        issues.Add(Error("burning in subtitles needs libavfilter's subtitles filter, and this FFmpeg was built without libass"));
                    }

                    break;

                case PlannedAction.EncodeAudio:
                    CheckEncoder(stream, stream.AudioSettings!.Codec, MediaCodec.Aac, stream.AudioSettings.EncoderName, format, container, issues);
                    CheckFilters(stream, stream.AudioSettings.Filters.OfType<AudioFilter.Custom>().Select(c => c.Backend), issues);
                    break;

                case PlannedAction.ConvertSubtitles:
                    if (!AbiLayout.SubtitleLayoutVerified)
                    {
                        issues.Add(Error("the AVSubtitle layout of this FFmpeg build was not confirmed, so subtitles cannot be converted"));
                        break;
                    }

                    if (AV.avcodec_find_decoder(source.CodecId) is null)
                    {
                        issues.Add(Error($"FFmpeg has no decoder for {stream.Source.CodecName} ({stream})"));
                    }

                    CheckEncoder(stream, stream.SubtitleCodec!.Value, MediaCodec.SubRip, null, format, container, issues);
                    break;
            }
        }

        if (!AbiLayout.MetadataLayoutVerified)
        {
            issues.Add(Warning("the tag layout of this FFmpeg build was not confirmed, so no tags, languages or chapters are written"));
        }

        return issues;
    }

    private static void CheckEncoder(
        PlannedStream stream,
        MediaCodec codec,
        MediaCodec fallback,
        string? encoderName,
        void* format,
        string container,
        List<TranscodeIssue> issues)
    {
        if (stream.Action != PlannedAction.ConvertSubtitles && !AbiLayout.FilterLayoutVerified)
        {
            issues.Add(Error($"re-encoding needs libavfilter, which was not found or not confirmed ({stream})"));
            return;
        }

        if (EncoderFactory.TryFind(codec, fallback, encoderName, out var found) is not { } encoder || encoder == 0)
        {
            var names = encoderName is null ? string.Join(", ", FFmpegFormatMap.EncoderNames(codec)) : encoderName;
            issues.Add(Error(string.IsNullOrEmpty(names)
                ? $"FFmpeg cannot encode {codec} ({stream})"
                : $"none of the encoders [{names}] is in this FFmpeg build ({stream})"));
            return;
        }

        if (AV.avformat_query_codec(format, AbiLayout.IdOfCodec((void*)encoder), AVConstants.ComplianceNormal) == 0)
        {
            issues.Add(Error($"{container} cannot hold what {found} produces ({stream})"));
        }
    }

    private static void CheckFilters(PlannedStream stream, IEnumerable<string> customBackends, List<TranscodeIssue> issues)
    {
        foreach (var backend in customBackends.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(backend, FilterChains.Backend, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(Error($"a custom filter written for {backend} cannot run on ffmpeg ({stream})"));
            }
        }
    }

    private StreamPipeline Create(TranscodePlan plan, PlannedStream stream, FFmpegDemuxer demuxer, TimeWindow window)
    {
        var threads = Threads > 0 ? Threads : Math.Min(Environment.ProcessorCount, 16);
        return stream.Action switch
        {
            PlannedAction.Copy => new CopyPipeline(stream, demuxer, window),
            PlannedAction.EncodeVideo => new VideoPipeline(stream, demuxer, window, threads, BurnSources(plan, stream)),
            PlannedAction.EncodeAudio => new AudioPipeline(stream, demuxer, window, threads),
            PlannedAction.ConvertSubtitles => new SubtitlePipeline(stream, demuxer, window),
            _ => throw new ArgumentOutOfRangeException(nameof(stream), stream.Action, null),
        };
    }

    /// <summary>
    /// What the subtitles filter needs for each burned-in stream: the file, and
    /// the stream's place among that file's subtitle streams.
    /// </summary>
    private static List<(string Path, int SubtitleIndex, bool SameInput)> BurnSources(TranscodePlan plan, PlannedStream stream) =>
        [.. stream.BurnIn.Select(burn => (
            plan.Request.Inputs[burn.Input],
            plan.Inputs[burn.Input].OfKind(MediaStreamKind.Subtitle).TakeWhile(s => s.Index != burn.Subtitles.Index).Count(),
            burn.Input == stream.Input))];

    /// <summary>
    /// Reads packets from every input that feeds a pipeline, always taking the
    /// earliest next packet among them, so the muxer receives the streams
    /// interleaved in time however many files they come from.
    /// </summary>
    private void Pump(
        TranscodePlan plan,
        List<FFmpegDemuxer> inputs,
        List<TimeWindow> windows,
        List<StreamPipeline> pipelines,
        IProgress<TranscodeProgress>? progress,
        Stopwatch clock,
        CancellationToken cancellation)
    {
        var routes = pipelines
            .GroupBy(p => (p.Plan.Input, p.SourceIndex))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var used = routes.Keys.Select(k => k.Input).Distinct().ToArray();

        // Seeking to the start lands on the key frame before it, and a subtitle that
        // came on screen earlier still is, but its packet sits before that point in
        // the file. Subtitle streams are therefore read from a second, unseeked
        // opening of the same file, which skips every other stream; the merge
        // below keeps the two in time order.
        var trimmed = plan.Start > TimeSpan.Zero;
        var subtitleStreams = trimmed
            ? routes.Keys.Where(k => plan.Inputs[k.Input].StreamAt(k.SourceIndex)?.Kind == MediaStreamKind.Subtitle).ToHashSet()
            : [];
        var extraDemuxers = new List<FFmpegDemuxer>();

        var readers = new List<InputReader>();
        try
        {
            foreach (var input in used)
            {
                var ownSubtitles = subtitleStreams.Where(k => k.Input == input).Select(k => k.SourceIndex).ToHashSet();
                if (trimmed)
                {
                    inputs[input].Seek(windows[input].From);
                    foreach (var index in ownSubtitles)
                    {
                        AbiLayout.DiscardStream(inputs[input].StreamHandle(index));
                    }
                }

                readers.Add(new InputReader(input, inputs[input], windows[input].Origin));

                if (ownSubtitles.Count > 0)
                {
                    var unseeked = FFmpegDemuxer.Open(plan.Request.Inputs[input]);
                    extraDemuxers.Add(unseeked);
                    for (var index = 0; index < unseeked.Streams.Count; index++)
                    {
                        if (!ownSubtitles.Contains(index))
                        {
                            AbiLayout.DiscardStream(unseeked.StreamHandle(index));
                        }
                    }

                    readers.Add(new InputReader(input, unseeked, windows[input].Origin));
                }
            }

            foreach (var reader in readers)
            {
                reader.Advance();
            }

            // The first report goes out at once, so a caller sees the job has started.
            var lastReport = -ProgressInterval;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();

                InputReader? next = null;
                foreach (var reader in readers)
                {
                    if (!reader.Exhausted && (next is null || reader.Time < next.Time))
                    {
                        next = reader;
                    }
                }

                if (next is null || pipelines.All(p => p.Done))
                {
                    return;
                }

                if (routes.TryGetValue((next.Input, next.Packet->StreamIndex), out var targets))
                {
                    foreach (var target in targets)
                    {
                        if (!target.Done)
                        {
                            target.Process(next.Packet);
                        }
                    }
                }

                next.Advance();

                if (progress is not null && clock.Elapsed - lastReport >= ProgressInterval)
                {
                    lastReport = clock.Elapsed;
                    progress.Report(new TranscodeProgress(Position(pipelines), plan.ExpectedDuration, clock.Elapsed));
                }
            }
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }

            extraDemuxers.ForEach(d => d.Dispose());
        }
    }

    /// <summary>How far the output has got: the furthest audio or video stream, since subtitles arrive in bursts.</summary>
    private static TimeSpan Position(List<StreamPipeline> pipelines)
    {
        var media = pipelines.Where(p => p.Plan.Source.Kind is MediaStreamKind.Video or MediaStreamKind.Audio).ToList();
        return (media.Count > 0 ? media : pipelines).Max(p => p.Written);
    }

    /// <summary>Copies the first input's file-level tags, then applies the request's on top.</summary>
    private static void WriteTags(TranscodePlan plan, FFmpegMuxer muxer)
    {
        if (!AbiLayout.MetadataLayoutVerified)
        {
            return;
        }

        foreach (var (key, value) in plan.Inputs[0].Metadata)
        {
            // The muxer writes its own encoder tag; a stale one would claim the input's.
            if (!key.Equals("encoder", StringComparison.OrdinalIgnoreCase))
            {
                muxer.SetTag(key, value);
            }
        }

        if (plan.Request.Metadata is { } overrides)
        {
            foreach (var (key, value) in overrides)
            {
                muxer.SetTag(key, value);
            }
        }
    }

    /// <summary>Carries the first input's chapters over, cut to the trimmed stretch and moved to start at zero.</summary>
    private static void CopyChapters(TranscodePlan plan, FFmpegMuxer muxer)
    {
        if (!AbiLayout.MetadataLayoutVerified)
        {
            return;
        }

        var start = plan.Start;
        var end = plan.Request.End;
        foreach (var chapter in plan.Inputs[0].Chapters)
        {
            var from = chapter.Start > start ? chapter.Start : start;
            var to = end is { } stop && chapter.End > stop ? stop : chapter.End;
            if (to <= from)
            {
                continue;
            }

            muxer.AddChapter(from - start, to - start, chapter.Title);
        }
    }


    private static string SuggestedSubtitles(MediaContainer? container) => container switch
    {
        MediaContainer.Mp4 or MediaContainer.Mov => "MovText",
        MediaContainer.WebM => "WebVtt",
        _ => "a text format the container takes",
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Nothing more can be done about it, and the job's own error matters more.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static TranscodeIssue Error(string message) => new(TranscodeIssueSeverity.Error, message);

    private static TranscodeIssue Warning(string message) => new(TranscodeIssueSeverity.Warning, message);

    /// <summary>Reads one input a packet ahead, so inputs can be merged by time.</summary>
    private sealed class InputReader : IDisposable
    {
        private readonly FFmpegDemuxer _demuxer;
        private readonly TimeSpan _origin;
        private AVPacketNative* _packet;

        public InputReader(int input, FFmpegDemuxer demuxer, TimeSpan origin)
        {
            Input = input;
            _demuxer = demuxer;
            _origin = origin;
            _packet = (AVPacketNative*)FFmpegError.CheckAlloc(AV.av_packet_alloc(), "av_packet_alloc");
        }

        public int Input { get; }

        public AVPacketNative* Packet => _packet;

        public bool Exhausted { get; private set; }

        /// <summary>The time of the packet waiting to be taken, measured from the input's start.</summary>
        public TimeSpan Time { get; private set; }

        public void Advance()
        {
            AV.av_packet_unref(_packet);
            if (!_demuxer.ReadPacket(_packet))
            {
                Exhausted = true;
                return;
            }

            var timestamp = _packet->Dts != AVConstants.NoPtsValue ? _packet->Dts : _packet->Pts;
            if (timestamp != AVConstants.NoPtsValue)
            {
                var timeBase = AbiLayout.TimeBaseOf(_demuxer.StreamHandle(_packet->StreamIndex));
                Time = FFmpegStreamFormats.ToTimeSpan(timestamp, timeBase) - _origin;
            }
        }

        public void Dispose()
        {
            if (_packet is not null)
            {
                fixed (AVPacketNative** packet = &_packet)
                {
                    AV.av_packet_free(packet);
                }
            }
        }
    }
}
