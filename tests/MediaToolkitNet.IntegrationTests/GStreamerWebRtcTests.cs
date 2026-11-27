#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.GStreamer;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>A fact that is skipped unless GStreamer can make a WebRTC connection.</summary>
public sealed class GStreamerWebRtcFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="GStreamerWebRtcFactAttribute"/> class.</summary>
    public GStreamerWebRtcFactAttribute()
    {
        Skip = new GStreamerOnlyFactAttribute().Skip;
        if (Skip is null && OperatingSystem.IsLinux() && !GStreamerWebRtcPeer.IsAvailable)
        {
            Skip = "webrtcbin or its ICE plugin (gstreamer1.0-nice) is not installed.";
        }
    }
}

/// <summary>
/// WebRTC between two peers in one process, over the offer and answer directly and over HTTP as WHIP and WHEP do it.
/// </summary>
[SupportedOSPlatform("linux")]
public class GStreamerWebRtcTests
{
    private const string Sender =
        "videotestsrc is-live=true pattern=white ! video/x-raw,format=I420,width=64,height=48,framerate=25/1 ! " +
        "vp8enc deadline=1 keyframe-max-dist=10 ! rtpvp8pay pt=96 ! application/x-rtp,media=video,encoding-name=VP8,payload=96";

    private const string Receiver = "rtpvp8depay ! vp8dec ! videoconvert ! video/x-raw,format=BGRA ! appsink name=picture sync=false async=false";

    private const string Vp8 = "application/x-rtp,media=video,encoding-name=VP8,payload=96,clock-rate=90000";

    // Collects the brightness of the frames a peer receives.
    private static List<(int Width, byte Level)> Watch(GStreamerWebRtcPeer peer)
    {
        var seen = new List<(int, byte)>();
        peer.StreamReceived += stream =>
        {
            stream.ReceiveInto(Receiver);
            var sink = peer.Pipeline.GetSink("picture");
            sink.Element.SetValue("emit-signals", true);
            sink.Element.Connect("new-sample", _ => sink.TryPullVideo(TimeSpan.Zero, (in VideoFrame f) =>
            {
                lock (seen)
                {
                    seen.Add((f.Format.Width, f.GetPlane(0)[0]));
                }
            }));
        };
        return seen;
    }

    private static void ShouldSeeWhiteFrames(List<(int Width, byte Level)> seen)
    {
        SpinWait.SpinUntil(() => { lock (seen) { return seen.Count >= 5; } }, TimeSpan.FromSeconds(15))
            .Should().BeTrue("the media arrives");
        lock (seen)
        {
            seen.Take(5).Should().OnlyContain(f => f.Width == 64 && f.Level > 200);
        }
    }

    // Candidates are passed on as they are found, after the other side has its description.
    private static (Action Open, List<GStreamerIceCandidate> Held) Trickle(GStreamerWebRtcPeer from, GStreamerWebRtcPeer to)
    {
        var held = new List<GStreamerIceCandidate>();
        var open = false;
        from.IceCandidate += (_, candidate) =>
        {
            lock (held)
            {
                if (open)
                {
                    to.AddIceCandidate(candidate);
                }
                else
                {
                    held.Add(candidate);
                }
            }
        };

        return (() =>
        {
            lock (held)
            {
                open = true;
                held.ForEach(to.AddIceCandidate);
            }
        }, held);
    }

    [GStreamerWebRtcFact]
    public async Task TwoPeersExchangeAnOfferAndAnAnswerAndTheMediaArrives()
    {
        using var caller = new GStreamerWebRtcPeer();
        using var callee = new GStreamerWebRtcPeer();
        var seen = Watch(callee);
        var failures = new List<string>();
        caller.Failed += (_, e) => failures.Add(e.Message);
        callee.Failed += (_, e) => failures.Add(e.Message);
        caller.AddSender(Sender);
        var toCallee = Trickle(caller, callee);
        var toCaller = Trickle(callee, caller);
        caller.Start();
        callee.Start();

        var offer = await caller.CreateOfferAsync();
        var answer = await callee.AcceptOfferAsync(offer);
        await caller.AcceptAnswerAsync(answer);
        toCallee.Open();
        toCaller.Open();

        offer.Should().Contain("m=video").And.Contain("VP8");
        answer.Should().Contain("a=recvonly");
        ShouldSeeWhiteFrames(seen);
        caller.ConnectionState.Should().Be(GStreamerWebRtcState.Connected);
        failures.Should().BeEmpty();
    }

