#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.GStreamer;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>A theory that is skipped unless gst-rtsp-server, GStreamer and FFmpeg are usable.</summary>
public sealed class GStreamerRtspTheoryAttribute : TheoryAttribute
{
    /// <summary>Initializes a new instance of the <see cref="GStreamerRtspTheoryAttribute"/> class.</summary>
    public GStreamerRtspTheoryAttribute() => Skip = new GStreamerRtspFactAttribute().Skip;
}

/// <summary>A theory that is skipped unless a WebRTC connection can be made.</summary>
public sealed class GStreamerWebRtcTheoryAttribute : TheoryAttribute
{
    /// <summary>Initializes a new instance of the <see cref="GStreamerWebRtcTheoryAttribute"/> class.</summary>
    public GStreamerWebRtcTheoryAttribute() => Skip = new GStreamerWebRtcFactAttribute().Skip;
}

/// <summary>The codecs and transports of the RTSP server and of WebRTC.</summary>
[SupportedOSPlatform("linux")]
public class GStreamerStreamCodecTests
{
    private const string Picture = "videotestsrc is-live=true pattern=white ! video/x-raw,format=I420,width=320,height=240,framerate=25/1 ! ";
    private const string Tone = "audiotestsrc is-live=true wave=square ! audio/x-raw,rate=48000,channels=2 ! audioconvert ! ";

    private static string VideoPay(string codec, string pay) => codec switch
    {
        "H264" => $"x264enc tune=zerolatency key-int-max=10 ! rtph264pay name={pay} pt=96 config-interval=-1",
        "H265" => $"x265enc tune=zerolatency key-int-max=10 ! rtph265pay name={pay} pt=96 config-interval=-1",
        "VP8" => $"vp8enc deadline=1 keyframe-max-dist=10 ! rtpvp8pay name={pay} pt=96",
        "VP9" => $"vp9enc deadline=1 cpu-used=8 keyframe-max-dist=10 ! rtpvp9pay name={pay} pt=96",
        _ => throw new ArgumentException(codec),
    };

    // ------------------------------------------------------------------ RTSP

    // Read by the ffmpeg program and judged by the FFmpeg prober, neither of them GStreamer.
    [GStreamerRtspTheory]
    [InlineData("H264", "h264", "udp")]
    [InlineData("H265", "hevc", "udp")]
    [InlineData("VP8", "vp8", "udp")]
    [InlineData("H264", "h264", "tcp")]
    public void AVideoCodecIsServedAndReadByFfmpeg(string name, string codec, string transport)
    {
        using var media = new TestMedia();
        var path = media.PathTo("read.mkv");
        using var server = new GStreamerRtspServer(port: 0);
        server.AddStream("/video", $"( {Picture}{VideoPay(name, "pay0")} )");
        server.Start();

        FfmpegProgram.Run($"-v error -rtsp_transport {transport} -i {server.Url("/video")} -t 1 -c copy {path}");

        var video = new FFmpegProber().Probe(path).OfKind(MediaStreamKind.Video).Single();
        video.CodecName.Should().Be(codec);
        (video.Video!.Value.Width, video.Video.Value.Height).Should().Be((320, 240));
    }

    // FFmpeg's VP9 RTP depacketizer, which it calls experimental, opens this stream and then writes no packet of it
    // (7.1), so VP9 is read back by rtspsrc.
    [GStreamerRtspFact]
    public void Vp9IsServedAndDecodedByRtspsrc()
    {
        using var server = new GStreamerRtspServer(port: 0);
        server.AddStream("/video", $"( {Picture}{VideoPay("VP9", "pay0")} )");
        server.Start();
        using var client = GStreamerPipeline.Parse(
            $"rtspsrc location={server.Url("/video")} latency=200 protocols=udp ! rtpvp9depay ! vp9dec ! videoconvert ! " +
            "video/x-raw,format=BGRA ! appsink name=sink sync=false");
        var sink = client.GetSink("sink");
        client.Play();
        var frames = new List<(int Width, byte Level)>();
        while (frames.Count < 10 &&
               sink.TryPullVideo(TimeSpan.FromSeconds(5), (in VideoFrame f) => frames.Add((f.Format.Width, f.GetPlane(0)[0]))))
        {
        }

        frames.Should().HaveCount(10).And.OnlyContain(f => f.Width == 320 && f.Level > 200);
    }

