#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.GStreamer;
using MediaToolkitNet.GStreamer.Native;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>Pad probes, and changes to a pipeline that is playing.</summary>
[SupportedOSPlatform("linux")]
public class GStreamerDynamicTests
{
    private const string TenSmallFrames = "videotestsrc num-buffers=10 ! video/x-raw,format=GRAY8,width=16,height=16 ! ";

    [GStreamerOnlyFact]
    public void AProbeSeesEveryBufferAndCanDropIt()
    {
        using var pipeline = GStreamerPipeline.Parse(TenSmallFrames + "identity name=id ! fakesink name=sink signal-handoffs=true");
        var seen = new List<(long Size, TimeSpan? Timestamp)>();
        var arrived = 0;
        pipeline.FindElement("sink")!.Connect("handoff", _ => Interlocked.Increment(ref arrived));
        pipeline.FindElement("id")!.AddProbe("src", GstPadProbeType.Buffer, info =>
        {
            lock (seen)
            {
                seen.Add((info.Size, info.Timestamp));
                return seen.Count % 2 == 0 ? GstPadProbeReturn.Drop : GstPadProbeReturn.Ok;
            }
        });

        pipeline.Play();

        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();
        arrived.Should().Be(5);
        seen.Should().HaveCount(10);
        seen.Select(s => s.Size).Should().OnlyContain(size => size == 256);
        seen.Select(s => s.Timestamp!.Value).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [GStreamerOnlyFact]
    public void AProbeSeesTheEndOfStreamEvent()
    {
        using var pipeline = GStreamerPipeline.Parse(TenSmallFrames + "identity name=id ! fakesink");
        var ends = 0;
        pipeline.FindElement("id")!.AddProbe("src", GstPadProbeType.EventDownstream, info =>
        {
            if (info.Event == GstEventType.Eos)
            {
                Interlocked.Increment(ref ends);
            }

            return GstPadProbeReturn.Ok;
        });

        pipeline.Play();

        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();
        ends.Should().Be(1);
    }

    [GStreamerOnlyFact]
    public void AThrowingProbeIsReportedAndTheDataGoesOn()
    {
        using var pipeline = GStreamerPipeline.Parse(TenSmallFrames + "identity name=id ! fakesink name=sink signal-handoffs=true");
        var identity = pipeline.FindElement("id")!;
        var arrived = 0;
        var reported = new List<string>();
        pipeline.FindElement("sink")!.Connect("handoff", _ => Interlocked.Increment(ref arrived));
        identity.Failed += (_, e) =>
        {
            lock (reported)
            {
                reported.Add(e.Message);
            }
        };
        identity.AddProbe("src", GstPadProbeType.Buffer, _ => throw new InvalidOperationException("boom"));

        pipeline.Play();

        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();
        arrived.Should().Be(10);
        reported.Should().HaveCount(10).And.OnlyContain(m => m.Contains("boom"));
    }

    [GStreamerOnlyFact]
    public void ADisposedProbeIsNotCalledAndAMissingPadIsRefused()
    {
        using var pipeline = GStreamerPipeline.Parse(TenSmallFrames + "identity name=id ! fakesink");
        var identity = pipeline.FindElement("id")!;
        var calls = 0;
        var probe = identity.AddProbe("src", GstPadProbeType.Buffer, _ =>
        {
            Interlocked.Increment(ref calls);
            return GstPadProbeReturn.Ok;
        });
        probe.Dispose();

        var missing = () => identity.AddProbe("nowhere", GstPadProbeType.Buffer, _ => GstPadProbeReturn.Ok);
        pipeline.Play();

        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();
        calls.Should().Be(0);
        missing.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("nowhere");
    }

    [GStreamerFact]
    public void ARecordingBranchIsAddedAndTakenOutWhileThePreviewGoesOn()
    {
        using var media = new TestMedia();
        var path = media.PathTo("branch.mov");
        using var pipeline = GStreamerPipeline.Create();
        var source = pipeline.Add("videotestsrc");
        var caps = pipeline.Add("capsfilter");
        var tee = pipeline.Add("tee");
        var previewQueue = pipeline.Add("queue");
        var preview = pipeline.Add("fakesink");
        source.SetValue("is-live", true);
        caps.Set("caps", "video/x-raw,width=64,height=48,framerate=25/1");
        preview.SetValue("signal-handoffs", true);
        var previewed = 0;
        preview.Connect("handoff", _ => Interlocked.Increment(ref previewed));
        source.Link(caps);
        caps.Link(tee);
        tee.Link(previewQueue);
        previewQueue.Link(preview);
        pipeline.Play();
        WaitUntil(() => Volatile.Read(ref previewed) > 5);

        var queue = pipeline.Add("queue");
        var encoder = pipeline.Add("jpegenc");
        var muxer = pipeline.Add("qtmux");
        var file = pipeline.Add("filesink");
        file.SetValue("location", path);
        file.SetValue("async", false);
        queue.Link(encoder);
        encoder.Link(muxer);
        muxer.Link(file);
        foreach (var element in new[] { file, muxer, encoder, queue })
        {
            element.SyncStateWithPipeline();
        }

        tee.Link(queue, "src_1");
        Thread.Sleep(1000);

        using var finished = new ManualResetEventSlim();
        file.AddProbe("sink", GstPadProbeType.EventDownstream, info =>
        {
            if (info.Event == GstEventType.Eos)
            {
                finished.Set();
            }

            return GstPadProbeReturn.Ok;
        });
        tee.AddProbe("src_1", GstPadProbeType.Idle, _ =>
        {
            tee.Unlink(queue);
            queue.EndOfStream("sink");
            return GstPadProbeReturn.Remove;
        });

        finished.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the end reaches the file");
        foreach (var element in new[] { queue, encoder, muxer, file })
        {
            pipeline.Remove(element);
        }

        tee.ReleasePad("src_1");
        var afterRemoval = Volatile.Read(ref previewed);
        WaitUntil(() => Volatile.Read(ref previewed) > afterRemoval + 5);

        Volatile.Read(ref previewed).Should().BeGreaterThan(afterRemoval + 5, "the preview did not stop");
        pipeline.State.Should().Be(GstState.Playing);
        pipeline.LastError().Should().BeNull();
        var info = new FFmpegProber().Probe(path);
        info.OfKind(MediaStreamKind.Video).Should().ContainSingle();
        info.Duration.Should().BeGreaterThan(TimeSpan.FromMilliseconds(500));
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !condition())
        {
            Thread.Sleep(10);
        }
    }
}
