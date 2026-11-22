#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.GStreamer;
using MediaToolkitNet.GStreamer.Native;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>A fact that runs where GStreamer is usable; unlike the transcoding tests it needs no FFmpeg.</summary>
public sealed class GStreamerOnlyFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="GStreamerOnlyFactAttribute"/> class.</summary>
    public GStreamerOnlyFactAttribute() =>
        Skip = !OperatingSystem.IsLinux() ? "The GStreamer backend runs on Linux only."
            : !GStreamerBackend.Instance.IsAvailable ? "GStreamer is not usable here."
            : null;
}

/// <summary>
/// The pipeline's bus and the signals of its elements, driven with test sources that need no files: what the bus
/// delivers is what the pipeline did, and what a handler sees is what GStreamer emitted.
/// </summary>
[SupportedOSPlatform("linux")]
public class GStreamerBusTests
{
    [GStreamerOnlyFact]
    public void StateChangesStreamStartAndTheEndAreDelivered()
    {
        using var pipeline = GStreamerPipeline.Parse("videotestsrc num-buffers=5 ! fakesink");
        var messages = Listen(pipeline);

        pipeline.Play();
        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue("the bus thread keeps the end of the stream for the waiter");

        WaitFor(messages, m => m.Type == GstMessageType.Eos);
        var seen = Snapshot(messages);
        seen.Where(m => m.Type == GstMessageType.StateChanged).Should().OnlyContain(m => m.FromPipeline, "only the pipeline's own changes are reported by default");
        seen.Where(m => m.Type == GstMessageType.StateChanged).Select(m => (m.OldState, m.NewState)).Should().Contain(
            [(GstState.Null, GstState.Ready), (GstState.Ready, GstState.Paused), (GstState.Paused, GstState.Playing)]);
        seen.Should().Contain(m => m.Type == GstMessageType.StreamStart);
        seen.Should().Contain(m => m.Type == GstMessageType.Eos);
    }

    [GStreamerOnlyFact]
    public void ErrorsCarryTheirSourceAndTheFailureStillSaysWhy()
    {
        using var pipeline = GStreamerPipeline.Parse("filesrc location=/nonexistent/mediatoolkitnet.mp4 ! fakesink");
        var messages = Listen(pipeline);

        var act = () => pipeline.Play();

        // The error the bus thread read is the reason the pipeline gives; GStreamer's debug text names the failing element.
        act.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("filesrc");
        WaitFor(messages, m => m.Type == GstMessageType.Error);
        var error = Snapshot(messages).First(m => m.Type == GstMessageType.Error);
        error.Source.Should().NotBeNullOrEmpty();
        error.Text.Should().NotBeNullOrEmpty();
        error.Debug.Should().Contain("filesrc");
    }

    [GStreamerOnlyFact]
    public void AnApplicationMessageKeepsItsNameAndSource()
    {
        using var pipeline = GStreamerPipeline.Parse("fakesrc ! fakesink");
        var messages = Listen(pipeline);

        pipeline.Bus.PostApplication("mediatoolkitnet-hello");

        WaitFor(messages, m => m.Type == GstMessageType.Application);
        var message = Snapshot(messages).First(m => m.Type == GstMessageType.Application);
        message.StructureName.Should().Be("mediatoolkitnet-hello");
        message.FromPipeline.Should().BeTrue();
        message.Source.Should().StartWith("pipeline");
    }

    [GStreamerOnlyFact]
    public void TagsWithATextValueArriveAsADictionary()
    {
        using var pipeline = GStreamerPipeline.Parse("audiotestsrc num-buffers=2 ! taginject tags=\"title=Hello,artist=Somebody\" ! fakesink");
        var messages = Listen(pipeline);

        pipeline.Play();
        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();

        WaitFor(messages, m => m.Type == GstMessageType.Tag && m.Tags.ContainsKey("title"));
        var tags = Snapshot(messages).First(m => m.Type == GstMessageType.Tag && m.Tags.ContainsKey("title")).Tags;
        tags["title"].Should().Be("Hello");
        tags["artist"].Should().Be("Somebody");
    }

    [GStreamerOnlyFact]
    public void TagsWithANumberAreLeftOutAndTheTextOnesStay()
    {
        using var pipeline = GStreamerPipeline.Parse("audiotestsrc num-buffers=2 ! taginject tags=\"title=Hello,bitrate=128000,track-number=3\" ! fakesink");
        var messages = Listen(pipeline);

        pipeline.Play();
        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();

        WaitFor(messages, m => m.Type == GstMessageType.Tag && m.Tags.ContainsKey("title"));
        var tags = Snapshot(messages).First(m => m.Type == GstMessageType.Tag && m.Tags.ContainsKey("title")).Tags;
        tags.Keys.Should().Contain("title").And.NotContain("bitrate").And.NotContain("track-number", "a bit rate and a track number are not text");
    }

    [GStreamerOnlyFact]
    public void AMaskLeavesOutWhatIsNotWantedWithoutHidingTheEnd()
    {
        using var pipeline = GStreamerPipeline.Parse("videotestsrc num-buffers=3 ! fakesink");
        pipeline.Bus.Mask = GstMessageType.Error;
        var messages = Listen(pipeline);

        pipeline.Play();

        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();
        Thread.Sleep(300);
        Snapshot(messages).Should().BeEmpty("neither the state changes nor the end of the stream are in the mask");
    }

