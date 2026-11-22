#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using FluentAssertions;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.FFmpeg.Filtering;
using MediaToolkitNet.FFmpeg.Native;
using MediaToolkitNet.FFmpeg.Reading;
using System.Buffers.Binary;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// The API FFmpeg 8.x, 9.0 and 9.1 added, as <c>MediaToolkitNet.FFmpeg</c> exposes
/// it. Each test needs the feature in the libraries that were loaded and is
/// skipped where it is missing.
/// </summary>
public unsafe class NewFFmpegApiTests
{
    // ------------------------------------------------------ frame side data (any series)

    [FFmpegFact]
    public void TheReaderHandsOverTheSideDataOfAFrame()
    {
        using var media = new TestMedia();
        var plain = media.PathTo("plain.mp4");
        var rotated = media.PathTo("rotated.mp4");
        FfmpegProgram.Run($"-f lavfi -i testsrc=d=1:s=320x240:r=25 -c:v libx264 -pix_fmt yuv420p \"{plain}\"");
        FfmpegProgram.Run($"-display_rotation 90 -i \"{plain}\" -c copy \"{rotated}\"");

        using var reader = FFmpegMediaReader.Open(rotated);
        var matrix = new int[9];
        var found = false;
        reader.Video!.TryReadNext((in VideoFrame _, in FFmpegFrameDetails details) =>
        {
            var bytes = details.SideData(FFmpegSideDataType.DisplayMatrix);
            found = bytes.Length == 36;
            if (found)
            {
                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes);
                for (var i = 0; i < 9; i++)
                {
                    matrix[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[(i * 4)..]);
                }
            }
        }).Should().BeTrue();

        found.Should().BeTrue("a 90 degree rotation is stored as a display matrix");

