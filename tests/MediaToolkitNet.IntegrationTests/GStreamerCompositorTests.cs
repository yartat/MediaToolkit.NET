#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.FFmpeg.Reading;
using MediaToolkitNet.GStreamer;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// The compositor on layers of solid colour, so that where each one lands, how big it is, how much of it shows through and which
/// is on top can be read from single pixels of the picture that comes out.
/// </summary>
[SupportedOSPlatform("linux")]
public unsafe class GStreamerCompositorTests
{
    private static readonly VideoFormat Output = new(320, 240, PixelFormat.Bgra32, new Rational(25, 1));
    private static readonly (int R, int G, int B) Orange = (200, 100, 30);
    private static readonly (int R, int G, int B) Blue = (30, 100, 200);
    private static readonly (int R, int G, int B) Green = (20, 180, 40);

    [GStreamerOnlyFact]
    public void ALayerLandsWhereItWasPutAndTheRestIsTheLayerBelow()
    {
        using var compositor = new GStreamerCompositor(Output);
        var below = compositor.AddLayer(Output);
        var above = compositor.AddLayer(new VideoFormat(100, 80, PixelFormat.Bgra32, new Rational(25, 1)), x: 20, y: 30);
        using var result = new Result(compositor);
        compositor.Start();

        Push(compositor, below, Output, Orange, frames: 10);
        Push(compositor, above, new VideoFormat(100, 80, PixelFormat.Bgra32, new Rational(25, 1)), Blue, frames: 10);
        compositor.EndOfStream();
        result.WaitForFrames(8);

        result.Should(60, 60, Blue, "inside the upper layer");
        result.Should(20, 30, Blue, "its top left pixel");
        result.Should(119, 109, Blue, "its bottom right pixel");
        result.Should(120, 110, Orange, "just outside it");
        result.Should(19, 30, Orange, "just left of it");
        result.Should(300, 200, Orange, "far from it");
    }

    [GStreamerOnlyFact]
    public void ALayerIsScaledToTheSizeAskedFor()
    {
        using var compositor = new GStreamerCompositor(Output);
        var below = compositor.AddLayer(Output);
        var small = new VideoFormat(64, 48, PixelFormat.Bgra32, new Rational(25, 1));
        var above = compositor.AddLayer(small, x: 100, y: 60, width: 160, height: 120);
        using var result = new Result(compositor);
        compositor.Start();

        Push(compositor, below, Output, Orange, frames: 10);
        Push(compositor, above, small, Green, frames: 10);
        compositor.EndOfStream();
        result.WaitForFrames(8);

        result.Should(259, 179, Green, "the last pixel of the scaled layer");
        result.Should(260, 180, Orange, "the next one is outside");
        result.Should(100, 60, Green, "its first pixel");
    }

    [GStreamerOnlyFact]
    public void AnAlphaBelowOneLetsTheLayerBelowShowThrough()
    {
        using var compositor = new GStreamerCompositor(Output);
        var below = compositor.AddLayer(Output);
        var above = compositor.AddLayer(Output, alpha: 0.5);
        using var result = new Result(compositor);
        compositor.Start();

        Push(compositor, below, Output, Orange, frames: 10);
        Push(compositor, above, Output, Blue, frames: 10);
        compositor.EndOfStream();
        result.WaitForFrames(8);

        // Half of each: R (200+30)/2, G (100+100)/2, B (30+200)/2.
        result.Should(160, 120, (115, 100, 115), "an even mix of both", tolerance: 8);
    }

    [GStreamerOnlyFact]
    public void ALayerWithAHigherZOrderIsOnTopWhateverItsIndex()
    {
        using var compositor = new GStreamerCompositor(Output);
        var first = compositor.AddLayer(Output);
        var second = compositor.AddLayer(Output);
        using var result = new Result(compositor);
        compositor.Start();

        Push(compositor, first, Output, Orange, frames: 5);
        Push(compositor, second, Output, Blue, frames: 5);
        result.WaitForFrames(3);
        result.Should(160, 120, Blue, "the later layer is on top to begin with", waitFor: true);

        compositor.SetZOrder(first, 10);
        Push(compositor, first, Output, Orange, frames: 5, from: 5);
        Push(compositor, second, Output, Blue, frames: 5, from: 5);
        compositor.EndOfStream();

        result.Should(160, 120, Orange, "the first layer was raised above it", waitFor: true);
    }

