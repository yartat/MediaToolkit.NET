#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.GStreamer;
using MediaToolkitNet.GStreamer.Native;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// Frames going into a pipeline through <c>appsrc</c>, and typed reads and writes of element properties. Every test
/// pushes values that differ from any default and reads them back out of the far end of the pipeline.
/// </summary>
[SupportedOSPlatform("linux")]
public unsafe class GStreamerSourceTests
{
    [GStreamerOnlyFact]
    public void VideoFramesComeOutWithTheirPixelsAndTimestamps()
    {
        using var pipeline = GStreamerPipeline.Parse("appsrc name=src ! appsink name=sink sync=false");
        var source = pipeline.GetSource("src");
        var sink = pipeline.GetSink("sink");
        var format = new VideoFormat(64, 48, PixelFormat.Bgra32, new Rational(25, 1));
        source.SetFormat(format);
        pipeline.Play();

        var data = new byte[64 * 48 * 4];
        for (var i = 0; i < 5; i++)
        {
            Array.Fill(data, (byte)(10 + i));
            fixed (byte* p = data)
            {
                source.PushVideo(new VideoFrame(format, TimeSpan.FromMilliseconds(40 * i), p, 64 * 4)).Should().BeTrue();
            }
        }

        source.EndOfStream();
        var seen = new List<(byte First, byte Last, long Ms, int Width)>();
        while (sink.TryPullVideo(TimeSpan.FromSeconds(3), (in VideoFrame f) =>
                   seen.Add((f.GetPlane(0)[0], f.GetPlane(0)[(64 * 48 * 4) - 1], (long)f.Timestamp.TotalMilliseconds, f.Format.Width))))
        {
        }

        seen.Should().Equal((10, 10, 0, 64), (11, 11, 40, 64), (12, 12, 80, 64), (13, 13, 120, 64), (14, 14, 160, 64));
        sink.IsEndOfStream.Should().BeTrue();
    }

    [GStreamerOnlyFact]
    public void ARowLongerThanThePictureIsRepackedToGStreamersStrides()
    {
        // 34 wide: the chroma planes are 17 wide, and GStreamer pads every row to four bytes. The frame's own strides are
        // larger still, so a copy that trusts either side's stride lands the planes in the wrong place.
        using var pipeline = GStreamerPipeline.Parse("appsrc name=src ! appsink name=sink sync=false");
        var source = pipeline.GetSource("src");
        var sink = pipeline.GetSink("sink");
        var format = new VideoFormat(34, 20, PixelFormat.Yuv420P, new Rational(10, 1));
        source.SetFormat(format);
        pipeline.Play();

        var y = new byte[64 * 20];
        var u = new byte[40 * 10];
        var v = new byte[40 * 10];
        for (var row = 0; row < 20; row++)
        {
            Array.Fill(y, (byte)(100 + row), row * 64, 34);
        }

        for (var row = 0; row < 10; row++)
        {
            Array.Fill(u, (byte)(50 + row), row * 40, 17);
            Array.Fill(v, (byte)(150 + row), row * 40, 17);
        }

        fixed (byte* py = y)
        fixed (byte* pu = u)
        fixed (byte* pv = v)
        {
            Span<nint> planes = [(nint)py, (nint)pu, (nint)pv];
            Span<int> strides = [64, 40, 40];
            source.PushVideo(new VideoFrame(format, TimeSpan.Zero, planes, strides, 3)).Should().BeTrue();
        }

        source.EndOfStream();
        var rows = new List<byte>();
        sink.TryPullVideo(TimeSpan.FromSeconds(3), (in VideoFrame f) =>
        {
            for (var row = 0; row < 20; row++)
            {
                rows.Add(f.GetPlane(0)[row * f.Stride(0)]);
            }

            for (var row = 0; row < 10; row++)
            {
                rows.Add(f.GetPlane(1)[row * f.Stride(1)]);
                rows.Add(f.GetPlane(2)[(row * f.Stride(2)) + 16]);
            }
        }).Should().BeTrue();

        rows.Should().Equal(
            Enumerable.Range(0, 20).Select(r => (byte)(100 + r))
                .Concat(Enumerable.Range(0, 10).SelectMany(r => new[] { (byte)(50 + r), (byte)(150 + r) })));
    }

