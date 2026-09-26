#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.InteropServices;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg.Native;

namespace MediaToolkitNet.FFmpeg.Transcoding;

/// <summary>
/// Converts text subtitles to another text format: decoded to ASS events, the
/// form every FFmpeg text decoder produces, and encoded again.
/// </summary>
internal sealed unsafe class SubtitlePipeline : StreamPipeline
{
    /// <summary>Room for one encoded event. SubRip and ASS lines are a few hundred bytes.</summary>
    private const int BufferSize = 1 << 20;

    private static readonly AVRationalNative Microseconds = new(1, AVConstants.TimeBase);
    private static readonly AVRationalNative Milliseconds = new(1, 1000);

    private FFmpegDecoder _decoder = null!;
    private void* _encoder;
    private byte* _buffer;
    private byte* _subtitle;
    private AVPacketNative* _packet;

    public SubtitlePipeline(PlannedStream plan, FFmpegDemuxer demuxer, TimeWindow window)
        : base(plan, demuxer, window)
    {
    }

    /// <summary>The encoder that was opened, for messages.</summary>
    public string EncoderName { get; private set; } = string.Empty;

    public override void Process(AVPacketNative* packet)
    {
        new Span<byte>(_subtitle, AbiLayout.SubtitleSize).Clear();

        int got;
        var decoded = AV.avcodec_decode_subtitle2(_decoder.Handle, _subtitle, &got, packet);
        if (decoded < 0 || got == 0)
        {
            // A packet that decodes to nothing, or not at all, costs one line of
            // subtitles; it is not worth abandoning the whole file over.
            return;
        }

        try
        {
            Convert(packet);
        }
        finally
        {
            AV.avsubtitle_free(_subtitle);
        }
    }

    public override void Dispose()
    {
        _decoder?.Dispose();

        if (_encoder is not null)
        {
            var encoder = _encoder;
            AV.avcodec_free_context(&encoder);
            _encoder = null;
        }

        if (_buffer is not null)
        {
            NativeMemory.Free(_buffer);
            _buffer = null;
        }

        if (_subtitle is not null)
        {
            NativeMemory.Free(_subtitle);
            _subtitle = null;
        }

        if (_packet is not null)
        {
            fixed (AVPacketNative** packet = &_packet)
            {
                AV.av_packet_free(packet);
            }
        }
    }

    protected override int OpenCore(FFmpegMuxer muxer)
    {
        AbiLayout.RequireSubtitleLayout();

        _decoder = FFmpegDecoder.Open(Demuxer, SourceIndex);
        var codec = EncoderFactory.Find(Plan.SubtitleCodec!.Value, MediaCodec.SubRip, null, out var encoderName);
        EncoderName = encoderName;

        _encoder = FFmpegError.CheckAlloc(AV.avcodec_alloc_context3(codec), "avcodec_alloc_context3");
        AbiLayout.SetCodecTimeBase(_encoder, Milliseconds);

        // The ASS encoder writes nothing usable without a header, and MP4 timed
        // text takes its default style from it. Every text decoder makes one,
        // even SubRip's, which is a default style; ffmpeg hands it over the same way.
        if (AbiLayout.SubtitleHeaderOf(_decoder.Handle) is { } header)
        {
            AbiLayout.SetSubtitleHeader(_encoder, header);
        }

        FFmpegError.Check(AV.avcodec_open2(_encoder, codec, null), $"avcodec_open2({encoderName})");

        _buffer = (byte*)NativeMemory.Alloc(BufferSize);
        _subtitle = (byte*)NativeMemory.AllocZeroed(AbiLayout.SubtitleSize);
        _packet = (AVPacketNative*)FFmpegError.CheckAlloc(AV.av_packet_alloc(), "av_packet_alloc");
        return muxer.AddEncodedStream(_encoder, Milliseconds);
    }

    private void Convert(AVPacketNative* source)
    {
        var (pts, startMs, endMs) = AbiLayout.TimingOfSubtitle(_subtitle);
        if (pts == AVConstants.NoPtsValue)
        {
            if (source->Pts == AVConstants.NoPtsValue)
            {
                return;
            }

            pts = AV.av_rescale_q(source->Pts, InputTimeBase, Microseconds);
        }

        // An event with no end is shown until the next; the packet duration is
        // the best guess there is.
        if (endMs <= startMs && source->Duration > 0)
        {
            endMs = startMs + (uint)AV.av_rescale_q(source->Duration, InputTimeBase, Milliseconds);
        }

        var from = Window.From.Ticks / 10;
        var start = pts + (startMs * 1000L);
        var end = pts + (endMs * 1000L);
        if (end <= from)
        {
            return;
        }

        if (Window.To is { } to && start >= to.Ticks / 10)
        {
            Done = true;
            return;
        }

        // An event already on screen at the start is cut to begin there, and one
        // still up at the end is cut there.
        if (Window.To is { } stop && end > stop.Ticks / 10)
        {
            end = stop.Ticks / 10;
        }

        start = Math.Max(start, from) - from;
        end -= from;

        AbiLayout.SetTimingOfSubtitle(_subtitle, start, 0, (uint)((end - start) / 1000));
        var size = AV.avcodec_encode_subtitle(_encoder, _buffer, BufferSize, _subtitle);
        if (size < 0)
        {
            throw new MediaToolkitNetException(
                FFmpegLibraries.BackendName,
                $"avcodec_encode_subtitle({EncoderName}): {FFmpegError.Describe(size)}",
                size);
        }

        if (size == 0)
        {
            return;
        }

        FFmpegError.Check(AV.av_new_packet(_packet, size), "av_new_packet");
        new ReadOnlySpan<byte>(_buffer, size).CopyTo(new Span<byte>(_packet->Data, size));
        _packet->Pts = AV.av_rescale_q(start, Microseconds, OutputTimeBase);
        _packet->Dts = _packet->Pts;
        _packet->Duration = AV.av_rescale_q(end - start, Microseconds, OutputTimeBase);
        _packet->Flags = AVConstants.PacketFlagKey;
        Write(_packet);
    }
}
