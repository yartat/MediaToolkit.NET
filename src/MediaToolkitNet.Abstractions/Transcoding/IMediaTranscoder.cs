#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

namespace MediaToolkitNet.Abstractions.Transcoding;

/// <summary>What a transcoder can do. Each backend declares its own.</summary>
[Flags]
public enum TranscodeCapabilities
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Streams can be copied without re-encoding, which is what a container change needs.</summary>
    StreamCopy = 1 << 0,

    /// <summary>Video can be re-encoded.</summary>
    VideoEncode = 1 << 1,

    /// <summary>Audio can be re-encoded.</summary>
    AudioEncode = 1 << 2,

    /// <summary>More than one video stream can be written.</summary>
    MultipleVideo = 1 << 3,

    /// <summary>More than one audio stream can be written.</summary>
    MultipleAudio = 1 << 4,

    /// <summary>Subtitles can be carried over as they are.</summary>
    SubtitleCopy = 1 << 5,

    /// <summary>Text subtitles can be converted to another text format.</summary>
    SubtitleConvert = 1 << 6,

    /// <summary>Subtitles can be rendered into the picture.</summary>
    SubtitleBurnIn = 1 << 7,

    /// <summary>More than one input file can feed one output.</summary>
    MultipleInputs = 1 << 8,

    /// <summary>A start and an end can be given.</summary>
    Trim = 1 << 9,

    /// <summary>The filters of <see cref="VideoFilter"/> and <see cref="AudioFilter"/> are applied.</summary>
    Filters = 1 << 10,

    /// <summary>File-level and stream-level tags are written.</summary>
    Metadata = 1 << 11,

    /// <summary>Chapters are carried over.</summary>
    Chapters = 1 << 12,
}

/// <summary>How serious a <see cref="TranscodeIssue"/> is.</summary>
public enum TranscodeIssueSeverity
{
    /// <summary>The job runs, but not exactly as asked; the message says how.</summary>
    Warning,

    /// <summary>The job cannot run.</summary>
    Error,
}

/// <summary>Something about a request that a transcoder cannot do as asked.</summary>
/// <param name="Severity">Whether the job can still run.</param>
/// <param name="Message">What the problem is, in the transcoder's words.</param>
public sealed record TranscodeIssue(TranscodeIssueSeverity Severity, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{Severity.ToString().ToLowerInvariant()}: {Message}";
}

/// <summary>How far a running job has got.</summary>
/// <param name="Position">Output time written so far.</param>
/// <param name="Duration">Output time expected in all, or <see cref="TimeSpan.Zero"/> when unknown.</param>
/// <param name="Elapsed">Wall-clock time spent.</param>
public readonly record struct TranscodeProgress(TimeSpan Position, TimeSpan Duration, TimeSpan Elapsed)
{
    /// <summary>Share done, from 0 to 1, or <see cref="double.NaN"/> when the duration is unknown.</summary>
    public double Fraction => Duration > TimeSpan.Zero
        ? Math.Clamp(Position / Duration, 0d, 1d)
        : double.NaN;

    /// <summary>Output time written per unit of wall-clock time: 2 means twice as fast as playback.</summary>
    public double Speed => Elapsed > TimeSpan.Zero ? Position / Elapsed : 0d;
}

/// <summary>What a finished job produced.</summary>
/// <param name="Output">The file written.</param>
/// <param name="Duration">Length of the output.</param>
/// <param name="Elapsed">Wall-clock time the job took.</param>
/// <param name="Warnings">What was done differently from what was asked.</param>
public sealed record TranscodeResult(
    string Output,
    TimeSpan Duration,
    TimeSpan Elapsed,
    IReadOnlyList<TranscodeIssue> Warnings);

/// <summary>
/// Runs <see cref="TranscodeRequest"/> jobs. Every transcoding backend implements
/// this, and declares through <see cref="Capabilities"/> and
/// <see cref="Validate"/> which requests it can carry out.
/// </summary>
public interface IMediaTranscoder
{
    /// <summary>Name of the backend implementing this transcoder.</summary>
    string Backend { get; }

    /// <summary>What this backend can do in general. <see cref="Validate"/> answers for one request.</summary>
    TranscodeCapabilities Capabilities { get; }

    /// <summary>
    /// Probes the inputs and lists everything about the request this backend
    /// cannot do. A request with no <see cref="TranscodeIssueSeverity.Error"/>
    /// runs; warnings are repeated in the result.
    /// </summary>
    IReadOnlyList<TranscodeIssue> Validate(TranscodeRequest request);

    /// <summary>Runs the job.</summary>
    /// <param name="request">The job.</param>
    /// <param name="progress">Receives progress, from the thread doing the work.</param>
    /// <param name="cancellation">Stops the job; the partial output is deleted.</param>
    /// <exception cref="TranscodeRejectedException">The request has errors; nothing was written.</exception>
    /// <exception cref="MediaToolkitNetException">The job failed while running.</exception>
    Task<TranscodeResult> RunAsync(
        TranscodeRequest request,
        IProgress<TranscodeProgress>? progress = null,
        CancellationToken cancellation = default);
}

/// <summary>A request that a transcoder, or every transcoder, refused before writing anything.</summary>
public sealed class TranscodeRejectedException : MediaToolkitNetException
{
    /// <summary>Creates the exception from what was wrong.</summary>
    /// <param name="backend">The backend that refused, or <c>mediatoolkitnet</c> when all did.</param>
    /// <param name="issues">What was wrong.</param>
    public TranscodeRejectedException(string backend, IReadOnlyList<TranscodeIssue> issues)
        : base($"[{backend}] {Describe(issues)}")
    {
        BackendName = backend;
        Issues = issues;
    }

    /// <summary>The backend that refused, or <c>mediatoolkitnet</c> when every one did.</summary>
    public string BackendName { get; }

    /// <summary>What was wrong.</summary>
    public IReadOnlyList<TranscodeIssue> Issues { get; }

    private static string Describe(IReadOnlyList<TranscodeIssue> issues)
    {
        var errors = issues.Where(i => i.Severity == TranscodeIssueSeverity.Error).Select(i => i.Message).ToList();
        return errors.Count == 0
            ? "the request was refused"
            : $"the request was refused: {string.Join("; ", errors)}";
    }
}
