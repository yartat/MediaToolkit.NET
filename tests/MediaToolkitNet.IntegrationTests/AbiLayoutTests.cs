using FluentAssertions;
using FluentAssertions.Execution;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.FFmpeg.Native;
using MediaToolkitNet.Interop;
using Xunit;
using Xunit.Abstractions;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// Checks every struct field this binding reads by offset against the FFmpeg
/// that is actually installed.
/// </summary>
/// <remarks>
/// <para>
/// The point of these tests is that a wrong offset is caught here rather than in
/// production, where it does not announce itself: reading one is a plausible
/// number from the wrong field, and writing one — the muxer writes codec
/// parameters through <c>AVStream::codecpar</c> — corrupts whatever really lives
/// there.
/// </para>
/// <para>
/// Each test therefore establishes the expected value by a route that does not
/// use the offset it is checking: an AVOption whose write goes through FFmpeg's
/// own offset, a function that fills a struct FFmpeg allocated, or a file whose
/// contents this test wrote itself.
/// </para>
/// </remarks>
public unsafe class AbiLayoutTests(ITestOutputHelper output)
{
    [FFmpegFact]
    public void TheLibrariesLoadedAreOneKnownSeries()
    {
        output.WriteLine($"{FFmpegEnvironment.Versions} — {FFmpegEnvironment.Series}");

        using var _ = new AssertionScope();

        FFmpegLibraries.Generation.Should().NotBeNull();
        FFmpegGeneration.Known.Should().Contain(FFmpegEnvironment.Series);
    }

    [FFmpegFact]
    public void TheOffsetsInUseAreTheOnesOfThatSeries()
    {
        var series = FFmpegEnvironment.Series;

        output.WriteLine($"AVCodecContext::time_base {AbiLayout.CodecContextTimeBase}");
        output.WriteLine($"AVCodecContext::pix_fmt   {AbiLayout.CodecContextPixFmt}");

        using var _ = new AssertionScope();

        AbiLayout.CodecContextTimeBase.Should().Be(
            series.CodecContextTimeBase, "the loader points the layout at the series it found");
        AbiLayout.CodecContextPixFmt.Should().Be(series.CodecContextPixFmt);
    }

    [FFmpegFact]
    public void EveryLayoutSelfCheckPassed()
    {
        using var _ = new AssertionScope();

        AbiLayout.EncoderLayoutVerified.Should().BeTrue("encoding is refused otherwise");
        AbiLayout.CodecParametersLayoutVerified.Should().BeTrue();
        AbiLayout.AudioFrameLayoutVerified.Should().BeTrue("audio encoding is refused otherwise");
    }

    [FFmpegFact]
    public void TheProbedOffsetsLandInsideTheStructsTheyDescribe()
    {
        output.WriteLine(
            $"AVCodecParameters: format {AbiLayout.CodecParametersFormat}, " +
            $"bit_rate {AbiLayout.CodecParametersBitRate}, width {AbiLayout.CodecParametersWidth}, " +
            $"height {AbiLayout.CodecParametersHeight}");
        output.WriteLine(
            $"AVFrame: sample_rate {AbiLayout.FrameSampleRate}, ch_layout {AbiLayout.FrameChannelLayout}");

        using var _ = new AssertionScope();

        AbiLayout.CodecParametersFormat.Should().BeInRange(8, 256);
        AbiLayout.CodecParametersBitRate.Should().BeInRange(8, 256);
        AbiLayout.CodecParametersWidth.Should().Be(AbiLayout.CodecParametersHeight - 4);
        AbiLayout.FrameSampleRate.Should().BeInRange(160, 1024);
        AbiLayout.FrameChannelLayout.Should().BeGreaterThan(AbiLayout.FrameSampleRate);
        (AbiLayout.FrameChannelLayout % 8).Should().Be(0, "AVChannelLayout is eight byte aligned");
    }

    // ------------------------------------------------------------ AVCodecContext

    [FFmpegFact]
    public void CodecContextTimeBaseIsWhereTheOptionWritesIt()
    {
        // av_opt_set reaches the field through FFmpeg's own offset, so reading the
        // value back through ours proves the two agree.
        var codec = Encoder("mjpeg");
        var context = AV.avcodec_alloc_context3(codec);

        try
        {
            AV.SetOption(context, "time_base", "1/12345").Should().Be(0, "AVCodecContext has a time_base option");

            var written = *(AVRationalNative*)((byte*)context + AbiLayout.CodecContextTimeBase);

            using var _ = new AssertionScope();

            written.Num.Should().Be(1, $"time_base was read at {AbiLayout.CodecContextTimeBase}");
            written.Den.Should().Be(12345, $"time_base was read at {AbiLayout.CodecContextTimeBase}");
        }
        finally
        {
            Free(context);
        }
    }

