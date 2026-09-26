#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Globalization;
using MediaToolkitNet;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;

/// <summary>
/// The <c>transcode</c> command: turns a handful of flags into a
/// <see cref="TranscodeRequest"/>, runs it on the chosen or the first willing
/// backend, and probes the result.
/// </summary>
internal static class TranscodeCommand
{
    public const string Usage = """
          transcode <in> <out> [flags]  copy or re-encode into another container; flags:
              --backend ffmpeg|gstreamer|mpv   use this backend instead of the first that accepts
              --vcodec copy|none|H264|Hevc|Vp9|Av1|Mjpeg
              --acodec copy|none|Aac|Opus|Mp3|Flac|Vorbis|Ac3|...
              --scodec copy|none|burn|SubRip|Ass|WebVtt|MovText
              --all-audio                      every audio stream rather than the first
              --size WxH  --fps N  --quality Q  --speed VeryFast|...  --vbitrate B
              --abitrate B  --rate HZ  --channels N  --volume G
              --vencoder NAME  --aencoder NAME  the encoder by name, as the backend names it
              --vf FILTERS  --af FILTERS       backend-specific filter chain, appended
              --start S  --end S               trim, in seconds
              --sub FILE                       subtitles from another file
              --tag KEY=VALUE                  file-level tag, repeatable
          With no codec flags every stream is copied: a container change.
          mpv-args <in> <out> [flags]   print the mpv command line for the same job
          gst-args <in> <out> [flags]   print the GStreamer pipeline for the same job
""";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3)
        {
            throw new ArgumentException("transcode needs an input and an output.");
        }

        var request = Build(args[1], args[2], args[3..], out var backendName);

        IMediaTranscoder transcoder;
        if (backendName is null)
        {
            transcoder = MediaToolkitNetBackends.CreateTranscoder(request);
        }
        else
        {
            var backend = MediaToolkitNetBackends.Find(backendName)
                ?? throw new ArgumentException($"No backend is named {backendName}.");
            transcoder = backend.CreateTranscoder();
        }

        Console.WriteLine($"Backend: {transcoder.Backend}");
        foreach (var issue in transcoder.Validate(request))
        {
            Console.WriteLine($"  {issue}");
        }

        var result = await transcoder.RunAsync(
            request,
            new ImmediateProgress(p => Console.Write(
                $"\r  {p.Position:hh\\:mm\\:ss\\.ff} / {p.Duration:hh\\:mm\\:ss\\.ff}" +
                $"{(double.IsNaN(p.Fraction) ? string.Empty : $"  {p.Fraction:P0}")}  {p.Speed:0.0}x   ")));

        Console.WriteLine();
        Console.WriteLine($"Wrote {result.Output}: {result.Duration:hh\\:mm\\:ss\\.fff} in {result.Elapsed.TotalSeconds:0.00} s");
        foreach (var warning in result.Warnings)
        {
            Console.WriteLine($"  {warning}");
        }

        Console.WriteLine();
        return CliProbe(result.Output);
    }

    /// <summary>
    /// Reports on the thread doing the work. <see cref="Progress{T}"/> posts to
    /// the thread pool instead, so its last report can arrive after the result.
    /// </summary>
    private sealed class ImmediateProgress(Action<TranscodeProgress> report) : IProgress<TranscodeProgress>
    {
        public void Report(TranscodeProgress value) => report(value);
    }

    /// <summary>
    /// The <c>mpv-args</c> command: resolves the same flags with the FFmpeg
    /// prober and prints the mpv command line the mpv transcoder would run, so
    /// its option mapping can be checked with the mpv program where libmpv is not
    /// installed.
    /// </summary>
    public static int PrintMpvCommandLine(string[] args)
    {
        var request = Build(args[1], args[2], args[3..], out _);
        var prober = new MediaToolkitNet.FFmpeg.FFmpegProber();
        var plan = TranscodePlan.Resolve(request, [.. request.Inputs.Select(prober.Probe)]);
        foreach (var issue in plan.Issues.Concat(MediaToolkitNet.Mpv.Transcoding.MpvTranscoder.Check(plan)))
        {
            Console.Error.WriteLine($"# {issue}");
        }

        if (plan.HasErrors || MediaToolkitNet.Mpv.Transcoding.MpvTranscoder.Check(plan).Any(i => i.Severity == TranscodeIssueSeverity.Error))
        {
            return 3;
        }

        Console.WriteLine(MediaToolkitNet.Mpv.Transcoding.MpvTranscoder.CommandLine(plan));
        return 0;
    }

    /// <summary>
    /// The <c>gst-args</c> command: resolves the same flags with the GStreamer
    /// prober and prints the pipeline the GStreamer transcoder would build, in
    /// the form <c>gst-launch-1.0</c> takes.
    /// </summary>
    public static int PrintGstPipeline(string[] args)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("The GStreamer backend runs on Linux only.");
            return 2;
        }

        var request = Build(args[1], args[2], args[3..], out _);
        var prober = new MediaToolkitNet.GStreamer.Transcoding.GStreamerProber();
        var plan = TranscodePlan.Resolve(request, [.. request.Inputs.Select(prober.Probe)]);
        var issues = plan.Issues.Concat(MediaToolkitNet.GStreamer.Transcoding.GStreamerTranscoder.Check(plan)).ToList();
        foreach (var issue in issues)
        {
            Console.Error.WriteLine($"# {issue}");
        }

        if (issues.Any(i => i.Severity == TranscodeIssueSeverity.Error))
        {
            return 3;
        }

        if (request.Start is not null || request.End is not null)
        {
            Console.Error.WriteLine("# the trim is a seek the transcoder sends at run time; gst-launch runs this from the start");
        }

        Console.WriteLine("gst-launch-1.0 -e " + MediaToolkitNet.GStreamer.Transcoding.GStreamerTranscoder.Describe(plan));
        return 0;
    }

    private static int CliProbe(string path)
    {
        var info = MediaToolkitNetBackends.Probe(path);
        Console.WriteLine($"Result ({info.Backend}): {info.ContainerName}, {info.Duration:hh\\:mm\\:ss\\.fff}");
        foreach (var stream in info.Streams)
        {
            var flags = (stream.IsDefault ? " default" : string.Empty) + (stream.IsForced ? " forced" : string.Empty);
            Console.WriteLine($"  {stream}{(stream.Title is null ? string.Empty : $" \"{stream.Title}\"")}{flags}");
        }

        foreach (var chapter in info.Chapters)
        {
            Console.WriteLine($"  chapter {chapter.Start:hh\\:mm\\:ss\\.fff}-{chapter.End:hh\\:mm\\:ss\\.fff} {chapter.Title}");
        }

        foreach (var (key, value) in info.Metadata)
        {
            Console.WriteLine($"  tag {key} = {value}");
        }

        return 0;
    }

    internal static TranscodeRequest Build(string input, string output, string[] flags, out string? backend)
    {
        backend = null;
        string? vcodec = null, acodec = null, scodec = null, subtitleFile = null, vf = null, af = null;
        var allAudio = false;
        var video = new VideoOutputSettings();
        var audio = new AudioOutputSettings();
        var videoFilters = new List<VideoFilter>();
        var audioFilters = new List<AudioFilter>();
        var tags = new Dictionary<string, string>();
        TimeSpan? start = null, end = null;

        for (var i = 0; i < flags.Length; i++)
        {
            var flag = flags[i];
            string Next() => ++i < flags.Length ? flags[i] : throw new ArgumentException($"{flag} needs a value.");

            switch (flag)
            {
                case "--backend": backend = Next(); break;
                case "--vcodec": vcodec = Next(); break;
                case "--acodec": acodec = Next(); break;
                case "--scodec": scodec = Next(); break;
                case "--all-audio": allAudio = true; break;
                case "--sub": subtitleFile = Next(); break;
                case "--vf": vf = Next(); break;
                case "--af": af = Next(); break;
                case "--start": start = TimeSpan.FromSeconds(double.Parse(Next(), CultureInfo.InvariantCulture)); break;
                case "--end": end = TimeSpan.FromSeconds(double.Parse(Next(), CultureInfo.InvariantCulture)); break;
                case "--size":
                    var size = Next().Split('x');
                    video = video with { Width = int.Parse(size[0], CultureInfo.InvariantCulture), Height = int.Parse(size[1], CultureInfo.InvariantCulture) };
                    break;
                case "--fps": video = video with { FrameRate = new Rational(int.Parse(Next(), CultureInfo.InvariantCulture), 1) }; break;
                case "--quality": video = video with { Quality = double.Parse(Next(), CultureInfo.InvariantCulture) }; break;
                case "--speed": video = video with { Speed = Enum.Parse<EncoderSpeed>(Next(), ignoreCase: true) }; break;
                case "--vbitrate": video = video with { BitrateBitsPerSecond = int.Parse(Next(), CultureInfo.InvariantCulture) }; break;
                case "--vencoder": video = video with { EncoderName = Next() }; break;
                case "--aencoder": audio = audio with { EncoderName = Next() }; break;
                case "--abitrate": audio = audio with { BitrateBitsPerSecond = int.Parse(Next(), CultureInfo.InvariantCulture) }; break;
                case "--rate": audio = audio with { SampleRate = int.Parse(Next(), CultureInfo.InvariantCulture) }; break;
                case "--channels": audio = audio with { Channels = int.Parse(Next(), CultureInfo.InvariantCulture) }; break;
                case "--volume": audioFilters.Add(new AudioFilter.Volume(double.Parse(Next(), CultureInfo.InvariantCulture))); break;
                case "--tag":
                    var pair = Next().Split('=', 2);
                    tags[pair[0]] = pair.Length > 1 ? pair[1] : string.Empty;
                    break;
                default: throw new ArgumentException($"Unknown flag {flag}.");
            }
        }

        var inputs = subtitleFile is null ? new[] { input } : [input, subtitleFile];
        var request = new TranscodeRequest(output)
        {
            Inputs = inputs,
            Start = start,
            End = end,
            Metadata = tags.Count == 0 ? null : tags,
        };

        if (vcodec is null && acodec is null && scodec is null && subtitleFile is null)
        {
            return request;
        }

        var streams = new List<OutputStream>();
        var firstVideo = StreamSource.First(MediaStreamKind.Video);
        var backendFor = backend ?? "ffmpeg";
        if (vf is not null)
        {
            videoFilters.Add(new VideoFilter.Custom(backendFor, vf));
        }

        if (af is not null)
        {
            audioFilters.Add(new AudioFilter.Custom(backendFor, af));
        }

        switch (vcodec?.ToLowerInvariant())
        {
            case "none":
                break;
            case null or "copy" when scodec != "burn":
                streams.Add(OutputStream.Copy(firstVideo));
                break;
            default:
                var codec = vcodec is null or "copy" ? MediaCodec.H264 : Enum.Parse<MediaCodec>(vcodec, ignoreCase: true);
                streams.Add(OutputStream.Video(firstVideo, video with { Codec = codec, Filters = videoFilters }));
                break;
        }

        var audioSource = allAudio ? StreamSource.All(MediaStreamKind.Audio) : StreamSource.First(MediaStreamKind.Audio);
        switch (acodec?.ToLowerInvariant())
        {
            case "none":
                break;
            case null or "copy":
                streams.Add(OutputStream.Copy(audioSource));
                break;
            default:
                streams.Add(OutputStream.Audio(
                    audioSource,
                    audio with { Codec = Enum.Parse<MediaCodec>(acodec, ignoreCase: true), Filters = audioFilters }));
                break;
        }

        var subtitleInput = subtitleFile is null ? 0 : 1;
        var subtitles = subtitleFile is null
            ? StreamSource.All(MediaStreamKind.Subtitle)
            : StreamSource.First(MediaStreamKind.Subtitle, input: 1);
        switch (scodec?.ToLowerInvariant())
        {
            case "none":
                break;
            case null or "copy":
                streams.Add(OutputStream.Copy(subtitles));
                break;
            case "burn":
                streams.Add(OutputStream.BurnIn(StreamSource.First(MediaStreamKind.Subtitle, input: subtitleInput), firstVideo));
                break;
            default:
                streams.Add(OutputStream.Subtitles(subtitles, Enum.Parse<MediaCodec>(scodec, ignoreCase: true)));
                break;
        }

        return request with { Streams = streams };
    }
}
