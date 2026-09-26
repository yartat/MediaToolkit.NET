#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using System.Globalization;
using System.Text;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.FFmpeg.Transcoding;
using MediaToolkitNet.Mpv.Native;

namespace MediaToolkitNet.Mpv.Transcoding;

/// <summary>
/// Runs <see cref="TranscodeRequest"/> jobs through mpv's encoding mode: mpv
/// plays the file as fast as it can and encodes what it would have shown and
/// played.
/// </summary>
/// <remarks>
/// <para>
/// That model is what decides what this backend can do. mpv writes the one
/// video track and the one audio track it plays, re-encoded; it cannot copy a
/// stream. Subtitles it shows are rendered into the picture, so burning in is
/// the only thing it does with them. Filters are mpv's, which take any
/// libavfilter chain, and a trim is its <c>--start</c> and <c>--end</c>.
/// <see cref="Check"/> turns every request outside that into an error before
/// anything runs.
/// </para>
/// <para>
/// Encoder, muxer and filter names are libavcodec's, libavformat's and
/// libavfilter's, because mpv encodes through them, which is why this uses the
/// same tables as the FFmpeg backend.
/// </para>
/// </remarks>
public sealed unsafe class MpvTranscoder : IMediaTranscoder
{
    /// <summary>The name <see cref="VideoFilter.Custom"/> uses for mpv's own filter syntax.</summary>
    public const string FilterBackend = "mpv";

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long mpv may go without moving on before the job is given up.
    /// mpv does not stop when an encoder or a conversion it needs cannot start:
    /// it logs "Could not initialize video chain" and waits for ever, so a stall
    /// is the only failure it shows. Once it has logged an error the wait is
    /// much shorter.
    /// </summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan StallTimeoutAfterError = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public string Backend => Native.Mpv.BackendName;

    /// <inheritdoc />
    public TranscodeCapabilities Capabilities =>
        TranscodeCapabilities.VideoEncode | TranscodeCapabilities.AudioEncode | TranscodeCapabilities.SubtitleBurnIn
        | TranscodeCapabilities.MultipleInputs | TranscodeCapabilities.Trim | TranscodeCapabilities.Filters
        | TranscodeCapabilities.Metadata;

