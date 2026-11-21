#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using FluentAssertions;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.FFmpeg.Reading;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// AUDIT.md SEC-1, MEM-3 and BUG-1. The media is made with the <c>ffmpeg</c>
/// program, found beside the libraries the tests load or on <c>PATH</c>.
/// </summary>
public class AuditFFmpegTests
{
    [FFmpegFact]
    public void AnHlsPlaylistCannotPullInAFileFromAnotherFolder()
    {
        using var media = new TestMedia();
        var segment = media.PathTo("seg.ts");
        Run($"-f lavfi -i testsrc=d=1:s=64x48 -c:v mpeg2video -f mpegts \"{segment}\"");

        var playlist = media.PathTo("local.m3u8");
        File.WriteAllText(
            playlist,
            $"#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\n{segment.Replace('\\', '/')}\n#EXT-X-ENDLIST\n");

        var act = () => new FFmpegProber().Probe(playlist);

        act.Should().Throw<Exception>("a playlist is not a media file, and this one reaches outside its folder");
    }

    [FFmpegFact]
    public void ProbingDoesNotOpenAConnectionUnlessNetworkInputIsAllowed()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            // Run off the test thread: against a server that never answers, a probe
            // that connects would wait for ever.
            var probe = Task.Run(() => new FFmpegProber().Probe($"http://127.0.0.1:{port}/x.ts"));
            try
            {
                probe.Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException)
            {
                // The refusal itself; checked below.
            }