    [FFmpegFact]
    public void CodecContextPixFmtIsWhereParametersToContextWritesIt()
    {
        // avcodec_parameters_to_context copies the format across using FFmpeg's
        // offsets on both sides; a non-zero pixel format keeps a zeroed field from
        // passing by accident.
        var codec = Encoder("mjpeg");
        var context = AV.avcodec_alloc_context3(codec);
        var parameters = AV.avcodec_parameters_alloc();

        try
        {
            AbiLayout.FillCodecParameters(
                parameters,
                AVMediaType.Video,
                AbiLayout.IdOfCodec(codec),
                (int)AVPixelFormat.Yuv422P,
                bitRate: 0,
                width: 320,
                height: 240);

            AV.avcodec_parameters_to_context(context, parameters).Should().BeGreaterThanOrEqualTo(0);

            (*(int*)((byte*)context + AbiLayout.CodecContextPixFmt))
                .Should().Be((int)AVPixelFormat.Yuv422P, $"pix_fmt was read at {AbiLayout.CodecContextPixFmt}");
        }
        finally
        {
            Free(context);
            FreeParameters(parameters);
        }
    }

    [FFmpegTheory]
    [InlineData("mjpeg", AVMediaType.Video)]
    [InlineData("ac3", AVMediaType.Audio)]
    [InlineData("pcm_s16le", AVMediaType.Audio)]
    public void CodecContextCodecTypeSaysWhatKindOfEncoderItIs(string name, AVMediaType expected)
    {
        var codec = Encoder(name);
        var context = AV.avcodec_alloc_context3(codec);

        try
        {
            ((AVMediaType)(*(int*)((byte*)context + AbiLayout.CodecContextCodecType)))
                .Should().Be(expected, $"codec_type was read at {AbiLayout.CodecContextCodecType}");
        }
        finally
        {
            Free(context);
        }
    }

    // ------------------------------------------------------------------ AVCodec

    [FFmpegTheory]
    [InlineData("mjpeg", AVMediaType.Video)]
    [InlineData("ac3", AVMediaType.Audio)]
    public void CodecTypeSaysWhatKindOfCodecItIs(string name, AVMediaType expected) =>
        ((AVMediaType)(*(int*)((byte*)Encoder(name) + AbiLayout.CodecType)))
            .Should().Be(expected, $"AVCodec::type was read at {AbiLayout.CodecType}");

    [FFmpegTheory]
    [InlineData("mjpeg")]
    [InlineData("ac3")]
    [InlineData("aac")]
    public void CodecIdAgreesWithTheParametersFFmpegFillsFromIt(string name)
    {
        // Two routes to the same number: AVCodec::id read by this binding, and
        // AVCodecParameters::codec_id written by avcodec_parameters_from_context.
        var codec = Encoder(name);
        var context = AV.avcodec_alloc_context3(codec);
        var parameters = AV.avcodec_parameters_alloc();

        try
        {
            AV.avcodec_parameters_from_context(parameters, context).Should().BeGreaterThanOrEqualTo(0);

            AbiLayout.CodecIdOf(parameters).Should().Be(
                AbiLayout.IdOfCodec(codec),
                $"AVCodec::id at {AbiLayout.CodecId} and AVCodecParameters::codec_id at " +
                $"{AbiLayout.CodecParametersCodecId} describe the same codec");
        }
        finally
        {
            Free(context);
            FreeParameters(parameters);
        }
    }

    [FFmpegTheory]
    [InlineData("mjpeg", AVMediaType.Video)]
    [InlineData("ac3", AVMediaType.Audio)]
    public void CodecParametersCarryTheKindOfTheirEncoder(string name, AVMediaType expected)
    {
        var codec = Encoder(name);
        var context = AV.avcodec_alloc_context3(codec);
        var parameters = AV.avcodec_parameters_alloc();

        try
        {
            AV.avcodec_parameters_from_context(parameters, context).Should().BeGreaterThanOrEqualTo(0);

            AbiLayout.CodecTypeOf(parameters).Should().Be(
                expected, $"codec_type was read at {AbiLayout.CodecParametersCodecType}");
        }
        finally
        {
            Free(context);
            FreeParameters(parameters);
        }
    }

    // ------------------------------------------------------------------ AVFrame

