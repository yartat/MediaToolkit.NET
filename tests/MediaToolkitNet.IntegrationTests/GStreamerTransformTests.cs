#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.GStreamer;
using MediaToolkitNet.GStreamer.Native;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// Elements written in .NET. A registration lasts as long as the process, so every test registers a name of its own.
/// </summary>
[SupportedOSPlatform("linux")]
public class GStreamerTransformTests
{
    private const string Black = "videotestsrc num-buffers=10 pattern=black ! video/x-raw,format=BGRA,width=32,height=16 ! ";

    private static string NewName(string prefix) => $"{prefix}{Guid.NewGuid():n}"[..24];

    private sealed class Invert : GStreamerTransform
    {
        public static int Disposed;
        public static readonly List<int> Widths = [];

        public override void SetCaps(GStreamerCaps caps)
        {
            lock (Widths)
            {
                Widths.Add(caps.Structures[0].Get<int>("width"));
            }
        }

        public override bool Process(Span<byte> data, TimeSpan? timestamp)
        {
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(255 - data[i]);
            }

            return true;
        }

        public override void Dispose() => Interlocked.Increment(ref Disposed);
    }

    private sealed class Every(Func<int, bool> keep, List<long> times) : GStreamerTransform
    {
        private int _count;

        public override bool Process(Span<byte> data, TimeSpan? timestamp)
        {
            times.Add((long)timestamp!.Value.TotalMilliseconds);
            return keep(_count++);
        }
    }

    private static List<(byte Blue, byte Alpha)> Pull(string description)
    {
        using var pipeline = GStreamerPipeline.Parse(description + " ! appsink name=sink sync=false");
        var sink = pipeline.GetSink("sink");
        pipeline.Play();
        var seen = new List<(byte, byte)>();
        while (sink.TryPullVideo(TimeSpan.FromSeconds(5), (in VideoFrame f) => seen.Add((f.GetPlane(0)[0], f.GetPlane(0)[3]))))
        {
        }

        return seen;
    }

    [GStreamerOnlyFact]
    public void AnElementWrittenInDotNetChangesTheBuffersOfAParsedPipeline()
    {
        var name = NewName("mtninvert");
        GStreamerTransform.Register(name, "video/x-raw,format=BGRA", () => new Invert());
        var disposedBefore = Volatile.Read(ref Invert.Disposed);

        var frames = Pull(Black + name);

        frames.Should().HaveCount(10).And.OnlyContain(f => f.Blue == 255 && f.Alpha == 0);
        lock (Invert.Widths)
        {
            Invert.Widths.Should().Contain(32);
        }

        SpinWait.SpinUntil(() => Volatile.Read(ref Invert.Disposed) > disposedBefore, TimeSpan.FromSeconds(3))
            .Should().BeTrue("the instance goes with its element");
    }

    [GStreamerOnlyFact]
    public void ItIsAnElementLikeAnyOtherAndEachOneHasAnInstanceOfItsOwn()
    {
        var name = NewName("mtninvert");
        GStreamerTransform.Register(name, "video/x-raw,format=BGRA", () => new Invert());

        var details = GStreamerElements.Describe(name)!;
        var twice = Pull(Black + name + " ! " + name);
        using var pipeline = GStreamerPipeline.Create();
        var added = pipeline.Add(name, "mine");

        details.Info.Classification.Should().Be("Filter");
        details.PadTemplates.Select(t => (t.Name, t.Direction)).Should().BeEquivalentTo(
            [("sink", GstPadDirection.Sink), ("src", GstPadDirection.Source)]);
        details.PadTemplates.Should().OnlyContain(t => t.Caps.Contains("BGRA"));
        twice.Should().HaveCount(10).And.OnlyContain(f => f.Blue == 0 && f.Alpha == 255, "two inversions cancel");
        added.Name.Should().Be("mine");
    }

    [GStreamerOnlyFact]
    public void BuffersCanBeDroppedAndCarryTheirTimes()
    {
        var name = NewName("mtnhalf");
        var times = new List<long>();
        GStreamerTransform.Register(name, "video/x-raw,format=BGRA", () => new Every(i => i % 2 == 0, times));

        var frames = Pull(
            "videotestsrc num-buffers=10 ! video/x-raw,format=BGRA,width=32,height=16,framerate=25/1 ! " + name);

        frames.Should().HaveCount(5);
        times.Should().Equal(Enumerable.Range(0, 10).Select(i => i * 40L));
    }

    [GStreamerOnlyFact]
    public void AnExceptionStopsTheStreamAndIsReported()
    {
        var name = NewName("mtnfail");
        var reported = new List<string>();
        GStreamerTransform.Register(
            name, "video/x-raw,format=BGRA", () => new Every(_ => throw new InvalidOperationException("boom"), []),
            onError: e =>
            {
                lock (reported)
                {
                    reported.Add(e.Message);
                }
            });
        using var pipeline = GStreamerPipeline.Parse(Black + name + " ! fakesink");

        var run = () =>
        {
            pipeline.Play();
            pipeline.WaitForCompletion(TimeSpan.FromSeconds(5));
        };

        run.Should().Throw<MediaToolkitNetException>();
        lock (reported)
        {
            reported.Should().Contain("boom");
        }
    }

    private sealed class Counted : GStreamerTransform
    {
        public static int Made;
        public static int Gone;

        public Counted() => Interlocked.Increment(ref Made);

        public override bool Process(Span<byte> data, TimeSpan? timestamp)
        {
            _ = new byte[4096]; // work for the collector while GStreamer calls in
            data[0] ^= 0xFF;
            return true;
        }

        public override void Dispose() => Interlocked.Increment(ref Gone);
    }

    [GStreamerOnlyFact]
    public void ManyElementsAtOnceSurviveTheGarbageCollectorAndAreAllLetGo()
    {
        var name = NewName("mtncount");
        GStreamerTransform.Register(name, "video/x-raw,format=BGRA", () => new Counted());

        Parallel.For(0, 40, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var frames = Pull($"videotestsrc num-buffers=30 ! video/x-raw,format=BGRA,width=16,height=16 ! {name} ! {name}");
            frames.Should().HaveCount(30);
            if (i % 5 == 0)
            {
                GC.Collect();
            }
        });

        SpinWait.SpinUntil(() => Volatile.Read(ref Counted.Gone) == 80, TimeSpan.FromSeconds(5)).Should().BeTrue();
        Counted.Made.Should().Be(80, "one instance for each element");
    }

    [GStreamerOnlyFact]
    public void MemoryThatIsNotWritableIsNotWrittenTo()
    {
        var name = NewName("mtninvert");
        GStreamerTransform.Register(name, "application/x-mediatoolkitnet-test", () => new Invert2());
        using var pipeline = GStreamerPipeline.Parse($"appsrc name=src ! {name} ! appsink name=sink sync=false");
        var source = pipeline.GetSource("src");
        var sink = pipeline.GetSink("sink");
        source.SetCaps("application/x-mediatoolkitnet-test");
        pipeline.Play();
        var mine = new byte[] { 1, 2, 3, 4 };
        var seen = Array.Empty<byte>();

        unsafe
        {
            fixed (byte* p = mine)
            {
                source.PushNoCopy((nint)p, mine.Length, TimeSpan.Zero);
                sink.TryPullData(TimeSpan.FromSeconds(3), (in GStreamerData data) => seen = data.Bytes.ToArray()).Should().BeTrue();
                pipeline.Stop();
            }
        }

        seen.Should().Equal(254, 253, 252, 251);
        mine.Should().Equal([1, 2, 3, 4], "the element was given a copy to write into");
    }

    private sealed class Invert2 : GStreamerTransform
    {
        public override bool Process(Span<byte> data, TimeSpan? timestamp)
        {
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(255 - data[i]);
            }

            return true;
        }
    }

    [GStreamerOnlyFact]
    public void ANameCanOnlyBeTakenOnceAndCapsHaveToBeCaps()
    {
        var name = NewName("mtntwice");
        GStreamerTransform.Register(name, "video/x-raw", () => new Invert2());

        var again = () => GStreamerTransform.Register(name, "video/x-raw", () => new Invert2());
        var existing = () => GStreamerTransform.Register("identity", "video/x-raw", () => new Invert2());
        var nonsense = () => GStreamerTransform.Register(NewName("mtnbad"), "=nonsense", () => new Invert2());

        again.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain(name);
        existing.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("identity");
        nonsense.Should().Throw<MediaToolkitNetException>();
    }
}
