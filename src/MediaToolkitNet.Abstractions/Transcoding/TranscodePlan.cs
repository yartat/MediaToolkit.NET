#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions.Recording;

namespace MediaToolkitNet.Abstractions.Transcoding;

/// <summary>What one planned output stream does with its input.</summary>
public enum PlannedAction
{
    /// <summary>Packets are copied as they are.</summary>
    Copy,

    /// <summary>Video is decoded, filtered and encoded again.</summary>
    EncodeVideo,

    /// <summary>Audio is decoded, filtered and encoded again.</summary>
    EncodeAudio,

    /// <summary>Text subtitles are decoded and written in another format.</summary>
    ConvertSubtitles,
}

/// <summary>Subtitles to render into an encoded video stream.</summary>
/// <param name="Input">Which input they come from.</param>
/// <param name="Subtitles">The subtitle stream.</param>
public sealed record PlannedBurnIn(int Input, MediaStreamInfo Subtitles);

/// <summary>One stream of the output, resolved against what the inputs contain.</summary>
/// <param name="OutputIndex">Position in the output, counting from 0.</param>
/// <param name="Input">Which input the stream is read from.</param>
/// <param name="Source">The input stream.</param>
/// <param name="Action">What is done to it.</param>
/// <param name="Request">The part of the request this came from.</param>
public sealed record PlannedStream(
    int OutputIndex,
    int Input,
    MediaStreamInfo Source,
    PlannedAction Action,
    OutputStream Request)
{
    /// <summary>Subtitles to render into this stream; only ever set on encoded video.</summary>
    public IReadOnlyList<PlannedBurnIn> BurnIn { get; init; } = [];

    /// <summary>The language the output stream is tagged with.</summary>
    public string? Language => Request.Language ?? Source.Language;

    /// <summary>The title the output stream carries.</summary>
    public string? Title => Request.Title ?? Source.Title;

    /// <summary>Whether the output stream is marked as the default of its kind.</summary>
    public bool IsDefault => Request.IsDefault ?? Source.IsDefault;

    /// <summary>Whether the output stream is marked as forced.</summary>
    public bool IsForced => Request.IsForced ?? Source.IsForced;

    /// <summary>The video settings, for <see cref="PlannedAction.EncodeVideo"/>.</summary>
    public VideoOutputSettings? VideoSettings => (Request as EncodeVideo)?.Settings;

    /// <summary>The audio settings, for <see cref="PlannedAction.EncodeAudio"/>.</summary>
    public AudioOutputSettings? AudioSettings => (Request as EncodeAudio)?.Settings;

    /// <summary>The subtitle format, for <see cref="PlannedAction.ConvertSubtitles"/>.</summary>
    public MediaCodec? SubtitleCodec => (Request as ConvertSubtitles)?.Codec;

    /// <inheritdoc />
    public override string ToString() =>
        $"output #{OutputIndex}: {Action} {(Input == 0 ? string.Empty : $"input {Input} ")}{Source}";
}

/// <summary>
/// A <see cref="TranscodeRequest"/> resolved against what its inputs contain:
/// every selector turned into concrete streams, and every problem that does not
/// depend on the backend found before one is chosen.
/// </summary>
/// <remarks>
/// Every transcoder starts from this, so the rules about which stream a
/// selector means, and which combinations make no sense, are the same whichever
/// backend runs the job. The backend then adds what only it knows: which codecs
/// the container takes and which encoders exist.
/// </remarks>
public sealed class TranscodePlan
{
    private TranscodePlan(
        TranscodeRequest request,
        IReadOnlyList<MediaInfo> inputs,
        IReadOnlyList<PlannedStream> streams,
        IReadOnlyList<TranscodeIssue> issues)
    {
        Request = request;
        Inputs = inputs;
        Streams = streams;
        Issues = issues;
    }

    /// <summary>The request this plan was made from.</summary>
    public TranscodeRequest Request { get; }

    /// <summary>What each input was probed to contain, in request order.</summary>
    public IReadOnlyList<MediaInfo> Inputs { get; }

    /// <summary>The output streams, in output order.</summary>
    public IReadOnlyList<PlannedStream> Streams { get; }

    /// <summary>Problems found while resolving.</summary>
    public IReadOnlyList<TranscodeIssue> Issues { get; }

    /// <summary>True when any issue is an error, so the job cannot run.</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == TranscodeIssueSeverity.Error);

    /// <summary>
    /// The container to write, or <see langword="null"/> when it was left to the
    /// extension and the extension names none this library knows; the backend may
    /// still recognise it.
    /// </summary>
    public MediaContainer? Container => Request.Container != MediaContainer.Auto
        ? Request.Container
        : MediaFormats.ContainerFromPath(Request.Output);

    /// <summary>Where in the inputs the output starts.</summary>
    public TimeSpan Start => Request.Start ?? TimeSpan.Zero;