    [FFmpegFact]
    public void AudioFrameFieldsAreWhereAvFrameGetBufferReadsThem()
    {
        // av_frame_get_buffer allocates from nb_samples, format and ch_layout. Six
        // planar channels means six planes of 1024 floats, and nothing else does.
        var frame = AV.av_frame_alloc();

        try
        {
            AbiLayout.PrepareAudioFrame(
                frame,
                sampleRate: 48000,
                channels: 6,
                channelMask: ChannelLayout.FivePoint1Back,
                sampleFormat: (int)AVSampleFormat.FltP,
                samples: 1024);

            AV.av_frame_get_buffer(frame, 0).Should().BeGreaterThanOrEqualTo(
                0, "FFmpeg read the sample rate and layout where this binding wrote them");

            using var _ = new AssertionScope();

            frame->Linesize[0].Should().Be(1024 * 4, "one plane of 1024 floats");
            frame->Data[5].Should().NotBe(0, "a sixth plane exists only if six channels were understood");
            frame->Data[6].Should().Be(0, "and there is no seventh");
        }
        finally
        {
            var local = frame;
            AV.av_frame_free(&local);
        }
    }

    [FFmpegFact]
    public void PacketFieldsAreWhereTheyAreRead()
    {
        var packet = AV.av_packet_alloc();

        try
        {
            using var _ = new AssertionScope();

            packet->Pos.Should().Be(-1, "av_packet_alloc documents pos as -1");
            packet->Pts.Should().Be(AVConstants.NoPtsValue);
            packet->Dts.Should().Be(AVConstants.NoPtsValue);
            packet->Size.Should().Be(0);
        }
        finally
        {
            var local = packet;
            AV.av_packet_free(&local);
        }
    }

    // --------------------------------------------------- AVFormatContext, AVStream

    [FFmpegFact]
    public void AnOutputContextKeepsItsIoWhereItIsRead()
    {
        using var media = new TestMedia();
        var path = media.PathTo("io.mka");

        void* context = null;
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(path, scratch);
        AV.avformat_alloc_output_context2(&context, null, null, utf8.Pointer).Should().BeGreaterThanOrEqualTo(0);

        try
        {
            ((nint)AbiLayout.GetPb(context)).Should().Be(0, "nothing is open yet");

            void* io = null;
            AV.avio_open(&io, utf8.Pointer, AVConstants.AvioFlagWrite).Should().BeGreaterThanOrEqualTo(0);
            AbiLayout.SetPb(context, io);

            ((nint)AbiLayout.GetPb(context)).Should().Be(
                (nint)io, $"pb was read at {AbiLayout.FormatContextPb}");

            AV.avio_closep((void**)((byte*)context + AbiLayout.FormatContextPb));
        }
        finally
        {
            AV.avformat_free_context(context);
        }
    }

    [FFmpegFact]
    public void AnOpenedInputReportsTheStreamsThatWereWrittenIntoIt()
    {
        using var media = new TestMedia();
        var path = media.WriteVideoAndAudio("two-streams.mkv");

        using var demuxer = FFmpegDemuxer.Open(path);

        using var _ = new AssertionScope();

        demuxer.Streams.Should().HaveCount(2, $"nb_streams was read at {AbiLayout.FormatContextNbStreams}");
        demuxer.Streams[0].MediaType.Should().Be(AVMediaType.Video);
        demuxer.Streams[0].CodecName.Should().Be("mjpeg");
        demuxer.Streams[1].MediaType.Should().Be(AVMediaType.Audio);
        demuxer.Streams[1].CodecName.Should().Be("ac3");
    }

    [FFmpegFact]
    public void EveryStreamKnowsItsOwnPosition()
    {
        using var media = new TestMedia();
        var path = media.WriteVideoAndAudio("indices.mkv");

        using var demuxer = FFmpegDemuxer.Open(path);

        using var _ = new AssertionScope();

        for (var i = 0; i < demuxer.Streams.Count; i++)
        {
            AbiLayout.StreamIndexOf(demuxer.StreamHandle(i)).Should().Be(
                i, $"AVStream::index was read at {AbiLayout.StreamIndex}");
        }
    }

    [FFmpegFact]
    public void AStreamPointsAtTheCodecParametersThatDescribeIt()
    {
        // This is the field the muxer writes through. A wrong offset here does not
        // read rubbish, it writes through whatever the rubbish points at.
        using var media = new TestMedia();
        var path = media.WriteVideoAndAudio("codecpar.mkv");

        using var demuxer = FFmpegDemuxer.Open(path);

        using var _ = new AssertionScope();

        for (var i = 0; i < demuxer.Streams.Count; i++)
        {
            var parameters = demuxer.CodecParameters(i);

            ((nint)parameters).Should().NotBe(
                0, $"AVStream::codecpar was read at {AbiLayout.StreamCodecPar}");
            AbiLayout.CodecTypeOf(parameters).Should().Be(
                demuxer.Streams[i].MediaType,
                $"the parameters at offset {AbiLayout.StreamCodecPar} belong to stream {i}");
            AbiLayout.CodecIdOf(parameters).Should().Be(demuxer.Streams[i].CodecId);
        }
    }

