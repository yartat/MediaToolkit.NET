#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Globalization;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.FFmpeg.Reading;

/// <summary>
/// The <c>read</c> command: reads a file stream by stream with
/// <see cref="FFmpegMediaReader"/>, in order and by time.
/// </summary>
internal static class ReadCommand
{
    public const string Usage = """
          read <file> [time...] [--format F] [--size WxH] [--rate HZ]
                                       read every stream frame by frame, then the frames at each time
""";

    public static int Run(string[] args)
    {
        var path = args.Length > 1 ? args[1] : throw new ArgumentException("read needs a file.");
        var times = new List<TimeSpan>();
        var options = new MediaReaderOptions();
        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--format":
                    options = options with { VideoPixelFormat = Enum.Parse<PixelFormat>(args[++i], ignoreCase: true) };
                    break;
                case "--size":
                    var size = args[++i].Split('x');
                    options = options with
                    {
                        VideoWidth = int.Parse(size[0], CultureInfo.InvariantCulture),
                        VideoHeight = int.Parse(size[1], CultureInfo.InvariantCulture),
                    };
                    break;
                case "--rate":
                    options = options with { AudioSampleRate = int.Parse(args[++i], CultureInfo.InvariantCulture) };
                    break;
                default:
                    times.Add(TimeSpan.FromSeconds(double.Parse(args[i], CultureInfo.InvariantCulture)));
                    break;
            }
        }

        using var reader = FFmpegMediaReader.Open(path, options);
        Console.WriteLine($"{reader.Info.ContainerName}, {reader.Info.Duration:hh\\:mm\\:ss\\.fff}");

        foreach (var video in reader.VideoStreams)
        {
            var count = 0;
            var checksum = 0L;
            VideoFormat format = default;
            TimeSpan first = TimeSpan.MinValue, last = default;
            while (video.TryReadNext((in VideoFrame frame) =>
            {
                format = frame.Format;
                if (first == TimeSpan.MinValue)
                {
                    first = frame.Timestamp;
                }

                last = frame.Timestamp;
                checksum += Sum(frame.GetPlane(0));
            }))
            {
                count++;
            }

            Console.WriteLine($"  video #{video.Info.Index}: {count} frames of {format}, {first:ss\\.fff}..{last:ss\\.fff} s, checksum {checksum}");
        }

        foreach (var audio in reader.AudioStreams)
        {
            var blocks = 0;
            var samples = 0L;
            AudioFormat format = default;
            while (audio.TryReadNext((in AudioFrame frame) =>
            {
                format = frame.Format;
                samples += frame.SampleCount;
            }))
            {
                blocks++;
            }

            var seconds = format.SampleRate > 0 ? samples / (double)format.SampleRate : 0;
            Console.WriteLine($"  audio #{audio.Info.Index}: {blocks} blocks, {samples} samples of {format} = {seconds:0.000} s");
        }

        foreach (var subtitles in reader.SubtitleStreams)
        {
            Console.WriteLine($"  subtitles #{subtitles.Info.Index} [{subtitles.Info.Language}]:");
            foreach (var cue in subtitles.ReadAll())
            {
                Console.WriteLine($"    {cue.Start:ss\\.fff} -> {cue.End:ss\\.fff}  {cue.Text.Replace("\n", " / ", StringComparison.Ordinal)}");
            }
        }

        // Seeking: the first time from the start, then on from there, then back.
        if (reader.Video is { } seekable && times.Count > 0)
        {
            seekable.Reset();
            foreach (var time in times)
            {
                var shown = TimeSpan.MinValue;
                var found = seekable.TryReadAt(time, (in VideoFrame frame) => shown = frame.Timestamp);
                var next = TimeSpan.MinValue;
                var more = seekable.TryReadNext((in VideoFrame frame) => next = frame.Timestamp);
                var then = more ? next.ToString(@"ss\.fff", CultureInfo.InvariantCulture) : "the end";
                Console.WriteLine(found
                    ? $"  at {time:ss\\.fff}: frame {shown:ss\\.fff}, then {then}"
                    : $"  at {time:ss\\.fff}: past the end");
            }
        }

        return 0;
    }

    private static long Sum(ReadOnlySpan<byte> data)
    {
        var total = 0L;
        for (var i = 0; i < data.Length; i += 997)
        {
            total += data[i];
        }

        return total;
    }
}