    [GStreamerOnlyFact]
    public void AudioBlocksComeOutWithTheirSamplesAndTimestamps()
    {
        using var pipeline = GStreamerPipeline.Parse("appsrc name=src ! appsink name=sink sync=false");
        var source = pipeline.GetSource("src");
        var sink = pipeline.GetSink("sink");
        var format = new AudioFormat(48000, 2, SampleFormat.S16);
        source.SetFormat(format);
        pipeline.Play();

        for (var block = 0; block < 3; block++)
        {
            var samples = new short[480 * 2];
            Array.Fill(samples, (short)(1000 * (block + 1)));
            fixed (short* p = samples)
            {
                source.PushAudio(new AudioFrame(format, TimeSpan.FromMilliseconds(10 * block), 480, (byte*)p)).Should().BeTrue();
            }
        }

        source.EndOfStream();
        var seen = new List<(short First, int Samples, long Ms, int Channels)>();
        while (sink.TryPullAudio(TimeSpan.FromSeconds(3), (in AudioFrame f) =>
                   seen.Add((System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(f.Interleaved)[0], f.SampleCount, (long)f.Timestamp.TotalMilliseconds, f.Format.Channels))))
        {
        }

        seen.Should().Equal((1000, 480, 0, 2), (2000, 480, 10, 2), (3000, 480, 20, 2));
    }

    [GStreamerOnlyFact]
    public void ADurationReachesTheBuffer()
    {
        using var pipeline = GStreamerPipeline.Parse("appsrc name=src ! fakesink name=sink signal-handoffs=true sync=false");
        var source = pipeline.GetSource("src");
        var seen = new List<(ulong Pts, ulong Duration, int Size)>();
        pipeline.FindElement("sink")!.Connect("handoff", args =>
        {
            var buffer = (void*)args.Get<nint>(0);
            lock (seen)
            {
                seen.Add((GstLayout.PtsOfBuffer(buffer), GstLayout.DurationOfBuffer(buffer), 0));
            }
        });
        source.SetCaps("application/x-mediatoolkitnet-test");
        pipeline.Play();

        source.Push([1, 2, 3, 4], TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(40)).Should().BeTrue();
        source.EndOfStream();
        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();

        lock (seen)
        {
            seen.Should().ContainSingle().Which.Should().Be((500_000_000UL, 40_000_000UL, 0));
        }
    }

