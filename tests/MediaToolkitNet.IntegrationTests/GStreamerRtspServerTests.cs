#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.GStreamer;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>A fact that is skipped unless GStreamer, FFmpeg and gst-rtsp-server are usable.</summary>
public sealed class GStreamerRtspFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="GStreamerRtspFactAttribute"/> class.</summary>
    public GStreamerRtspFactAttribute()
    {
        Skip = new GStreamerFactAttribute().Skip;
        if (Skip is null && OperatingSystem.IsLinux() && !GStreamerRtspServer.IsAvailable)
        {
            Skip = "gst-rtsp-server is not installed.";
        }
    }
}

/// <summary>
/// The RTSP server, read by clients that are not it: the <c>ffmpeg</c> program and a plain <c>rtspsrc</c> pipeline.
/// </summary>
[SupportedOSPlatform("linux")]
public unsafe class GStreamerRtspServerTests
{
    private const string Pattern =
        "( videotestsrc is-live=true pattern=white ! video/x-raw,format=I420,width=64,height=48,framerate=25/1 ! jpegenc ! rtpjpegpay name=pay0 pt=26 )";

    private static List<(int Width, int Height, byte Level)> Watch(string url, int frames)
    {
        using var client = GStreamerPipeline.Parse(
            $"rtspsrc location={url} latency=0 protocols=tcp ! rtpjpegdepay ! jpegdec ! videoconvert ! video/x-raw,format=BGRA ! " +
            "appsink name=sink sync=false");
        var sink = client.GetSink("sink");
        client.Play();
        var seen = new List<(int, int, byte)>();
        while (seen.Count < frames &&
               sink.TryPullVideo(TimeSpan.FromSeconds(5), (in VideoFrame f) => seen.Add((f.Format.Width, f.Format.Height, f.GetPlane(0)[0]))))
        {
        }

        return seen;
    }

    [GStreamerRtspFact]
    public void AStreamIsReadByAnRtspClient()
    {
        using var server = new GStreamerRtspServer(port: 0);
        server.AddStream("/test", Pattern);
        server.Start();

        var frames = Watch(server.Url("/test"), 5);

        server.Port.Should().BeGreaterThan(0, "port 0 asks for a free one");
        server.Url("/test").Should().Be($"rtsp://127.0.0.1:{server.Port}/test");
        frames.Should().HaveCount(5).And.OnlyContain(f => f.Width == 64 && f.Height == 48 && f.Level > 200);
    }

    [GStreamerRtspFact]
    public void FfmpegReadsTheStreamToo()
    {
        using var media = new TestMedia();
        var path = media.PathTo("from-rtsp.mkv");
        using var server = new GStreamerRtspServer(port: 0);
        server.AddStream("/test", Pattern);
        server.Start();

        FfmpegProgram.Run($"-v error -rtsp_transport tcp -i {server.Url("/test")} -frames:v 5 -c copy {path}");

        var video = new FFmpegProber().Probe(path).OfKind(MediaStreamKind.Video).Single().Video!.Value;
        (video.Width, video.Height).Should().Be((64, 48));
    }

    [GStreamerRtspFact]
    public void FramesPushedByTheApplicationAreServed()
    {
        var format = new VideoFormat(64, 48, PixelFormat.Bgra32, new Rational(25, 1));
        var stop = new CancellationTokenSource();
        Task? pushing = null;
        using var server = new GStreamerRtspServer(port: 0);
        server.AddStream(
            "/app",
            "( appsrc name=frames is-live=true do-timestamp=true ! videoconvert ! video/x-raw,format=I420 ! jpegenc ! rtpjpegpay name=pay0 pt=26 )",
            media =>
            {
                var source = media.GetSource("frames");
                source.SetFormat(format);
                pushing = Task.Run(() =>
                {
                    var data = new byte[64 * 48 * 4];
                    Array.Fill(data, (byte)128);
                    for (var i = 0; i < 250 && !stop.IsCancellationRequested; i++)
                    {
                        fixed (byte* p = data)
                        {
                            source.PushVideo(new VideoFrame(format, TimeSpan.Zero, p, 64 * 4));
                        }

                        Thread.Sleep(40);
                    }
                });
            });
        server.Start();

        var frames = Watch(server.Url("/app"), 5);
        stop.Cancel();
        pushing!.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        frames.Should().HaveCount(5).And.OnlyContain(f => f.Level > 118 && f.Level < 138);
    }

    [GStreamerRtspFact]
    public void ClientsAreCountedAndDisposeSendsThemAway()
    {
        var server = new GStreamerRtspServer(port: 0);
        server.AddStream("/test", Pattern);
        server.Start();
        using var client = GStreamerPipeline.Parse(
            $"rtspsrc location={server.Url("/test")} latency=0 protocols=tcp ! rtpjpegdepay ! appsink name=sink sync=false");
        var sink = client.GetSink("sink");
        client.Play();
        sink.TryPullData(TimeSpan.FromSeconds(5), (in GStreamerData _) => { }).Should().BeTrue();

        var watching = server.ClientCount;
        var port = server.Port;
        server.Dispose();

        watching.Should().Be(1);
        var connect = () =>
        {
            using var socket = new System.Net.Sockets.TcpClient();
            socket.Connect("127.0.0.1", port);
        };
        connect.Should().Throw<System.Net.Sockets.SocketException>("nothing listens any more");
    }

    [GStreamerRtspFact]
    public void ViewersComeAndGoEachWithAMediaOfItsOwn()
    {
        var built = 0;
        var failures = new List<string>();
        using var server = new GStreamerRtspServer(port: 0);
        server.Failed += (_, e) => failures.Add(e.Message);
        server.AddStream(
            "/own",
            "( videotestsrc name=picture is-live=true ! video/x-raw,format=I420,width=64,height=48 ! jpegenc ! rtpjpegpay name=pay0 pt=26 )",
            media =>
            {
                media.FindElement("picture")!.SetValue("pattern", "white");
                Interlocked.Increment(ref built);
            },
            shared: false);
        server.Start();

        for (var i = 0; i < 5; i++)
        {
            Watch(server.Url("/own"), 2).Should().HaveCount(2).And.OnlyContain(f => f.Level > 200);
        }

        var together = Enumerable.Range(0, 2).Select(_ => Task.Run(() => Watch(server.Url("/own"), 5))).ToArray();
        Task.WaitAll(together, TimeSpan.FromSeconds(20)).Should().BeTrue();

        together.Should().OnlyContain(t => t.Result.Count == 5);
        built.Should().Be(7);
        failures.Should().BeEmpty();
    }

    [GStreamerRtspFact]
    public void ARemovedStreamIsNoLongerThere()
    {
        using var server = new GStreamerRtspServer(port: 0);
        server.AddStream("/test", Pattern);
        server.Start();
        Watch(server.Url("/test"), 1).Should().HaveCount(1);

        server.RemoveStream("/test");

        var read = () => FfmpegProgram.Run($"-v error -rtsp_transport tcp -i {server.Url("/test")} -frames:v 1 -f null -");
        read.Should().Throw<Exception>();
    }

    [GStreamerRtspFact]
    public void APortInUseAndAPathWithoutASlashAreRefused()
    {
        using var first = new GStreamerRtspServer(port: 0);
        first.Start();
        using var second = new GStreamerRtspServer(port: first.Port);

        var taken = () => second.Start();
        var path = () => first.AddStream("test", Pattern);

        taken.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain(first.Port.ToString());
        path.Should().Throw<ArgumentException>();
    }
}
