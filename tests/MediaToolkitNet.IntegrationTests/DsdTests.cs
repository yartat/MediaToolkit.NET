#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Buffers.Binary;
using FluentAssertions;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.FFmpeg.Filtering;
using MediaToolkitNet.FFmpeg.Native;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// Direct Stream Digital as FFmpeg 9.1 carries it: a sample format of its own (one byte is eight one-bit samples),
/// which the decoder produces only when asked and which swresample turns into PCM.
/// </summary>
public unsafe class DsdTests
{
    private const int Rate = 2_822_400;

    [FFmpegFact]
    public void ByDefaultADsdStreamIsDecodedToFloatAsBefore()
    {
        using var media = new TestMedia();
        var path = media.WriteDsdiff("noise.dff", Noise(2, 4096), 2);

        var (format, _) = DecodeFirstFrame(path, nativeDsd: false);

        format.Should().Be((int)AVSampleFormat.FltP);
    }

    [FFmpegFeatureFact(nameof(FFmpegFeatures.NativeDsd))]
    public void NativeDecodingHandsOverTheBitstreamUntouched()
    {
        using var media = new TestMedia();
        var source = Noise(2, 4096);
        var path = media.WriteDsdiff("noise.dff", source, 2);

        var (format, bytes) = DecodeFirstFrame(path, nativeDsd: true);

        format.Should().Be((int)AVSampleFormat.Dsd);
        bytes.Should().NotBeEmpty();
        bytes.Should().Equal(source.Take(bytes.Length), "one sample is one byte per channel, interleaved, most significant bit first");
    }

    [FFmpegFact]
    public void NativeDecodingIsRefusedWhereTheLibrariesCannotDoIt()
    {
        if (FFmpegFeatures.NativeDsd)
        {
            return;
        }

        using var media = new TestMedia();
        var path = media.WriteDsdiff("noise.dff", Noise(2, 4096), 2);
        using var demuxer = FFmpegDemuxer.Open(path);

        var open = () => FFmpegDecoder.Open(demuxer, 0, nativeDsd: true);

        open.Should().Throw<NotSupportedException>().WithMessage("*NativeDsd*");
    }

