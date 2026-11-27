#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.GStreamer;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>Caps and structures as typed values, and action signals emitted from outside.</summary>
[SupportedOSPlatform("linux")]
public class GStreamerStructureTests
{
    [GStreamerOnlyFact]
    public void AStructureIsReadWithTheTypesOfItsFields()
    {
        var structure = GStreamerStructure.Parse(
            "measure, count=(int)7, big=(guint64)5000000000, on=(boolean)true, level=(double)0.5, " +
            "name=(string)\"two words\", rate=(fraction)30000/1001, formats=(string){ I420, NV12 }");

        structure.Name.Should().Be("measure");
        structure.Fields.Keys.Should().Equal("count", "big", "on", "level", "name", "rate", "formats");
        structure.Get<int>("count").Should().Be(7);
        structure.Get<ulong>("big").Should().Be(5_000_000_000UL);
        structure.Get<bool>("on").Should().BeTrue();
        structure.Get<double>("level").Should().Be(0.5);
        structure.Get<string>("name").Should().Be("two words");
        structure.Get<Rational>("rate").Should().Be(new Rational(30000, 1001));
        structure.Get<object?[]>("formats").Should().Equal("I420", "NV12");
    }

    [GStreamerOnlyFact]
    public void TextThatIsNoStructureOrCapsIsRefused()
    {
        var structure = () => GStreamerStructure.Parse("=nonsense");
        var caps = () => GStreamerCaps.Parse("=nonsense");

        structure.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("=nonsense");
        caps.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("=nonsense");
    }

    [GStreamerOnlyFact]
    public void ACapsPropertyIsWrittenAndReadAsCaps()
    {
        using var pipeline = GStreamerPipeline.Create();
        var filter = pipeline.Add("capsfilter");

        filter.SetValue("caps", GStreamerCaps.Parse("video/x-raw, width=(int)64, height=(int)48; audio/x-raw"));

        var caps = filter.Get<GStreamerCaps>("caps");
        caps.Structures.Select(s => s.Name).Should().Equal("video/x-raw", "audio/x-raw");
        caps.Structures[0].Get<int>("width").Should().Be(64);
        caps.ToString().Should().Contain("height=(int)48");
    }

    [GStreamerOnlyFact]
    public void CapsASignalCarriesArriveTyped()
    {
        using var pipeline = GStreamerPipeline.Parse(
            "videotestsrc num-buffers=2 ! video/x-raw,width=64,height=48 ! jpegenc ! decodebin name=decoder ! fakesink");
        var seen = new List<(string Name, int Width)>();
        pipeline.FindElement("decoder")!.Connect("autoplug-continue", args =>
        {
            var structure = args.Get<GStreamerCaps>(1).Structures[0];
            lock (seen)
            {
                seen.Add((structure.Name, structure.Fields.ContainsKey("width") ? structure.Get<int>("width") : 0));
            }

            args.ReturnValue = true;
        });

        pipeline.Play();

        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();
        lock (seen)
        {
            seen.Should().Contain(("image/jpeg", 64));
        }
    }

    [GStreamerOnlyFact]
    public void ActionSignalsTakeArgumentsAndAnswerWithAStructure()
    {
        using var pipeline = GStreamerPipeline.Parse("audiotestsrc num-buffers=5 ! multiudpsink name=udp");
        var udp = pipeline.FindElement("udp")!;
        udp.Emit("add", "127.0.0.1", 47000).Should().BeNull("add returns nothing");
        pipeline.Play();
        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();

        var stats = (GStreamerStructure)udp.Emit("get-stats", "127.0.0.1", 47000)!;

        stats.Get<ulong>("packets-sent").Should().Be(5);
        stats.Get<ulong>("bytes-sent").Should().Be(5 * 1024 * 2);
    }

    [GStreamerOnlyFact]
    public void AnEnumResultOfAnActionSignalIsItsNumber()
    {
        using var pipeline = GStreamerPipeline.Parse("appsrc name=src ! fakesink");
        var source = pipeline.FindElement("src")!;
        pipeline.Play();

        source.Emit("end-of-stream").Should().Be(0, "GST_FLOW_OK");

        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();
    }

    [GStreamerOnlyFact]
    public void WhatCannotBeEmittedIsRefusedWithTheReason()
    {
        using var pipeline = GStreamerPipeline.Parse("appsrc ! appsink name=sink");
        var sink = pipeline.FindElement("sink")!;

        var unknown = () => sink.Emit("no-such-signal");
        var notAnAction = () => sink.Emit("new-sample");
        var wrongCount = () => sink.Emit("try-pull-preroll");
        var resultWouldDie = () => sink.Emit("try-pull-sample", 0UL);

        unknown.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("no-such-signal");
        notAnAction.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("not an action");
        wrongCount.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("takes 1");
        resultWouldDie.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("GstSample");
    }
}