        // A rotation by 90 degrees puts one of the two upper-left entries at 0 and the other at +-1.0 (16.16).
        Math.Abs(matrix[0]).Should().Be(0);
        Math.Abs(matrix[1]).Should().Be(65536);
    }

    // ------------------------------------------- encoder reconfiguration (9.1)

    [FFmpegFeatureFact(nameof(FFmpegFeatures.EncoderReconfiguration))]
    public void AQualityRaisedMidwayShowsInTheSecondHalfOfTheFile()
    {
        if (MediaToolkitNet.FFmpeg.EncoderFactory.TryFind(
                MediaToolkitNet.Abstractions.Recording.MediaCodec.H264, MediaToolkitNet.Abstractions.Recording.MediaCodec.H264, "libx264", out _) is null)
        {
            return; // this FFmpeg has no libx264, the encoder that allows it
        }

        using var media = new TestMedia();
        var path = media.PathTo("reconf.mkv");
        var (first, second) = RecordWithQualityChange(path);

        second.Should().BeGreaterThan(first * 3, $"frames 25-49 took {first} bytes at crf 45, frames 75-99 took {second} after crf 5");
    }

    [FFmpegFeatureFact(nameof(FFmpegFeatures.EncoderReconfiguration))]
    public void AnOptionThatCannotChangeIsRefusedInsteadOfTakingTheProcessDown()
    {
        using var media = new TestMedia();
        var outcome = ChildProbe.Run("reconfigure-bitrate", media.PathTo("x.mkv"));

        outcome.ExitCode.Should().Be(0, outcome.Output);
        outcome.Value("result").Should().BeOneOf("refused", "no-libx264");
    }

    // Noise does not compress, so the quality asked for is what decides the size of a frame.
    private static (long First, long Second) RecordWithQualityChange(string path)
    {
        var format = new MediaToolkitNet.Abstractions.Formats.VideoFormat(
            320, 240, MediaToolkitNet.Abstractions.Formats.PixelFormat.Yuv420P, new MediaToolkitNet.Abstractions.Formats.Rational(25, 1));
        var luma = new byte[320 * 240];
        var chroma = new byte[320 * 240 / 4];
        Array.Fill(chroma, (byte)128);
        var random = new Random(5);

        using (var recorder = new FFmpegRecorder(path))
        {
            var stream = recorder.AddVideoStream(new MediaToolkitNet.Abstractions.Recording.VideoEncodingSettings(
                format, MediaToolkitNet.Abstractions.Recording.MediaCodec.H264, 0, 25)
            {
                EncoderName = "libx264",
                Options = new Dictionary<string, string> { ["preset"] = "ultrafast", ["crf"] = "45" },
            });
            recorder.Start();
            for (var i = 0; i < 100; i++)
            {
                if (i == 50)
                {
                    recorder.Reconfigure(stream, new Dictionary<string, string> { ["crf"] = "5" });
                }

                random.NextBytes(luma);
                fixed (byte* y = luma, u = chroma, v = chroma)
                {
                    Span<nint> planes = [(nint)y, (nint)u, (nint)v];
                    Span<int> strides = [320, 160, 160];
                    recorder.WriteVideo(stream, new VideoFrame(format, TimeSpan.Zero, planes, strides, 3));
                }
            }

            recorder.Stop();
        }

        using var demuxer = FFmpegDemuxer.Open(path);
        var packet = AV.av_packet_alloc();
        try
        {
            long first = 0, second = 0;
            var index = 0;
            while (demuxer.ReadPacket(packet))
            {
                // The settled stretches: the very first frames carry the start-up cost of the rate control.
                if (index is >= 25 and < 50)
                {
                    first += packet->Size;
                }
                else if (index >= 75)
                {
                    second += packet->Size;
                }

                index++;

                AV.av_packet_unref(packet);
            }

            return (first, second);
        }
        finally
        {
            var p = packet;
            AV.av_packet_free(&p);
        }
    }

    // ----------------------------------------------- bitstream filter graphs (9.1)

    [FFmpegFeatureFact(nameof(FFmpegFeatures.BitstreamFilterGraphs))]
    public void PacketsGoThroughAGraphOfASourceAndASinkUnchanged()
    {
        using var media = new TestMedia();
        var clip = media.PathTo("clip.mp4");
        FfmpegProgram.Run($"-f lavfi -i testsrc=d=2:s=320x240:r=25 -c:v libx264 -g 10 -pix_fmt yuv420p \"{clip}\"");

        using var demuxer = FFmpegDemuxer.Open(clip);
        var video = demuxer.FindStream(AVMediaType.Video)!.Value.Index;
        using var graph = FFmpegBitstreamFilterGraph.Create();
        var source = graph.AddSource(demuxer, video);
        var sink = graph.AddSink();
        graph.Link(source, sink);
        graph.Configure();

        var sent = new List<byte[]>();
        var received = new List<byte[]>();
        var packet = AV.av_packet_alloc();
        var filtered = AV.av_packet_alloc();
        try
        {
            void Drain()
            {
                while (graph.TryReceive(sink, filtered))
                {
                    received.Add(new ReadOnlySpan<byte>(filtered->Data, filtered->Size).ToArray());
                    AV.av_packet_unref(filtered);
                }
            }

            while (demuxer.ReadPacket(packet))
            {
                if (packet->StreamIndex != video)
                {
                    AV.av_packet_unref(packet);
                    continue;
                }

                sent.Add(new ReadOnlySpan<byte>(packet->Data, packet->Size).ToArray());
                graph.TrySend(source, packet).Should().BeTrue();
                Drain();
            }

            graph.TrySend(source, null);
            Drain();
        }
        finally
        {
            var p = packet;
            AV.av_packet_free(&p);
            var f = filtered;
            AV.av_packet_free(&f);
        }

        sent.Should().HaveCount(50);
        received.Should().HaveCount(sent.Count);
        for (var i = 0; i < sent.Count; i++)
        {
            received[i].Should().Equal(sent[i], $"packet {i} has no filter to change it");
        }
    }

    [FFmpegFeatureFact(nameof(FFmpegFeatures.BitstreamFilterGraphs))]
    public void AClassicBitstreamFilterIsRefusedWithTheReason()
    {
        using var graph = FFmpegBitstreamFilterGraph.Create();

        var act = () => graph.AddFilter("h264_mp4toannexb");

        act.Should().Throw<MediaToolkitNet.Abstractions.MediaToolkitNetException>()
            .Which.Message.Should().Contain("h264_mp4toannexb").And.Contain("graph");
    }

    [FFmpegFeatureFact(nameof(FFmpegFeatures.BitstreamFilterGraphs))]
    public void AFilterThatTakesSeveralInputsCanBeAdded()
    {
        using var graph = FFmpegBitstreamFilterGraph.Create();

        // lcevc_merge joins the base layer of a stream and its enhancement layer.
        var act = () => graph.AddFilter("lcevc_merge");

        act.Should().NotThrow();
    }

    [FFmpegFeatureFact(nameof(FFmpegFeatures.BitstreamFilterGraphs))]
    public void AFilterThatDoesNotExistIsNamedInTheError()
    {
        using var graph = FFmpegBitstreamFilterGraph.Create();

        var act = () => graph.AddFilter("no_such_filter");

        act.Should().Throw<MediaToolkitNet.Abstractions.MediaToolkitNetException>().Which.Message.Should().Contain("no_such_filter");
    }

    // ----------------------------------------------------- gain map and downmix (9.1)

    private static FFmpegGainMap SampleGainMap() => new(
        0,
        true,
        new Rational(0, 1),
        new Rational(3, 1),
        [new FFmpegGainMapChannel(new Rational(0, 1), new Rational(3, 1), new Rational(1, 1), new Rational(1, 64), new Rational(1, 64))]);

    [FFmpegFeatureFact(nameof(FFmpegFeatures.GainMaps))]
    public void GainMapParametersSurviveTheIsoPayload()
    {
        var map = SampleGainMap();

        var payload = map.ToIso21496();
        var back = FFmpegGainMap.FromIso21496(payload);

        payload.Should().NotBeEmpty();
        payload.Length.Should().BeLessThanOrEqualTo(141);
        back.Should().BeEquivalentTo(map, o => o.ComparingByValue<Rational>());
    }

    [FFmpegFeatureFact(nameof(FFmpegFeatures.GainMaps))]
    public void FFmpegJudgesWhetherGainMapParametersAreValid()
    {
        SampleGainMap().IsValid().Should().BeTrue();

        var inverted = SampleGainMap() with
        {
            Channels = [new FFmpegGainMapChannel(new Rational(3, 1), new Rational(0, 1), new Rational(1, 1), new Rational(1, 64), new Rational(1, 64))],
        };
        inverted.IsValid().Should().BeFalse("the lower bound is above the upper one");
    }

    [FFmpegFeatureFact(nameof(FFmpegFeatures.GainMaps))]
    public void AFrameCarriesItsGainMapAsSideData()
    {
        var frame = AV.av_frame_alloc();
        try
        {
            var bytes = GainMapSideData(SampleGainMap());
            var block = AVModern.av_frame_new_side_data((AVFrameHead*)frame, (int)FFmpegSideDataType.GainMapParams, (nuint)bytes.Length);
            new ReadOnlySpan<byte>(bytes).CopyTo(AbiLayout.SideDataWritable(block));

            var details = new FFmpegFrameDetails((AVFrameHead*)frame);

            details.GainMap().Should().BeEquivalentTo(SampleGainMap(), o => o.ComparingByValue<Rational>());
        }
        finally
        {
            var f = frame;
            AV.av_frame_free(&f);
        }
    }

    // The native struct, written by the library's own allocator and the ISO round trip.
    private static byte[] GainMapSideData(FFmpegGainMap map) => FFmpegGainMapNative.ToNative(map);

    [FFmpegFeatureFact(nameof(FFmpegFeatures.DownmixMatrices))]
    public void ADownmixMatrixKeepsItsCoefficients()
    {
        var coefficients = new double[2 * 6];
        for (var i = 0; i < coefficients.Length; i++)
        {
            coefficients[i] = i / 10.0;
        }

        var matrix = new FFmpegDownmixMatrix(FFmpegDownmixType.LoRo, 6, 2, coefficients);

        var back = FFmpegDownmixMatrix.Parse(matrix.ToSideData());

        back.Should().NotBeNull();
        back!.Type.Should().Be(FFmpegDownmixType.LoRo);
        (back.InputChannels, back.OutputChannels).Should().Be((6, 2));
        back.Coefficient(1, 4).Should().Be(1.0 * 6 / 10 + 0.4);
        back.Coefficients.Should().Equal(coefficients);
    }

    [FFmpegFeatureFact(nameof(FFmpegFeatures.DownmixMatrices))]
    public void AFrameCarriesItsDownmixMatrixAsSideData()
    {
        var frame = AV.av_frame_alloc();
        try
        {
            var matrix = new FFmpegDownmixMatrix(FFmpegDownmixType.LtRt, 3, 2, [1, 0, 0.7, 0, 1, 0.7]);
            var bytes = matrix.ToSideData();
            var block = AVModern.av_frame_new_side_data((AVFrameHead*)frame, (int)FFmpegSideDataType.DownmixMatrix, (nuint)bytes.Length);
            new ReadOnlySpan<byte>(bytes).CopyTo(AbiLayout.SideDataWritable(block));

            var details = new FFmpegFrameDetails((AVFrameHead*)frame);

            details.DownmixMatrix().Should().BeEquivalentTo(matrix);
        }
        finally
        {
            var f = frame;
            AV.av_frame_free(&f);
        }
    }

    // ------------------------------------------------------------ stream groups (any series)

    [FFmpegFact]
    public void AnIamfFileReportsItsAudioElementAndMixPresentation()
    {
        using var media = new TestMedia();
        var path = media.PathTo("clip.iamf");
        FfmpegProgram.Run(
            "-f lavfi -i \"sine=d=1,aformat=channel_layouts=stereo:sample_rates=48000\" -map 0:0 -c:a opus -strict experimental " +
            "-stream_group \"type=iamf_audio_element:id=1:st=0,layer=ch_layout=stereo\" " +
            "-stream_group \"type=iamf_mix_presentation:id=2:stg=0:annotations=en-us=Mix,submix=parameter_id=100:parameter_rate=48000|element=stg=0:parameter_id=100:annotations=en-us=Sub|layout=sound_system=stereo\" " +
            $"-streamid 0:0 \"{path}\"");

        using var demuxer = FFmpegDemuxer.Open(path);
        var groups = demuxer.StreamGroups;

        groups.Should().HaveCount(2);
        groups[0].Index.Should().Be(0);
        groups[0].Id.Should().Be(1);
        groups[0].Type.Should().Be(FFmpegStreamGroupType.IamfAudioElement);
        groups[0].StreamIndices.Should().Equal(0);
        groups[1].Id.Should().Be(2);
        groups[1].Type.Should().Be(FFmpegStreamGroupType.IamfMixPresentation);
        groups[1].TileGrid.Should().BeNull();
    }

    [FFmpegFact]
    public void AFileWithoutGroupsReportsNone()
    {
        using var media = new TestMedia();
        var path = media.PathTo("plain.wav");
        FfmpegProgram.Run($"-f lavfi -i sine=d=1 \"{path}\"");

        using var demuxer = FFmpegDemuxer.Open(path);

        demuxer.StreamGroups.Should().BeEmpty();
    }

    // ------------------------------------------------------------ handle: input (Windows, 9.1)

    [FFmpegFeatureFact(nameof(FFmpegFeatures.HandleInput))]
    public void AFileHandleIsDemuxedWithSeekAndLeftOpen()
    {
        using var media = new TestMedia();
        var path = media.PathTo("clip.mkv");
        FfmpegProgram.Run($"-f lavfi -i testsrc=d=4:s=160x120:r=25 -c:v libx264 -g 10 -pix_fmt yuv420p \"{path}\"");

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: false);
        using (var demuxer = FFmpegDemuxer.OpenHandle(file.SafeFileHandle))
        {
            demuxer.Streams.Should().ContainSingle().Which.MediaType.Should().Be(AVMediaType.Video);
            using (var byPath = FFmpegDemuxer.Open(path))
            {
                demuxer.Duration.Should().Be(byPath.Duration);
            }

            // Seeking needs a handle that can seek; pipe: cannot do it.
            demuxer.Seek(TimeSpan.FromSeconds(3));
            var packet = AV.av_packet_alloc();
            try
            {
                demuxer.ReadPacket(packet).Should().BeTrue();
                var seconds = packet->Pts * demuxer.Streams[0].TimeBase.Value;
                seconds.Should().BeInRange(2.0, 3.5);
            }
            finally
            {
                var p = packet;
                AV.av_packet_free(&p);
            }
        }

        file.SafeFileHandle.IsClosed.Should().BeFalse("closing the demuxer must not close a handle it was lent");
        file.Length.Should().BeGreaterThan(0);
    }

    [FFmpegFact]
    public void TheHandleProtocolIsNotReachableThroughAStringUrl()
    {
        var open = () => FFmpegDemuxer.Open("handle:");

        open.Should().Throw<MediaToolkitNet.Abstractions.MediaToolkitNetException>();
    }

    [FFmpegFact]
    public void OpeningAHandleIsRefusedWhereTheProtocolIsMissing()
    {
        if (FFmpegFeatures.HandleInput)
        {
            return;
        }

        using var media = new TestMedia();
        var path = media.PathTo("empty.bin");
        File.WriteAllBytes(path, [0]);
        using var file = File.OpenHandle(path);

        var open = () => FFmpegDemuxer.OpenHandle(file);

        open.Should().Throw<NotSupportedException>().WithMessage("*HandleInput*");
    }

    // ---------------------------------------------------------- new filters (8.0, 9.1)

    [FFmpegFact]
    public void TheTypedNewFiltersBuildGraphsWhereTheLibrariesHaveThem()
    {
        var format = new MediaToolkitNet.Abstractions.Formats.VideoFormat(
            64, 48, MediaToolkitNet.Abstractions.Formats.PixelFormat.Rgba32, new MediaToolkitNet.Abstractions.Formats.Rational(25, 1));
        var pixels = new byte[64 * 48 * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 7);
        }

        var tried = 0;
        foreach (var (available, description) in new[]
        {
            (FFmpegNewFilters.ColorDetectAvailable, FFmpegNewFilters.ColorDetect(ColorDetectMode.AlphaMode)),
            (FFmpegNewFilters.LatticePalAvailable, FFmpegNewFilters.LatticePal(density: 8, maxColors: 32, dither: LatticeDither.Bayer)),
        })
        {
            if (!available)
            {
                continue;
            }

            tried++;
            using var graph = FFmpegFilterGraph.ForVideo(format, description);
            fixed (byte* data = pixels)
            {
                var frame = new VideoFrame(format, TimeSpan.Zero, data, 64 * 4);
                graph.Push(in frame);
            }

            graph.OutputVideoFormat.Width.Should().Be(64, description);
        }

        // The libraries that have neither (before FFmpeg 8.0) have nothing to build.
        (tried > 0 || !FFmpegNewFilters.ColorDetectAvailable).Should().BeTrue();
    }

    // --------------------------------------------------------------------- EXIF (8.1)

    // A little-endian TIFF with four entries in its first directory.
    private static byte[] SampleExif()
    {
        var tiff = new List<byte>();
        void U16(int v) { tiff.Add((byte)v); tiff.Add((byte)(v >> 8)); }
        void U32(long v) { U16((int)(v & 0xFFFF)); U16((int)(v >> 16)); }

        tiff.AddRange("II"u8.ToArray());
        U16(42);
        U32(8);                       // the directory follows the header
        U16(4);                       // four entries
        const int entriesEnd = 8 + 2 + (4 * 12) + 4;

        // Orientation (0x0112), SHORT, 1 value inline: 6
        U16(0x0112); U16(3); U32(1); U16(6); U16(0);
        // ImageDescription (0x010E), ASCII, "hello" + NUL does not fit inline: stored after the directory
        U16(0x010E); U16(2); U32(6); U32(entriesEnd);
        // XResolution (0x011A), RATIONAL, 1 value: stored after the string
        U16(0x011A); U16(5); U32(1); U32(entriesEnd + 6);
        // Software (0x0131), ASCII, "ab" + NUL fits inline
        U16(0x0131); U16(2); U32(3); tiff.AddRange("ab\0\0"u8.ToArray());
        U32(0);                       // no next directory
        tiff.AddRange("hello\0"u8.ToArray());
        U32(72); U32(1);              // 72/1

        var exif = new List<byte>("Exif\0\0"u8.ToArray());
        exif.AddRange(tiff);
        return [.. exif];
    }

    [FFmpegFeatureFact(nameof(FFmpegFeatures.Exif))]
    public void AnExifBufferIsReadIntoItsEntries()
    {
        var entries = FFmpegExif.Parse(SampleExif());

        entries.Select(e => e.Id).Should().BeEquivalentTo(new ushort[] { 0x0112, 0x010E, 0x011A, 0x0131 });
        var byId = entries.ToDictionary(e => e.Id);
        byId[0x0112].Type.Should().Be(TiffDataType.Short);
        byId[0x0112].Values.Should().Equal(new object[] { 6UL });
        byId[0x0112].Name.Should().Be("Orientation");
        byId[0x010E].Values.Should().Equal(new object[] { "hello" });
        byId[0x011A].Type.Should().Be(TiffDataType.Rational);
        byId[0x011A].Values.Should().Equal(new object[] { (72, 1) });
        byId[0x0131].Values.Should().Equal(new object[] { "ab" });
    }

    [FFmpegFeatureFact(nameof(FFmpegFeatures.Exif))]
    public void ThePictureOfAJpegWithExifCarriesItsEntries()
    {
        using var media = new TestMedia();
        var plain = media.PathTo("plain.jpg");
        FfmpegProgram.Run($"-f lavfi -i testsrc=s=160x120 -frames:v 1 \"{plain}\"");

        // A JPEG segment: FF E1, a big-endian length that counts itself, then "Exif\0\0" and the TIFF.
        var exif = SampleExif();
        var bytes = File.ReadAllBytes(plain);
        var withExif = new List<byte>(bytes[..2]) { 0xFF, 0xE1, (byte)((exif.Length + 2) >> 8), (byte)(exif.Length + 2) };
        withExif.AddRange(exif);
        withExif.AddRange(bytes[2..]);
        var photo = media.PathTo("photo.jpg");
        File.WriteAllBytes(photo, [.. withExif]);

        using var reader = FFmpegMediaReader.Open(photo);
        IReadOnlyList<ExifEntry>? entries = null;
        var rotated = false;
        reader.Video!.TryReadNext((in VideoFrame _, in FFmpegFrameDetails details) =>
        {
            entries = details.Exif();
            rotated = details.HasSideData(FFmpegSideDataType.DisplayMatrix);
        }).Should().BeTrue();

        entries.Should().NotBeNull();
        entries!.Select(e => e.Name).Should().Contain(["ImageDescription", "Software"]);

        // The decoder turns the Orientation entry into a display matrix and takes it out of the EXIF.
        entries!.Select(e => e.Name).Should().NotContain("Orientation");
        rotated.Should().BeTrue("Orientation 6 is a rotation");
    }

    [FFmpegFeatureFact(nameof(FFmpegFeatures.Exif))]
    public void ABufferThatIsNotExifIsRefused()
    {
        var act = () => FFmpegExif.Parse(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, ExifHeaderMode.TiffHeader);

        act.Should().Throw<MediaToolkitNet.Abstractions.MediaToolkitNetException>();
    }

    [FFmpegFeatureFact(nameof(FFmpegFeatures.Exif))]
    public void FFmpegNamesTheTagsItKnows()
    {
        FFmpegExif.TagName(0x0112).Should().Be("Orientation");
        FFmpegExif.TagName(0xFFFE).Should().BeNull();
    }

    // ------------------------------------------------- synchronous receive_frame (8.1)

    [FFmpegFeatureFact(nameof(FFmpegFeatures.SynchronousDecoding))]
    public void ASynchronousReceiveReturnsAFrameBeforeTheDecoderThreadsHaveFilled()
    {
        using var media = new TestMedia();
        var clip = media.PathTo("clip.ts");
        FfmpegProgram.Run($"-f lavfi -i testsrc=d=2:s=320x240:r=25 -c:v libx264 -preset ultrafast -bf 0 -g 12 -f mpegts \"{clip}\"");

        var normal = PacketsUntilFirstFrame(clip, synchronous: false);
        var synchronous = PacketsUntilFirstFrame(clip, synchronous: true);

        // Frame threading holds a frame back for as many packets as there are threads.
        normal.Should().BeGreaterThan(1, "four decoder threads keep the pipeline full before the first frame comes out");
        synchronous.Should().BeLessThan(normal, $"normal needed {normal} packets and synchronous {synchronous}");
    }

    // ------------------------------------------------------ MIME codec strings (8.1)

    [FFmpegFact]
    public void TheStringBuilderLayoutTheMimeCallReadsIsTheOneThatIsInstalled()
    {
        if (AVModern.av_bprint_init is null)
        {
            return; // libavutil without the string builder: nothing to check
        }

        AbiLayout.BprintLayoutVerified.Should().BeTrue("av_bprint_init points str into the struct's own reserved bytes");
    }

    [FFmpegFeatureFact(nameof(FFmpegFeatures.MimeCodecStrings))]
    public void ACodecStringIsWhatABrowserWouldAskFor()
    {
        using var media = new TestMedia();
        var clip = media.PathTo("clip.mp4");
        FfmpegProgram.Run(
            "-f lavfi -i testsrc=d=1:s=320x240:r=25 -f lavfi -i sine=d=1 " +
            "-c:v libx264 -profile:v high -level 4.0 -pix_fmt yuv420p -c:a aac " +
            $"\"{clip}\"");

        using var demuxer = FFmpegDemuxer.Open(clip);
        var video = demuxer.FindStream(AVMediaType.Video)!.Value.Index;
        var audio = demuxer.FindStream(AVMediaType.Audio)!.Value.Index;

        demuxer.MimeCodecString(video).Should().Be("avc1.640028");
        demuxer.MimeCodecString(audio).Should().Be("mp4a.40.2");
    }

    private static int PacketsUntilFirstFrame(string path, bool synchronous)
    {
        using var demuxer = FFmpegDemuxer.Open(path);
        var video = demuxer.FindStream(AVMediaType.Video)!.Value.Index;
        using var decoder = FFmpegDecoder.Open(demuxer, video, threadCount: 4);

        var packet = AV.av_packet_alloc();
        var frame = AV.av_frame_alloc();
        try
        {
            var sent = 0;
            while (demuxer.ReadPacket(packet))
            {
                if (packet->StreamIndex == video)
                {
                    sent++;
                    decoder.SendPacket(packet);
                    if (decoder.ReceiveFrame((AVFrameHead*)frame, synchronous) == FFmpegDecoder.ReceiveResult.Frame)
                    {
                        return sent;
                    }
                }

                AV.av_packet_unref(packet);
            }

            return int.MaxValue;
        }
        finally
        {
            var f = frame;
            AV.av_frame_free(&f);
            var p = packet;
            AV.av_packet_free(&p);
        }
    }
}
