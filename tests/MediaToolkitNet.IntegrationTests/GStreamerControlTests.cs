#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.GStreamer;
using MediaToolkitNet.GStreamer.Native;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>Frame stepping, clocks and latency, queries, and events sent into a pipeline.</summary>
[SupportedOSPlatform("linux")]
public unsafe class GStreamerControlTests
{
    private const string Video = "video/x-raw,format=I420,width=64,height=48,framerate=25/1";

    [GStreamerOnlyFact]
    public void SteppingAPausedPipelineMovesItByFrames()
    {
        using var pipeline = GStreamerPipeline.Parse($"videotestsrc ! {Video} ! fakesink sync=true");
        pipeline.Pause();
        pipeline.WaitForState(TimeSpan.FromSeconds(5));

        pipeline.Step().Should().BeTrue();
        pipeline.WaitForState(TimeSpan.FromSeconds(5));
        var one = pipeline.Position;
        pipeline.Step(5).Should().BeTrue();
        pipeline.WaitForState(TimeSpan.FromSeconds(5));

        one.Should().Be(TimeSpan.FromMilliseconds(40));
        pipeline.Position.Should().Be(TimeSpan.FromMilliseconds(240));
        pipeline.State.Should().Be(GstState.Paused);
    }

    [GStreamerOnlyFact]
    public void APipelineSynchronisedWithAnotherSharesItsClockAndBaseTime()
    {
        using var first = GStreamerPipeline.Parse("audiotestsrc is-live=true ! fakesink sync=true");
        using var second = GStreamerPipeline.Parse("audiotestsrc is-live=true ! fakesink sync=true");
        first.RunningTime.Should().BeNull("a stopped pipeline has no running time");
        first.Play();
        Thread.Sleep(300);

        second.SynchronizeWith(first);
        second.Play();
        Thread.Sleep(100);

        second.BaseTime.Should().Be(first.BaseTime);
        first.RunningTime!.Value.Should().BeGreaterThan(TimeSpan.FromMilliseconds(300));
        (second.RunningTime!.Value - first.RunningTime!.Value).Duration().Should().BeLessThan(TimeSpan.FromMilliseconds(50));
        second.ClockTime.Should().NotBeNull();
    }