    // An endpoint that answers one WHIP or WHEP request with a peer of its own, with all its candidates in the answer.
    private static (Uri Url, Task<(string? Authorization, string ContentType)> Served, HttpListener Listener) Endpoint(
        GStreamerWebRtcPeer peer, int status = 201)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var served = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using var body = new StreamReader(context.Request.InputStream);
            var offer = await body.ReadToEndAsync();
            context.Response.StatusCode = status;
            if (status == 201)
            {
                await peer.AcceptOfferAsync(offer);
                var answer = System.Text.Encoding.UTF8.GetBytes(await peer.GetCompleteDescriptionAsync());
                context.Response.ContentType = "application/sdp";
                context.Response.Headers["Location"] = "/session/1";
                await context.Response.OutputStream.WriteAsync(answer);
            }

            context.Response.Close();
            return (context.Request.Headers["Authorization"], context.Request.ContentType ?? string.Empty);
        });
        return (new Uri($"http://127.0.0.1:{port}/whip"), served, listener);
    }

    [GStreamerWebRtcFact]
    public async Task MediaIsPublishedToAWhipEndpoint()
    {
        using var server = new GStreamerWebRtcPeer();
        var seen = Watch(server);
        server.Start();
        var (url, served, listener) = Endpoint(server);
        using var _ = listener;
        using var publisher = new GStreamerWebRtcPeer();
        publisher.AddSender(Sender);
        publisher.Start();

        var session = await publisher.ExchangeAsync(url, bearerToken: "secret-token");

        var request = await served;
        request.Authorization.Should().Be("Bearer secret-token");
        request.ContentType.Should().Be("application/sdp");
        session.Should().Be(new Uri(url, "/session/1"));
        ShouldSeeWhiteFrames(seen);
    }

    [GStreamerWebRtcFact]
    public async Task MediaIsPlayedFromAWhepEndpoint()
    {
        using var server = new GStreamerWebRtcPeer();
        server.AddSender(Sender);
        server.Start();
        var (url, served, listener) = Endpoint(server);
        using var _ = listener;
        using var viewer = new GStreamerWebRtcPeer();
        var seen = Watch(viewer);
        viewer.AddReceiver(Vp8);
        viewer.Start();

        await viewer.ExchangeAsync(url);

        (await served).Authorization.Should().BeNull();
        ShouldSeeWhiteFrames(seen);
    }

    [GStreamerWebRtcFact]
    public async Task DisposingAPeerInTheMiddleOfNegotiatingEndsItCleanly()
    {
        for (var i = 0; i < 10; i++)
        {
            var peer = new GStreamerWebRtcPeer();
            peer.AddSender(Sender);
            peer.Start();
            var offer = peer.CreateOfferAsync();
            await Task.Delay(i * 15);

            peer.Dispose();

            var finished = await Task.WhenAny(offer, Task.Delay(TimeSpan.FromSeconds(3)));
            finished.Should().BeSameAs(offer, "a disposed peer leaves nothing waiting");
            _ = offer.Exception;
        }
    }

    [GStreamerWebRtcFact]
    public async Task ARefusalOfTheEndpointAndADescriptionThatIsNoneAreReported()
    {
        using var server = new GStreamerWebRtcPeer();
        server.Start();
        var (url, served, listener) = Endpoint(server, status: 403);
        using var _ = listener;
        using var publisher = new GStreamerWebRtcPeer();
        publisher.AddSender(Sender);
        publisher.Start();

        var refused = () => publisher.ExchangeAsync(url, bearerToken: "secret-token");
        var nonsense = () => server.AcceptOfferAsync("this is no session description");

        (await refused.Should().ThrowAsync<MediaToolkitNetException>()).Which.Message
            .Should().Contain("403").And.NotContain("secret-token");
        await nonsense.Should().ThrowAsync<MediaToolkitNetException>();
        await served;
    }
}
