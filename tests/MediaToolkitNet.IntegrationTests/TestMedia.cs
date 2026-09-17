using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.FFmpeg;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// Writes short files for the tests to read back, and cleans up after itself.
/// </summary>
public sealed class TestMedia : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mediatoolkitnet-tests", Guid.NewGuid().ToString("n"));

    /// <summary>Creates a directory for this test's files.</summary>
    public TestMedia() => Directory.CreateDirectory(_directory);

    /// <summary>A path inside it, which nothing has written to yet.</summary>
    public string PathTo(string name) => System.IO.Path.Combine(_directory, name);

    /// <summary>
    /// Writes one second of a 440 Hz tone.
    /// </summary>
    /// <param name="name">File name, whose extension picks the container.</param>
    /// <param name="format">Rate, channels and layout to encode.</param>
    /// <param name="settings">What to encode it with; its format is replaced.</param>
    /// <returns>Returns the path written.</returns>
    public string WriteAudio(string name, AudioFormat format, AudioEncodingSettings settings)
    {
        var path = PathTo(name);
        using var recorder = new FFmpegRecorder(path);
        var stream = recorder.AddAudioStream(settings with { Format = format });
        recorder.Start();
        WriteTone(recorder, stream, format, format.SampleRate);
        recorder.Stop();
        return path;
    }

    /// <summary>
    /// Writes a second of moving grey at 25 frames per second.
    /// </summary>
    /// <param name="name">File name, whose extension picks the container.</param>
    /// <param name="codec">Video codec to encode with.</param>
    /// <returns>Returns the path written.</returns>
    public string WriteVideo(string name, MediaCodec codec = MediaCodec.Mjpeg)
    {
        var path = PathTo(name);
        using var recorder = new FFmpegRecorder(path);
        var format = new VideoFormat(320, 240, PixelFormat.Yuv420P, new Rational(25, 1));
        var stream = recorder.AddVideoStream(new VideoEncodingSettings(format, codec));
        recorder.Start();
        WriteFrames(recorder, stream, format, 25);
        recorder.Stop();
        return path;
    }

    /// <summary>
    /// Writes a file carrying both a video and an audio stream, in that order.
    /// </summary>
    /// <param name="name">File name, whose extension picks the container.</param>
    /// <returns>Returns the path written.</returns>
    public string WriteVideoAndAudio(string name)
    {
        var path = PathTo(name);
        using var recorder = new FFmpegRecorder(path);

        var videoFormat = new VideoFormat(320, 240, PixelFormat.Yuv420P, new Rational(25, 1));
        var audioFormat = new AudioFormat(48000, 2, SampleFormat.S16, ChannelLayout.Stereo);

        var video = recorder.AddVideoStream(new VideoEncodingSettings(videoFormat, MediaCodec.Mjpeg));
        var audio = recorder.AddAudioStream(new AudioEncodingSettings(audioFormat, MediaCodec.Ac3, 192_000));

        recorder.Start();
        WriteFrames(recorder, video, videoFormat, 25);
        WriteTone(recorder, audio, audioFormat, audioFormat.SampleRate);
        recorder.Stop();
        return path;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary file is not worth failing a test over.
        }
    }

    private static void WriteTone(FFmpegRecorder recorder, int stream, AudioFormat format, int samples)
    {
        const int block = 1024;
        var buffer = new byte[block * format.Channels * 2];
        var written = 0;

        while (written < samples)
        {
            var take = Math.Min(block, samples - written);
            for (var i = 0; i < take; i++)
            {
                var value = (short)(Math.Sin(2 * Math.PI * 440 * (written + i) / format.SampleRate) * 12000);
                for (var channel = 0; channel < format.Channels; channel++)
                {
                    var at = ((i * format.Channels) + channel) * 2;
                    buffer[at] = (byte)(value & 0xFF);
                    buffer[at + 1] = (byte)((value >> 8) & 0xFF);
                }
            }

            unsafe
            {
                fixed (byte* data = buffer)
                {
                    recorder.WriteAudio(stream, new AudioFrame(format, format.DurationOf(written), take, data));
                }
            }

            written += take;
        }
    }

    private static void WriteFrames(FFmpegRecorder recorder, int stream, VideoFormat format, int count)
    {
        var luma = new byte[format.Width * format.Height];
        var chroma = new byte[format.Width * format.Height / 4];

        for (var i = 0; i < count; i++)
        {
            Array.Fill(luma, (byte)(16 + (i * 8)));
            Array.Fill(chroma, (byte)128);

            unsafe
            {
                fixed (byte* y = luma, u = chroma, v = chroma)
                {
                    Span<nint> planes = [(nint)y, (nint)u, (nint)v];
                    Span<int> strides = [format.Width, format.Width / 2, format.Width / 2];
                    recorder.WriteVideo(stream, new VideoFrame(format, TimeSpan.Zero, planes, strides, 3));
                }
            }
        }
    }
}
