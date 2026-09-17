using System.Diagnostics;
using MediaToolkitNet;
using MediaToolkitNet.Core;
using MediaToolkitNet.Core.Capture;
using MediaToolkitNet.Core.Devices;
using MediaToolkitNet.Core.Formats;
using MediaToolkitNet.Core.Frames;
using MediaToolkitNet.Core.Playback;
using MediaToolkitNet.Core.Recording;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.FFmpeg.Native;

return args.Length == 0 ? Usage() : Run(args);

static int Usage()
{
    Console.WriteLine("""
        MediaToolkit.NET — low-level media wrapper demo.

        Commands:
          backends                     list backends and their availability
          devices [audio|video|all]    list devices
          devices dshow                list devices through DirectShow (Windows only)
          probe <file|URL>             inspect a container with FFmpeg
          play <file|URL> [seconds]    play with the best available backend
          decode <file> [seconds]      decode with FFmpeg and report frame stats
          rec-audio <seconds>          capture audio from the default device
          rec-video <seconds>          capture video from the default camera
          rec-file <seconds> <file>    record the camera to a file
          rec-audio-file <sec> <file>  record audio to a file
          encoders                     list Media Foundation encoders (Windows only)
        """);
    return 1;
}

static int Run(string[] args)
{
    try
    {
        return args[0].ToLowerInvariant() switch
        {
            "backends" => ShowBackends(),
            "devices" => ShowDevices(args.Length > 1 ? args[1] : "all"),
            "probe" => Probe(Argument(args, 1, "file path")),
            "play" => Play(Argument(args, 1, "file path"), Seconds(args, 2, 10)),
            "decode" => Decode(Argument(args, 1, "file path"), Seconds(args, 2, 5)),
            "rec-audio" => RecordAudio(Seconds(args, 1, 3)),
            "rec-video" => RecordVideo(Seconds(args, 1, 3)),
            "encoders" => ListEncoders(),
            "rec-audio-file" => RecordAudioToFile(Seconds(args, 1, 3), Argument(args, 2, "output file path")),
            "rec-file" => RecordToFile(Seconds(args, 1, 3), Argument(args, 2, "output file path"),
                args.Length > 3 ? Enum.Parse<MediaCodec>(args[3], ignoreCase: true) : MediaCodec.H264),
            _ => Usage(),
        };
    }
    catch (MediaBackendUnavailableException ex)
    {
        Console.Error.WriteLine($"Backend unavailable: {ex.Message}");
        return 2;
    }
    catch (MediaToolkitNetException ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 3;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Unexpected error: {ex.Message}");
        return 4;
    }
}

static string Argument(string[] args, int index, string what) =>
    index < args.Length ? args[index] : throw new ArgumentException($"Missing argument: {what}.");

static int Seconds(string[] args, int index, int fallback) =>
    index < args.Length && int.TryParse(args[index], out var value) ? value : fallback;

static int ShowBackends()
{
    Console.WriteLine("Registered backends, in preference order:");
    foreach (var backend in MediaToolkitNetBackends.Registered)
    {
        var status = backend.IsAvailable ? "available" : "unavailable";
        var version = backend.NativeVersion is { } text ? $", {text}" : string.Empty;
        Console.WriteLine($"  {backend.Name,-16} {status,-12} [{backend.Capabilities}]{version}");
    }

    return 0;
}

static int ShowDevices(string filter)
{
    if (filter.Equals("dshow", StringComparison.OrdinalIgnoreCase))
    {
        return ShowDirectShowDevices();
    }

    var kind = filter.ToLowerInvariant() switch
    {
        "audio" => MediaDeviceKind.AudioCapture | MediaDeviceKind.AudioRender,
        "video" => MediaDeviceKind.VideoCapture,
        _ => MediaDeviceKind.All,
    };

    var devices = MediaToolkitNetBackends.EnumerateDevices(kind);
    if (devices.Count == 0)
    {
        Console.WriteLine("No devices found.");
        return 0;
    }

    foreach (var group in devices.GroupBy(device => device.Backend))
    {
        Console.WriteLine($"[{group.Key}]");
        foreach (var device in group)
        {
            var mark = device.IsDefault ? "*" : " ";
            Console.WriteLine($" {mark} {device.Kind,-32} {device.Name}");
            Console.WriteLine($"     id: {device.Id}");
        }
    }

    return 0;
}

static int ShowDirectShowDevices()
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("DirectShow is only available on Windows.");
        return 2;
    }

    foreach (var device in MediaToolkitNet.Windows.WindowsBackend.CreateDirectShowEnumerator().Enumerate())
    {
        Console.WriteLine($"  {device.Kind,-14} {device.Name}");
        Console.WriteLine($"     id: {device.Id}");
    }

    return 0;
}

