#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.GStreamer;
using MediaToolkitNet.GStreamer.Native;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>Inputs that come and go, animated properties, switching sources and holding back a recording.</summary>
[SupportedOSPlatform("linux")]
public unsafe class GStreamerProcessingTests
{
    private static readonly VideoFormat Small = new(32, 32, PixelFormat.Bgra32, new Rational(25, 1));
    private static readonly AudioFormat Mono = new(8000, 1, SampleFormat.S16);

    private static void PushColour(GStreamerCompositor compositor, int layer, byte level, int frame)
    {
        var data = new byte[32 * 32 * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = data[i + 1] = data[i + 2] = level;
            data[i + 3] = 255;
        }

        fixed (byte* p = data)
        {
            compositor.Push(layer, new VideoFrame(Small, TimeSpan.FromMilliseconds(40 * frame), p, 32 * 4));
        }
    }

    [GStreamerOnlyFact]
    public void ALayerIsAddedAndRemovedWhileTheCompositorRuns()
    {
        using var compositor = new GStreamerCompositor(Small);
        var under = compositor.AddLayer(Small);
        var seen = new SortedDictionary<long, byte>();
        compositor.FrameComposed += (in VideoFrame f) =>
        {
            lock (seen)
            {
                seen[(long)f.Timestamp.TotalMilliseconds / 40] = f.GetPlane(0)[0];
            }
        };
        var failures = new List<string>();
        compositor.Failed += (_, e) => failures.Add(e.Message);
        compositor.Start();
        for (var i = 0; i < 10; i++)
        {
            PushColour(compositor, under, 50, i);
        }

        var over = compositor.AddLayer(Small);
        for (var i = 10; i < 20; i++)
        {
            PushColour(compositor, under, 50, i);
            PushColour(compositor, over, 200, i);
        }

        WaitUntil(() => { lock (seen) { return seen.ContainsKey(18); } });
        compositor.RemoveLayer(over);
        for (var i = 20; i < 30; i++)
        {
            PushColour(compositor, under, 50, i);
        }

        WaitUntil(() => { lock (seen) { return seen.ContainsKey(27); } });
        lock (seen)
        {
            ((int)seen[5]).Should().Be(50);
            ((int)seen[15]).Should().Be(200, "the new layer is on top");
            ((int)seen[27]).Should().Be(50, "it is gone again");
        }

        var gone = () => PushColour(compositor, over, 200, 31);
        gone.Should().Throw<ArgumentOutOfRangeException>();
        failures.Should().BeEmpty();
    }

    [GStreamerOnlyFact]
    public void AnInputIsAddedAndRemovedWhileTheMixerRuns()
    {
        using var mixer = new GStreamerAudioMixer(Mono);
        var first = mixer.AddInput(Mono);
        var levels = new SortedDictionary<long, short>();
        mixer.FrameMixed += (in AudioFrame f) =>
        {
            var samples = MemoryMarshal.Cast<byte, short>(f.Interleaved);
            lock (levels)
            {
                levels[(long)f.Timestamp.TotalMilliseconds] = samples[samples.Length / 2];
            }
        };
        var failures = new List<string>();
        mixer.Failed += (_, e) => failures.Add(e.Message);
        mixer.Start();

        void Push(int input, short value, int block)
        {
            var samples = new short[800];
            Array.Fill(samples, value);
            fixed (short* p = samples)
            {
                mixer.Push(input, new AudioFrame(Mono, TimeSpan.FromMilliseconds(100 * block), samples.Length, (byte*)p));
            }
        }

        for (var i = 0; i < 5; i++)
        {
            Push(first, 1000, i);
        }

        var second = mixer.AddInput(Mono);
        for (var i = 5; i < 10; i++)
        {
            Push(first, 1000, i);
            Push(second, 3000, i);
        }

        short At(long milliseconds)
        {
            lock (levels)
            {
                return levels.LastOrDefault(l => l.Key <= milliseconds).Value;
            }
        }

        WaitUntil(() => { lock (levels) { return levels.Keys.Any(k => k >= 900); } });
        mixer.RemoveInput(second);
        for (var i = 10; i < 15; i++)
        {
            Push(first, 1000, i);
        }

        WaitUntil(() => { lock (levels) { return levels.Keys.Any(k => k >= 1350); } });
        ((int)At(250)).Should().BeCloseTo(1000, 20);
        ((int)At(750)).Should().BeCloseTo(4000, 40);
        ((int)At(1250)).Should().BeCloseTo(1000, 20);
        failures.Should().BeEmpty();
    }

    [GStreamerOnlyFact]
    public void AnAnimatedPropertyChangesWithTheStream()
    {
        using var pipeline = GStreamerPipeline.Parse(
            "audiotestsrc num-buffers=10 samplesperbuffer=800 wave=square ! audio/x-raw,format=S16LE,rate=8000,channels=1 ! " +
            "volume name=fader ! appsink name=sink sync=false");
        var fader = pipeline.FindElement("fader")!;
        fader.Animate("volume", [(TimeSpan.Zero, 1.0), (TimeSpan.FromSeconds(1), 0.0)]);
        var sink = pipeline.GetSink("sink");
        pipeline.Play();

        var peaks = new List<int>();
        while (sink.TryPullAudio(TimeSpan.FromSeconds(3), (in AudioFrame f) =>
                   peaks.Add(Math.Abs((int)MemoryMarshal.Cast<byte, short>(f.Interleaved)[0]))))
        {
        }

        peaks.Should().HaveCount(10).And.BeInDescendingOrder();
        peaks[5].Should().BeCloseTo(peaks[0] / 2, 300, "half way through the fade");
        var still = () => fader.Animate("name", [(TimeSpan.Zero, 1.0)]);
        still.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("name");
    }