    [FFmpegFeatureFact(nameof(FFmpegFeatures.NativeDsd))]
    public void ADsdFrameBecomesPcmInAFilterGraph()
    {
        using var media = new TestMedia();
        var path = media.WriteDsdiff("tone.dff", Tone(0.1), 2);
        using var demuxer = FFmpegDemuxer.Open(path);
        using var decoder = FFmpegDecoder.Open(demuxer, 0, nativeDsd: true);
        using var graph = FFmpegFilterGraph.ForAudio(
            Rate / 8, (int)AVSampleFormat.Dsd, "stereo", new AVRationalNative(1, Rate / 8), "aresample=44100,aformat=sample_fmts=flt");

        var packet = AV.av_packet_alloc();
        var frame = AV.av_frame_alloc();
        var peak = 0f;
        var samples = 0;
        try
        {
            while (demuxer.ReadPacket(packet))
            {
                decoder.SendPacket(packet);
                while (decoder.ReceiveFrame((AVFrameHead*)frame) == FFmpegDecoder.ReceiveResult.Frame)
                {
                    graph.Push((AVFrameHead*)frame);
                    AV.av_frame_unref(frame);
                }

                AV.av_packet_unref(packet);
                Drain();
            }

            graph.TryPush(null);
            Drain();
        }
        finally
        {
            var f = frame;
            AV.av_frame_free(&f);
            var p = packet;
            AV.av_packet_free(&p);
        }

        graph.OutputAudioFormat.SampleRate.Should().Be(44100);
        samples.Should().BeGreaterThan(4000, "0.1 s at 44.1 kHz is 4410 samples");
        peak.Should().BeInRange(0.35f, 0.65f, "the tone was modulated at half scale");

        void Drain()
        {
            while (graph.TryReceive((in AudioFrame audio) =>
            {
                var plane = audio.GetPlane(0);
                for (var i = 0; i + 4 <= plane.Length; i += 4)
                {
                    peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(plane.Slice(i, 4))));
                }

                samples += audio.SampleCount;
            }))
            {
            }
        }
    }

    private static (int Format, byte[] Bytes) DecodeFirstFrame(string path, bool nativeDsd)
    {
        using var demuxer = FFmpegDemuxer.Open(path);
        using var decoder = FFmpegDecoder.Open(demuxer, 0, nativeDsd: nativeDsd);
        var packet = AV.av_packet_alloc();
        var frame = AV.av_frame_alloc();
        try
        {
            while (demuxer.ReadPacket(packet))
            {
                decoder.SendPacket(packet);
                AV.av_packet_unref(packet);
                if (decoder.ReceiveFrame((AVFrameHead*)frame) == FFmpegDecoder.ReceiveResult.Frame)
                {
                    var head = (AVFrameHead*)frame;
                    var bytes = nativeDsd
                        ? new ReadOnlySpan<byte>((void*)head->Data[0], head->NbSamples * 2).ToArray()
                        : [];
                    return (head->Format, bytes);
                }
            }

            throw new InvalidOperationException("the file produced no frame");
        }
        finally
        {
            var f = frame;
            AV.av_frame_free(&f);
            var p = packet;
            AV.av_packet_free(&p);
        }
    }

    private static byte[] Noise(int channels, int samples)
    {
        var bytes = new byte[channels * samples];
        new Random(11).NextBytes(bytes);
        return bytes;
    }

    // A second-order sigma-delta modulator: a 1 kHz and a 1.5 kHz tone at half scale, one per channel.
    private static byte[] Tone(double seconds)
    {
        var samples = (int)(Rate * seconds) / 8;
        var bytes = new byte[samples * 2];
        for (var channel = 0; channel < 2; channel++)
        {
            var frequency = channel == 0 ? 1000.0 : 1500.0;
            double i1 = 0, i2 = 0;
            for (var n = 0; n < samples * 8; n++)
            {
                var y = i2 >= 0 ? 1.0 : -1.0;
                var x = 0.5 * Math.Sin(2 * Math.PI * frequency * n / Rate);
                i1 += x - y;
                i2 += i1 - y;
                if (y > 0)
                {
                    bytes[((n / 8) * 2) + channel] |= (byte)(0x80 >> (n % 8));
                }
            }
        }

        return bytes;
    }
}

internal static class DsdiffWriter
{
    /// <summary>Writes an uncompressed DSD64 DSDIFF file whose sound data is <paramref name="interleaved"/>.</summary>
    public static string WriteDsdiff(this TestMedia media, string name, byte[] interleaved, int channels)
    {
        var path = media.PathTo(name);
        using var stream = File.Create(path);
        var channelIds = channels == 2 ? "SLFTSRGT"u8.ToArray() : throw new ArgumentOutOfRangeException(nameof(channels));

        byte[] Chunk(string tag, byte[] body)
        {
            var chunk = new byte[12 + body.Length + (body.Length % 2)];
            System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(chunk, 0);
            BinaryPrimitives.WriteUInt64BigEndian(chunk.AsSpan(4), (ulong)body.Length);
            body.CopyTo(chunk, 12);
            return chunk;
        }

        var rate = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(rate, 2_822_400);
        var channelBody = new byte[2 + channelIds.Length];
        BinaryPrimitives.WriteUInt16BigEndian(channelBody, (ushort)channels);
        channelIds.CopyTo(channelBody, 2);
        var compression = new byte[] { (byte)'D', (byte)'S', (byte)'D', (byte)' ', 14 }
            .Concat("not compressed"u8.ToArray()).ToArray();
        var version = new byte[] { 1, 5, 0, 0 };

        var property = "SND "u8.ToArray()
            .Concat(Chunk("FS  ", rate)).Concat(Chunk("CHNL", channelBody)).Concat(Chunk("CMPR", compression)).ToArray();
        var body = "DSD "u8.ToArray()
            .Concat(Chunk("FVER", version)).Concat(Chunk("PROP", property)).Concat(Chunk("DSD ", interleaved)).ToArray();

        stream.Write("FRM8"u8);
        var size = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(size, (ulong)body.Length);
        stream.Write(size);
        stream.Write(body);
        return path;
    }
}