    /// <inheritdoc />
    public IReadOnlyList<TranscodeIssue> Validate(TranscodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var issues = new List<TranscodeIssue>();
        if (Plan(request, issues) is { } plan)
        {
            issues.AddRange(plan.Issues);
            if (!plan.HasErrors)
            {
                issues.AddRange(Check(plan));
            }
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
    /// What mpv cannot do with a plan that is otherwise sound. Public so a plan
    /// resolved with another prober can be checked too.
    /// </summary>
    public static IReadOnlyList<TranscodeIssue> Check(TranscodePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var issues = new List<TranscodeIssue>();

        foreach (var stream in plan.Streams)
        {
            switch (stream.Action)
            {
                case PlannedAction.Copy:
                    issues.Add(Error($"mpv's encoding mode re-encodes whatever it plays, so it cannot copy {stream.Source}"));
                    break;
                case PlannedAction.ConvertSubtitles:
                    issues.Add(Error($"mpv renders subtitles into the picture and cannot write them as a stream ({stream.Source})"));
                    break;
                case PlannedAction.EncodeVideo when stream.Input != 0:
                    issues.Add(Error("mpv plays one main file, so video has to come from the first input"));
                    break;
            }

            if (stream.Request.Language is not null || stream.Request.Title is not null
                || stream.Request.IsDefault is not null || stream.Request.IsForced is not null)
            {
                issues.Add(Warning($"mpv writes no stream tags of its own, so the language, title and flags asked for {stream.Source} are not set"));
            }

            var customs = (stream.VideoSettings?.Filters.OfType<VideoFilter.Custom>().Select(c => c.Backend) ?? [])
                .Concat(stream.AudioSettings?.Filters.OfType<AudioFilter.Custom>().Select(c => c.Backend) ?? []);
            foreach (var backend in customs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!backend.Equals(FilterBackend, StringComparison.OrdinalIgnoreCase)
                    && !backend.Equals(FilterChains.Backend, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(Error($"a custom filter written for {backend} cannot run on mpv"));
                }
            }
        }

        var video = plan.With(PlannedAction.EncodeVideo).ToList();
        var audio = plan.With(PlannedAction.EncodeAudio).ToList();
        if (video.Count > 1)
        {
            issues.Add(Error("mpv writes the one video track it plays; the request asks for several"));
        }

        if (audio.Count > 1)
        {
            issues.Add(Error("mpv writes the one audio track it plays; the request asks for several"));
        }

        if (video.Count + audio.Count == 0)
        {
            issues.Add(Error("mpv's encoding mode needs a video or an audio stream to encode"));
        }

        var burns = video.SelectMany(v => v.BurnIn).ToList();
        if (burns.Count > 1)
        {
            issues.Add(Error("mpv shows one subtitle track at a time, so it can burn in only one"));
        }

        foreach (var (codec, name) in new[] { (video.FirstOrDefault()?.VideoSettings?.Codec, video.FirstOrDefault()?.VideoSettings?.EncoderName), (audio.FirstOrDefault()?.AudioSettings?.Codec, audio.FirstOrDefault()?.AudioSettings?.EncoderName) })
        {
            if (codec is { } wanted && name is null && FFmpegFormatMap.EncoderNames(wanted).Length == 0)
            {
                issues.Add(Error($"no encoder is known for {wanted}"));
            }
        }

        if (video.Count == 1 && audio.Count == 1)
        {
            issues.Add(Warning("mpv writes its streams in the order its encoders start, so audio may come before video in the output"));
        }

        if (plan.Request.CopyChapters && plan.Inputs[0].Chapters.Count > 0)
        {
            issues.Add(Warning("mpv's encoding mode does not write chapters, so the input's are not carried over"));
        }

        return issues;
    }

    /// <summary>
    /// The mpv options that carry out a plan, in the order they are set. mpv's
    /// command line takes the same options with <c>--</c> in front, which is what
    /// <see cref="CommandLine"/> writes.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> OptionsFor(TranscodePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var request = plan.Request;
        var options = new List<KeyValuePair<string, string>>
        {
            new("o", request.Output),
            new("terminal", "no"),
            new("idle", "no"),
            new("load-scripts", "no"),
            new("ytdl", "no"),
            new("sub-auto", "no"),
            new("audio-file-auto", "no"),
            new("ocopy-metadata", "yes"),
        };

        if (FFmpegFormatMap.MuxerName(plan.Container) is { } muxer)
        {
            options.Add(new("of", muxer));
        }

        if (request.Metadata is { Count: > 0 } tags)
        {
            options.Add(new("oset-metadata", KeyValueList(tags)));
        }

        var main = plan.Inputs[0];
        if (plan.With(PlannedAction.EncodeVideo).FirstOrDefault() is { } video)
        {
            var settings = video.VideoSettings!;
            var encoder = settings.EncoderName ?? FFmpegFormatMap.EncoderNames(settings.Codec)[0];
            options.Add(new("vid", TrackId(main, video.Source).ToString(CultureInfo.InvariantCulture)));
            options.Add(new("ovc", encoder));

            var codecOptions = FFmpegFormatMap.VideoEncoderOptions(encoder, settings with { Options = null });
            if (settings.BitrateBitsPerSecond > 0)
            {
                codecOptions["b"] = settings.BitrateBitsPerSecond.ToString(CultureInfo.InvariantCulture);
            }

            if (settings.KeyFrameInterval > 0)
            {
                codecOptions["g"] = settings.KeyFrameInterval.ToString(CultureInfo.InvariantCulture);
            }

            if (settings.Quality is { } quality && !FFmpegFormatMap.UsesConstantQuality(encoder))
            {
                codecOptions["global_quality"] = ((long)Math.Round(quality * 118)).ToString(CultureInfo.InvariantCulture);
                codecOptions["flags"] = "+qscale";
            }

            // libavcodec's MJPEG encoder refuses limited-range YUV unless told
            // the file may be unofficial, and mpv hands it the range the source
            // has; FFmpeg's own pipeline converts to full range instead.
            if (encoder == "mjpeg")
            {
                codecOptions["strict"] = "-1";
            }

            AddAll(codecOptions, settings.Options);
            if (codecOptions.Count > 0)
            {
                options.Add(new("ovcopts", KeyValueList(codecOptions)));
            }

            var chain = FilterChains.Join(FilterChains.VideoSettings(settings, FilterBackend).Append(
                settings.PixelFormat is { } pixel ? $"format=pix_fmts={PixelFormatName(pixel)}" : null));
            if (chain.Length > 0)
            {
                options.Add(new("vf", MpvFilterChain.Lavfi(chain)));
            }

            if (video.BurnIn.FirstOrDefault() is { } burn)
            {
                options.Add(new("sid", SubtitleTrack(plan, burn, options).ToString(CultureInfo.InvariantCulture)));
            }
            else
            {
                options.Add(new("sid", "no"));
            }
        }
        else
        {
            options.Add(new("vid", "no"));
            options.Add(new("sid", "no"));
        }

        if (plan.With(PlannedAction.EncodeAudio).FirstOrDefault() is { } audio)
        {
            var settings = audio.AudioSettings!;
            var encoder = settings.EncoderName ?? FFmpegFormatMap.EncoderNames(settings.Codec)[0];
            if (audio.Input == 0)
            {
                options.Add(new("aid", TrackId(main, audio.Source).ToString(CultureInfo.InvariantCulture)));
            }
            else
            {
                // A track from another file is added as an external one, numbered
                // after every audio track the main file has.
                options.Add(new("audio-files-append", request.Inputs[audio.Input]));
                options.Add(new("aid", (main.OfKind(MediaStreamKind.Audio).Count() + 1).ToString(CultureInfo.InvariantCulture)));
            }

            options.Add(new("oac", encoder));
            var codecOptions = new Dictionary<string, string>(StringComparer.Ordinal);
            if (settings.BitrateBitsPerSecond > 0)
            {
                codecOptions["b"] = settings.BitrateBitsPerSecond.ToString(CultureInfo.InvariantCulture);
            }

            if (settings.Quality is { } quality)
            {
                codecOptions["global_quality"] = ((long)Math.Round(quality * 118)).ToString(CultureInfo.InvariantCulture);
                codecOptions["flags"] = "+qscale";
            }

            AddAll(codecOptions, settings.Options);
            if (codecOptions.Count > 0)
            {
                options.Add(new("oacopts", KeyValueList(codecOptions)));
            }

            var format = new List<string>();
            if (settings.SampleFormat is { } sampleFormat)
            {
                format.Add($"sample_fmts={SampleFormatName(sampleFormat)}");
            }

            if (settings.SampleRate is { } rate)
            {
                format.Add($"sample_rates={rate}");
            }

            if (settings.Channels is not null || settings.ChannelMask is not null)
            {
                var channels = settings.Channels ?? System.Numerics.BitOperations.PopCount(settings.ChannelMask!.Value);
                format.Add($"channel_layouts={FFmpegFormatMap.ChannelLayoutDescription(channels, settings.ChannelMask ?? 0)}");
            }

            var chain = FilterChains.Join(FilterChains.AudioSettings(settings, FilterBackend).Append(
                format.Count > 0 ? "aformat=" + string.Join(':', format) : null));
            if (chain.Length > 0)
            {
                options.Add(new("af", MpvFilterChain.Lavfi(chain)));
            }
        }
        else
        {
            options.Add(new("aid", "no"));
        }

        if (request.Start is { } start)
        {
            options.Add(new("start", Seconds(start)));
        }

        if (request.End is { } end)
        {
            options.Add(new("end", Seconds(end)));
        }

        return options;
    }

    /// <summary>
    /// The <c>mpv</c> command line equivalent to <see cref="OptionsFor"/>, for
    /// running the same job by hand or seeing what this backend would do.
    /// </summary>
    public static string CommandLine(TranscodePlan plan)
    {
        var builder = new StringBuilder("mpv --no-config");
        foreach (var (name, value) in OptionsFor(plan))
        {
            builder.Append(" --").Append(name).Append('=').Append(Quote(value));
        }

        return builder.Append(' ').Append(Quote(plan.Request.Inputs[0])).ToString();
    }

    private TranscodeResult Run(TranscodeRequest request, IProgress<TranscodeProgress>? progress, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        var issues = new List<TranscodeIssue>();
        var plan = Plan(request, issues);
        if (plan is not null)
        {
            issues.AddRange(plan.Issues);
            if (!plan.HasErrors)
            {
                issues.AddRange(Check(plan));
            }
        }

        if (plan is null || issues.Any(i => i.Severity == TranscodeIssueSeverity.Error))
        {
            throw new TranscodeRejectedException(Backend, issues);
        }

        var finished = false;
        var position = TimeSpan.Zero;
        try
        {
            using (var client = MpvClient.Create(OptionsFor(plan)))
            {
                client.RequestLogMessages("error");
                client.Command("loadfile", request.Inputs[0]);
                var lastReport = -ProgressInterval;
                var lastMove = clock.Elapsed;
                var errors = new List<string>();
                while (true)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        client.Command("quit");
                        cancellation.ThrowIfCancellationRequested();
                    }

                    var evt = client.WaitEvent(TimeSpan.FromMilliseconds(100));
                    if (evt is not null && evt->EventId == MpvEventId.EndFile)
                    {
                        var reason = evt->Data is null ? 0 : ((int*)evt->Data)[0];
                        var error = evt->Data is null ? 0 : ((int*)evt->Data)[1];
                        if (reason == EndFileReasonError)
                        {
                            var said = errors.Count > 0 ? $" ({string.Join("; ", errors.Distinct().TakeLast(3))})" : string.Empty;
                            throw new MediaToolkitNetException(Backend, $"mpv stopped encoding: {Native.Mpv.Describe(error)}{said}", error);
                        }

                        break;
                    }

                    if (evt is not null && evt->EventId == MpvEventId.Shutdown)
                    {
                        break;
                    }

                    if (evt is not null && evt->EventId == MpvEventId.LogMessage)
                    {
                        errors.Add(MpvClient.TextOfLogMessage(evt));
                    }

                    if (client.GetNumber("time-pos") is { } seconds && TimeSpan.FromSeconds(seconds) - plan.Start > position)
                    {
                        position = TimeSpan.FromSeconds(seconds) - plan.Start;
                        lastMove = clock.Elapsed;
                    }
                    else if (clock.Elapsed - lastMove > (errors.Count > 0 ? StallTimeoutAfterError : StallTimeout))
                    {
                        client.Command("quit");
                        var said = errors.Count > 0 ? $": {string.Join("; ", errors.Distinct().TakeLast(3))}" : string.Empty;
                        throw new MediaToolkitNetException(
                            Backend, $"mpv stopped making progress at {position}{said}", 0);
                    }

                    if (progress is not null && clock.Elapsed - lastReport >= ProgressInterval)
                    {
                        lastReport = clock.Elapsed;
                        progress.Report(new TranscodeProgress(position, plan.ExpectedDuration, clock.Elapsed));
                    }
                }
            }

            // Disposing the client is what wrote the trailer, so only now is the file done.
            VerifyOutput(plan);
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

    private const int EndFileReasonError = 4;

    /// <summary>
    /// Confirms the output holds the streams the plan encodes. An encoder that
    /// fails to open does not stop mpv: it writes the other stream, ends the
    /// file normally and reports nothing, so this is the only place it shows.
    /// </summary>
    private void VerifyOutput(TranscodePlan plan)
    {
        var written = new MpvProber().Probe(plan.Request.Output);
        foreach (var (action, kind) in new[] { (PlannedAction.EncodeVideo, MediaStreamKind.Video), (PlannedAction.EncodeAudio, MediaStreamKind.Audio) })
        {
            if (plan.With(action).FirstOrDefault() is { } stream && !written.OfKind(kind).Any())
            {
                var encoder = stream.VideoSettings?.EncoderName ?? stream.AudioSettings?.EncoderName;
                throw new MediaToolkitNetException(
                    Backend, $"mpv wrote no {kind.ToString().ToLowerInvariant()} stream; the {encoder} encoder most likely failed to open", 0);
            }
        }
    }

    /// <summary>
    /// The request with every encoder named: the first of the usual candidates
    /// that mpv's libavcodec has, as its <c>encoder-list</c> says. mpv given an
    /// encoder it does not have writes the file without that stream and does
    /// not fail, so the choice is made here, where it can be refused.
    /// </summary>
    private static TranscodeRequest WithAvailableEncoders(TranscodeRequest request, List<TranscodeIssue> issues)
    {
        if (!request.Streams.Any(s => s is EncodeVideo or EncodeAudio))
        {
            return request;
        }

        var available = AvailableEncoders();
        string? Pick(MediaCodec codec, string? named)
        {
            if (named is not null)
            {
                if (!available.Contains(named))
                {
                    issues.Add(Error($"mpv's libavcodec has no encoder named {named}"));
                }

                return named;
            }

            var candidates = FFmpegFormatMap.EncoderNames(codec);
            var found = candidates.FirstOrDefault(available.Contains);
            if (found is null)
            {
                issues.Add(Error(candidates.Length == 0
                    ? $"no encoder is known for {codec}"
                    : $"mpv's libavcodec has no encoder for {codec}; tried {string.Join(", ", candidates)}"));
            }

            return found;
        }

        return request with
        {
            Streams =
            [
                .. request.Streams.Select(s => s switch
                {
                    EncodeVideo v => OutputStream.Video(v.Source, v.Settings with { EncoderName = Pick(v.Settings.Codec, v.Settings.EncoderName) })
                        with { Language = v.Language, Title = v.Title, IsDefault = v.IsDefault, IsForced = v.IsForced },
                    EncodeAudio a => OutputStream.Audio(a.Source, a.Settings with { EncoderName = Pick(a.Settings.Codec, a.Settings.EncoderName) })
                        with { Language = a.Language, Title = a.Title, IsDefault = a.IsDefault, IsForced = a.IsForced },
                    _ => s,
                }),
            ],
        };
    }

    /// <summary>The encoder names mpv's <c>encoder-list</c> property holds.</summary>
    private static HashSet<string> AvailableEncoders()
    {
        using var client = MpvClient.Create([new("idle", "yes"), new("terminal", "no"), new("vo", "null"), new("ao", "null")]);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var count = (int)(client.GetNumber("encoder-list/count") ?? 0);
        for (var i = 0; i < count; i++)
        {
            if (client.Get($"encoder-list/{i}/driver") is { } name)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static TranscodePlan? Plan(TranscodeRequest request, List<TranscodeIssue> issues)
    {
        if (!Native.Mpv.IsAvailable)
        {
            issues.Add(Error("libmpv is not available"));
            return null;
        }

        var prober = new MpvProber();
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

        var named = WithAvailableEncoders(request, issues);
        return issues.Any(i => i.Severity == TranscodeIssueSeverity.Error) ? null : TranscodePlan.Resolve(named, infos);
    }

    /// <summary>
    /// mpv numbers the tracks of each kind from 1, in container order, which is
    /// the order streams have in a probe result.
    /// </summary>
    private static int TrackId(MediaInfo input, MediaStreamInfo stream) =>
        input.OfKind(stream.Kind).TakeWhile(s => s.Index != stream.Index).Count() + 1;

    private static int SubtitleTrack(TranscodePlan plan, PlannedBurnIn burn, List<KeyValuePair<string, string>> options)
    {
        if (burn.Input == 0)
        {
            return TrackId(plan.Inputs[0], burn.Subtitles);
        }

        // External subtitles are numbered after the main file's own.
        options.Add(new("sub-files-append", plan.Request.Inputs[burn.Input]));
        return plan.Inputs[0].OfKind(MediaStreamKind.Subtitle).Count()
            + TrackId(plan.Inputs[burn.Input], burn.Subtitles);
    }

    /// <summary>
    /// Writes a key-value list mpv reads back exactly: each value as
    /// <c>%length%text</c>, which needs no escaping whatever the text holds.
    /// </summary>
    private static string KeyValueList(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Join(',', pairs.Select(p => $"{p.Key}=%{Encoding.UTF8.GetByteCount(p.Value)}%{p.Value}"));

    private static void AddAll(Dictionary<string, string> target, IReadOnlyDictionary<string, string>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (key, value) in source)
        {
            target[key] = value;
        }
    }

    private static string PixelFormatName(Abstractions.Formats.PixelFormat format) => format switch
    {
        Abstractions.Formats.PixelFormat.Yuv420P => "yuv420p",
        Abstractions.Formats.PixelFormat.Yuv422P => "yuv422p",
        Abstractions.Formats.PixelFormat.Yuv444P => "yuv444p",
        Abstractions.Formats.PixelFormat.Nv12 => "nv12",
        Abstractions.Formats.PixelFormat.Yuyv422 => "yuyv422",
        Abstractions.Formats.PixelFormat.Uyvy422 => "uyvy422",
        Abstractions.Formats.PixelFormat.Rgb24 => "rgb24",
        Abstractions.Formats.PixelFormat.Bgr24 => "bgr24",
        Abstractions.Formats.PixelFormat.Rgba32 => "rgba",
        Abstractions.Formats.PixelFormat.Bgra32 => "bgra",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "The pixel format has no libavfilter name."),
    };

    private static string SampleFormatName(Abstractions.Formats.SampleFormat format) => format switch
    {
        Abstractions.Formats.SampleFormat.U8 => "u8",
        Abstractions.Formats.SampleFormat.S16 => "s16",
        Abstractions.Formats.SampleFormat.S32 => "s32",
        Abstractions.Formats.SampleFormat.F32 => "flt",
        Abstractions.Formats.SampleFormat.F64 => "dbl",
        Abstractions.Formats.SampleFormat.U8Planar => "u8p",
        Abstractions.Formats.SampleFormat.S16Planar => "s16p",
        Abstractions.Formats.SampleFormat.S32Planar => "s32p",
        Abstractions.Formats.SampleFormat.F32Planar => "fltp",
        Abstractions.Formats.SampleFormat.F64Planar => "dblp",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "The sample format has no libavfilter name."),
    };

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Quote(string value) =>
        value.All(c => char.IsLetterOrDigit(c) || "-_./:=+,%".Contains(c))
            ? value
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static TranscodeIssue Error(string message) => new(TranscodeIssueSeverity.Error, message);

    private static TranscodeIssue Warning(string message) => new(TranscodeIssueSeverity.Warning, message);
}