    /// <summary>How long the output is expected to be, or <see cref="TimeSpan.Zero"/> when that is unknown.</summary>
    public TimeSpan ExpectedDuration
    {
        get
        {
            var end = Request.End ?? Inputs[0].Duration;
            return end > Start ? end - Start : TimeSpan.Zero;
        }
    }

    /// <summary>The planned streams with one action.</summary>
    public IEnumerable<PlannedStream> With(PlannedAction action) => Streams.Where(s => s.Action == action);

    /// <summary>Resolves a request against probed inputs.</summary>
    /// <param name="request">The job.</param>
    /// <param name="inputs">What each of <see cref="TranscodeRequest.Inputs"/> contains, in the same order.</param>
    /// <returns>Returns the plan; check <see cref="HasErrors"/> before running it.</returns>
    public static TranscodePlan Resolve(TranscodeRequest request, IReadOnlyList<MediaInfo> inputs)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count != request.Inputs.Count)
        {
            throw new ArgumentException("There must be one probe result for each input of the request.", nameof(inputs));
        }

        var issues = new List<TranscodeIssue>();
        CheckRequest(request, inputs, issues);
        if (request.Inputs.Count == 0)
        {
            return new TranscodePlan(request, inputs, [], issues);
        }

        var wanted = request.Streams.Count > 0 ? request.Streams : DefaultStreams(inputs[0], issues);

        // Dropped streams are taken out of every selector, so Drop beside All
        // means "all but these".
        var dropped = new HashSet<(int Input, int Index)>();
        foreach (var drop in wanted.OfType<DropStream>())
        {
            var matches = Match(drop.Source, inputs, issues).ToList();
            if (matches.Count == 0)
            {
                issues.Add(Warning($"{drop.Source}, which was to be dropped, matched no stream"));
            }

            dropped.UnionWith(matches.Select(m => (drop.Source.Input, m.Index)));
        }

        var planned = new List<PlannedStream>();
        foreach (var entry in wanted)
        {
            if (entry is DropStream or BurnSubtitles)
            {
                continue;
            }

            var matches = Match(entry.Source, inputs, issues).ToList();
            if (entry.Source.MatchesAll)
            {
                matches.RemoveAll(m => dropped.Contains((entry.Source.Input, m.Index)));
            }
            else if (matches.Count == 1 && dropped.Contains((entry.Source.Input, matches[0].Index)))
            {
                issues.Add(Error($"{entry.Source} is both dropped and asked for"));
                continue;
            }

            if (matches.Count == 0 && entry.Source.Input >= inputs.Count)
            {
                // Match has already said the input does not exist.
                continue;
            }

            if (matches.Count == 0)
            {
                // "All" asks for whatever there is, and nothing is an answer;
                // anything more specific named a stream that is not there.
                issues.Add(entry.Source.MatchesAll
                    ? Warning($"{entry.Source} matched no stream")
                    : Error($"{entry.Source} matched no stream in {inputs[entry.Source.Input].Uri}"));
                continue;
            }

            foreach (var match in matches)
            {
                if (ActionFor(entry, match, issues) is { } action)
                {
                    planned.Add(new PlannedStream(planned.Count, entry.Source.Input, match, action, entry));
                }
            }
        }

        foreach (var burn in wanted.OfType<BurnSubtitles>())
        {
            AttachBurnIn(burn, inputs, planned, issues);
        }

        if (planned.Count == 0 && !issues.Any(i => i.Severity == TranscodeIssueSeverity.Error))
        {
            issues.Add(Error("the output would contain no streams"));
        }

        return new TranscodePlan(request, inputs, planned, issues);
    }

    private static void CheckRequest(TranscodeRequest request, IReadOnlyList<MediaInfo> inputs, List<TranscodeIssue> issues)
    {
        if (request.Inputs.Count == 0)
        {
            issues.Add(Error("the request names no input"));
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Output))
        {
            issues.Add(Error("the request names no output"));
        }
        else
        {
            var output = Path.GetFullPath(request.Output);
            for (var i = 0; i < request.Inputs.Count; i++)
            {
                if (IsLocalPath(request.Inputs[i]) &&
                    string.Equals(Path.GetFullPath(request.Inputs[i]), output, PathComparison))
                {
                    issues.Add(Error($"the output is input {i}, which would be overwritten while it is read"));
                }
            }
        }

        if (request.Start is { } start && start < TimeSpan.Zero)
        {
            issues.Add(Error($"the start {start} is negative"));
        }

        if (request.End is { } end && end <= (request.Start ?? TimeSpan.Zero))
        {
            issues.Add(Error($"the end {end} is not after the start {request.Start ?? TimeSpan.Zero}"));
        }

        if (request.Start is { } late && inputs[0].Duration > TimeSpan.Zero && late >= inputs[0].Duration)
        {
            issues.Add(Error($"the start {late} is past the end of the input, which lasts {inputs[0].Duration}"));
        }
    }

    /// <summary>Every video, audio and subtitle stream of the first input, for a request that names none.</summary>
    private static List<OutputStream> DefaultStreams(MediaInfo input, List<TranscodeIssue> issues)
    {
        foreach (var skipped in input.Streams.Where(s => s.Kind is MediaStreamKind.Data or MediaStreamKind.Attachment))
        {
            issues.Add(Warning($"{skipped} is not carried over; name it in the request to copy it"));
        }

        return
        [
            OutputStream.Copy(StreamSource.All(MediaStreamKind.Video)),
            OutputStream.Copy(StreamSource.All(MediaStreamKind.Audio)),
            OutputStream.Copy(StreamSource.All(MediaStreamKind.Subtitle)),
        ];
    }

    /// <summary>The streams a selector means.</summary>
    private static IEnumerable<MediaStreamInfo> Match(StreamSource source, IReadOnlyList<MediaInfo> inputs, List<TranscodeIssue> issues)
    {
        if (source.Input >= inputs.Count)
        {
            issues.Add(Error($"{source} names input {source.Input}, but the request has {inputs.Count}"));
            return [];
        }

        var input = inputs[source.Input];
        if (source.Index is { } index)
        {
            return input.StreamAt(index) is { } stream ? [stream] : [];
        }

        var candidates = input.OfKind(source.Kind!.Value);
        if (source.Language is { } language)
        {
            candidates = candidates.Where(s => string.Equals(s.Language, language, StringComparison.OrdinalIgnoreCase));
        }

        if (source.KindIndex is { } n)
        {
            return candidates.Skip(n).Take(1);
        }

        // "The first video" means the picture, not the cover art; "all video"
        // means everything, cover art included, so a remux keeps it.
        return source.MatchesAll ? candidates : candidates.Where(s => !s.IsAttachedPicture).Take(1);
    }

    private static PlannedAction? ActionFor(OutputStream request, MediaStreamInfo stream, List<TranscodeIssue> issues)
    {
        switch (request)
        {
            case CopyStream:
                return PlannedAction.Copy;

            case EncodeVideo when stream.Kind == MediaStreamKind.Video:
                return PlannedAction.EncodeVideo;

            case EncodeAudio when stream.Kind == MediaStreamKind.Audio:
                return PlannedAction.EncodeAudio;

            case ConvertSubtitles when stream.Kind == MediaStreamKind.Subtitle && stream.IsTextSubtitle:
                return PlannedAction.ConvertSubtitles;

            case ConvertSubtitles when stream.Kind == MediaStreamKind.Subtitle:
                issues.Add(Error($"{stream} is bitmap subtitles, which cannot be converted to text; copy or burn them in instead"));
                return null;

            default:
                issues.Add(Error($"{stream} is {stream.Kind.ToString().ToLowerInvariant()}, which {Describe(request)} does not apply to"));
                return null;
        }
    }

    private static void AttachBurnIn(
        BurnSubtitles burn, IReadOnlyList<MediaInfo> inputs, List<PlannedStream> planned, List<TranscodeIssue> issues)
    {
        if (burn.Source.MatchesAll || burn.Target.MatchesAll)
        {
            issues.Add(Error("burning in takes one subtitle stream and one video stream, not all of a kind"));
            return;
        }

        var subtitles = Match(burn.Source, inputs, issues).FirstOrDefault();
        if (subtitles is null)
        {
            issues.Add(Error($"{burn.Source}, which was to be burned in, matched no stream"));
            return;
        }

        if (subtitles.Kind != MediaStreamKind.Subtitle)
        {
            issues.Add(Error($"{subtitles} is not a subtitle stream, so it cannot be burned in"));
            return;
        }

        var video = Match(burn.Target, inputs, issues).FirstOrDefault();
        if (video is null)
        {
            issues.Add(Error($"{burn.Target}, which subtitles were to be burned into, matched no stream"));
            return;
        }

        var targets = planned
            .Where(p => p.Action == PlannedAction.EncodeVideo && p.Input == burn.Target.Input && p.Source.Index == video.Index)
            .ToList();

        if (targets.Count == 0)
        {
            var copied = planned.Any(p => p.Action == PlannedAction.Copy && p.Input == burn.Target.Input && p.Source.Index == video.Index);
            issues.Add(Error(copied
                ? $"burning subtitles into {video} needs it re-encoded, but the request copies it"
                : $"burning subtitles into {video} needs it re-encoded, but the request does not write it"));
            return;
        }

        foreach (var target in targets)
        {
            planned[target.OutputIndex] = target with
            {
                BurnIn = [.. target.BurnIn, new PlannedBurnIn(burn.Source.Input, subtitles)],
            };
        }
    }

    private static string Describe(OutputStream request) => request switch
    {
        EncodeVideo => "video encoding",
        EncodeAudio => "audio encoding",
        ConvertSubtitles => "subtitle conversion",
        _ => request.GetType().Name,
    };

    private static bool IsLocalPath(string uri) => !uri.Contains("://", StringComparison.Ordinal);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static TranscodeIssue Error(string message) => new(TranscodeIssueSeverity.Error, message);

    private static TranscodeIssue Warning(string message) => new(TranscodeIssueSeverity.Warning, message);
}