    [GStreamerOnlyFact]
    public void AnAnimatedPadPropertyFadesALayer()
    {
        using var pipeline = GStreamerPipeline.Parse(
            "compositor name=mix background=black ! video/x-raw,format=BGRA,width=32,height=32 ! appsink name=sink sync=false " +
            "videotestsrc num-buffers=10 pattern=white ! video/x-raw,width=32,height=32,framerate=10/1 ! mix.sink_0");
        pipeline.FindElement("mix")!.AnimatePad("sink_0", "alpha", [(TimeSpan.Zero, 1.0), (TimeSpan.FromSeconds(1), 0.0)]);
        var sink = pipeline.GetSink("sink");
        pipeline.Play();

        var levels = new List<int>();
        while (sink.TryPullVideo(TimeSpan.FromSeconds(3), (in VideoFrame f) => levels.Add(f.GetPlane(0)[0])))
        {
        }

        levels.Should().HaveCount(10).And.BeInDescendingOrder();
        levels[0].Should().BeGreaterThan(240);
        levels[9].Should().BeLessThan(50);
    }

    [GStreamerOnlyFact]
    public void TheActivePadOfASelectorSwitchesTheSource()
    {
        using var pipeline = GStreamerPipeline.Parse(
            "input-selector name=select ! appsink name=sink " +
            "videotestsrc is-live=true pattern=white ! video/x-raw,format=BGRA,width=16,height=16 ! select.sink_0 " +
            "videotestsrc is-live=true pattern=black ! video/x-raw,format=BGRA,width=16,height=16 ! select.sink_1");
        var selector = pipeline.FindElement("select")!;
        var sink = pipeline.GetSink("sink");
        sink.DropWhenFull(true, 1);
        selector.SetActivePad("sink_0");
        pipeline.Play();

        byte Next()
        {
            byte level = 0;
            sink.TryPullVideo(TimeSpan.FromSeconds(3), (in VideoFrame f) => level = f.GetPlane(0)[0]).Should().BeTrue();
            return level;
        }

        Next().Should().BeGreaterThan(200);
        selector.SetActivePad("sink_1");
        SpinWait.SpinUntil(() => Next() < 30, TimeSpan.FromSeconds(3)).Should().BeTrue("the other source takes over");
        var missing = () => selector.SetActivePad("sink_9");
        missing.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("sink_9");
    }

    [GStreamerOnlyFact]
    public void ThePreRecordGateStartsWithTheLastSecondFromAKeyFrame()
    {
        using var pipeline = GStreamerPipeline.Parse(
            "videotestsrc is-live=true ! video/x-raw,width=64,height=48,framerate=25/1 ! " +
            "vp8enc deadline=1 keyframe-max-dist=5 lag-in-frames=0 ! queue name=pre ! appsink name=sink sync=false async=false");
        using var gate = new GStreamerPreRecordGate(pipeline.FindElement("pre")!, TimeSpan.FromSeconds(1));
        var sink = pipeline.GetSink("sink");
        pipeline.Play();
        Thread.Sleep(2500);
        sink.TryPullData(TimeSpan.Zero, (in GStreamerData _) => { }).Should().BeFalse("the gate is shut");
        var opened = pipeline.RunningTime!.Value;

        gate.Open();

        var packets = new List<(TimeSpan Time, bool KeyFrame)>();
        while (packets.Count < 40 &&
               sink.TryPullData(TimeSpan.FromSeconds(3), (in GStreamerData d) => packets.Add((d.Timestamp!.Value, d.IsKeyFrame))))
        {
        }

        packets.Should().HaveCount(40);
        packets[0].KeyFrame.Should().BeTrue();
        (opened - packets[0].Time).Should().BeGreaterThan(TimeSpan.FromMilliseconds(700)).And.BeLessThan(TimeSpan.FromMilliseconds(1400));
        packets.Zip(packets.Skip(1), (a, b) => (b.Time - a.Time).TotalMilliseconds)
            .Should().OnlyContain(gap => gap > 0 && gap < 100, "nothing stale is left in front and nothing is missing");
    }

    [GStreamerOnlyFact]
    public void AnElementAddedToAPipelineStillOnItsWayStaysStopped()
    {
        using var pipeline = GStreamerPipeline.Parse("appsrc name=src ! fakesink");
        var source = pipeline.GetSource("src");
        source.SetCaps("application/x-mediatoolkitnet-test");
        pipeline.Play(); // asynchronous: it is on its way until the first buffer prerolls the sink

        var late = pipeline.Add("identity", "late");
        source.Push([1], TimeSpan.Zero);
        pipeline.WaitForState(TimeSpan.FromSeconds(5));
        Thread.Sleep(200);

        late.State.Should().Be(GstState.Null, "it has not been linked or brought up yet");
        late.SyncStateWithPipeline();
        pipeline.WaitForState(TimeSpan.FromSeconds(5));
        late.State.Should().Be(GstState.Playing);
    }

    private static void WaitUntil(Func<bool> condition) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)).Should().BeTrue("what was pushed comes out");
}
