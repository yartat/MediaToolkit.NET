#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using FluentAssertions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.FFmpeg.Filtering;
using MediaToolkitNet.FFmpeg.Reading;
using MediaToolkitNet.FFmpeg.Transcoding;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>AUDIT.md BUG-2, BUG-3, BUG-4 and MEM-4.</summary>
public class AuditFFmpegTranscodeTests
{
    [FFmpegFact]
    public async Task AnEncoderOptionNobodyUnderstoodIsReported()
    {
        using var media = new TestMedia();
        var input = media.WriteVideo("in.mkv");

        var result = await new FFmpegTranscoder().RunAsync(new TranscodeRequest(media.PathTo("out.mkv"))
        {
            Inputs = [input],
            Streams =
            [
                OutputStream.Video(
                    StreamSource.First(MediaStreamKind.Video),
                    new VideoOutputSettings(MediaCodec.Mjpeg)
                    {
                        Options = new Dictionary<string, string> { ["no_such_option"] = "1" },
                    }),
            ],
        });

        result.Warnings.Should().Contain(w => w.Message.Contains("no_such_option"));
    }

    [FFmpegFact]
    public async Task AVideoThatChangesSizeHalfWayIsStillTranscodedToTheSizeAsked()
    {
        using var media = new TestMedia();
        var a = media.PathTo("a.ts");
        var b = media.PathTo("b.ts");
        Ffmpeg($"-f lavfi -i testsrc=d=1:s=160x120:r=25 -c:v mpeg2video -f mpegts \"{a}\"");
        Ffmpeg($"-f lavfi -i testsrc=d=1:s=320x240:r=25 -c:v mpeg2video -output_ts_offset 1 -f mpegts \"{b}\"");
        var joined = Concatenate(media, "joined.ts", a, b);

        var output = media.PathTo("out.mkv");
        await new FFmpegTranscoder().RunAsync(new TranscodeRequest(output)
        {
            Inputs = [joined],
            Streams =
            [
                OutputStream.Video(
                    StreamSource.First(MediaStreamKind.Video),
                    new VideoOutputSettings(MediaCodec.Mjpeg) { Width = 200, Height = 150 }),
            ],
        });

        var video = new FFmpegProber().Probe(output).OfKind(MediaStreamKind.Video).Single().Video!.Value;
        (video.Width, video.Height).Should().Be((200, 150));
    }

    [FFmpegFact]
    public async Task AStreamThatAppearsAfterTheFileWasOpenedDoesNotStopTheJob()
    {
        using var media = new TestMedia();
        var a = media.PathTo("a.ts");
        var b = media.PathTo("b.ts");

        // Past the demuxer's probe window (5 MB) there is audio the open did not see.
        Ffmpeg($"-f lavfi -i testsrc=d=6:s=640x480:r=25 -c:v mpeg2video -b:v 12M -minrate 12M -maxrate 12M -bufsize 2M -mpegts_service_id 1 -streamid 0:256 -f mpegts \"{a}\"");
        Ffmpeg(
            "-f lavfi -i testsrc=d=1:s=640x480:r=25 -f lavfi -i sine=d=1 -c:v mpeg2video -c:a mp2 " +
            $"-mpegts_service_id 1 -streamid 0:256 -streamid 1:257 -output_ts_offset 6 -f mpegts \"{b}\"");
        var joined = Concatenate(media, "late.ts", a, b);
        new FileInfo(a).Length.Should().BeGreaterThan(6_000_000, "the first part must be longer than the probe window");

        var act = () => new FFmpegTranscoder().RunAsync(new TranscodeRequest(media.PathTo("out.mkv"))
        {
            Inputs = [joined],
            Streams =
            [
                OutputStream.Video(
                    StreamSource.First(MediaStreamKind.Video),
                    new VideoOutputSettings(MediaCodec.Mjpeg) { Width = 160, Height = 120 }),
            ],
        });

        await act.Should().NotThrowAsync();
    }

    [FFmpegFact]
    public async Task AudioWhoseTimestampsJumpBackIsStillTranscoded()
    {
        using var media = new TestMedia();
        var a = media.PathTo("a.ts");
        var b = media.PathTo("b.ts");
        Ffmpeg($"-f lavfi -i sine=d=2 -c:a mp2 -f mpegts \"{a}\"");
        Ffmpeg($"-f lavfi -i sine=f=660:d=2 -c:a mp2 -f mpegts \"{b}\"");
        var joined = Concatenate(media, "jump.ts", a, b);

        var act = () => new FFmpegTranscoder().RunAsync(new TranscodeRequest(media.PathTo("out.mka"))
        {
            Inputs = [joined],
            Streams = [OutputStream.Audio(StreamSource.First(MediaStreamKind.Audio), new AudioOutputSettings(MediaCodec.Flac))],
        });

        await act.Should().NotThrowAsync("timestamps that do not increase are dropped or repaired, not passed to the muxer");
    }

    [FFmpegFact]
    public async Task ATrimmedSecondOfAudioHoldsASecondOfSamples()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var output = media.PathTo("trimmed.mkv");

        await new FFmpegTranscoder().RunAsync(new TranscodeRequest(output)
        {
            Inputs = [input],
            Start = TimeSpan.FromSeconds(0.5),
            End = TimeSpan.FromSeconds(1.5),
            Streams =
            [
                OutputStream.Video(StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings(MediaCodec.Mjpeg)),
                OutputStream.Audio(StreamSource.First(MediaStreamKind.Audio), new AudioOutputSettings(MediaCodec.Flac)),
            ],
        });

        var samples = 0L;
        using var reader = FFmpegMediaReader.Open(output);
        while (reader.Audio!.TryReadNext((in AudioFrame frame) => samples += frame.SampleCount))
        {
        }

        samples.Should().BeInRange(46_000, 50_000, "one second at 48 kHz");
    }

    [FFmpegFact]
    public void PlanarAudioWithMoreChannelsThanPlanesIsRefused()
    {
        var format = new AudioFormat(48000, 10, SampleFormat.F32Planar);
        using var graph = FFmpegFilterGraph.ForAudio(format, string.Empty);
        var samples = new float[480];

        unsafe
        {
            fixed (float* data = samples)
            {
                Span<nint> planes = stackalloc nint[MediaPlanes.MaxPlanes];
                for (var i = 0; i < planes.Length; i++)
                {
                    planes[i] = (nint)data;
                }

                var frame = new AudioFrame(format, TimeSpan.Zero, samples.Length, planes, MediaPlanes.MaxPlanes);
                Exception? thrown = null;
                try
                {
                    graph.Push(in frame);
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }

                thrown.Should().BeOfType<ArgumentException>();
            }
        }
    }

    private static string Concatenate(TestMedia media, string name, params string[] parts)
    {
        var path = media.PathTo(name);
        using var output = File.Create(path);
        foreach (var part in parts)
        {
            using var input = File.OpenRead(part);
            input.CopyTo(output);
        }

        return path;
    }

    private static void Ffmpeg(string arguments)
    {
        var directory = Environment.GetEnvironmentVariable(FFmpegEnvironment.DirectoryVariable);
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var program = directory is not null && File.Exists(Path.Combine(directory, exe)) ? Path.Combine(directory, exe) : "ffmpeg";
        using var process = Process.Start(new ProcessStartInfo(program, "-v error -y " + arguments)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"the ffmpeg program is needed to make the test media: {error}");
    }
}
