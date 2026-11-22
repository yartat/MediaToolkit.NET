#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using FluentAssertions;
using FluentAssertions.Execution;
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

/// <summary>A fact that needs the NVENC H.264 encoder as well as GStreamer and FFmpeg; the machine's GPU decides.</summary>
public sealed class NvencFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="NvencFactAttribute"/> class.</summary>
    public NvencFactAttribute()
    {
        Skip = !OperatingSystem.IsLinux() ? "The GStreamer backend runs on Linux only."
            : !GStreamerBackend.Instance.IsAvailable ? "GStreamer is not usable here."
            : FFmpegEnvironment.SkipReason
            ?? (!GStreamerElements.Exists("nvh264enc") ? "There is no NVENC encoder here." : null);
    }
}

/// <summary>
/// The GStreamer recorder, whose output is read back by FFmpeg's prober, reader and program: the recorder is not asked
/// what it wrote. Every stream is made of values that differ from any default.
/// </summary>
[SupportedOSPlatform("linux")]
public unsafe class GStreamerRecorderTests
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(120);

    [GStreamerFact]
    public void VideoAndAudioAreWrittenAsAskedAndTheColoursSurvive()
    {
        using var media = new TestMedia();
        var path = media.PathTo("rec.mkv");
        var video = new VideoFormat(96, 64, PixelFormat.Bgra32, new Rational(25, 1));
        var audio = new AudioFormat(44100, 1, SampleFormat.F32);

        using (var recorder = new GStreamerRecorder(path))
        {
            var v = recorder.AddVideoStream(new VideoEncodingSettings(video, MediaCodec.H264, 800_000, 25));
            var a = recorder.AddAudioStream(new AudioEncodingSettings(audio, MediaCodec.Flac));
            recorder.IsRecording.Should().BeFalse();
            recorder.Start();
            recorder.IsRecording.Should().BeTrue();

            WriteVideo(recorder, v, video, frames: 50, color: (B: 200, G: 100, R: 30));
            WriteTone(recorder, a, audio, seconds: 2);
            recorder.Elapsed.Should().BeCloseTo(TimeSpan.FromSeconds(2), Tolerance);

            File.Exists(path).Should().BeFalse("the output appears when the recording is stopped, not before");
            recorder.Stop();
            recorder.IsRecording.Should().BeFalse();
        }

        var info = new FFmpegProber().Probe(path);
        using var scope = new AssertionScope();
        info.Container.Should().Be(MediaContainer.Matroska);
        info.OfKind(MediaStreamKind.Video).Should().ContainSingle().Which.Codec.Should().Be(MediaCodec.H264);
        info.OfKind(MediaStreamKind.Audio).Should().ContainSingle().Which.Codec.Should().Be(MediaCodec.Flac);
        var picture = info.OfKind(MediaStreamKind.Video).Single().Video!.Value;
        (picture.Width, picture.Height).Should().Be((96, 64));
        var sound = info.OfKind(MediaStreamKind.Audio).Single().Audio!.Value;
        (sound.SampleRate, sound.Channels).Should().Be((44100, 1));
        info.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(2), Tolerance);

        using var reader = FFmpegMediaReader.Open(path, new MediaReaderOptions { VideoPixelFormat = PixelFormat.Bgra32 });
        (int B, int G, int R)? pixel = null;
        var count = 0;
        while (reader.Video!.TryReadNext((in VideoFrame f) =>
               {
                   var plane = f.GetPlane(0);
                   var middle = ((32 * f.Stride(0)) + (48 * 4));
                   pixel ??= (plane[middle], plane[middle + 1], plane[middle + 2]);
               }))
        {
            count++;
        }

        count.Should().Be(50);
        pixel!.Value.B.Should().BeCloseTo(200, 14);
        pixel.Value.G.Should().BeCloseTo(100, 14);
        pixel.Value.R.Should().BeCloseTo(30, 14);
    }

    [GStreamerFact]
    public void AnAudioFileWithNoMuxerIsWrittenByItsEncoder()
    {
        using var media = new TestMedia();
        var path = media.PathTo("rec.flac");
        var audio = new AudioFormat(22050, 1, SampleFormat.S16);

        using (var recorder = new GStreamerRecorder(path))
        {
            var a = recorder.AddAudioStream(new AudioEncodingSettings(audio, MediaCodec.Flac));
            recorder.Start();
            WriteTone(recorder, a, audio, seconds: 1);
            recorder.Stop();
        }

        var info = new FFmpegProber().Probe(path);
        info.Container.Should().Be(MediaContainer.Flac);
        var sound = info.OfKind(MediaStreamKind.Audio).Single().Audio!.Value;
        (sound.SampleRate, sound.Channels).Should().Be((22050, 1));
        info.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(1), Tolerance);
    }

    [GStreamerFact]
    public void AnUnstoppedRecordingLeavesNothingAndKeepsWhatWasThere()
    {
        using var media = new TestMedia();
        var path = media.PathTo("keep.mkv");
        File.WriteAllText(path, "the previous recording");
        var video = new VideoFormat(64, 48, PixelFormat.Bgra32, new Rational(10, 1));

        using (var recorder = new GStreamerRecorder(path))
        {
            var v = recorder.AddVideoStream(new VideoEncodingSettings(video, MediaCodec.Mjpeg));
            recorder.Start();
            WriteVideo(recorder, v, video, frames: 5, color: (1, 2, 3));
        }

        File.ReadAllText(path).Should().Be("the previous recording");
        Directory.GetFiles(Path.GetDirectoryName(path)!).Should().ContainSingle("no partial file stays beside the output");
    }

    [GStreamerFact]
    public void ASetOfStreamsCannotChangeOnceStartedAndFramesNeedAStart()
    {
        using var media = new TestMedia();
        var video = new VideoFormat(64, 48, PixelFormat.Bgra32, new Rational(10, 1));
        using var recorder = new GStreamerRecorder(media.PathTo("x.mkv"));
        var v = recorder.AddVideoStream(new VideoEncodingSettings(video, MediaCodec.Mjpeg));
        var data = new byte[64 * 48 * 4];

        string? early = null;
        fixed (byte* p = data)
        {
            try
            {
                recorder.WriteVideo(v, new VideoFrame(video, TimeSpan.Zero, p, 64 * 4));
            }
            catch (InvalidOperationException ex)
            {
                early = ex.Message;
            }
        }

        early.Should().NotBeNull("nothing is written before Start");
        recorder.Start();
        var late = () => recorder.AddAudioStream(new AudioEncodingSettings(new AudioFormat(8000, 1, SampleFormat.S16), MediaCodec.Flac));
        late.Should().Throw<InvalidOperationException>();
        recorder.Stop();
        recorder.Stop();
    }

    [GStreamerFact]
    public void ACodecNoElementEncodesIsRefusedWithTheElementsLookedFor()
    {
        using var media = new TestMedia();
        using var recorder = new GStreamerRecorder(media.PathTo("x.mkv"));
        var video = new VideoFormat(64, 48, PixelFormat.Bgra32, new Rational(10, 1));

        var act = () => recorder.AddVideoStream(new VideoEncodingSettings(video, MediaCodec.Aac));

        act.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("Aac");
    }

    [GStreamerFact]
    public void ASegmentDurationSplitsTheRecordingIntoFilesThatPlayOnTheirOwn()
    {
        using var media = new TestMedia();
        var path = media.PathTo("seg.mp4");
        var video = new VideoFormat(64, 48, PixelFormat.Bgra32, new Rational(10, 1));

        using (var recorder = new GStreamerRecorder(path) { SegmentDuration = TimeSpan.FromSeconds(1) })
        {
            var v = recorder.AddVideoStream(new VideoEncodingSettings(video, MediaCodec.H264, 0, 5));
            recorder.Start();
            WriteVideo(recorder, v, video, frames: 30, color: (10, 20, 30));
            recorder.Stop();
        }

        var files = Directory.GetFiles(Path.GetDirectoryName(path)!, "seg_*.mp4").Order().ToList();
        files.Count.Should().BeInRange(3, 4, "three seconds in one-second segments");
        var frames = 0;
        foreach (var file in files)
        {
            new FFmpegProber().Probe(file).OfKind(MediaStreamKind.Video).Should().ContainSingle();
            frames += FfmpegProgram.Count(file, string.Empty);
        }

        frames.Should().Be(30, "no frame is lost between segments");
        File.Exists(path).Should().BeFalse("with segments there is no single output file");
    }

    [NvencFact]
    public void AnEncoderNamedByHandIsTheOneThatRuns()
    {
        using var media = new TestMedia();
        var path = media.PathTo("nv.mp4");
        var video = new VideoFormat(192, 96, PixelFormat.Bgra32, new Rational(20, 1)); // NVENC takes nothing under 160 wide

        using (var recorder = new GStreamerRecorder(path))
        {
            var v = recorder.AddVideoStream(new VideoEncodingSettings(video, MediaCodec.H264, 2_000_000, 10) { EncoderName = "nvh264enc" });
            recorder.Encoders.Should().Equal("nvh264enc");
            recorder.Start();
            WriteVideo(recorder, v, video, frames: 40, color: (90, 90, 90));
            recorder.Stop();
        }

        var info = new FFmpegProber().Probe(path);
        info.OfKind(MediaStreamKind.Video).Single().Codec.Should().Be(MediaCodec.H264);
        info.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(2), Tolerance);
        FfmpegProgram.Count(path, string.Empty).Should().Be(40);
        FfmpegProgram.Count(path, "-skip_frame nokey").Should().BeInRange(4, 5, "a key frame every ten frames");
    }

    private static void WriteVideo(GStreamerRecorder recorder, int stream, VideoFormat format, int frames, (byte B, byte G, byte R) color)
    {
        var data = new byte[format.Width * format.Height * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = color.B;
            data[i + 1] = color.G;
            data[i + 2] = color.R;
            data[i + 3] = 255;
        }

        fixed (byte* p = data)
        {
            for (var i = 0; i < frames; i++)
            {
                recorder.WriteVideo(stream, new VideoFrame(format, TimeSpan.FromSeconds(i / format.FrameRate.Value), p, format.Width * 4));
            }
        }
    }

    private static void WriteTone(GStreamerRecorder recorder, int stream, AudioFormat format, int seconds)
    {
        const int block = 441;
        var bytesPerSample = format.SampleFormat.BytesPerSample();
        var buffer = new byte[block * format.Channels * bytesPerSample];
        var written = 0L;
        var total = (long)seconds * format.SampleRate;
        fixed (byte* p = buffer)
        {
            while (written < total)
            {
                var count = (int)Math.Min(block, total - written);
                for (var s = 0; s < count; s++)
                {
                    var value = Math.Sin(2 * Math.PI * 1000 * (written + s) / format.SampleRate) * 0.5;
                    for (var c = 0; c < format.Channels; c++)
                    {
                        var at = ((s * format.Channels) + c) * bytesPerSample;
                        if (format.SampleFormat == SampleFormat.F32)
                        {
                            BitConverter.TryWriteBytes(new Span<byte>(buffer, at, 4), (float)value);
                        }
                        else
                        {
                            BitConverter.TryWriteBytes(new Span<byte>(buffer, at, 2), (short)(value * short.MaxValue));
                        }
                    }
                }

                recorder.WriteAudio(stream, new AudioFrame(format, TimeSpan.FromSeconds((double)written / format.SampleRate), count, p));
                written += count;
            }
        }
    }
}