    [GStreamerOnlyFact]
    public void ALatencySetOnALivePipelineDelaysWhatItRenders()
    {
        using var pipeline = GStreamerPipeline.Parse($"videotestsrc is-live=true ! {Video} ! fakesink name=sink sync=true signal-handoffs=true");
        using var first = new ManualResetEventSlim();
        pipeline.FindElement("sink")!.Connect("handoff", _ => first.Set());
        pipeline.SetLatency(TimeSpan.FromMilliseconds(600));
        var clock = Stopwatch.StartNew();

        pipeline.Play();

        first.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(500));
        pipeline.RecalculateLatency().Should().BeTrue();
    }

    [GStreamerOnlyFact]
    public void ThePlayingPipelineReportsItsLatency()
    {
        using var synchronised = GStreamerPipeline.Parse("audiotestsrc is-live=true ! fakesink sync=true");
        using var unsynchronised = GStreamerPipeline.Parse("fakesrc ! fakesink sync=false");
        synchronised.Play();
        unsynchronised.Play();
        synchronised.WaitForState(TimeSpan.FromSeconds(5));
        unsynchronised.WaitForState(TimeSpan.FromSeconds(5));

        var latency = synchronised.QueryLatency()!.Value;

        latency.IsLive.Should().BeTrue();
        latency.Minimum.Should().BeGreaterThan(TimeSpan.Zero, "the live source delivers a block when it is complete");
        unsynchronised.QueryLatency()!.Value.IsLive.Should().BeFalse("no sink keeps to the clock");
    }

    [GStreamerOnlyFact]
    public void AFileSaysWhereItCanSeekAndALiveSourceThatItCannot()
    {
        using var media = new TestMedia();
        var path = media.PathTo("two.mkv");
        using (var writer = GStreamerPipeline.Parse(
                   $"videotestsrc num-buffers=50 ! {Video} ! jpegenc ! matroskamux ! filesink location={path}"))
        {
            writer.Play();
            writer.WaitForCompletion(TimeSpan.FromSeconds(10)).Should().BeTrue();
        }

        using var file = GStreamerPipeline.Parse($"filesrc location={path} ! matroskademux ! fakesink");
        using var live = GStreamerPipeline.Parse("fakesrc is-live=true ! fakesink");
        file.Pause();
        file.WaitForState(TimeSpan.FromSeconds(5));
        live.Play();
        live.WaitForState(TimeSpan.FromSeconds(5));

        var range = file.QuerySeeking()!.Value;

        range.CanSeek.Should().BeTrue();
        range.Start.Should().Be(TimeSpan.Zero);
        range.End!.Value.Should().BeCloseTo(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));
        (live.QuerySeeking()?.CanSeek ?? false).Should().BeFalse();
    }

    [GStreamerOnlyFact]
    public void ABufferingQueueSaysHowFullItIs()
    {
        using var buffered = GStreamerPipeline.Parse("audiotestsrc ! queue2 name=queue use-buffering=true ! fakesink sync=true");
        using var plain = GStreamerPipeline.Parse("audiotestsrc ! fakesink");
        buffered.Play();
        buffered.WaitForState(TimeSpan.FromSeconds(5));
        Thread.Sleep(200);

        var buffering = buffered.QueryBuffering()!.Value;

        buffering.Percent.Should().BeInRange(0, 100);
        plain.Play();
        plain.WaitForState(TimeSpan.FromSeconds(5));
        plain.QueryBuffering()!.Value.IsBuffering.Should().BeFalse("a source answers too, and it is not waiting for data");
    }

    [GStreamerOnlyFact]
    public void ACustomEventReachesAProbeWithItsFields()
    {
        using var pipeline = GStreamerPipeline.Parse("audiotestsrc is-live=true ! identity name=id ! fakesink");
        var identity = pipeline.FindElement("id")!;
        var seen = new List<(GstEventType? Type, string Name, int Number)>();
        identity.AddProbe("src", GstPadProbeType.EventDownstream | GstPadProbeType.EventUpstream, info =>
        {
            if (info.Details is { } details)
            {
                lock (seen)
                {
                    seen.Add((info.Event, details.Name, details.Get<int>("number")));
                }
            }

            return GstPadProbeReturn.Ok;
        });
        pipeline.Play();
        pipeline.WaitForState(TimeSpan.FromSeconds(5));

        identity.SendEvent(GStreamerStructure.Parse("down, number=(int)1")).Should().BeTrue();
        // The source has no use for it, so nothing answers that it took it; it travels up all the same.
        identity.SendEvent(GStreamerStructure.Parse("up, number=(int)2"), GstEventType.CustomUpstream).Should().BeFalse();

        SpinWait.SpinUntil(() => { lock (seen) { return seen.Count >= 2; } }, TimeSpan.FromSeconds(3));
        lock (seen)
        {
            seen.Should().BeEquivalentTo(
            [
                ((GstEventType?)GstEventType.CustomDownstream, "down", 1),
                ((GstEventType?)GstEventType.CustomUpstream, "up", 2),
            ]);
        }
    }

    [GStreamerOnlyFact]
    public void AForcedKeyFrameIsTheNextOneEncoded()
    {
        using var pipeline = GStreamerPipeline.Parse(
            "appsrc name=src ! videoconvert ! vp8enc name=enc keyframe-max-dist=1000 deadline=1 lag-in-frames=0 ! appsink name=sink sync=false");
        var source = pipeline.GetSource("src");
        var sink = pipeline.GetSink("sink");
        var encoder = pipeline.FindElement("enc")!;
        var format = new VideoFormat(64, 48, PixelFormat.Bgra32, new Rational(25, 1));
        source.SetFormat(format);
        pipeline.Play();
        var data = new byte[64 * 48 * 4];
        var keys = new List<bool>();

        for (var i = 0; i < 10; i++)
        {
            if (i == 5)
            {
                encoder.ForceKeyFrame().Should().BeTrue();
            }

            fixed (byte* p = data)
            {
                source.PushVideo(new VideoFrame(format, TimeSpan.FromMilliseconds(40 * i), p, 64 * 4));
            }

            // Each packet is taken before the next frame goes in, so the request cannot overtake an earlier frame.
            sink.TryPullData(TimeSpan.FromSeconds(3), (in GStreamerData packet) => keys.Add(packet.IsKeyFrame)).Should().BeTrue();
        }

        keys.Select((key, index) => (key, index)).Where(k => k.key).Select(k => k.index).Should().Equal(0, 5);
    }
}