    [GStreamerRtspTheory]
    [InlineData("opusenc ! rtpopuspay name=pay1 pt=97", MediaCodec.Opus)]
    [InlineData("avenc_aac ! rtpmp4gpay name=pay1 pt=97", MediaCodec.Aac)]
    public void SoundIsServedBesideThePicture(string sound, MediaCodec codec)
    {
        using var media = new TestMedia();
        var path = media.PathTo("both.mkv");
        using var server = new GStreamerRtspServer(port: 0);
        server.AddStream("/both", $"( {Picture}{VideoPay("H264", "pay0")} {Tone}{sound} )");
        server.Start();

        FfmpegProgram.Run($"-v error -rtsp_transport udp -i {server.Url("/both")} -t 1 -c copy {path}");

        var info = new FFmpegProber().Probe(path);
        info.OfKind(MediaStreamKind.Video).Single().Codec.Should().Be(MediaCodec.H264);
        var audio = info.OfKind(MediaStreamKind.Audio).Single();
        audio.Codec.Should().Be(codec);
        audio.Audio!.Value.SampleRate.Should().Be(48000);
    }

    // True when frames come through a plain rtspsrc restricted to the transport.
    private static bool Arrives(string url, string transport)
    {
        using var client = GStreamerPipeline.Parse(
            $"rtspsrc location={url} latency=200 protocols={transport} timeout=2000000 tcp-timeout=2000000 ! rtpvp8depay ! vp8dec ! " +
            "videoconvert ! video/x-raw,format=BGRA ! appsink name=sink sync=false");
        var sink = client.GetSink("sink");
        try
        {
            client.Play();
        }
        catch (Abstractions.MediaToolkitNetException)
        {
            return false; // a refused transport can fail the start itself
        }

        byte level = 0;
        return sink.TryPullVideo(TimeSpan.FromSeconds(6), (in VideoFrame f) => level = f.GetPlane(0)[0]) && level > 200;
    }

    [GStreamerRtspFact]
    public void TheTransportsOfAStreamCanBeLimited()
    {
        using var server = new GStreamerRtspServer(port: 0);
        server.AddStream("/any", $"( {Picture}{VideoPay("VP8", "pay0")} )");
        server.AddStream("/tcp", $"( {Picture}{VideoPay("VP8", "pay0")} )", transports: GStreamerRtspTransports.Tcp);
        server.AddStream("/udp", $"( {Picture}{VideoPay("VP8", "pay0")} )", transports: GStreamerRtspTransports.Udp);
        server.Start();

        Arrives(server.Url("/any"), "udp").Should().BeTrue();
        Arrives(server.Url("/any"), "tcp").Should().BeTrue();
        Arrives(server.Url("/tcp"), "tcp").Should().BeTrue();
        Arrives(server.Url("/tcp"), "udp").Should().BeFalse("the stream is offered over TCP only");
        Arrives(server.Url("/udp"), "udp").Should().BeTrue();
        Arrives(server.Url("/udp"), "tcp").Should().BeFalse("the stream is offered over UDP only");
    }

    // ------------------------------------------------------------------ WebRTC

    private static string RtpCaps(string media, string codec, int payload) =>
        $"application/x-rtp,media={media},encoding-name={codec},payload={payload}";

    private static string Receiver(string codec) => codec switch
    {
        "H264" => "rtph264depay ! avdec_h264",
        "VP9" => "rtpvp9depay ! vp9dec",
        "VP8" => "rtpvp8depay ! vp8dec",
        _ => throw new ArgumentException(codec),
    } + " ! videoconvert ! video/x-raw,format=BGRA ! appsink name=picture sync=false async=false emit-signals=true";

    private sealed class Seen
    {
        public List<byte> Levels { get; } = [];

        public List<int> Peaks { get; } = [];

        public List<string> Codecs { get; } = [];
    }