    [GStreamerOnlyFact]
    public void ALayerCanBeMovedWhileItRuns()
    {
        var small = new VideoFormat(80, 60, PixelFormat.Bgra32, new Rational(25, 1));
        using var compositor = new GStreamerCompositor(Output);
        var below = compositor.AddLayer(Output);
        var above = compositor.AddLayer(small, x: 10, y: 10);
        using var result = new Result(compositor);
        compositor.Start();

        Push(compositor, below, Output, Orange, frames: 8);
        Push(compositor, above, small, Blue, frames: 8);
        result.Should(40, 30, Blue, "at first it is at the top left", waitFor: true);

        compositor.SetPosition(above, 200, 150);
        Push(compositor, below, Output, Orange, frames: 8, from: 8);
        Push(compositor, above, small, Blue, frames: 8, from: 8);
        compositor.EndOfStream();

        result.Should(240, 180, Blue, "then at the bottom right", waitFor: true);
        result.Should(40, 30, Orange, "and no longer where it was");
    }

    [GStreamerOnlyFact]
    public void ALayerThatEndsLeavesTheOthersGoingAndTheFrameCountFollowsTheRate()
    {
        var small = new VideoFormat(80, 60, PixelFormat.Bgra32, new Rational(25, 1));
        using var compositor = new GStreamerCompositor(Output);
        var below = compositor.AddLayer(Output);
        var above = compositor.AddLayer(small, x: 10, y: 10);
        using var result = new Result(compositor);
        compositor.Start();

        Push(compositor, above, small, Blue, frames: 5);
        compositor.EndOfStream(above);
        Push(compositor, below, Output, Orange, frames: 25);
        compositor.EndOfStream(below);
        result.WaitForFrames(20);

        result.Count.Should().BeInRange(23, 26, "twenty-five frames of the base layer at 25 a second");
        result.Format.Should().Be(Output);
        result.Should(40, 30, Orange, "the layer that ended is gone from the last frame");
    }

    [GStreamerOnlyFact]
    public void MisuseIsRefusedWithWhatWasWrong()
    {
        using var compositor = new GStreamerCompositor(Output);
        var layer = compositor.AddLayer(Output);
        var data = new byte[320 * 240 * 4];

        fixed (byte* pinned = data)
        {
            // A lambda cannot use a fixed pointer, so it takes the address as a number; the array stays pinned for the block.
            var address = (nint)pinned;
            var early = () => compositor.Push(layer, new VideoFrame(Output, TimeSpan.Zero, (byte*)address, 320 * 4));
            early.Should().Throw<InvalidOperationException>("nothing is pushed before Start");

            compositor.Start();
            var late = () => compositor.AddLayer(Output);
            late.Should().Throw<InvalidOperationException>("the layers are fixed once it runs");

            var unknown = () => compositor.Push(7, new VideoFrame(Output, TimeSpan.Zero, (byte*)address, 320 * 4));
            unknown.Should().Throw<ArgumentOutOfRangeException>();

            var wrong = new VideoFormat(100, 100, PixelFormat.Bgra32, new Rational(25, 1));
            var mismatch = () => compositor.Push(layer, new VideoFrame(wrong, TimeSpan.Zero, (byte*)address, 100 * 4));
            mismatch.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("100x100").And.Contain("320x240");
        }

        var empty = new GStreamerCompositor(Output);
        var start = () => empty.Start();
        start.Should().Throw<InvalidOperationException>("a compositor with no layer has nothing to compose");
        empty.Dispose();
    }

    [GStreamerFact]
    public void ComposedFramesGoStraightToTheRecorderAndPlayBackAsComposed()
    {
        using var media = new TestMedia();
        var path = media.PathTo("composed.mp4");
        var small = new VideoFormat(100, 80, PixelFormat.Bgra32, new Rational(25, 1));

        using (var recorder = new GStreamerRecorder(path))
        using (var compositor = new GStreamerCompositor(Output))
        {
            var video = recorder.AddVideoStream(new VideoEncodingSettings(Output, MediaCodec.H264, 0, 25));
            var below = compositor.AddLayer(Output);
            var above = compositor.AddLayer(small, x: 20, y: 30);
            var recorded = 0;
            compositor.FrameComposed += (in VideoFrame frame) =>
            {
                recorder.WriteVideo(video, in frame);
                Interlocked.Increment(ref recorded);
            };
            recorder.Start();
            compositor.Start();

            Push(compositor, below, Output, Orange, frames: 25);
            Push(compositor, above, small, Blue, frames: 25);
            compositor.EndOfStream();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && Volatile.Read(ref recorded) < 24)
            {
                Thread.Sleep(20);
            }

            recorder.Stop();
        }