    [GStreamerOnlyFact]
    public void ALongQueueReportsEnoughDataAndTheStartReportsNeedData()
    {
        using var pipeline = GStreamerPipeline.Parse("appsrc name=src block=false ! appsink name=sink");
        var source = pipeline.GetSource("src");
        var format = new VideoFormat(64, 48, PixelFormat.Bgra32, new Rational(25, 1));
        source.SetFormat(format);
        source.Element.SetValue("max-bytes", 30_000UL);
        var needs = 0;
        var enough = 0;
        source.NeedData += (_, _) => Interlocked.Increment(ref needs);
        source.EnoughData += (_, _) => Interlocked.Increment(ref enough);

        // The sink prerolls on the first buffer and takes no more while the pipeline is paused: the rest queue in the source.
        pipeline.Pause();

        // An empty queue is what asks for data, and it asks from GStreamer's own thread a moment after the state change:
        // wait for it before pushing, or a fast push fills the queue first and nothing is ever asked for.
        WaitUntil(() => Volatile.Read(ref needs) > 0);
        Volatile.Read(ref needs).Should().BeGreaterThan(0, "an empty queue asks for data");

        var data = new byte[64 * 48 * 4];
        fixed (byte* p = data)
        {
            for (var i = 0; i < 20; i++)
            {
                source.PushVideo(new VideoFrame(format, TimeSpan.FromMilliseconds(40 * i), p, 64 * 4));
            }
        }

        WaitUntil(() => Volatile.Read(ref enough) > 0);
        Volatile.Read(ref enough).Should().BeGreaterThan(0, "twenty frames of 12 KB do not fit in 30 KB");
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && !condition())
        {
            Thread.Sleep(10);
        }
    }

    [GStreamerOnlyFact]
    public void ABufferPushedAfterTheEndIsRefusedNotThrown()
    {
        using var pipeline = GStreamerPipeline.Parse("appsrc name=src ! fakesink");
        var source = pipeline.GetSource("src");
        source.SetCaps("application/x-mediatoolkitnet-test");
        pipeline.Play();

        source.EndOfStream();

        source.Push([1], TimeSpan.Zero).Should().BeFalse();
    }

    [GStreamerOnlyFact]
    public void AFrameThatDoesNotMatchTheCapsIsRefusedWithBothNamed()
    {
        using var pipeline = GStreamerPipeline.Parse("appsrc name=src ! fakesink");
        var source = pipeline.GetSource("src");
        source.SetFormat(new VideoFormat(64, 48, PixelFormat.Bgra32, new Rational(25, 1)));
        var other = new VideoFormat(32, 32, PixelFormat.Bgra32, new Rational(25, 1));
        var data = new byte[32 * 32 * 4];

        string? message = null;
        fixed (byte* p = data)
        {
            try
            {
                source.PushVideo(new VideoFrame(other, TimeSpan.Zero, p, 32 * 4));
            }
            catch (MediaToolkitNetException ex)
            {
                message = ex.Message;
            }
        }

        message.Should().Contain("32x32").And.Contain("64x48");
    }

    [GStreamerOnlyFact]
    public void TypedPropertiesReadBackWhatWasWritten()
    {
        using var pipeline = GStreamerPipeline.Parse("audiotestsrc name=tone ! fakesink");
        var tone = pipeline.FindElement("tone")!;

        tone.SetValue("freq", 880.5);
        tone.SetValue("is-live", true);
        tone.SetValue("num-buffers", 17);
        tone.SetValue("samplesperbuffer", 441);
        tone.SetValue("wave", 2); // an enum, by its value: square
        tone.SetValue("volume", 0.25);

        tone.GetValue("freq").Should().Be(880.5);
        tone.GetValue("is-live").Should().Be(true);
        tone.GetValue("num-buffers").Should().Be(17);
        tone.GetValue("samplesperbuffer").Should().Be(441);
        tone.GetValue("wave").Should().Be(2, "an enum reads back as its integer value");
        tone.GetValue("volume").Should().Be(0.25);
        tone.Get<double>("freq").Should().Be(880.5);
    }

    [GStreamerOnlyFact]
    public void ATextPropertyAndAnUnsignedOneReadBack()
    {
        using var pipeline = GStreamerPipeline.Parse("appsrc name=src ! identity name=id ! fakesink");
        var source = pipeline.FindElement("src")!;
        var identity = pipeline.FindElement("id")!;

        source.SetValue("max-bytes", 123_456UL);
        identity.SetValue("drop-probability", 0.5f);

        source.GetValue("max-bytes").Should().Be(123_456UL);
        identity.GetValue("drop-probability").Should().Be(0.5f);
        identity.GetValue("name").Should().Be("id");
    }

    [GStreamerOnlyFact]
    public void AValueOfTheWrongTypeNamesThePropertyAndDoesNotSetIt()
    {
        using var pipeline = GStreamerPipeline.Parse("audiotestsrc name=tone ! fakesink");
        var tone = pipeline.FindElement("tone")!;

        var act = () => tone.SetValue("freq", "loud");

        act.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("freq");
        tone.GetValue("freq").Should().Be(440.0, "the property keeps its default");
    }

    [GStreamerOnlyFact]
    public void APropertyThatDoesNotExistIsRefusedWithItsName()
    {
        using var pipeline = GStreamerPipeline.Parse("audiotestsrc name=tone ! fakesink");
        var tone = pipeline.FindElement("tone")!;

        var set = () => tone.SetValue("no-such-property", 1);
        var get = () => tone.GetValue("no-such-property");

        set.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("no-such-property");
        get.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("no-such-property");
    }
}