static int Probe(string path)
{
    using var demuxer = FFmpegDemuxer.Open(path);

    Console.WriteLine($"Source: {path}");
    Console.WriteLine($"Duration: {demuxer.Duration:hh\\:mm\\:ss\\.fff}");
    Console.WriteLine($"Streams: {demuxer.Streams.Count}");

    foreach (var stream in demuxer.Streams)
    {
        Console.WriteLine($"  #{stream.Index} {stream.MediaType,-10} {stream.CodecName,-12} " +
                          $"time base {stream.TimeBase}, {stream.AverageFrameRate.Value:0.###} fps, " +
                          $"duration {stream.Duration:hh\\:mm\\:ss\\.fff}");
    }

    return 0;
}

static int Play(string path, int seconds)
{
    using var player = MediaToolkitNetBackends.CreatePlayer();
    Console.WriteLine($"Playback backend: {player.Backend}");

    player.StateChanged += (_, change) => Console.WriteLine($"  state: {change.Previous} -> {change.Current}");
    player.Failed += (_, error) => Console.Error.WriteLine($"  failure: {error.Message}");

    player.Open(path);
    player.Play();

    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < TimeSpan.FromSeconds(seconds) && player.State is PlaybackState.Playing or PlaybackState.Stopped)
    {
        Thread.Sleep(500);
        Console.WriteLine($"  position {player.Position:hh\\:mm\\:ss} of {player.Duration:hh\\:mm\\:ss}");
    }

    player.Stop();
    return 0;
}

static int Decode(string path, int seconds)
{
    using var player = new FFmpegPlayer { RealTimePacing = false };

    var videoFrames = 0;
    var audioFrames = 0;
    var lastVideoFormat = default(VideoFormat);
    var lastAudioFormat = default(AudioFormat);

    player.VideoFrameDecoded += (in VideoFrame frame) =>
    {
        videoFrames++;
        lastVideoFormat = frame.Format;
    };

    player.AudioFrameDecoded += (in AudioFrame frame) =>
    {
        audioFrames++;
        lastAudioFormat = frame.Format;
    };

    player.Failed += (_, error) => Console.Error.WriteLine($"  failure: {error.Message}");

    player.Open(path);
    player.Play();

    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < TimeSpan.FromSeconds(seconds) && player.State == PlaybackState.Playing)
    {
        Thread.Sleep(100);
    }

    player.Stop();

    Console.WriteLine($"Decoded in {deadline.Elapsed.TotalSeconds:0.0} s:");
    Console.WriteLine($"  video frames: {videoFrames}" + (lastVideoFormat.IsValid ? $", format {lastVideoFormat}" : string.Empty));
    Console.WriteLine($"  audio blocks: {audioFrames}" + (lastAudioFormat.IsValid ? $", format {lastAudioFormat}" : string.Empty));
    return 0;
}

static int RecordAudio(int seconds)
{
    using var capture = MediaToolkitNetBackends.CreateAudioCapture(AudioCaptureSettings.Default);
    Console.WriteLine($"Audio capture backend: {capture.Backend}, format {capture.Format}");

    var blocks = 0;
    var peak = 0.0;

    capture.FrameCaptured += (in AudioFrame frame) =>
    {
        blocks++;
        peak = Math.Max(peak, PeakOf(frame));
    };

    capture.Failed += (_, error) => Console.Error.WriteLine($"  failure: {error.Message}");

    capture.Start();
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    capture.Stop();

    Console.WriteLine($"Blocks received: {blocks}, peak level: {peak:0.000}");
    return 0;
}

static double PeakOf(in AudioFrame frame)
{
    if (frame.PlaneCount == 0 || frame.Format.SampleFormat.IsPlanar())
    {
        return 0;
    }

    var peak = 0.0;
    switch (frame.Format.SampleFormat)
    {
        case SampleFormat.S16:
            foreach (var sample in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(frame.Interleaved))
            {
                peak = Math.Max(peak, Math.Abs(sample) / (double)short.MaxValue);
            }

            break;

        case SampleFormat.F32:
            foreach (var sample in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(frame.Interleaved))
            {
                peak = Math.Max(peak, Math.Abs(sample));
            }

            break;
    }

    return peak;
}

static int RecordVideo(int seconds)
{
    using var capture = MediaToolkitNetBackends.CreateVideoCapture(VideoCaptureSettings.Default);
    Console.WriteLine($"Video capture backend: {capture.Backend}, format {capture.Format}");

    if (capture.SupportedFormats.Count > 0)
    {
        Console.WriteLine("Device modes:");
        foreach (var format in capture.SupportedFormats.Take(10))
        {
            Console.WriteLine($"  {format}");
        }
    }

    var frames = 0;
    capture.FrameCaptured += (in VideoFrame frame) => Interlocked.Increment(ref frames);
    capture.Failed += (_, error) => Console.Error.WriteLine($"  failure: {error.Message}");

    capture.Start();
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    capture.Stop();

    Console.WriteLine($"Frames received: {frames} ({frames / (double)seconds:0.0} fps)");
    return 0;
}