            listener.Pending().Should().BeFalse("the default policy keeps a probe off the network");
            probe.IsFaulted.Should().BeTrue("the input is refused");
        }
        finally
        {
            listener.Stop();
        }
    }

    [FFmpegFact]
    public void AudioFramesReportTheFormatTheDecoderProduced()
    {
        using var media = new TestMedia();
        var late = MakeLateAudioStream(media);

        using var reader = FFmpegMediaReader.Open(late);
        var seen = new List<(int Rate, int Channels)>();
        while (reader.Audio!.TryReadNext((in AudioFrame frame) => seen.Add((frame.Format.SampleRate, frame.Format.Channels))))
        {
        }

        seen.Should().NotBeEmpty();
        seen.Should().OnlyContain(f => f.Rate == 44100 && f.Channels == 1, "the encoder wrote 44.1 kHz mono");
    }

    [FFmpegFact]
    public void ReadingAtATimeInMpegTsNeverReturnsAFrameAfterIt()
    {
        using var media = new TestMedia();
        var ts = media.PathTo("gop.ts");
        Run($"-f lavfi -i testsrc=d=6:s=160x120:r=25 -c:v mpeg2video -g 12 -f mpegts \"{ts}\"");

        using var reader = FFmpegMediaReader.Open(ts);
        var tolerance = TimeSpan.FromSeconds(1.0 / 25);
        foreach (var seconds in new[] { 2.5, 3.0, 4.2 })
        {
            var asked = TimeSpan.FromSeconds(seconds);
            var got = TimeSpan.MinValue;
            reader.Video!.TryReadAt(asked, (in VideoFrame frame) => got = frame.Timestamp).Should().BeTrue();

            got.Should().BeLessThanOrEqualTo(asked, $"the frame on screen at {seconds} s starts at or before it");
            got.Should().BeGreaterThan(asked - (2 * tolerance));
        }
    }

    [FFmpegFact]
    public void AVideoThatChangesSizeIsConvertedToTheSizeAskedForThroughout()
    {
        using var media = new TestMedia();
        var joined = Join(
            media,
            "-f lavfi -i testsrc=d=1:s=160x120:r=25 -c:v mpeg2video -f mpegts",
            "-f lavfi -i testsrc=d=1:s=320x240:r=25 -c:v mpeg2video -output_ts_offset 1 -f mpegts");

        using var reader = FFmpegMediaReader.Open(joined, new MediaReaderOptions { VideoWidth = 200, VideoHeight = 150 });
        var sizes = new List<(int, int)>();
        while (reader.Video!.TryReadNext((in VideoFrame frame) => sizes.Add((frame.Format.Width, frame.Format.Height))))
        {
        }

        sizes.Count.Should().BeGreaterThan(40);
        sizes.Distinct().Should().BeEquivalentTo([(200, 150)]);
    }

    [FFmpegFact]
    public void AVideoThatChangesSizeIsReadAtEachSizeWhenNothingIsAskedFor()
    {
        using var media = new TestMedia();
        var joined = Join(
            media,
            "-f lavfi -i testsrc=d=1:s=160x120:r=25 -c:v mpeg2video -f mpegts",
            "-f lavfi -i testsrc=d=1:s=320x240:r=25 -c:v mpeg2video -output_ts_offset 1 -f mpegts");

        using var reader = FFmpegMediaReader.Open(joined);
        var sizes = new List<(int, int)>();
        while (reader.Video!.TryReadNext((in VideoFrame frame) =>
        {
            // The planes must really hold a frame of the size the format states.
            frame.GetPlane(0).Length.Should().BeGreaterThanOrEqualTo(frame.Format.Width * frame.Format.Height);
            sizes.Add((frame.Format.Width, frame.Format.Height));
        }))
        {
        }

        sizes.Distinct().Should().BeEquivalentTo([(160, 120), (320, 240)]);
    }

    [FFmpegFact]
    public void AudioThatChangesRateAndChannelsReportsEachFramesOwnFormat()
    {
        using var media = new TestMedia();
        var joined = Join(
            media,
            "-f lavfi -i sine=d=1:r=48000 -ac 2 -c:a mp2 -f mpegts",
            "-f lavfi -i sine=d=1:r=44100 -ac 1 -c:a mp2 -output_ts_offset 1 -f mpegts");

        using var reader = FFmpegMediaReader.Open(joined);
        var samples = new Dictionary<(int Rate, int Channels), long>();
        while (reader.Audio!.TryReadNext((in AudioFrame frame) =>
        {
            var key = (frame.Format.SampleRate, frame.Format.Channels);
            samples[key] = samples.GetValueOrDefault(key) + frame.SampleCount;
        }))
        {
        }

        // The decoder may hand out one frame in between, with the new channel
        // count and the old rate; it is still a frame that says what it holds.
        samples.Keys.Should().Contain([(48000, 2), (44100, 1)]);
        samples.Where(f => f.Key != (48000, 2) && f.Key != (44100, 1)).Sum(f => f.Value).Should().BeLessThan(2500);
        samples[(48000, 2)].Should().BeInRange(45_000, 51_000);
        samples[(44100, 1)].Should().BeInRange(41_000, 47_000);
    }

    [FFmpegFact]
    public void AudioThatChangesRateIsConvertedToTheRateAskedForThroughout()
    {
        using var media = new TestMedia();
        var joined = Join(
            media,
            "-f lavfi -i sine=d=1:r=48000 -ac 2 -c:a mp2 -f mpegts",
            "-f lavfi -i sine=d=1:r=44100 -ac 1 -c:a mp2 -output_ts_offset 1 -f mpegts");

        using var reader = FFmpegMediaReader.Open(joined, new MediaReaderOptions { AudioSampleRate = 22050, AudioChannels = 1 });
        var formats = new HashSet<(int, int)>();
        var total = 0L;
        while (reader.Audio!.TryReadNext((in AudioFrame frame) =>
        {
            formats.Add((frame.Format.SampleRate, frame.Format.Channels));
            total += frame.SampleCount;
        }))
        {
        }

        formats.Should().BeEquivalentTo([(22050, 1)]);
        total.Should().BeInRange(40_000, 48_000, "two seconds at 22.05 kHz");
    }

    // Each argument is an ffmpeg command line without its output file; the
    // transport streams are written one after the other into a single file.
    private static string Join(TestMedia media, params string[] parts)
    {
        var files = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            var path = media.PathTo($"part{i}.ts");
            Run($"{parts[i]} \"{path}\"");
            files.Add(path);
        }

        var joined = media.PathTo("joined.ts");
        using var output = File.Create(joined);
        foreach (var part in files)
        {
            using var input = File.OpenRead(part);
            input.CopyTo(output);
        }

        return joined;
    }

    // A transport stream whose audio starts after the video has been going for
    // six seconds, so the demuxer meets it only after it has been opened.
    private static string MakeLateAudioStream(TestMedia media)
    {
        var a = media.PathTo("a.ts");
        var b = media.PathTo("b.ts");
        Run($"-f lavfi -i testsrc=d=6:s=160x120:r=25 -c:v mpeg2video -mpegts_service_id 1 -streamid 0:256 -f mpegts \"{a}\"");
        Run(
            "-f lavfi -i testsrc=d=3:s=160x120:r=25 -f lavfi -i sine=d=3 -c:v mpeg2video -c:a mp2 " +
            $"-mpegts_service_id 1 -streamid 0:256 -streamid 1:257 -output_ts_offset 6 -f mpegts \"{b}\"");
        var late = media.PathTo("late.ts");
        using (var output = File.Create(late))
        {
            foreach (var part in new[] { a, b })
            {
                using var input = File.OpenRead(part);
                input.CopyTo(output);
            }
        }

        return late;
    }

    private static void Run(string arguments)
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
