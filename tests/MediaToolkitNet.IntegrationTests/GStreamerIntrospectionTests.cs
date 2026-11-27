#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.GStreamer;
using MediaToolkitNet.GStreamer.Native;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>What elements, pads, properties, messages and buffers can be asked about.</summary>
[SupportedOSPlatform("linux")]
public class GStreamerIntrospectionTests
{
    [GStreamerOnlyFact]
    public void AnElementFactoryListsItsPropertiesAndPadTemplates()
    {
        var identity = GStreamerElements.Describe("identity")!;
        var tee = GStreamerElements.Describe("tee")!;

        identity.Info.Name.Should().Be("identity");
        identity.Properties.Should().Contain(p => p.Name == "drop-probability" && p.TypeName == "gfloat" && p.CanRead && p.CanWrite);
        identity.Properties.Should().Contain(p => p.Name == "name" && p.TypeName == "gchararray");
        identity.PadTemplates.Select(t => (t.Name, t.Direction, t.Presence)).Should().BeEquivalentTo(
        [
            ("sink", GstPadDirection.Sink, GstPadPresence.Always),
            ("src", GstPadDirection.Source, GstPadPresence.Always),
        ]);
        tee.PadTemplates.Should().Contain(t => t.Name == "src_%u" && t.Presence == GstPadPresence.Request);
        GStreamerElements.Describe("videoconvert")!.PadTemplates.Should().OnlyContain(t => t.Caps.Contains("video/x-raw"));
        GStreamerElements.Describe("nosuchelement").Should().BeNull();
    }

    [GStreamerOnlyFact]
    public void FractionAndArrayPropertiesAreReadAndWritten()
    {
        using var pipeline = GStreamerPipeline.Create();
        var crop = pipeline.Add("aspectratiocrop");
        var convert = pipeline.Add("audioconvert");

        crop.SetValue("aspect-ratio", new Rational(16, 9));
        convert.Set("mix-matrix", "<<(float)1.0, (float)0.5>, <(float)0.0, (float)0.25>>");

        crop.Get<Rational>("aspect-ratio").Should().Be(new Rational(16, 9));
        var rows = convert.Get<object?[]>("mix-matrix");
        rows.Should().HaveCount(2);
        ((object?[])rows[0]!).Should().Equal(1.0f, 0.5f);
        ((object?[])rows[1]!).Should().Equal(0.0f, 0.25f);
    }

    [GStreamerOnlyFact]
    public void AnElementIsPutIntoAPropertyAndReadBack()
    {
        using var pipeline = GStreamerPipeline.Create();
        using var other = GStreamerPipeline.Create();
        var player = pipeline.Add("playbin3");
        var sink = other.Add("fakesink", "chosen");
        other.Remove(sink);

        using var before = player.GetElement("video-sink");
        player.SetValue("video-sink", sink);
        using var after = player.GetElement("video-sink");

        before.Should().BeNull();
        after!.Name.Should().Be("chosen");
        var notAnObject = () => player.GetElement("uri");
        notAnObject.Should().Throw<Abstractions.MediaToolkitNetException>().Which.Message.Should().Contain("uri");
    }

    [GStreamerOnlyFact]
    public void APadPropertyReadsBack()
    {
        using var pipeline = GStreamerPipeline.Create();
        var tone = pipeline.Add("audiotestsrc");
        var mixer = pipeline.Add("audiomixer");
        tone.Link(mixer, "src", "sink_0");

        mixer.SetPadValue("sink_0", "volume", 0.25);
        mixer.SetPadValue("sink_0", "mute", true);

        mixer.GetPadValue("sink_0", "volume").Should().Be(0.25);
        mixer.GetPadValue("sink_0", "mute").Should().Be(true);
        var missing = () => mixer.GetPadValue("sink_9", "volume");
        missing.Should().Throw<Abstractions.MediaToolkitNetException>().Which.Message.Should().Contain("sink_9");
    }

    [GStreamerOnlyFact]
    public void AnElementMessageCarriesItsFields()
    {
        using var pipeline = GStreamerPipeline.Parse(
            "audiotestsrc num-buffers=20 ! audio/x-raw,channels=2,rate=48000 ! level interval=50000000 ! fakesink");
        var levels = new List<double[]>();
        pipeline.Bus.MessageReceived += (_, m) =>
        {
            if (m.Type == GstMessageType.Element && m.StructureName == "level")
            {
                lock (levels)
                {
                    levels.Add([.. m.Details!.Get<object?[]>("rms").Cast<double>()]);
                }
            }
        };

        pipeline.Play();

        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();
        SpinWait.SpinUntil(() => { lock (levels) { return levels.Count >= 4; } }, TimeSpan.FromSeconds(3));
        lock (levels)
        {
            levels.Should().HaveCountGreaterThanOrEqualTo(4);
            levels[1].Should().HaveCount(2).And.OnlyContain(db => db < 0 && db > -10, "the default tone is loud");
        }
    }

    [GStreamerOnlyFact]
    public void ADataProbeSeesTheBytesAndCanDropTheBuffer()
    {
        using var pipeline = GStreamerPipeline.Parse(
            "videotestsrc num-buffers=6 pattern=white ! video/x-raw,format=GRAY8,width=16,height=16 ! identity name=id ! " +
            "fakesink name=sink signal-handoffs=true");
        var arrived = 0;
        var seen = new List<(int Length, byte First, string Media, long Ms)>();
        pipeline.FindElement("sink")!.Connect("handoff", _ => Interlocked.Increment(ref arrived));
        pipeline.FindElement("id")!.AddDataProbe("src", (in GStreamerData data) =>
        {
            lock (seen)
            {
                seen.Add((data.Bytes.Length, data.Bytes[0], data.Caps!.Structures[0].Name, (long)data.Timestamp!.Value.TotalMilliseconds));
                return seen.Count > 2 ? GstPadProbeReturn.Drop : GstPadProbeReturn.Ok;
            }
        });

        pipeline.Play();

        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();
        arrived.Should().Be(2);
        seen.Should().HaveCount(6).And.OnlyContain(s => s.Length == 256 && s.First > 200 && s.Media == "video/x-raw");
    }

    [GStreamerOnlyFact]
    public void SrtStatisticsAreReadAsAStructure()
    {
        const string Address = "srt://127.0.0.1:47123";
        using var sender = GStreamerPipeline.Parse(
            $"audiotestsrc is-live=true ! srtsink uri={Address}?mode=listener wait-for-connection=false");
        using var receiver = GStreamerPipeline.Parse($"srtsrc name=in uri={Address}?mode=caller ! fakesink sync=false");
        sender.Play();
        receiver.Play();
        var source = receiver.FindElement("in")!;

        long Received()
        {
            var stats = source.Get<GStreamerStructure>("stats");
            return stats.Fields.TryGetValue("bytes-received", out var bytes) ? Convert.ToInt64(bytes) : 0;
        }

        SpinWait.SpinUntil(() => Received() > 0, TimeSpan.FromSeconds(8)).Should().BeTrue("data arrives over the loopback");
    }
}