static int RecordToFile(int seconds, string outputPath, MediaCodec codec)
{
    using var capture = MediaToolkitNetBackends.CreateVideoCapture(VideoCaptureSettings.Default);
    using var recorder = MediaToolkitNetBackends.CreateRecorder(outputPath);

    Console.WriteLine($"Capture: {capture.Backend}, format {capture.Format}");
    Console.WriteLine($"Recording: {recorder.Backend} -> {outputPath}");

    var stream = recorder.AddVideoStream(new VideoEncodingSettings(capture.Format, codec));
    recorder.Start();

    var written = 0;
    var dropped = 0;

    capture.FrameCaptured += (in VideoFrame frame) =>
    {
        try
        {
            recorder.WriteVideo(stream, frame);
            Interlocked.Increment(ref written);
        }
        catch (MediaToolkitNetException)
        {
            // A rejected frame must not tear down the capture thread.
            Interlocked.Increment(ref dropped);
        }
    };

    capture.Failed += (_, error) => Console.Error.WriteLine($"  capture failure: {error.Message}");

    capture.Start();
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    capture.Stop();
    recorder.Stop();

    var size = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
    Console.WriteLine($"Frames written: {written}, dropped: {dropped}, file size: {size / 1024.0:0.0} KB");
    return 0;
}

static unsafe int ListEncoders()
{
    if (!OperatingSystem.IsWindows())
    {
        return 2;
    }

    MediaToolkitNet.Windows.Native.MfApi.Startup();

    foreach (var (label, category) in new[]
             {
                 ("video", MediaToolkitNet.Windows.Native.MfApi.CategoryVideoEncoder),
                 ("audio", MediaToolkitNet.Windows.Native.MfApi.CategoryAudioEncoder),
             })
    {
        void** activates;
        uint count;
        var hr = MediaToolkitNet.Windows.Native.MfApi.MFTEnumEx(
            category, MediaToolkitNet.Windows.Native.MfApi.EnumFlagAll, null, null, &activates, &count);

        Console.WriteLine($"Encoders ({label}): hr=0x{hr:X8}, found {count}");
        if (hr < 0)
        {
            continue;
        }

        for (var i = 0u; i < count; i++)
        {
            // Most registered MFTs carry a friendly name, but the attribute is
            // optional, so fall back rather than printing a blank line.
            var name = MediaToolkitNet.Windows.Native.Mf.GetString(
                activates[i], MediaToolkitNet.Windows.Native.WinGuids.MftFriendlyName);
            Console.WriteLine($"  {name ?? "(unnamed MFT)"}");
            MediaToolkitNet.Interop.Com.Com.Release(activates[i]);
        }

        MediaToolkitNet.Interop.Com.Ole32.CoTaskMemFree(activates);
    }

    return 0;
}

static int RecordAudioToFile(int seconds, string outputPath)
{
    var requested = AudioCaptureSettings.Default;
    using var capture = MediaToolkitNetBackends.CreateAudioCapture(requested);
    using var recorder = MediaToolkitNetBackends.CreateRecorder(outputPath);

    Console.WriteLine($"Capture: {capture.Backend}, format {capture.Format}");
    Console.WriteLine($"Recording: {recorder.Backend} -> {outputPath}");

    // The AAC encoder only takes 16-bit PCM, so the captured float mix has to be
    // converted before it is handed over.
    var target = capture.Format with { SampleFormat = SampleFormat.S16 };
    var stream = recorder.AddAudioStream(new AudioEncodingSettings(target, MediaCodec.Aac, 128_000));
    recorder.Start();

    var written = 0;
    var scratch = new byte[64 * 1024];

    capture.FrameCaptured += (in AudioFrame frame) =>
    {
        var samples = ConvertToS16(frame, scratch);
        if (samples == 0)
        {
            return;
        }

        unsafe
        {
            fixed (byte* data = scratch)
            {
                var converted = new AudioFrame(target, frame.Timestamp, samples, data);
                recorder.WriteAudio(stream, converted);
            }
        }

        Interlocked.Increment(ref written);
    };

    capture.Failed += (_, error) => Console.Error.WriteLine($"  capture failure: {error.Message}");

    capture.Start();
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    capture.Stop();
    recorder.Stop();

    var size = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
    Console.WriteLine($"Blocks written: {written}, file size: {size / 1024.0:0.0} KB");
    return 0;
}

static int ConvertToS16(in AudioFrame frame, byte[] destination)
{
    if (frame.Format.SampleFormat.IsPlanar())
    {
        return 0;
    }

    var channels = frame.Format.Channels;
    var target = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(destination.AsSpan());
    var capacity = target.Length / channels;
    var samples = Math.Min(frame.SampleCount, capacity);

    switch (frame.Format.SampleFormat)
    {
        case SampleFormat.S16:
            frame.Interleaved[..(samples * channels * 2)].CopyTo(destination);
            return samples;

        case SampleFormat.F32:
            var source = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(frame.Interleaved);
            for (var i = 0; i < samples * channels; i++)
            {
                target[i] = (short)Math.Clamp(source[i] * short.MaxValue, short.MinValue, short.MaxValue);
            }

            return samples;

        default:
            return 0;
    }
}
