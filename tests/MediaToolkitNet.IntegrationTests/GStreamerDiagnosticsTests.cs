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

/// <summary>Plugin ranks, parsed bus messages and the debug log.</summary>
[SupportedOSPlatform("linux")]
public class GStreamerDiagnosticsTests
{
    private static List<GStreamerMessage> Listen(GStreamerPipeline pipeline, GstMessageType type)
    {
        var messages = new List<GStreamerMessage>();
        pipeline.Bus.Mask |= type;
        pipeline.Bus.MessageReceived += (_, message) =>
        {
            if (message.Type == type)
            {
                lock (messages)
                {
                    messages.Add(message);
                }
            }
        };
        return messages;
    }

    private static GStreamerMessage First(List<GStreamerMessage> messages)
    {
        SpinWait.SpinUntil(() => { lock (messages) { return messages.Count > 0; } }, TimeSpan.FromSeconds(5))
            .Should().BeTrue("the message arrives");
        lock (messages)
        {
            return messages[0];
        }
    }

    private static string DecoderOfVp8()
    {
        using var pipeline = GStreamerPipeline.Parse(
            "videotestsrc num-buffers=5 ! video/x-raw,width=64,height=48 ! vp8enc ! decodebin ! fakesink");
        pipeline.Pause();
        pipeline.WaitForState(TimeSpan.FromSeconds(5));
        return pipeline.ToDot();
    }

    [GStreamerOnlyFact]
    public void TheRankOfADecoderDecidesWhetherItIsChosen()
    {
        var rank = GStreamerElements.GetRank("vp8dec")!.Value;
        var before = DecoderOfVp8();
        try
        {
            GStreamerElements.SetRank("vp8dec", 0).Should().BeTrue();
            var without = DecoderOfVp8();

            GStreamerElements.GetRank("vp8dec").Should().Be(0u);
            before.Should().Contain("vp8dec0").And.NotContain("avdec_vp8");
            without.Should().Contain("avdec_vp8").And.NotContain("vp8dec0\\n");
        }
        finally
        {
            GStreamerElements.SetRank("vp8dec", rank);
        }

        rank.Should().BeGreaterThan(0u);
        GStreamerElements.GetRank("nosuchelement").Should().BeNull();
        GStreamerElements.SetRank("nosuchelement", 1).Should().BeFalse();
    }

    [GStreamerOnlyFact]
    public void ScanningADirectoryWithoutPluginsChangesNothing()
    {
        using var media = new TestMedia();

        GStreamerElements.ScanPlugins(Path.GetDirectoryName(media.PathTo("x"))!).Should().BeFalse();

        var missing = () => GStreamerElements.ScanPlugins("/no/such/directory");
        missing.Should().Throw<DirectoryNotFoundException>();
    }

    [GStreamerOnlyFact]
    public void AQosMessageSaysWhatWasDropped()
    {
        using var pipeline = GStreamerPipeline.Parse(
            "videotestsrc num-buffers=20 ! video/x-raw,width=16,height=16,framerate=25/1 ! identity sleep-time=100000 ! " +
            "fakesink sync=true qos=true max-lateness=0");
        var messages = Listen(pipeline, GstMessageType.Qos);

        pipeline.Play();
        pipeline.WaitForCompletion(TimeSpan.FromSeconds(10)).Should().BeTrue();

        var qos = First(messages).Qos!.Value;
        qos.Dropped.Should().BeGreaterThan(0UL);
        qos.Jitter.Should().BeGreaterThan(TimeSpan.Zero, "the buffer was late");
        qos.Timestamp.Should().NotBeNull();
    }

    [GStreamerOnlyFact]
    public void AProgressMessageSaysWhatIsBeingDone()
    {
        using var pipeline = GStreamerPipeline.Parse("rtspsrc location=rtsp://127.0.0.1:1/none ! fakesink");
        var messages = Listen(pipeline, GstMessageType.Progress);

        try
        {
            pipeline.Play();
        }
        catch (MediaToolkitNetException)
        {
            // Nothing listens there; the refusal may come before the call returns.
        }

        var progress = First(messages).Progress!.Value;
        progress.Type.Should().Be(GstProgressType.Start);
        progress.Code.Should().NotBeNullOrEmpty();
        progress.Text.Should().NotBeNullOrEmpty();
    }

    [GStreamerFact]
    public void ATocMessageListsTheChapters()
    {
        using var media = new TestMedia();
        var path = media.PathTo("chapters.mkv");
        var chapters = media.PathTo("chapters.txt");
        File.WriteAllText(chapters, string.Join('\n',
            ";FFMETADATA1",
            "[CHAPTER]", "TIMEBASE=1/1000", "START=0", "END=400", "title=Opening",
            "[CHAPTER]", "TIMEBASE=1/1000", "START=400", "END=1000", "title=Rest", ""));
        FfmpegProgram.Run($"-v error -i {media.WriteVideo("plain.mkv")} -i {chapters} -map 0 -map_chapters 1 -c copy {path}");
        using var pipeline = GStreamerPipeline.Parse($"filesrc location={path} ! matroskademux ! fakesink");
        var messages = Listen(pipeline, GstMessageType.Toc);

        pipeline.Pause();

        var entries = Flatten(First(messages).Toc).Where(e => e.Type == GstTocEntryType.Chapter).ToList();
        entries.Select(e => (e.Title, e.Start.TotalMilliseconds, e.Stop.TotalMilliseconds))
            .Should().Equal(("Opening", 0.0, 400.0), ("Rest", 400.0, 1000.0));

        static IEnumerable<GStreamerTocEntry> Flatten(IReadOnlyList<GStreamerTocEntry> list) =>
            list.SelectMany(e => Flatten(e.Entries).Prepend(e));
    }

    [GStreamerOnlyFact]
    public void AStreamStartCarriesItsGroup()
    {
        using var pipeline = GStreamerPipeline.Parse("audiotestsrc num-buffers=3 ! fakesink");
        var messages = Listen(pipeline, GstMessageType.StreamStart);

        pipeline.Play();

        First(messages).GroupId.Should().NotBeNull();
    }

    [GStreamerOnlyFact]
    public void TheDebugLogIsDeliveredForTheCategoriesAskedUntilDisposed()
    {
        var seen = new List<GStreamerLogMessage>();
        var log = GStreamerLog.Subscribe("videotestsrc:6", message =>
        {
            lock (seen)
            {
                seen.Add(message);
            }

            throw new InvalidOperationException("a handler that throws must not matter");
        });
        Run();
        log.Dispose();
        int count;
        lock (seen)
        {
            count = seen.Count(m => m.Category == "videotestsrc");
        }

        Run();

        count.Should().BeGreaterThan(0);
        lock (seen)
        {
            seen.Count(m => m.Category == "videotestsrc").Should().Be(count, "nothing is delivered once disposed");
            var message = seen.First(m => m.Category == "videotestsrc");
            message.Message.Should().NotBeNullOrEmpty();
            message.Function.Should().NotBeNullOrEmpty();
            message.File.Should().NotBeNullOrEmpty();
            message.Line.Should().BeGreaterThan(0);
            message.Level.Should().BeOneOf(GstDebugLevel.Error, GstDebugLevel.Warning, GstDebugLevel.Fixme, GstDebugLevel.Info,
                GstDebugLevel.Debug, GstDebugLevel.Log);
        }

        static void Run()
        {
            using var pipeline = GStreamerPipeline.Parse("videotestsrc num-buffers=3 ! fakesink");
            pipeline.Play();
            pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();
        }
    }
}
