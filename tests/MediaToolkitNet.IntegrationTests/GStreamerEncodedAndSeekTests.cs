#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.GStreamer;
using MediaToolkitNet.GStreamer.Native;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>Encoded data out of <c>appsink</c>, and seeks with a range, a rate and flags.</summary>
[SupportedOSPlatform("linux")]
public class GStreamerEncodedAndSeekTests
{
    private const string TenFrames = "videotestsrc num-buffers=10 ! video/x-raw,format=I420,width=64,height=48,framerate=25/1 ! ";

    private sealed record Packet(int Size, long? Milliseconds, long? Decode, long? Duration, bool KeyFrame, bool Header, string Media);

    private static List<Packet> Encode(string encoder)
    {
        using var pipeline = GStreamerPipeline.Parse(TenFrames + encoder + " ! appsink name=sink sync=false");
        var sink = pipeline.GetSink("sink");
        pipeline.Play();
        var packets = new List<Packet>();
        while (sink.TryPullData(TimeSpan.FromSeconds(3), (in GStreamerData data) => packets.Add(new Packet(
                   data.Bytes.Length,
                   (long?)data.Timestamp?.TotalMilliseconds,
                   (long?)data.DecodeTimestamp?.TotalMilliseconds,
                   (long?)data.Duration?.TotalMilliseconds,
                   data.IsKeyFrame,
                   data.IsHeader,
                   data.Caps!.Structures[0].Name))))
        {
        }

        sink.IsEndOfStream.Should().BeTrue();
        return packets;
    }

    [GStreamerOnlyFact]
    public void EncodedPacketsComeWithTheirTimesAndKeyFrames()
    {
        var packets = Encode("vp8enc keyframe-max-dist=5 deadline=1");

        packets.Should().HaveCount(10);
        packets.Should().OnlyContain(p => p.Size > 0 && p.Media == "video/x-vp8" && p.Duration == 40);
        packets.Select(p => p.Milliseconds).Should().Equal(Enumerable.Range(0, 10).Select(i => (long?)(i * 40)));
        packets[0].KeyFrame.Should().BeTrue();
        packets.Count(p => p.KeyFrame).Should().BeInRange(2, 4);
    }

    [GStreamerOnlyFact]
    public void StreamHeadersAreMarked()
    {
        var packets = Encode("theoraenc");

        packets.Take(3).Should().OnlyContain(p => p.Header, "a Theora stream starts with three header packets");
        packets.Skip(3).Should().HaveCount(10).And.OnlyContain(p => !p.Header);
    }

    [GStreamerOnlyFact]
    public void ReorderedFramesHaveADecodeTimeOfTheirOwn()
    {
        var packets = Encode("x264enc bframes=2 b-adapt=false");

        packets.Should().HaveCount(10);
        packets.Should().Contain(p => p.Decode != p.Milliseconds);
        packets.Select(p => p.Decode).Should().BeInAscendingOrder();
    }

    // Fifty frames, two seconds, every frame a key frame.
    private static string TwoSeconds(TestMedia media)
    {
        var path = media.PathTo("two.mkv");
        using var pipeline = GStreamerPipeline.Parse(
            "videotestsrc num-buffers=50 ! video/x-raw,width=64,height=48,framerate=25/1 ! jpegenc ! matroskamux ! " +
            $"filesink location={path}");
        pipeline.Play();
        pipeline.WaitForCompletion(TimeSpan.FromSeconds(10)).Should().BeTrue();
        return path;
    }

    private static List<long> PlayRange(double rate)
    {
        using var media = new TestMedia();
        using var pipeline = GStreamerPipeline.Parse(
            $"filesrc location={TwoSeconds(media)} ! matroskademux ! jpegdec ! appsink name=sink sync=false");
        var sink = pipeline.GetSink("sink");
        pipeline.Pause();

        pipeline.Seek(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800), rate).Should().BeTrue();
        pipeline.Play();

        var times = new List<long>();
        while (sink.TryPullVideo(TimeSpan.FromSeconds(3), (in Abstractions.Frames.VideoFrame f) =>
                   times.Add((long)f.Timestamp.TotalMilliseconds)))
        {
        }

        return times;
    }

    [GStreamerOnlyFact]
    public void ASeekWithAStopPlaysThatRangeOnly() =>
        PlayRange(rate: 1).Should().Equal(Enumerable.Range(10, 10).Select(i => i * 40L));

    [GStreamerOnlyFact]
    public void ANegativeRatePlaysTheRangeBackwards() =>
        PlayRange(rate: -1).Should().Equal(Enumerable.Range(10, 10).Reverse().Select(i => i * 40L));

    [GStreamerOnlyFact]
    public void ASegmentSeekReportsTheEndOfTheSegmentSoItCanBeLooped()
    {
        using var media = new TestMedia();
        using var pipeline = GStreamerPipeline.Parse(
            $"filesrc location={TwoSeconds(media)} ! matroskademux ! jpegdec ! fakesink name=sink signal-handoffs=true");
        var frames = 0;
        pipeline.FindElement("sink")!.Connect("handoff", _ => Interlocked.Increment(ref frames));
        using var done = new SemaphoreSlim(0);
        pipeline.Bus.MessageReceived += (_, m) =>
        {
            if (m.Type == GstMessageType.SegmentDone)
            {
                done.Release();
            }
        };
        pipeline.Pause();
        var end = TimeSpan.FromMilliseconds(400);

        pipeline.Seek(TimeSpan.Zero, end, flags: GstSeekFlags.Flush | GstSeekFlags.Segment).Should().BeTrue();
        Interlocked.Exchange(ref frames, 0);
        pipeline.Play();
        done.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the segment ends");
        pipeline.Seek(TimeSpan.Zero, end, flags: GstSeekFlags.Segment).Should().BeTrue();

        done.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("it ends again");
        Volatile.Read(ref frames).Should().BeInRange(19, 21, "the range played twice");
    }

    [GStreamerOnlyFact]
    public void ARateOfZeroAndARangeThatEndsBeforeItStartsAreRefused()
    {
        using var pipeline = GStreamerPipeline.Parse("videotestsrc ! fakesink");

        var still = () => pipeline.Seek(TimeSpan.Zero, null, rate: 0);
        var reversed = () => pipeline.Seek(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));

        still.Should().Throw<ArgumentOutOfRangeException>();
        reversed.Should().Throw<ArgumentException>();
    }
}