    [GStreamerOnlyFact]
    public void AnErrorEndsTheWaitWithItsReason()
    {
        using var pipeline = GStreamerPipeline.Parse("videotestsrc ! appsink name=sink emit-signals=true sync=false");
        _ = Listen(pipeline);
        var sink = pipeline.FindElement("sink")!;
        sink.Connect("new-sample", args => args.ReturnValue = -5); // GST_FLOW_ERROR

        pipeline.Play();
        var act = () => pipeline.WaitForCompletion(TimeSpan.FromSeconds(5));

        act.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("-5");
    }

    [GStreamerOnlyFact]
    public void AHandlerSeesWhatTheSignalWasEmittedWith()
    {
        using var pipeline = GStreamerPipeline.Parse("videotestsrc num-buffers=10 ! fakesink name=sink signal-handoffs=true");
        var sink = pipeline.FindElement("sink")!;
        var calls = 0;
        string? pad = null;
        var bufferWasPointer = true;
        sink.Connect("handoff", args =>
        {
            Interlocked.Increment(ref calls);
            pad = args.NameOf(1);
            bufferWasPointer &= args.Get<nint>(0) != 0;
            args.Values.Count.Should().Be(2, "handoff carries a buffer and a pad");
        });

        pipeline.Play();
        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();

        calls.Should().Be(10);
        pad.Should().Be("sink");
        bufferWasPointer.Should().BeTrue();
    }

    [GStreamerOnlyFact]
    public void ADetailedSignalReportsThePropertyThatChanged()
    {
        using var pipeline = GStreamerPipeline.Parse("fakesrc ! identity name=id ! fakesink");
        var identity = pipeline.FindElement("id")!;
        var names = new List<string?>();
        identity.Connect("notify::silent", args => names.Add(args.NameOf(0)));

        identity.Set("silent", "false");

        names.Should().Equal("silent");
    }

    [GStreamerOnlyFact]
    public void ADisconnectedHandlerIsNotCalled()
    {
        using var pipeline = GStreamerPipeline.Parse("videotestsrc num-buffers=5 ! fakesink name=sink signal-handoffs=true");
        var calls = 0;
        var connection = pipeline.FindElement("sink")!.Connect("handoff", _ => calls++);
        connection.IsConnected.Should().BeTrue();

        connection.Dispose();
        pipeline.Play();
        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();

        calls.Should().Be(0);
        connection.IsConnected.Should().BeFalse();
    }

    [GStreamerOnlyFact]
    public void AHandlerThatThrowsIsReportedAndTheStreamGoesOn()
    {
        using var pipeline = GStreamerPipeline.Parse("videotestsrc num-buffers=5 ! fakesink name=sink signal-handoffs=true");
        var sink = pipeline.FindElement("sink")!;
        var failures = new List<string>();
        sink.Failed += (_, e) =>
        {
            lock (failures)
            {
                failures.Add(e.Message);
            }
        };
        sink.Connect("handoff", _ => throw new InvalidOperationException("boom"));

        pipeline.Play();

        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();
        lock (failures)
        {
            failures.Should().HaveCount(5).And.OnlyContain(m => m.Contains("boom") && m.Contains("handoff"));
        }
    }

    [GStreamerOnlyFact]
    public void ASignalThatDoesNotExistIsRefusedWithItsName()
    {
        using var pipeline = GStreamerPipeline.Parse("fakesrc ! fakesink name=sink");
        var sink = pipeline.FindElement("sink")!;

        var act = () => sink.Connect("no-such-signal", _ => { });

        act.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("no-such-signal");
    }

    [GStreamerOnlyFact]
    public void PipelinesComeAndGoWhileSignalsFire()
    {
        // The handlers outlive nothing: a pipeline torn down while buffers are still flowing must not call into
        // a handler that is gone, or free a handle twice.
        for (var i = 0; i < 40; i++)
        {
            using var pipeline = GStreamerPipeline.Parse("videotestsrc ! fakesink name=sink signal-handoffs=true sync=false");
            _ = Listen(pipeline);
            var calls = 0;
            pipeline.FindElement("sink")!.Connect("handoff", _ => Interlocked.Increment(ref calls));
            pipeline.Play();
            Thread.Sleep(5);
        }
    }

    private static List<GStreamerMessage> Listen(GStreamerPipeline pipeline)
    {
        var messages = new List<GStreamerMessage>();
        pipeline.Bus.MessageReceived += (_, m) =>
        {
            lock (messages)
            {
                messages.Add(m);
            }
        };
        return messages;
    }

    private static List<GStreamerMessage> Snapshot(List<GStreamerMessage> messages)
    {
        lock (messages)
        {
            return [.. messages];
        }
    }

    private static void WaitFor(List<GStreamerMessage> messages, Func<GStreamerMessage, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && !Snapshot(messages).Any(condition))
        {
            Thread.Sleep(10);
        }
    }
}
