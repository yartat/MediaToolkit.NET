#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer.Transcoding;

/// <summary>
/// Runs <see cref="TranscodeRequest"/> jobs as one GStreamer pipeline built
/// from the plan: <c>parsebin</c> per input, a branch per output stream, one
/// muxer.
/// </summary>
/// <remarks>
/// <para>
/// Video and audio can be copied or re-encoded, from any number of inputs, and
/// text subtitles burned in. Subtitles cannot be written as a stream: GStreamer's
/// muxers take subtitles as plain UTF-8 text, while its subtitle parsers put out
/// Pango markup, and no element converts one into the other, so
/// <see cref="Check"/> refuses copying or converting them and points at the
/// FFmpeg backend.
/// </para>
/// <para>
/// A trim is a seek sent to each input's <c>parsebin</c> while
/// <see cref="PipelineGate"/> holds back what the demuxers produced before it;
/// the reasons are there. The seek is accurate, so re-encoded streams start at
/// the requested time; copied ones are cut by the muxer to the segment the
/// seek set.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerTranscoder : IMediaTranscoder
{
    /// <summary>The name <see cref="VideoFilter.Custom"/> uses for a <c>gst-launch</c> fragment.</summary>
    public const string FilterBackend = GstConstants.BackendName;

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SeekTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    public string Backend => GstConstants.BackendName;

    /// <inheritdoc />
    public TranscodeCapabilities Capabilities =>
        TranscodeCapabilities.StreamCopy | TranscodeCapabilities.VideoEncode | TranscodeCapabilities.AudioEncode
        | TranscodeCapabilities.MultipleVideo | TranscodeCapabilities.MultipleAudio | TranscodeCapabilities.SubtitleBurnIn
        | TranscodeCapabilities.MultipleInputs | TranscodeCapabilities.Trim | TranscodeCapabilities.Filters
        | TranscodeCapabilities.Metadata;

    /// <inheritdoc />
    /// <remarks>
    /// Beyond the plan, this builds the pipeline without running it, so a
    /// missing element, a codec the container cannot take and an encoder
    /// property that does not exist all show up here rather than at run time.
    /// </remarks>
    public IReadOnlyList<TranscodeIssue> Validate(TranscodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var issues = new List<TranscodeIssue>();
        if (Prepare(request, issues) is { } graph)
        {
            using var pipeline = Parse(graph, issues);
        }

        return issues;
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

    /// <summary>
    /// What GStreamer cannot do with a plan that is otherwise sound. Public so a
    /// plan resolved with another prober can be checked too.
    /// </summary>
    public static IReadOnlyList<TranscodeIssue> Check(TranscodePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var issues = new List<TranscodeIssue>();
        var request = plan.Request;

        foreach (var stream in plan.Streams)
        {
            if (stream.Source.Kind == MediaStreamKind.Subtitle)
            {
                issues.Add(Error(
                    $"GStreamer's muxers take subtitles as plain text and its parsers produce Pango markup, so {stream.Source} " +
                    "cannot be written as a stream; burn it in, drop it, or use the FFmpeg backend"));
                continue;
            }

            if (stream.Source.Kind is MediaStreamKind.Data or MediaStreamKind.Attachment)
            {
                issues.Add(Error($"{stream.Source} is {stream.Source.Kind.ToString().ToLowerInvariant()}, which the GStreamer backend does not carry"));
                continue;
            }

            if (stream.Request.IsDefault is not null || stream.Request.IsForced is not null)
            {
                issues.Add(Warning($"GStreamer's muxers take no default or forced flag, so the flags asked for {stream.Source} are not set"));
            }

            if (stream.VideoSettings is { } video)
            {
                if (GStreamerCodecs.VideoEncoder(video) is null)
                {
                    issues.Add(Error($"no GStreamer element encodes {video.Codec}; tried {Tried(GStreamerCodecs.VideoCandidates(video.Codec))}"));
                }

                CheckCustom(video.Filters.OfType<VideoFilter.Custom>().Select(c => c.Backend), issues);
                if (stream.BurnIn.Count > 1)
                {
                    issues.Add(Error($"textoverlay draws one subtitle stream, and {stream.Source} has {stream.BurnIn.Count} to burn in"));
                }

                foreach (var burn in stream.BurnIn.Where(b => !b.Subtitles.IsTextSubtitle))
                {
                    issues.Add(Error($"{burn.Subtitles} is bitmap subtitles, and textoverlay burns in text only"));
                }
            }

            if (stream.AudioSettings is { } audio)
            {
                if (audio.Codec == MediaCodec.TrueHd && audio.EncoderName is null)
                {
                    issues.Add(Error("avenc_truehd encodes TrueHD, but no GStreamer muxer takes it; use the FFmpeg backend"));
                }
                else if (GStreamerCodecs.AudioEncoder(audio) is null && GStreamerCodecs.RawFormatOf(audio.Codec) is null)
                {
                    issues.Add(Error($"no GStreamer element encodes {audio.Codec}; tried {Tried(GStreamerCodecs.AudioCandidates(audio.Codec))}"));
                }

                CheckCustom(audio.Filters.OfType<AudioFilter.Custom>().Select(c => c.Backend), issues);
            }
        }

        switch (plan.Container)
        {
            case null:
                issues.Add(Error($"the container of {request.Output} is not known from its extension; name it in the request"));
                break;
            case MediaContainer.Flac or MediaContainer.Mp3 or MediaContainer.Wav when plan.Streams.Count != 1:
                issues.Add(Error($"a {plan.Container} file holds one audio stream, and the request writes {plan.Streams.Count}"));
                break;
            case MediaContainer.Wav when plan.Streams[0].AudioSettings is not { } pcm || GStreamerCodecs.RawFormatOf(pcm.Codec) is null:
                issues.Add(Error("wavenc takes raw PCM, so the audio has to be encoded to a PCM codec"));
                break;
        }

        var chapters = plan.Inputs[0].Chapters.Count > 0;
        var keepsChapters = plan.Container is MediaContainer.Matroska or MediaContainer.WebM;
        if (chapters && request.CopyChapters && !keepsChapters)
        {
            issues.Add(Warning($"GStreamer's {plan.Container} muxer does not write the input's chapters"));
        }
        else if (chapters && !request.CopyChapters && keepsChapters)
        {
            issues.Add(Warning("matroskamux writes the chapters that reach it with the streams, so the input's are kept although the request drops them"));
        }
        else if (chapters && keepsChapters && (request.Start is not null || request.End is not null))
        {
            issues.Add(Warning("matroskamux writes the input's chapters with their original times, not trimmed"));
        }

        foreach (var (key, value) in request.Metadata ?? new Dictionary<string, string>())
        {
            if (value.Length == 0)
            {
                issues.Add(Warning($"GStreamer cannot remove the tag {key} from what the input carries"));
            }
        }

        var tagValues = plan.Inputs[0].Metadata.Values.Concat(request.Metadata?.Values ?? []);
        if (keepsChapters && tagValues.Any(v => v.Any(char.IsWhiteSpace)))
        {
            issues.Add(Warning("matroskamux writes a tag value that contains spaces serialised, as \"a\\ b\", so such values read back escaped"));
        }

        var copiesVideo = plan.Streams.Any(s => s.Action == PlannedAction.Copy && s.Source.Kind == MediaStreamKind.Video);
        if (request.Start is not null && copiesVideo && plan.Streams.Any(s => s.Action != PlannedAction.Copy))
        {
            issues.Add(Warning("a copied video stream has to start on a key frame, so every stream of its input starts at the key frame before the start"));
        }

        return issues;
    }

    /// <summary>
    /// The pipeline for a plan as a <c>gst-launch-1.0</c> description, with the
    /// file names written in. A trim cannot be expressed in one, so a job that
    /// trims runs from the start when the description is run by hand.
    /// </summary>
    public static string Describe(TranscodePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return GStreamerGraph.Build(plan).Render(inline: true);
    }

    private TranscodeResult Run(TranscodeRequest request, IProgress<TranscodeProgress>? progress, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        var issues = new List<TranscodeIssue>();
        var graph = Prepare(request, issues);
        if (graph is null)
        {
            throw new TranscodeRejectedException(Backend, issues);
        }

        var plan = graph.Plan;
        var gates = new List<PipelineGate>();
        var finished = false;
        var position = TimeSpan.Zero;
        try
        {
            using (var pipeline = Parse(graph, issues))
            {
                if (pipeline is null || issues.Any(i => i.Severity == TranscodeIssueSeverity.Error))
                {
                    throw new TranscodeRejectedException(Backend, issues);
                }

                WriteTags(pipeline, graph, issues);
                foreach (var name in graph.Gates)
                {
                    gates.Add(PipelineGate.Install(pipeline.FindElement(name)!));
                }

                if (graph.Seeks.Count > 0)
                {
                    // PAUSED starts the demuxers; the gates keep what they push
                    // from the muxer until the seek has flushed it.
                    ChangeState(pipeline, GstState.Paused);
                    foreach (var (parser, pad, keyFrame) in graph.Seeks)
                    {
                        Seek(pipeline, parser, pad, keyFrame, request, cancellation);
                    }
                }

                // One report before anything runs, so a caller can cancel even
                // a job that would finish before the first poll.
                progress?.Report(new TranscodeProgress(TimeSpan.Zero, plan.ExpectedDuration, clock.Elapsed));
                cancellation.ThrowIfCancellationRequested();
                ChangeState(pipeline, GstState.Playing);

                var lastReport = -ProgressInterval;
                var lastMove = clock.Elapsed;
                while (!pipeline.WaitForCompletion(TimeSpan.FromMilliseconds(100)))
                {
                    cancellation.ThrowIfCancellationRequested();

                    var now = pipeline.Position - plan.Start;
                    if (now > position)
                    {
                        position = now;
                        lastMove = clock.Elapsed;
                    }
                    else if (clock.Elapsed - lastMove > StallTimeout)
                    {
                        throw new MediaToolkitNetException(
                            Backend, $"the pipeline made no progress for {StallTimeout.TotalSeconds} s at {position}", 0);
                    }

                    if (progress is not null && clock.Elapsed - lastReport >= ProgressInterval)
                    {
                        lastReport = clock.Elapsed;
                        progress.Report(new TranscodeProgress(position, plan.ExpectedDuration, clock.Elapsed));
                    }
                }
            }

            // Disposing the pipeline took it to NULL, which is when the muxer
            // finished the file.
            finished = true;
            var written = plan.ExpectedDuration > TimeSpan.Zero ? plan.ExpectedDuration : position;
            progress?.Report(new TranscodeProgress(written, plan.ExpectedDuration, clock.Elapsed));
            return new TranscodeResult(
                request.Output,
                written,
                clock.Elapsed,
                [.. issues.Where(i => i.Severity == TranscodeIssueSeverity.Warning)]);
        }
        finally
        {
            // Only now is the pipeline in NULL, so no probe can still be running.
            foreach (var gate in gates)
            {
                gate.Dispose();
            }

            if (!finished && File.Exists(request.Output))
            {
                try
                {
                    File.Delete(request.Output);
                }
                catch (IOException)
                {
                    // The job's own error matters more.
                }
            }
        }
    }

    /// <summary>Probes, plans, checks and builds the graph; null when any of it found an error.</summary>
    private static GStreamerGraph? Prepare(TranscodeRequest request, List<TranscodeIssue> issues)
    {
        if (!Gst.IsAvailable)
        {
            issues.Add(Error("GStreamer is not available"));
            return null;
        }

        var prober = new GStreamerProber();
        var infos = new List<MediaInfo>();
        foreach (var path in request.Inputs)
        {
            try
            {
                infos.Add(prober.Probe(path));
            }
            catch (MediaToolkitNetException ex)
            {
                issues.Add(Error($"could not open {path}: {ex.Message}"));
            }
        }

        if (infos.Count != request.Inputs.Count)
        {
            return null;
        }

        var plan = TranscodePlan.Resolve(request, infos);
        issues.AddRange(plan.Issues);
        if (plan.HasErrors)
        {
            return null;
        }

        issues.AddRange(Check(plan));
        if (issues.Any(i => i.Severity == TranscodeIssueSeverity.Error))
        {
            return null;
        }

        var graph = GStreamerGraph.Build(plan);
        issues.AddRange(graph.Issues);
        return issues.Any(i => i.Severity == TranscodeIssueSeverity.Error) ? null : graph;
    }

    /// <summary>
    /// Parses the description strictly and sets every property on it, adding
    /// an error for each property the element does not have. Returns null when
    /// the description does not parse, with the reason added as an error.
    /// </summary>
    /// <remarks>
    /// The parser links every static pad, so a codec the muxer does not take
    /// fails here, as <c>could not link jpegparse0 to mux</c>, before any file
    /// is opened.
    /// </remarks>
    private static GStreamerPipeline? Parse(GStreamerGraph graph, List<TranscodeIssue> issues)
    {
        GStreamerPipeline pipeline;
        try
        {
            pipeline = GStreamerPipeline.Parse(graph.Render(inline: false), strict: true);
        }
        catch (MediaToolkitNetException ex)
        {
            issues.Add(Error(ex.Message.Contains("could not link", StringComparison.Ordinal)
                ? $"{ex.Message}; a link into the muxer fails when the {graph.Plan.Container} container does not take that codec"
                : ex.Message));
            return null;
        }

        try
        {
            foreach (var (name, property, value) in graph.Properties)
            {
                var element = pipeline.FindElement(name)
                    ?? throw new MediaToolkitNetException(GstConstants.BackendName, $"the pipeline has no element named {name}", 0);
                if (element.HasProperty(property))
                {
                    element.Set(property, value);
                }
                else
                {
                    issues.Add(Error($"the {name} element has no property {property}"));
                }
            }

            if (graph.Muxer is { } muxer && graph.Plan.Request.Metadata is { Count: > 0 }
                && !pipeline.FindElement(muxer)!.Is(Gst.gst_tag_setter_get_type()))
            {
                issues.Add(Warning("this container's GStreamer muxer takes no file-level tags, so the requested metadata is not written"));
            }

            return pipeline;
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sets the file-level tags on the muxer, in the mode where they win over
    /// the same tags arriving with the streams: the first input's, as the
    /// prober read them, replaced key by key by the request's.
    /// </summary>
    /// <remarks>
    /// The input's own tags are set again because what reaches the muxer with
    /// the streams is every stream's tags, stream titles among them, and the
    /// file-level ones have to win over those.
    /// </remarks>
    private static void WriteTags(GStreamerPipeline pipeline, GStreamerGraph graph, List<TranscodeIssue> issues)
    {
        if (graph.Muxer is null)
        {
            return;
        }

        var tags = new Dictionary<string, string>(graph.Plan.Inputs[0].Metadata, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, text) in graph.Plan.Request.Metadata ?? new Dictionary<string, string>())
        {
            tags[key] = text;
        }

        if (tags.Count == 0)
        {
            return;
        }

        var muxer = pipeline.FindElement(graph.Muxer)!;
        if (!muxer.Is(Gst.gst_tag_setter_get_type()))
        {
            return;
        }

        Gst.gst_tag_setter_set_tag_merge_mode(muxer.Handle, (int)GstTagMergeMode.Keep);

        var value = stackalloc byte[GstTypes.ValueSize];
        foreach (var (key, text) in tags)
        {
            if (text.Length == 0)
            {
                continue;
            }

            var name = Utf8.Allocate(key.Replace('_', '-').ToLowerInvariant());
            var content = Utf8.Allocate(text);
            try
            {
                if (Gst.gst_tag_exists(name) == 0 || Gst.gst_tag_get_type(name) != GstTypes.String)
                {
                    if (graph.Plan.Request.Metadata?.ContainsKey(key) == true)
                    {
                        issues.Add(Warning($"GStreamer has no text tag named {key}, so it is not written"));
                    }

                    continue;
                }

                // A GValue has to be zeroed before g_value_init.
                new Span<byte>(value, GstTypes.ValueSize).Clear();
                Gst.g_value_init(value, GstTypes.String);
                Gst.g_value_set_string(value, content);
                Gst.gst_tag_setter_add_tag_value(muxer.Handle, (int)GstTagMergeMode.Replace, name, value);
                Gst.g_value_unset(value);
            }
            finally
            {
                Utf8.Free(name);
                Utf8.Free(content);
            }
        }
    }

    private static void ChangeState(GStreamerPipeline pipeline, GstState state)
    {
        // Not SetState: with the gates closed the pipeline cannot preroll
        // before the seek, so waiting for PAUSED would only time out.
        if ((GstStateChangeReturn)Gst.gst_element_set_state(pipeline.Handle, (int)state) == GstStateChangeReturn.Failure)
        {
            throw new MediaToolkitNetException(
                GstConstants.BackendName, $"the pipeline refused to go to {state}: {pipeline.LastError() ?? "no reason given"}", 0);
        }
    }

    /// <summary>
    /// Sends the trim's seek to one parser pad, retrying until the demuxer
    /// behind it has started and accepts it.
    /// </summary>
    private static void Seek(
        GStreamerPipeline pipeline, string parser, string padName, bool keyFrame, TranscodeRequest request, CancellationToken cancellation)
    {
        var element = pipeline.FindElement(parser)!;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < SeekTimeout)
        {
            cancellation.ThrowIfCancellationRequested();
            if (pipeline.LastError() is { } error)
            {
                throw new MediaToolkitNetException(GstConstants.BackendName, error, 0);
            }

            var pad = element.GetStaticPad(padName);
            if (pad is not null)
            {
                try
                {
                    var seek = Gst.gst_event_new_seek(
                        1.0,
                        (int)GstFormat.Time,
                        (int)(GstSeekFlags.Flush | (keyFrame ? GstSeekFlags.KeyUnit | GstSeekFlags.SnapBefore : GstSeekFlags.Accurate)),
                        (int)GstSeekType.Set,
                        Nanoseconds(request.Start ?? TimeSpan.Zero),
                        (int)(request.End is null ? GstSeekType.None : GstSeekType.Set),
                        request.End is { } end ? Nanoseconds(end) : -1);

                    if (Gst.gst_pad_send_event(pad, seek) != 0)
                    {
                        return;
                    }
                }
                finally
                {
                    Gst.gst_object_unref(pad);
                }
            }

            Thread.Sleep(10);
        }

        throw new MediaToolkitNetException(
            GstConstants.BackendName, $"{parser}.{padName} did not accept the seek to the start within {SeekTimeout.TotalSeconds} s; the input may not be seekable", 0);
    }

    private static void CheckCustom(IEnumerable<string> backends, List<TranscodeIssue> issues)
    {
        foreach (var backend in backends.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!backend.Equals(FilterBackend, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(Error($"a custom filter written for {backend} cannot run on GStreamer"));
            }
        }
    }

    private static string Tried(IReadOnlyList<string> candidates) =>
        candidates.Count == 0 ? "none is known for it" : string.Join(", ", candidates);

    private static long Nanoseconds(TimeSpan time) => time.Ticks * GstConstants.NanosecondsPerTick;

    private static TranscodeIssue Error(string message) => new(TranscodeIssueSeverity.Error, message);

    private static TranscodeIssue Warning(string message) => new(TranscodeIssueSeverity.Warning, message);
}
