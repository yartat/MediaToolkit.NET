#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Capture;
using MediaToolkitNet.Abstractions.Devices;
using MediaToolkitNet.Abstractions.Playback;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;

namespace MediaToolkitNet;

/// <summary>
/// Registry over every backend in this repository.
/// </summary>
/// <remarks>
/// <para>
/// Backends are listed in the order they should be preferred on the current
/// operating system: the native stack first for capture and playback, FFmpeg
/// wherever files have to be decoded or written. Nothing is loaded until a
/// backend is actually asked for, and probing one that is unavailable never
/// throws.
/// </para>
/// <para>
/// Reach past this class whenever you need something a backend offers and the
/// common abstraction does not, such as WASAPI loopback capture or mpv
/// properties.
/// </para>
/// </remarks>
public static class MediaToolkitNetBackends
{
    private static readonly Lazy<IEnumerable<IMediaBackend>> All = new(BuildRegistry);

    /// <summary>Every backend that compiles into this build, in preference order.</summary>
    public static IEnumerable<IMediaBackend> Registered => All.Value;

    /// <summary>Backends whose native libraries are present on this machine.</summary>
    public static IReadOnlyList<IMediaBackend> Available =>
        [.. Registered.Where(backend => backend.IsAvailable)];

    /// <summary>Finds a backend by its <see cref="IMediaBackend.Name"/>.</summary>
    public static IMediaBackend? Find(string name) =>
        Registered.FirstOrDefault(backend => string.Equals(backend.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the first available backend offering <paramref name="capability"/>.
    /// </summary>
    /// <exception cref="MediaBackendUnavailableException">Nothing on this machine can do it.</exception>
    public static IMediaBackend Require(BackendCapabilities capability)
    {
        var backend = Select(capability);
        return backend ?? throw new MediaBackendUnavailableException(
            "mediatoolkitnet",
            $"this system does not support {capability}. " +
            $"Available backends: {string.Join(", ", Registered.Select(b => b.Name))}.");
    }

    /// <summary>Returns the first available backend offering <paramref name="capability"/>, or null.</summary>
    public static IMediaBackend? Select(BackendCapabilities capability) =>
        Registered.FirstOrDefault(backend => (backend.Capabilities & capability) == capability && backend.IsAvailable);

    /// <summary>Creates a player using the best backend available.</summary>
    public static IMediaPlayer CreatePlayer() => Require(BackendCapabilities.Playback).CreatePlayer();

    /// <summary>Creates a recorder using the best backend available.</summary>
    public static IMediaRecorder CreateRecorder(string outputPath) =>
        Require(BackendCapabilities.Recording).CreateRecorder(outputPath);

    /// <summary>Opens an audio input endpoint using the best backend available.</summary>
    public static IAudioCapture CreateAudioCapture(AudioCaptureSettings settings) =>
        Require(BackendCapabilities.AudioCapture).CreateAudioCapture(settings);

    /// <summary>Opens an audio output endpoint using the best backend available.</summary>
    public static IAudioRenderer CreateAudioRenderer(AudioCaptureSettings settings) =>
        Require(BackendCapabilities.AudioRender).CreateAudioRenderer(settings);

    /// <summary>Opens a video input device using the best backend available.</summary>
    public static IVideoCapture CreateVideoCapture(VideoCaptureSettings settings) =>
        Require(BackendCapabilities.VideoCapture).CreateVideoCapture(settings);

    /// <summary>
    /// Reads what a file contains, with the first available backend that can,
    /// in the order <see cref="CreateTranscoder"/> asks them: FFmpeg reports the
    /// most, pixel and sample formats and stream flags included.
    /// </summary>
    /// <exception cref="MediaBackendUnavailableException">No backend on this system can probe.</exception>
    public static MediaInfo Probe(string uri)
    {
        var backend = Registered
            .OrderBy(TranscodingPreference)
            .FirstOrDefault(b => (b.Capabilities & BackendCapabilities.Probing) != 0 && b.IsAvailable);

        return backend is null
            ? throw new MediaBackendUnavailableException("mediatoolkitnet", "no backend on this system can probe a file")
            : backend.CreateProber().Probe(uri);
    }

    /// <summary>
    /// Picks the transcoder for a request: the first available backend that
    /// can transcode and reports no error for it.
    /// </summary>
    /// <remarks>
    /// FFmpeg is asked first, because it can do the most and does it exactly;
    /// GStreamer and mpv take the requests it cannot, such as one on a machine
    /// without FFmpeg. Every refusal is kept, so when none accepts, the exception
    /// says what each one objected to.
    /// </remarks>
    /// <exception cref="TranscodeRejectedException">No backend accepted the request.</exception>
    public static IMediaTranscoder CreateTranscoder(TranscodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var refusals = new List<TranscodeIssue>();
        foreach (var backend in Registered.OrderBy(TranscodingPreference))
        {
            if ((backend.Capabilities & BackendCapabilities.Transcoding) == 0 || !backend.IsAvailable)
            {
                continue;
            }

            IMediaTranscoder transcoder;
            IReadOnlyList<TranscodeIssue> issues;
            try
            {
                transcoder = backend.CreateTranscoder();
                issues = transcoder.Validate(request);
            }
            catch (MediaToolkitNetException ex)
            {
                refusals.Add(new TranscodeIssue(TranscodeIssueSeverity.Error, $"{backend.Name}: {ex.Message}"));
                continue;
            }

            var errors = issues.Where(i => i.Severity == TranscodeIssueSeverity.Error).ToList();
            if (errors.Count == 0)
            {
                return transcoder;
            }

            refusals.AddRange(errors.Select(e => e with { Message = $"{backend.Name}: {e.Message}" }));
        }

        if (refusals.Count == 0)
        {
            refusals.Add(new TranscodeIssue(TranscodeIssueSeverity.Error, "no backend on this system can transcode"));
        }

        throw new TranscodeRejectedException("mediatoolkitnet", refusals);
    }

    /// <summary>Runs a request on the transcoder <see cref="CreateTranscoder"/> picks.</summary>
    /// <exception cref="TranscodeRejectedException">No backend accepted the request.</exception>
    public static Task<TranscodeResult> TranscodeAsync(
        TranscodeRequest request,
        IProgress<TranscodeProgress>? progress = null,
        CancellationToken cancellation = default) =>
        CreateTranscoder(request).RunAsync(request, progress, cancellation);

    private static int TranscodingPreference(IMediaBackend backend) => backend.Name switch
    {
        "ffmpeg" => 0,
        "gstreamer" => 1,
        "mpv" => 2,
        _ => 3,
    };

    /// <summary>Lists devices from every available backend that can enumerate.</summary>
    public static IReadOnlyList<MediaDevice> EnumerateDevices(MediaDeviceKind kind = MediaDeviceKind.All)
    {
        var result = new List<MediaDevice>();
        foreach (var backend in Registered)
        {
            if ((backend.Capabilities & BackendCapabilities.DeviceEnumeration) == 0 || !backend.IsAvailable)
            {
                continue;
            }

            try
            {
                result.AddRange(backend.CreateDeviceEnumerator().Enumerate(kind));
            }
            catch (MediaToolkitNetException)
            {
                // One broken backend must not hide the devices of the others.
            }
        }

        return result;
    }

    private static IEnumerable<IMediaBackend> BuildRegistry()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Windows.WindowsBackend.Instance;
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return Linux.LinuxBackend.Instance;
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return MacOS.MacOSBackend.Instance;
        }

        // GStreamer offers no capture or playback interface of its own here, so
        // it sits behind the platform backend and only reports that it is there.
        if (OperatingSystem.IsLinux())
        {
            yield return GStreamer.GStreamerBackend.Instance;
        }

        // mpv renders playback itself, so it comes before FFmpeg for that role;
        // FFmpeg remains the only portable decoder and recorder.
        yield return Mpv.MpvBackend.Instance;
        yield return FFmpeg.FFmpegBackend.Instance;
    }
}