    // Takes whatever arrives into a decoder chosen by the codec the stream's caps name.
    private static Seen Watch(GStreamerWebRtcPeer peer)
    {
        var seen = new Seen();
        peer.StreamReceived += stream =>
        {
            var codec = stream.Caps!.Structures[0].Get<string>("encoding-name");
            lock (seen)
            {
                seen.Codecs.Add(codec);
            }

            if (codec == "OPUS")
            {
                stream.ReceiveInto("rtpopusdepay ! opusdec ! audioconvert ! audio/x-raw,format=S16LE ! " +
                                   "appsink name=sound sync=false async=false emit-signals=true");
                var sink = peer.Pipeline.GetSink("sound");
                sink.Element.Connect("new-sample", _ => sink.TryPullAudio(TimeSpan.Zero, (in AudioFrame f) =>
                {
                    var peak = 0;
                    foreach (var sample in MemoryMarshal.Cast<byte, short>(f.Interleaved))
                    {
                        peak = Math.Max(peak, Math.Abs((int)sample));
                    }

                    lock (seen)
                    {
                        seen.Peaks.Add(peak);
                    }
                }));
            }
            else
            {
                stream.ReceiveInto(Receiver(codec));
                var sink = peer.Pipeline.GetSink("picture");
                sink.Element.Connect("new-sample", _ => sink.TryPullVideo(TimeSpan.Zero, (in VideoFrame f) =>
                {
                    lock (seen)
                    {
                        seen.Levels.Add(f.GetPlane(0)[0]);
                    }
                }));
            }
        };
        return seen;
    }

    private static async Task Connect(GStreamerWebRtcPeer caller, GStreamerWebRtcPeer callee)
    {
        // Complete descriptions, with every candidate in them, so there is nothing to trickle.
        await caller.CreateOfferAsync();
        await callee.AcceptOfferAsync(await caller.GetCompleteDescriptionAsync());
        await caller.AcceptAnswerAsync(await callee.GetCompleteDescriptionAsync());
    }

    private static string WebRtcVideo(string codec) => Picture + codec switch
    {
        "H264" => "x264enc tune=zerolatency key-int-max=10 ! video/x-h264,profile=constrained-baseline ! rtph264pay config-interval=-1 pt=96",
        "VP9" => "vp9enc deadline=1 cpu-used=8 keyframe-max-dist=10 ! rtpvp9pay pt=96",
        "VP8" => "vp8enc deadline=1 keyframe-max-dist=10 ! rtpvp8pay pt=96",
        _ => throw new ArgumentException(codec),
    } + " ! " + RtpCaps("video", codec, 96);

    private static bool Enough(Seen seen, bool sound)
    {
        lock (seen)
        {
            return seen.Levels.Count >= 5 && (!sound || seen.Peaks.Count >= 5);
        }
    }

    [GStreamerWebRtcTheory]
    [InlineData("H264")]
    [InlineData("VP9")]
    public async Task AVideoCodecCrossesAWebRtcConnection(string codec)
    {
        using var caller = new GStreamerWebRtcPeer();
        using var callee = new GStreamerWebRtcPeer();
        var seen = Watch(callee);
        caller.AddSender(WebRtcVideo(codec));
        caller.Start();
        callee.Start();

        await Connect(caller, callee);

        SpinWait.SpinUntil(() => Enough(seen, sound: false), TimeSpan.FromSeconds(15)).Should().BeTrue("the picture arrives");
        lock (seen)
        {
            seen.Codecs.Should().Equal(codec);
            seen.Levels.Take(5).Should().OnlyContain(level => level > 200);
        }
    }

    [GStreamerWebRtcFact]
    public async Task APictureAndOpusSoundShareAConnection()
    {
        using var caller = new GStreamerWebRtcPeer();
        using var callee = new GStreamerWebRtcPeer();
        var seen = Watch(callee);
        caller.AddSender(WebRtcVideo("VP8"));
        caller.AddSender(Tone + "opusenc ! rtpopuspay pt=111 ! " + RtpCaps("audio", "OPUS", 111));
        caller.Start();
        callee.Start();

        await Connect(caller, callee);

        SpinWait.SpinUntil(() => Enough(seen, sound: true), TimeSpan.FromSeconds(15)).Should().BeTrue("both arrive");
        lock (seen)
        {
            seen.Codecs.Should().BeEquivalentTo(["VP8", "OPUS"]);
            seen.Peaks.Skip(2).Take(3).Should().OnlyContain(peak => peak > 3000, "the tone is loud");
        }
    }
}