        var info = new FFmpegProber().Probe(path);
        var picture = info.OfKind(MediaStreamKind.Video).Single().Video!.Value;
        (picture.Width, picture.Height).Should().Be((320, 240));
        FfmpegProgram.Count(path, string.Empty).Should().BeInRange(24, 26);

        using var reader = FFmpegMediaReader.Open(path, new MediaReaderOptions { VideoPixelFormat = PixelFormat.Bgra32 });
        (int R, int G, int B)? inside = null;
        (int R, int G, int B)? outside = null;
        reader.Video!.TryReadNext((in VideoFrame f) =>
        {
            var plane = f.GetPlane(0);
            var at = (60 * f.Stride(0)) + (60 * 4);
            inside = (plane[at + 2], plane[at + 1], plane[at]);
            var far = (200 * f.Stride(0)) + (300 * 4);
            outside = (plane[far + 2], plane[far + 1], plane[far]);
        }).Should().BeTrue();
        inside!.Value.B.Should().BeCloseTo(Blue.B, 16);
        inside.Value.R.Should().BeCloseTo(Blue.R, 16);
        outside!.Value.R.Should().BeCloseTo(Orange.R, 16);
        outside.Value.B.Should().BeCloseTo(Orange.B, 16);
    }

    [GStreamerOnlyFact]
    public void DisposingWhileFramesAreBeingPushedLetsGo()
    {
        for (var i = 0; i < 10; i++)
        {
            var compositor = new GStreamerCompositor(Output);
            var layer = compositor.AddLayer(Output);
            compositor.FrameComposed += (in VideoFrame _) => { };
            compositor.Start();
            var data = new byte[320 * 240 * 4];
            fixed (byte* p = data)
            {
                for (var f = 0; f < 5; f++)
                {
                    compositor.Push(layer, new VideoFrame(Output, TimeSpan.FromMilliseconds(40 * f), p, 320 * 4));
                }
            }

            compositor.Dispose();
        }
    }

    private static void Push(GStreamerCompositor compositor, int layer, VideoFormat format, (int R, int G, int B) colour, int frames, int from = 0)
    {
        var data = new byte[format.Width * format.Height * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = (byte)colour.B;
            data[i + 1] = (byte)colour.G;
            data[i + 2] = (byte)colour.R;
            data[i + 3] = 255;
        }

        fixed (byte* p = data)
        {
            for (var i = from; i < from + frames; i++)
            {
                compositor.Push(layer, new VideoFrame(format, TimeSpan.FromMilliseconds(40 * i), p, format.Width * 4));
            }
        }
    }

    /// <summary>Keeps the composed frames as they come, so that single pixels of the latest can be read.</summary>
    private sealed class Result : IDisposable
    {
        private readonly GStreamerCompositor _compositor;
        private readonly Lock _gate = new();
        private byte[] _last = [];
        private int _stride;
        private int _count;

        public Result(GStreamerCompositor compositor)
        {
            _compositor = compositor;
            compositor.FrameComposed += OnFrame;
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _count;
                }
            }
        }

        public VideoFormat Format { get; private set; }

        public void WaitForFrames(int count) => Wait(() => Count >= count);

        /// <summary>Asserts the colour of one pixel of the latest frame; with <paramref name="waitFor"/> it is waited for first.</summary>
        public void Should(int x, int y, (int R, int G, int B) colour, string because, int tolerance = 6, bool waitFor = false)
        {
            if (waitFor)
            {
                Wait(() => Near(Pixel(x, y), colour, tolerance));
            }

            var pixel = Pixel(x, y);
            pixel.R.Should().BeCloseTo(colour.R, (uint)tolerance, because);
            pixel.G.Should().BeCloseTo(colour.G, (uint)tolerance, because);
            pixel.B.Should().BeCloseTo(colour.B, (uint)tolerance, because);
        }

        public void Dispose() => _compositor.FrameComposed -= OnFrame;

        private static bool Near((int R, int G, int B) a, (int R, int G, int B) b, int tolerance) =>
            Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

        private (int R, int G, int B) Pixel(int x, int y)
        {
            lock (_gate)
            {
                if (_last.Length == 0)
                {
                    return (-1, -1, -1);
                }

                var at = (y * _stride) + (x * 4);
                return (_last[at + 2], _last[at + 1], _last[at]);
            }
        }

        private void OnFrame(in VideoFrame frame)
        {
            var plane = frame.GetPlane(0).ToArray();
            lock (_gate)
            {
                _last = plane;
                _stride = frame.Stride(0);
                _count++;
                Format = frame.Format;
            }
        }

        private static void Wait(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !condition())
            {
                Thread.Sleep(20);
            }
        }
    }
}
