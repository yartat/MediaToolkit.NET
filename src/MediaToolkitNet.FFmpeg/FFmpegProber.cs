#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg.Native;

namespace MediaToolkitNet.FFmpeg;

/// <summary>Reads what a file contains through libavformat, without decoding it.</summary>
public sealed unsafe class FFmpegProber : IMediaProber
{
    /// <inheritdoc />
    public string Backend => FFmpegLibraries.BackendName;

    /// <inheritdoc />
    public MediaInfo Probe(string uri)
    {
        using var demuxer = FFmpegDemuxer.Open(uri);
        return Describe(demuxer, uri);
    }

    /// <summary>Describes an input that is already open.</summary>
    internal static MediaInfo Describe(FFmpegDemuxer demuxer, string uri)
    {
        var context = demuxer.Handle;
        var containerName = AbiLayout.NameOfFormat(AbiLayout.InputFormatOf(context));

        var streams = new List<MediaStreamInfo>(demuxer.Streams.Count);
        foreach (var info in demuxer.Streams)
        {
            streams.Add(DescribeStream(demuxer, info));
        }

        // The longest stream, or the container's own figure when that is longer
        // or the streams state none, as they do not in Matroska without tags.
        var containerDuration = TimeSpan.FromTicks(AbiLayout.DurationOfInput(context) * 10);
        var duration = containerDuration > demuxer.Duration ? containerDuration : demuxer.Duration;

        return new MediaInfo(uri, containerName, duration, streams)
        {
            Backend = FFmpegLibraries.BackendName,
            Container = ContainerOf(containerName, uri),
            Metadata = AbiLayout.MetadataLayoutVerified
                ? AbiLayout.ReadDictionary(*AbiLayout.MetadataOf(context))
                : new Dictionary<string, string>(),
            Chapters = ReadChapters(context),
        };
    }

    private static MediaStreamInfo DescribeStream(FFmpegDemuxer demuxer, FFmpegStreamInfo info)
    {
        var stream = demuxer.StreamHandle(info.Index);
        var parameters = demuxer.CodecParameters(info.Index);
        var tags = AbiLayout.MetadataLayoutVerified
            ? AbiLayout.ReadDictionary(*AbiLayout.MetadataOfStream(stream))
            : new Dictionary<string, string>();
        var disposition = AbiLayout.DispositionOf(stream);
        var kind = KindOf(info.MediaType);

        var result = new MediaStreamInfo(info.Index, kind, info.CodecName)
        {
            Codec = MediaFormats.CodecFromName(info.CodecName),
            Language = tags.GetValueOrDefault("language"),
            Title = tags.GetValueOrDefault("title"),
            IsDefault = (disposition & AVConstants.DispositionDefault) != 0,
            IsForced = (disposition & AVConstants.DispositionForced) != 0,
            IsAttachedPicture = (disposition & AVConstants.DispositionAttachedPicture) != 0,
            Duration = info.Duration,
            BitRate = AbiLayout.CodecParametersLayoutVerified ? AbiLayout.BitRateOf(parameters) : 0,
            IsTextSubtitle = kind == MediaStreamKind.Subtitle
                && (AbiLayout.PropertiesOfCodec(info.CodecId) & AVConstants.CodecPropTextSub) != 0,
        };

        return kind switch
        {
            MediaStreamKind.Video when AbiLayout.CodecParametersLayoutVerified => result with
            {
                Video = new VideoFormat(
                    AbiLayout.WidthOf(parameters),
                    AbiLayout.HeightOf(parameters),
                    FFmpegFormatMap.FromAV((AVPixelFormat)AbiLayout.FormatOf(parameters)),
                    info.AverageFrameRate),
            },
            MediaStreamKind.Audio => result with { Audio = FFmpegStreamFormats.ReadAudio(parameters) },
            _ => result,
        };
    }

    private static List<MediaChapter> ReadChapters(void* context)
    {
        var result = new List<MediaChapter>();
        var count = AbiLayout.ChapterCountOf(context);
        for (var i = 0; i < count; i++)
        {
            var chapter = AbiLayout.ChapterAt(context, i);
            var (start, end, timeBase) = AbiLayout.SpanOfChapter(chapter);
            if (timeBase.Den <= 0)
            {
                continue;
            }

            var tags = AbiLayout.MetadataLayoutVerified
                ? AbiLayout.ReadDictionary(*AbiLayout.MetadataOfChapter(chapter))
                : new Dictionary<string, string>();

            result.Add(new MediaChapter(
                FFmpegStreamFormats.ToTimeSpan(start, timeBase),
                FFmpegStreamFormats.ToTimeSpan(end, timeBase),
                tags.GetValueOrDefault("title")));
        }

        return result;
    }

