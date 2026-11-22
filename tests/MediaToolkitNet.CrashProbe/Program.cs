#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.Interop;

// usage: CrashProbe <scenario> <media file>
// Prints "RESULT key=value" lines; exits 0 when it got to the end.
if (Environment.GetEnvironmentVariable("MEDIATOOLKITNET_FFMPEG_DIR") is { Length: > 0 } directory)
{
    NativeSearchPaths.Prepend(directory);
}

var scenario = args[0];
var media = args[1];

switch (scenario)
{
    case "failed-handler-throws":
        FailedHandlerThrows(media);
        break;
    case "dispose-inside-handler":
        DisposeInsideHandler(media);
        break;
    case "seek-while-paused":
        SeekWhilePaused(media);
        break;
    case "player-stalled-network":
        PlayerStalledNetwork(media);
        break;
    case "mpv-stop-vs-dispose":
        MpvStopVsDispose(media);
        break;
    case "reconfigure-bitrate":
        ReconfigureBitrate(media);
        break;
    case "wasapi-prefill":
        WasapiPrefill();
        break;
    default:
        Console.Error.WriteLine($"unknown scenario {scenario}");
        return 2;
}

Console.WriteLine("RESULT finished=true");
return 0;

static void FailedHandlerThrows(string media)
{
    using var player = new FFmpegPlayer { RealTimePacing = false };
    player.Open(media);
    player.VideoFrameDecoded += (in VideoFrame _) => throw new InvalidOperationException("a subscriber's frame handler threw");
    player.Failed += (_, _) => throw new InvalidOperationException("a subscriber's Failed handler threw");
    player.Play();
    Thread.Sleep(1000);
    Console.WriteLine($"RESULT state={player.State}");
}

static void DisposeInsideHandler(string media)
{
    var player = new FFmpegPlayer { RealTimePacing = false };
    player.Open(media);
    var clock = new Stopwatch();
    var disposed = new ManualResetEventSlim();
    player.VideoFrameDecoded += (in VideoFrame _) =>
    {
        if (clock.IsRunning)
        {
            return;
        }

        clock.Start();
        player.Dispose();
        clock.Stop();
        disposed.Set();
    };
    player.Play();
    disposed.Wait(TimeSpan.FromSeconds(10));
    Thread.Sleep(500);
    Console.WriteLine($"RESULT dispose_ms={clock.ElapsedMilliseconds}");
}

static void SeekWhilePaused(string media)
{
    using var player = new FFmpegPlayer { RealTimePacing = false };
    player.Open(media);
    var failures = 0;
    player.Failed += (_, _) => Interlocked.Increment(ref failures);
    var random = new Random(7);
    for (var i = 0; i < 300; i++)
    {
        player.Play();
        Thread.Yield();
        player.Pause();
        player.Seek(TimeSpan.FromMilliseconds(random.Next(0, 1500)));
    }

    Console.WriteLine($"RESULT failures={failures}");
}

static void WasapiPrefill()
{
    if (!OperatingSystem.IsWindows())
    {
        Console.WriteLine("RESULT skipped=true");
        return;
    }

    PrefillRenderer();
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static void PrefillRenderer()
{
    MediaToolkitNet.Windows.WasapiRenderer renderer;
    try
    {
        renderer = new MediaToolkitNet.Windows.WasapiRenderer(MediaToolkitNet.Abstractions.Capture.AudioCaptureSettings.Default);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"RESULT skipped=true ({ex.Message})");
        return;
    }

    // Two seconds of silence, written before Start: more than the endpoint buffer holds.
    var data = new byte[renderer.Format.BlockAlign * renderer.Format.SampleRate * 2];
#pragma warning disable CA1416 // only reached on Windows, see WasapiPrefill
    var write = Task.Run(() => renderer.Write(data));
#pragma warning restore CA1416
    var returned = write.Wait(3000);
    Console.WriteLine($"RESULT returned={returned}");
    if (!returned)
    {
        Environment.Exit(3); // the write is stuck; disposing under it would only hide that
    }

    renderer.Dispose();
}

static void MpvStopVsDispose(string media)
{
    var random = new Random(11);
    for (var i = 0; i < 150; i++)
    {
        var player = new MediaToolkitNet.Mpv.MpvPlayer();
        player.Options["vo"] = "null";
        player.Options["ao"] = "null";
        player.Open(media);
        player.Play();
        var worker = new Thread(() =>
        {
            try
            {
                for (var n = 0; n < 200; n++)
                {
                    player.Volume = 0.5;
                    player.Pause();
                    player.Play();
                    player.Stop();
                }
            }
            catch (ObjectDisposedException)
            {
                // Expected once the other thread has disposed the player.
            }
        });
        worker.Start();
        Thread.Sleep(random.Next(0, 15));
        player.Dispose();
        worker.Join();
    }
}

// Serves the file's first bytes over HTTP, declares it much longer, and then says
// nothing: a read that has used up what it was given waits for ever.
static void PlayerStalledNetwork(string media)
{
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    var bytes = File.ReadAllBytes(media);
    var server = new Thread(() =>
    {
        try
        {
            while (true)
            {
                var client = listener.AcceptTcpClient();
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var request = string.Empty;
                while (!request.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    request += System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                }

                var header = System.Text.Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nContent-Length: {bytes.Length * 1000L}\r\n\r\n");
                stream.Write(header);
                stream.Write(bytes);
                stream.Flush();
                Thread.Sleep(Timeout.Infinite);
            }
        }
        catch (Exception)
        {
            // The listener is stopped when the probe ends.
        }
    })
    {
        IsBackground = true,
    };
    server.Start();

    var player = new FFmpegPlayer { RealTimePacing = false };
    player.Open($"http://127.0.0.1:{port}/clip.ts");
    player.Play();
    Thread.Sleep(1500); // long enough to have read everything that was sent

    var clock = Stopwatch.StartNew();
    player.Dispose();
    Console.WriteLine($"RESULT dispose_ms={clock.ElapsedMilliseconds}");
}

// b, maxrate, bufsize and sar are not runtime parameters of an AVCodecContext, and in some
// FFmpeg builds asking to change them dereferences a null pointer instead of failing.
static void ReconfigureBitrate(string path)
{
    var format = new MediaToolkitNet.Abstractions.Formats.VideoFormat(
        320, 240, MediaToolkitNet.Abstractions.Formats.PixelFormat.Yuv420P, new MediaToolkitNet.Abstractions.Formats.Rational(25, 1));
    using var recorder = new FFmpegRecorder(path);
    int stream;
    try
    {
        stream = recorder.AddVideoStream(new MediaToolkitNet.Abstractions.Recording.VideoEncodingSettings(
            format, MediaToolkitNet.Abstractions.Recording.MediaCodec.H264, 200_000, 25) { EncoderName = "libx264" });
    }
    catch (MediaToolkitNet.Abstractions.MediaToolkitNetException)
    {
        Console.WriteLine("RESULT result=no-libx264");
        return;
    }

    recorder.Start();
    try
    {
        recorder.Reconfigure(stream, new Dictionary<string, string> { ["b"] = "3000000" });
        Console.WriteLine("RESULT result=applied");
    }
    catch (MediaToolkitNet.Abstractions.MediaToolkitNetException)
    {
        Console.WriteLine("RESULT result=refused");
    }
}