    [FFmpegFact]
    public void AStreamStatesTheTimeBaseAndFrameRateItWasWrittenWith()
    {
        using var media = new TestMedia();
        var path = media.WriteVideo("rate.mkv");

        using var demuxer = FFmpegDemuxer.Open(path);
        var handle = demuxer.StreamHandle(0);
        var timeBase = AbiLayout.TimeBaseOf(handle);
        var frameRate = AbiLayout.AvgFrameRateOf(handle);

        output.WriteLine($"time_base {timeBase.Num}/{timeBase.Den}, avg_frame_rate {frameRate.Num}/{frameRate.Den}");

        using var _ = new AssertionScope();

        timeBase.Num.Should().BeGreaterThan(0, $"time_base was read at {AbiLayout.StreamTimeBase}");
        timeBase.Den.Should().BeGreaterThan(0, $"time_base was read at {AbiLayout.StreamTimeBase}");
        frameRate.Num.Should().Be(25, $"avg_frame_rate was read at {AbiLayout.StreamAvgFrameRate}");
        frameRate.Den.Should().Be(1, $"avg_frame_rate was read at {AbiLayout.StreamAvgFrameRate}");
        AbiLayout.StartTimeOf(handle).Should().Be(0, $"start_time was read at {AbiLayout.StreamStartTime}");
    }

    [FFmpegFact]
    public void AStreamStatesTheLengthAContainerKeepsOneFor()
    {
        // Matroska leaves the stream duration unset and states it per file, so the
        // field is checked against a container that does write it.
        using var media = new TestMedia();
        var path = media.WriteAudio(
            "length.m4a",
            new AudioFormat(48000, 2, SampleFormat.S16, ChannelLayout.Stereo),
            new AudioEncodingSettings(default, MediaCodec.Aac, 128_000));

        using var demuxer = FFmpegDemuxer.Open(path);
        var handle = demuxer.StreamHandle(0);
        var duration = AbiLayout.DurationOf(handle);
        var timeBase = AbiLayout.TimeBaseOf(handle);

        var seconds = duration * timeBase.Num / (double)timeBase.Den;
        output.WriteLine($"duration {duration} × {timeBase.Num}/{timeBase.Den} = {seconds:0.###} s");

        seconds.Should().BeApproximately(
            1d, 0.2d, $"a second of audio was written, and duration was read at {AbiLayout.StreamDuration}");
    }

    [FFmpegFact]
    public void AStreamAtTheWrongPositionIsReportedRatherThanWrittenThrough()
    {
        // The guard that stands between a wrong offset and a write through it.
        using var media = new TestMedia();
        var path = media.WriteVideo("guard.mkv");

        using var demuxer = FFmpegDemuxer.Open(path);
        var handle = demuxer.StreamHandle(0);

        var wrong = () => AbiLayout.ValidateNewStream(handle, expectedIndex: 7);

        wrong.Should().Throw<MediaBackendUnavailableException>().WithMessage("*index*");
    }

    [FFmpegFact]
    public void PacketsComeOutOfTheStreamsTheyBelongTo()
    {
        using var media = new TestMedia();
        var path = media.WriteVideoAndAudio("packets.mkv");

        using var demuxer = FFmpegDemuxer.Open(path);
        var packet = AV.av_packet_alloc();
        var seen = new HashSet<int>();

        try
        {
            while (demuxer.ReadPacket(packet))
            {
                seen.Add(packet->StreamIndex);
                AV.av_packet_unref(packet);
            }
        }
        finally
        {
            var local = packet;
            AV.av_packet_free(&local);
        }

        seen.Should().BeEquivalentTo([0, 1], "both streams carry packets and neither reports a stranger's index");
    }

    private static void* Encoder(string name)
    {
        Span<byte> scratch = stackalloc byte[64];
        using var utf8 = new Utf8Scoped(name, scratch);
        var codec = AV.avcodec_find_encoder_by_name(utf8.Pointer);

        ((nint)codec).Should().NotBe(0, $"every FFmpeg build carries the {name} encoder");
        return codec;
    }

    private static void Free(void* context)
    {
        var local = context;
        AV.avcodec_free_context(&local);
    }

    private static void FreeParameters(void* parameters)
    {
        var local = parameters;
        AV.avcodec_parameters_free(&local);
    }
}