    private static MediaStreamKind KindOf(AVMediaType type) => type switch
    {
        AVMediaType.Video => MediaStreamKind.Video,
        AVMediaType.Audio => MediaStreamKind.Audio,
        AVMediaType.Subtitle => MediaStreamKind.Subtitle,
        AVMediaType.Attachment => MediaStreamKind.Attachment,
        _ => MediaStreamKind.Data,
    };

    /// <summary>
    /// Names the container. libavformat reports a family, such as
    /// <c>mov,mp4,m4a,3gp,3g2,mj2</c>, and the extension decides within it.
    /// </summary>
    private static MediaContainer? ContainerOf(string demuxerName, string uri)
    {
        var fromPath = uri.Contains("://", StringComparison.Ordinal) ? null : MediaFormats.ContainerFromPath(uri);
        return demuxerName switch
        {
            "matroska,webm" => fromPath is MediaContainer.WebM ? MediaContainer.WebM : MediaContainer.Matroska,
            "mov,mp4,m4a,3gp,3g2,mj2" => fromPath is MediaContainer.Mov ? MediaContainer.Mov : MediaContainer.Mp4,
            "mpegts" => MediaContainer.MpegTs,
            "ogg" => MediaContainer.Ogg,
            "avi" => MediaContainer.Avi,
            "wav" => MediaContainer.Wav,
            "flac" => MediaContainer.Flac,
            "mp3" => MediaContainer.Mp3,
            _ => null,
        };
    }
}

/// <summary>Reads stream formats through AVOptions, so they do not depend on struct layout.</summary>
internal static unsafe class FFmpegStreamFormats
{
    /// <summary>
    /// The audio format codec parameters describe. They are copied into a
    /// scratch codec context and read back through its <c>ar</c> and
    /// <c>ch_layout</c> options, rather than through fields of either struct.
    /// </summary>
    public static AudioFormat ReadAudio(void* codecParameters)
    {
        var context = AV.avcodec_alloc_context3(null);
        if (context is null)
        {
            return default;
        }

        try
        {
            if (AV.avcodec_parameters_to_context(context, codecParameters) < 0)
            {
                return default;
            }

            var (rate, channels, mask) = ReadAudioOptions(context);
            var sampleFormat = AbiLayout.CodecParametersLayoutVerified
                ? FFmpegFormatMap.FromAV((AVSampleFormat)AbiLayout.FormatOf(codecParameters))
                : SampleFormat.Unknown;

            return new AudioFormat(rate, channels, sampleFormat, mask);
        }
        finally
        {
            AV.avcodec_free_context(&context);
        }
    }

    /// <summary>Reads the sample rate and channel layout of an open codec context.</summary>
    public static (int SampleRate, int Channels, ulong ChannelMask) ReadAudioOptions(void* codecContext)
    {
        var rate = (int)AV.GetOption(codecContext, "ar");

        AVChannelLayoutNative layout = default;
        Span<byte> scratch = stackalloc byte[Interop.Utf8Scoped.StackThreshold];
        using var name = new Interop.Utf8Scoped("ch_layout", scratch);
        if (AV.av_opt_get_chlayout(codecContext, name.Pointer, 0, &layout) < 0)
        {
            return (rate, 0, 0);
        }

        try
        {
            // Only the native order is a speaker mask; the others say nothing about where the speakers are.
            return (rate, layout.NbChannels, layout.Order == 1 ? layout.Mask : 0);
        }
        finally
        {
            AV.av_channel_layout_uninit(&layout);
        }
    }

    /// <summary>Converts a timestamp in a time base, treating AV_NOPTS_VALUE as zero.</summary>
    public static TimeSpan ToTimeSpan(long value, AVRationalNative timeBase) =>
        value == AVConstants.NoPtsValue || timeBase.Den == 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(AV.av_rescale_q(value, timeBase, new AVRationalNative(1, (int)TimeSpan.TicksPerSecond)));
}
