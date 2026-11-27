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

/// <summary>Control of a whole pipeline. What a pipeline wrote is read back with FFmpeg.</summary>
[SupportedOSPlatform("linux")]
public class GStreamerPipelineTests
{
    [GStreamerFact]
    public void EndOfStreamLetsALivePipelineFinishItsFile()
    {
        using var media = new TestMedia();
        var path = media.PathTo("live.mov");
        using var pipeline = GStreamerPipeline.Parse(
            "videotestsrc is-live=true ! video/x-raw,width=64,height=48,framerate=25/1 ! videoconvert ! jpegenc ! " +
            $"qtmux ! filesink location={path}");
        pipeline.Play();
        Thread.Sleep(1000);

        pipeline.EndOfStream().Should().BeTrue();

        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();
        pipeline.Stop();
        var info = new FFmpegProber().Probe(path);
        info.OfKind(MediaStreamKind.Video).Should().ContainSingle();
        info.Duration.Should().BeGreaterThan(TimeSpan.FromMilliseconds(500));
    }

    [GStreamerOnlyFact]
    public void APipelineBuiltByHandDeliversFrames()
    {
        using var pipeline = GStreamerPipeline.Create();
        var source = pipeline.Add("videotestsrc");
        var caps = pipeline.Add("capsfilter", "caps");
        var sink = pipeline.Add("appsink", "sink");
        source.SetValue("num-buffers", 3);
        caps.Set("caps", "video/x-raw,format=BGRA,width=64,height=48");
        source.Link(caps);
        caps.Link(sink);
        var output = pipeline.GetSink("sink");

        pipeline.Play();

        var widths = new List<int>();
        while (output.TryPullVideo(TimeSpan.FromSeconds(3), (in VideoFrame f) => widths.Add(f.Format.Width)))
        {
        }

        widths.Should().Equal(64, 64, 64);
        source.Name.Should().StartWith("videotestsrc", "an element without a name is given one");
        pipeline.FindElement("caps").Should().NotBeNull();
    }

    [GStreamerOnlyFact]
    public void LinkTakesRequestPadsFromATee()
    {
        using var pipeline = GStreamerPipeline.Create();
        var source = pipeline.Add("videotestsrc");
        var tee = pipeline.Add("tee");
        source.SetValue("num-buffers", 4);
        source.Link(tee);
        var counts = new int[2];
        for (var i = 0; i < 2; i++)
        {
            var branch = i;
            var queue = pipeline.Add("queue");
            var sink = pipeline.Add("fakesink");
            sink.SetValue("signal-handoffs", true);
            sink.Connect("handoff", _ => Interlocked.Increment(ref counts[branch]));
            tee.Link(queue);
            queue.Link(sink);
        }

        pipeline.Play();

        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();
        counts.Should().Equal(4, 4);
    }

    [GStreamerFact]
    public void APadAddedWhileRunningIsLinkedByName()
    {
        using var media = new TestMedia();
        var file = media.WriteVideoAndAudio("movie.mkv");
        using var pipeline = GStreamerPipeline.Create();
        var source = pipeline.Add("filesrc");
        var demuxer = pipeline.Add("matroskademux");
        var queue = pipeline.Add("queue");
        var sink = pipeline.Add("fakesink");
        source.SetValue("location", file);
        sink.SetValue("signal-handoffs", true);
        var frames = 0;
        sink.Connect("handoff", _ => Interlocked.Increment(ref frames));
        source.Link(demuxer);
        queue.Link(sink);
        demuxer.Connect("pad-added", args =>
        {
            var pad = args.NameOf(0)!;
            if (pad.StartsWith("video", StringComparison.Ordinal))
            {
                demuxer.Link(queue, pad, "sink");
            }
        });

        pipeline.Play();

        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();
        frames.Should().Be(25);
    }

    [GStreamerOnlyFact]
    public void ALinkThatCannotBeMadeNamesBothElements()
    {
        using var pipeline = GStreamerPipeline.Create();
        var sound = pipeline.Add("audiotestsrc", "tone");
        var picture = pipeline.Add("videoconvert", "picture");

        var link = () => sound.Link(picture);

        link.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("tone").And.Contain("picture");
    }

    [GStreamerOnlyFact]
    public void AnElementThatCannotBeAddedIsRefusedWithItsName()
    {
        using var pipeline = GStreamerPipeline.Create();
        pipeline.Add("fakesink", "out");

        var unknown = () => pipeline.Add("nosuchelement");
        var taken = () => pipeline.Add("fakesink", "out");

        unknown.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("nosuchelement");
        taken.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("out");
    }

    [GStreamerOnlyFact]
    public void ABinFromADescriptionLinksLikeAnElement()
    {
        using var pipeline = GStreamerPipeline.Create();
        var source = pipeline.Add("videotestsrc");
        var bin = pipeline.AddBin("videoconvert ! video/x-raw,format=RGBA ! identity name=inner", "chain");
        var sink = pipeline.Add("appsink", "sink");
        source.SetValue("num-buffers", 3);
        source.Link(bin);
        bin.Link(sink);
        var output = pipeline.GetSink("sink");

        pipeline.Play();

        var formats = new List<PixelFormat>();
        while (output.TryPullVideo(TimeSpan.FromSeconds(3), (in VideoFrame f) => formats.Add(f.Format.PixelFormat)))
        {
        }

        formats.Should().Equal(PixelFormat.Rgba32, PixelFormat.Rgba32, PixelFormat.Rgba32);
        bin.Name.Should().Be("chain");
        pipeline.FindElement("inner").Should().NotBeNull("the elements of a bin are found by name too");
    }

    [GStreamerOnlyFact]
    public void ABinThatCannotBeBuiltIsRefusedAndLeftOut()
    {
        using var pipeline = GStreamerPipeline.Create();
        pipeline.Add("fakesink", "taken");

        var unknown = () => pipeline.AddBin("identity ! nosuchelement", "half");
        var taken = () => pipeline.AddBin("identity", "taken");

        unknown.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("nosuchelement");
        pipeline.FindElement("half").Should().BeNull();
        taken.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("taken");
    }

    [GStreamerOnlyFact]
    public void AnUnlinkedElementCanBeRemovedAndAnotherLinkedInItsPlace()
    {
        using var pipeline = GStreamerPipeline.Create();
        var source = pipeline.Add("videotestsrc");
        var first = pipeline.Add("fakesink", "first");
        var second = pipeline.Add("fakesink", "second");
        source.SetValue("num-buffers", 3);
        var counts = new int[2];
        first.SetValue("signal-handoffs", true);
        second.SetValue("signal-handoffs", true);
        first.Connect("handoff", _ => Interlocked.Increment(ref counts[0]));
        second.Connect("handoff", _ => Interlocked.Increment(ref counts[1]));
        source.Link(first);

        source.Unlink(first);
        pipeline.Remove(first);
        source.Link(second);
        pipeline.Play();

        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();
        counts.Should().Equal(0, 3);
        pipeline.FindElement("first").Should().BeNull();
    }

    [GStreamerOnlyFact]
    public void RemovingAnElementOfAnotherPipelineIsRefusedWithItsName()
    {
        using var pipeline = GStreamerPipeline.Create();
        using var other = GStreamerPipeline.Create();
        var foreign = other.Add("fakesink", "foreign");

        var remove = () => pipeline.Remove(foreign);

        remove.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("foreign");
    }
}
