#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg.Native;

/// <summary>
/// Every direct struct field access this binding performs, in one place.
/// </summary>
/// <remarks>
/// <para>
/// FFmpeg exposes most of its state through public structs whose layout changes
/// between major releases, so the list below is deliberately as short as
/// possible. Everything that FFmpeg offers an accessor or an AVOption for is
/// read that way instead: codec parameters travel through
/// <c>avcodec_parameters_to_context</c>, bitrate and GOP size are set with
/// <c>av_opt_set_int</c>, and the encoder frame size is read with
/// <c>av_opt_get_int</c>.
/// </para>
/// <para>
/// The offsets are for a 64-bit target. Those below have held from FFmpeg 6
/// through 9; the two that move between series live in
/// <see cref="FFmpegGeneration"/> and arrive through <see cref="Use"/>.
/// <see cref="Validate"/> checks them against the values FFmpeg documents for a
/// freshly allocated object, so a mismatched build fails with a clear message
/// instead of corrupting memory.
/// </para>
/// </remarks>
public static unsafe class AbiLayout
{
    /// <summary>Offset of <c>AVFormatContext::pb</c>.</summary>
    public const int FormatContextPb = 32;

    /// <summary>Offset of <c>AVFormatContext::nb_streams</c>.</summary>
    public const int FormatContextNbStreams = 44;

    /// <summary>Offset of <c>AVFormatContext::streams</c>.</summary>
    public const int FormatContextStreams = 48;

    /// <summary>Offset of <c>AVStream::index</c>.</summary>
    public const int StreamIndex = 8;

    /// <summary>Offset of <c>AVStream::time_base</c>.</summary>
    public const int StreamTimeBase = 32;

    /// <summary>Offset of <c>AVStream::start_time</c>.</summary>
    public const int StreamStartTime = 40;

    /// <summary>Offset of <c>AVStream::duration</c>.</summary>
    public const int StreamDuration = 48;

    /// <summary>Offset of <c>AVStream::avg_frame_rate</c>.</summary>
    public const int StreamAvgFrameRate = 88;

    /// <summary>Offset of <c>AVStream::codecpar</c>.</summary>
    public const int StreamCodecPar = 16;

    /// <summary>Offset of <c>AVCodecParameters::codec_type</c>.</summary>
    public const int CodecParametersCodecType = 0;

    /// <summary>Offset of <c>AVCodecParameters::codec_id</c>.</summary>
    public const int CodecParametersCodecId = 4;

    /// <summary>
    /// Offset of <c>AVCodecParameters::codec_tag</c>: the four bytes right after
    /// <c>codec_id</c>, the same in every series from 6 to 9.
    /// </summary>
    public const int CodecParametersCodecTag = 8;

    /// <summary>Offset of <c>AVCodec::type</c>.</summary>
    public const int CodecType = 16;

    /// <summary>Offset of <c>AVCodec::id</c>.</summary>
    public const int CodecId = 20;

    /// <summary>
    /// Offset of <c>AVCodecParameters::format</c>, discovered at startup.
    /// See <see cref="ProbeCodecParameters"/>.
    /// </summary>
    public static int CodecParametersFormat { get; private set; } = -1;

    /// <summary>Offset of <c>AVCodecParameters::bit_rate</c>, discovered at startup.</summary>
    public static int CodecParametersBitRate { get; private set; } = -1;

    /// <summary>Offset of <c>AVCodecParameters::width</c>, discovered at startup.</summary>
    public static int CodecParametersWidth { get; private set; } = -1;

    /// <summary>Offset of <c>AVCodecParameters::height</c>, discovered at startup.</summary>
    public static int CodecParametersHeight { get; private set; } = -1;

    /// <summary>True when <see cref="ProbeCodecParameters"/> located every offset it looks for.</summary>
    public static bool CodecParametersLayoutVerified { get; private set; }

    /// <summary>Offset of <c>AVFrame::sample_rate</c>, discovered at startup.</summary>
    public static int FrameSampleRate { get; private set; } = -1;

    /// <summary>Offset of <c>AVFrame::ch_layout</c>, discovered at startup.</summary>
    public static int FrameChannelLayout { get; private set; } = -1;

    /// <summary>
    /// True when the audio fields of AVFrame were located and a write round-trip
    /// through <c>av_frame_get_buffer</c> confirmed them. Audio encoding is
    /// refused when this is false.
    /// </summary>
    public static bool AudioFrameLayoutVerified { get; private set; }

    /// <summary>Offset of <c>AVCodecContext::codec_type</c>. Read only by <see cref="Validate"/>.</summary>
    public const int CodecContextCodecType = 12;

    /// <summary>
    /// Offset of <c>AVCodecContext::time_base</c>, which belongs to the series
    /// that was loaded. <see cref="Use"/> sets it.
    /// </summary>
    public static int CodecContextTimeBase { get; private set; } = -1;

    /// <summary>
    /// Offset of <c>AVCodecContext::pix_fmt</c>, which belongs to the series that
    /// was loaded. Read only by <see cref="Validate"/>.
    /// </summary>
    public static int CodecContextPixFmt { get; private set; } = -1;

    /// <summary>
    /// Points the layout at the series of FFmpeg that was loaded. Called before
    /// <see cref="Validate"/> and before any field is read.
    /// </summary>
    /// <param name="generation">The series.</param>
    internal static void Use(FFmpegGeneration generation)
    {
        CodecContextTimeBase = generation.CodecContextTimeBase;
        CodecContextPixFmt = generation.CodecContextPixFmt;
    }

    /// <summary>
    /// True when the encoder-side offsets passed <see cref="Validate"/>. Encoding
    /// is refused when this is false.
    /// </summary>
    public static bool EncoderLayoutVerified { get; private set; }

    // ------------------------------------------------------------- accessors

    /// <summary>Reads <c>AVFormatContext::nb_streams</c>.</summary>
    public static int NbStreams(void* formatContext) => *(int*)((byte*)formatContext + FormatContextNbStreams);

    /// <summary>Reads <c>AVFormatContext::streams[index]</c>.</summary>
    public static void* Stream(void* formatContext, int index) =>
        (*(void***)((byte*)formatContext + FormatContextStreams))[index];

    /// <summary>Writes <c>AVFormatContext::pb</c>.</summary>
    public static void SetPb(void* formatContext, void* pb) => *(void**)((byte*)formatContext + FormatContextPb) = pb;

    /// <summary>Reads <c>AVFormatContext::pb</c>.</summary>
    public static void* GetPb(void* formatContext) => *(void**)((byte*)formatContext + FormatContextPb);

    /// <summary>Reads <c>AVStream::index</c>.</summary>
    public static int StreamIndexOf(void* stream) => *(int*)((byte*)stream + StreamIndex);

    /// <summary>Reads <c>AVStream::time_base</c>.</summary>
    public static AVRationalNative TimeBaseOf(void* stream) => *(AVRationalNative*)((byte*)stream + StreamTimeBase);

    /// <summary>Writes <c>AVStream::time_base</c>.</summary>
    public static void SetTimeBase(void* stream, AVRationalNative value) =>
        *(AVRationalNative*)((byte*)stream + StreamTimeBase) = value;

    /// <summary>Reads <c>AVStream::duration</c>, in stream time base units.</summary>
    public static long DurationOf(void* stream) => *(long*)((byte*)stream + StreamDuration);

    /// <summary>Reads <c>AVStream::start_time</c>, in stream time base units.</summary>
    public static long StartTimeOf(void* stream) => *(long*)((byte*)stream + StreamStartTime);

    /// <summary>Reads <c>AVStream::avg_frame_rate</c>.</summary>
    public static AVRationalNative AvgFrameRateOf(void* stream) =>
        *(AVRationalNative*)((byte*)stream + StreamAvgFrameRate);

    /// <summary>Reads <c>AVStream::codecpar</c>.</summary>
    public static void* CodecParametersOf(void* stream) => *(void**)((byte*)stream + StreamCodecPar);

    /// <summary>Reads <c>AVCodecParameters::codec_type</c>.</summary>
    public static AVMediaType CodecTypeOf(void* codecParameters) =>
        (AVMediaType)(*(int*)((byte*)codecParameters + CodecParametersCodecType));

    /// <summary>Reads <c>AVCodecParameters::codec_id</c>.</summary>
    public static int CodecIdOf(void* codecParameters) =>
        *(int*)((byte*)codecParameters + CodecParametersCodecId);

    /// <summary>
    /// Clears <c>AVCodecParameters::codec_tag</c>, so a muxer picks the tag its
    /// own container uses for the codec instead of keeping the input's.
    /// </summary>
    public static void ClearCodecTag(void* codecParameters) =>
        *(uint*)((byte*)codecParameters + CodecParametersCodecTag) = 0;

    /// <summary>Reads <c>AVCodecParameters::format</c>: an AVPixelFormat for video, an AVSampleFormat for audio.</summary>
    public static int FormatOf(void* codecParameters)
    {
        RequireCodecParametersLayout();
        return *(int*)((byte*)codecParameters + CodecParametersFormat);
    }

    /// <summary>Reads <c>AVCodecParameters::bit_rate</c>.</summary>
    public static long BitRateOf(void* codecParameters)
    {
        RequireCodecParametersLayout();
        return *(long*)((byte*)codecParameters + CodecParametersBitRate);
    }

    /// <summary>Reads <c>AVCodecParameters::width</c>.</summary>
    public static int WidthOf(void* codecParameters)
    {
        RequireCodecParametersLayout();
        return *(int*)((byte*)codecParameters + CodecParametersWidth);
    }

    /// <summary>Reads <c>AVCodecParameters::height</c>.</summary>
    public static int HeightOf(void* codecParameters)
    {
        RequireCodecParametersLayout();
        return *(int*)((byte*)codecParameters + CodecParametersHeight);
    }

    /// <summary>Reads <c>AVCodec::id</c> from a codec descriptor.</summary>
    public static int IdOfCodec(void* codec) => *(int*)((byte*)codec + CodecId);

    /// <summary>Reads <c>AVCodec::type</c> from a codec descriptor.</summary>
    public static AVMediaType TypeOfCodec(void* codec) => (AVMediaType)(*(int*)((byte*)codec + CodecType));

    /// <summary>
    /// Fills the parts of an <c>AVCodecParameters</c> that this binding uses to
    /// configure an encoder. Everything else is left at its default and filled
    /// in by <c>avcodec_parameters_from_context</c> once the encoder is open.
    /// </summary>
    /// <param name="parameters">Target allocated by <c>avcodec_parameters_alloc</c>.</param>
    /// <param name="mediaType">Video or audio.</param>
    /// <param name="codecId">Value read from the chosen <c>AVCodec</c>.</param>
    /// <param name="format">An AVPixelFormat for video or an AVSampleFormat for audio.</param>
    /// <param name="bitRate">Target bit rate, or 0 to leave it to the encoder.</param>
    /// <param name="width">Frame width; ignored for audio.</param>
    /// <param name="height">Frame height; ignored for audio.</param>
    public static void FillCodecParameters(
        void* parameters,
        AVMediaType mediaType,
        int codecId,
        int format,
        long bitRate,
        int width,
        int height)
    {
        RequireCodecParametersLayout();

        var raw = (byte*)parameters;
        *(int*)(raw + CodecParametersCodecType) = (int)mediaType;
        *(int*)(raw + CodecParametersCodecId) = codecId;
        *(int*)(raw + CodecParametersFormat) = format;
        *(long*)(raw + CodecParametersBitRate) = bitRate;

        if (mediaType == AVMediaType.Video)
        {
            *(int*)(raw + CodecParametersWidth) = width;
            *(int*)(raw + CodecParametersHeight) = height;
        }
    }

    /// <summary>Writes <c>AVCodecContext::time_base</c>.</summary>
    public static void SetCodecTimeBase(void* codecContext, AVRationalNative value)
    {
        RequireEncoderLayout();
        *(AVRationalNative*)((byte*)codecContext + CodecContextTimeBase) = value;
    }

    // -------------------------------------------------------------- avfilter

    /// <summary>Offset of <c>AVFilterInOut::name</c>.</summary>
    public const int FilterInOutName = 0;

    /// <summary>Offset of <c>AVFilterInOut::filter_ctx</c>.</summary>
    public const int FilterInOutContext = 8;

    /// <summary>Offset of <c>AVFilterInOut::pad_idx</c>.</summary>
    public const int FilterInOutPadIndex = 16;

    /// <summary>Offset of <c>AVFilterInOut::next</c>.</summary>
    public const int FilterInOutNext = 24;

    /// <summary>Offset of <c>AVFilter::name</c>.</summary>
    public const int FilterName = 0;

    /// <summary>Offset of <c>AVFilter::description</c>.</summary>
    public const int FilterDescription = 8;

    /// <summary>
    /// True when <see cref="ProbeFilterLayout"/> confirmed the two libavfilter
    /// layouts above against a graph it built itself. Filtering is refused when
    /// this is false, including when libavfilter was not found at all.
    /// </summary>
    public static bool FilterLayoutVerified { get; private set; }

    /// <summary>
    /// Fills the <c>AVFilterInOut</c> that names one open end of a parsed filter
    /// chain. This is the one public libavfilter struct the API expects the
    /// caller to write, which is why its offsets are here.
    /// </summary>
    /// <param name="inOut">An entry from <c>avfilter_inout_alloc</c>.</param>
    /// <param name="name">
    /// The label used in the chain description, allocated with <c>av_strdup</c>
    /// or an equivalent: <c>avfilter_inout_free</c> frees it.
    /// </param>
    /// <param name="filterContext">The filter that end belongs to.</param>
    /// <param name="padIndex">Which pad of that filter.</param>
    public static void FillFilterInOut(void* inOut, byte* name, void* filterContext, int padIndex)
    {
        RequireFilterLayout();

        var raw = (byte*)inOut;
        *(byte**)(raw + FilterInOutName) = name;
        *(void**)(raw + FilterInOutContext) = filterContext;
        *(int*)(raw + FilterInOutPadIndex) = padIndex;
        *(void**)(raw + FilterInOutNext) = null;
    }

    /// <summary>Reads <c>AVFilter::name</c>.</summary>
    public static byte* NameOfFilter(void* filter) => *(byte**)((byte*)filter + FilterName);

    /// <summary>Reads <c>AVFilter::description</c>, which may be null.</summary>
    public static byte* DescriptionOfFilter(void* filter) => *(byte**)((byte*)filter + FilterDescription);

    /// <summary>Throws unless the libavfilter layouts were confirmed.</summary>
    public static void RequireFilterLayout()
    {
        if (!FilterLayoutVerified)
        {
            throw Fail(
                FFmpegLibraries.AvFilter is null
                    ? "libavfilter was not found next to the other FFmpeg libraries, so filter graphs are unavailable."
                    : "the libavfilter struct layout could not be confirmed, so filter graphs are unavailable.");
        }
    }

    /// <summary>
    /// Confirms the <c>AVFilterInOut</c> and <c>AVFilter</c> offsets against a
    /// graph parsed here, rather than trusting them.
    /// </summary>
    /// <remarks>
    /// <c>[in]null[out]</c> builds a graph of one pass-through filter with both
    /// of its pads free, so <c>avfilter_graph_parse2</c> hands back exactly one
    /// input named <c>in</c> and one output named <c>out</c>, both belonging to
    /// that same filter. Reading those four values back through the offsets pins
    /// all of them: a wrong <c>name</c> or <c>next</c> shows up as a mismatch
    /// rather than as a pointer into the middle of the struct.
    /// </remarks>
    private static bool ProbeFilterLayout()
    {
        if (FFmpegLibraries.AvFilter is null)
        {
            return false;
        }

        var graph = AV.avfilter_graph_alloc();
        if (graph is null)
        {
            return false;
        }

        try
        {
            void* inputs = null;
            void* outputs = null;
            Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var description = new Utf8Scoped("[in]null[out]", scratch);
            if (AV.avfilter_graph_parse2(graph, description.Pointer, &inputs, &outputs) < 0)
            {
                return false;
            }

            try
            {
                if (inputs is null || outputs is null)
                {
                    return false;
                }

                var inputContext = *(void**)((byte*)inputs + FilterInOutContext);
                return Utf8.ToManagedOrEmpty(*(byte**)((byte*)inputs + FilterInOutName)) == "in"
                       && Utf8.ToManagedOrEmpty(*(byte**)((byte*)outputs + FilterInOutName)) == "out"
                       && *(void**)((byte*)inputs + FilterInOutNext) is null
                       && *(void**)((byte*)outputs + FilterInOutNext) is null
                       && *(int*)((byte*)inputs + FilterInOutPadIndex) == 0
                       && inputContext is not null
                       && inputContext == *(void**)((byte*)outputs + FilterInOutContext)
                       && NameOfKnownFilter("null") == "null";
            }
            finally
            {
                AV.avfilter_inout_free(&inputs);
                AV.avfilter_inout_free(&outputs);
            }
        }
        finally
        {
            AV.avfilter_graph_free(&graph);
        }
    }

    private static string NameOfKnownFilter(string name)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(name, scratch);
        var filter = AV.avfilter_get_by_name(utf8.Pointer);
        return filter is null ? string.Empty : Utf8.ToManagedOrEmpty(NameOfFilter(filter));
    }

    // ------------------------------------------------- containers and streams
    //
    // Everything below was read with offsetof against the headers of n7.1.5,
    // n8.1.2 and n9.0.1, and is the same in all three. Each group is confirmed at
    // startup by writing a value through an API and reading it back at the
    // offset, or the other way round, so none of it is trusted blind.

    /// <summary>Offset of <c>AVFormatContext::iformat</c>.</summary>
    public const int FormatContextInputFormat = 8;

    /// <summary>Offset of <c>AVFormatContext::oformat</c>.</summary>
    public const int FormatContextOutputFormat = 16;

    /// <summary>Offset of <c>AVFormatContext::nb_chapters</c>.</summary>
    public const int FormatContextNbChapters = 72;

    /// <summary>Offset of <c>AVFormatContext::chapters</c>.</summary>
    public const int FormatContextChapters = 80;

    /// <summary>Offset of <c>AVFormatContext::url</c>. Read only by <see cref="ValidateOpenInput"/>.</summary>
    public const int FormatContextUrl = 88;

    /// <summary>Offset of <c>AVFormatContext::start_time</c>, right after <c>url</c>, in AV_TIME_BASE units.</summary>
    public const int FormatContextStartTime = 96;

    /// <summary>Offset of <c>AVFormatContext::duration</c>, right after <c>start_time</c>, in AV_TIME_BASE units.</summary>
    public const int FormatContextDuration = 104;

    /// <summary>Offset of <c>AVFormatContext::metadata</c>.</summary>
    public const int FormatContextMetadata = 192;

    /// <summary>Offset of <c>AVFormatContext::start_time_realtime</c>. Read only by the metadata probe.</summary>
    public const int FormatContextStartTimeRealtime = 200;

    /// <summary>Offset of <c>AVFormatContext::fps_probe_size</c>. Read only by the metadata probe.</summary>
    public const int FormatContextFpsProbeSize = 208;

    /// <summary>Offset of <c>name</c> in both <c>AVInputFormat</c> and <c>AVOutputFormat</c>.</summary>
    public const int FormatName = 0;

    /// <summary>Offset of <c>AVOutputFormat::flags</c>.</summary>
    public const int OutputFormatFlags = 44;

    /// <summary>Offset of <c>AVStream::disposition</c>.</summary>
    public const int StreamDisposition = 64;

    /// <summary>
    /// Offset of <c>AVStream::discard</c>: the four bytes between
    /// <c>disposition</c> and <c>sample_aspect_ratio</c>, the same in 7, 8 and 9.
    /// </summary>
    public const int StreamDiscard = 68;

    /// <summary>Offset of <c>AVStream::sample_aspect_ratio</c>.</summary>
    public const int StreamSampleAspectRatio = 72;

    /// <summary>Offset of <c>AVStream::metadata</c>.</summary>
    public const int StreamMetadata = 80;

    /// <summary>Offset of <c>AVChapter::id</c>.</summary>
    public const int ChapterId = 0;

    /// <summary>Offset of <c>AVChapter::time_base</c>.</summary>
    public const int ChapterTimeBase = 8;

    /// <summary>Offset of <c>AVChapter::start</c>.</summary>
    public const int ChapterStart = 16;

    /// <summary>Offset of <c>AVChapter::end</c>.</summary>
    public const int ChapterEnd = 24;

    /// <summary>Offset of <c>AVChapter::metadata</c>.</summary>
    public const int ChapterMetadata = 32;

    /// <summary><c>sizeof(AVChapter)</c>. Chapters written to an output are allocated here, as ffmpeg does it.</summary>
    public const int ChapterSize = 40;

    /// <summary>Offset of <c>AVDictionaryEntry::key</c>.</summary>
    public const int DictionaryEntryKey = 0;

    /// <summary>Offset of <c>AVDictionaryEntry::value</c>.</summary>
    public const int DictionaryEntryValue = 8;

    /// <summary>Offset of <c>AVCodecDescriptor::type</c>.</summary>
    public const int DescriptorType = 4;

    /// <summary>Offset of <c>AVCodecDescriptor::name</c>.</summary>
    public const int DescriptorName = 8;

    /// <summary>Offset of <c>AVCodecDescriptor::props</c>.</summary>
    public const int DescriptorProps = 24;

    /// <summary>Offset of <c>AVCodecContext::subtitle_header_size</c>.</summary>
    public const int CodecContextSubtitleHeaderSize = 748;

    /// <summary>Offset of <c>AVCodecContext::subtitle_header</c>.</summary>
    public const int CodecContextSubtitleHeader = 752;

    /// <summary><c>sizeof(AVSubtitle)</c>. The caller allocates it for <c>avcodec_decode_subtitle2</c>.</summary>
    public const int SubtitleSize = 32;

    /// <summary>Offset of <c>AVSubtitle::format</c>: 0 for bitmaps, 1 for text.</summary>
    public const int SubtitleFormat = 0;

    /// <summary>Offset of <c>AVSubtitle::start_display_time</c>, in milliseconds after <c>pts</c>.</summary>
    public const int SubtitleStartDisplayTime = 4;

    /// <summary>Offset of <c>AVSubtitle::end_display_time</c>, in milliseconds after <c>pts</c>.</summary>
    public const int SubtitleEndDisplayTime = 8;

    /// <summary>Offset of <c>AVSubtitle::num_rects</c>.</summary>
    public const int SubtitleNumRects = 12;

    /// <summary>Offset of <c>AVSubtitle::rects</c>.</summary>
    public const int SubtitleRects = 16;

    /// <summary>Offset of <c>AVSubtitle::pts</c>, in AV_TIME_BASE units.</summary>
    public const int SubtitlePts = 24;

    /// <summary><c>sizeof(AVSubtitleRect)</c>.</summary>
    public const int SubtitleRectSize = 96;

    /// <summary>Offset of <c>AVSubtitleRect::type</c>.</summary>
    public const int SubtitleRectType = 76;

    /// <summary>Offset of <c>AVSubtitleRect::text</c>.</summary>
    public const int SubtitleRectText = 80;

    /// <summary>Offset of <c>AVSubtitleRect::ass</c>.</summary>
    public const int SubtitleRectAss = 88;

    /// <summary>
    /// True when <c>AVDictionaryEntry</c>, <c>AVFormatContext::metadata</c> and
    /// the <c>AVStream</c> tag fields were confirmed. Tags are neither read nor
    /// written when this is false.
    /// </summary>
    public static bool MetadataLayoutVerified { get; private set; }

    /// <summary>True when <c>AVCodecDescriptor</c> was confirmed, which is how text subtitles are told from bitmaps.</summary>
    public static bool DescriptorLayoutVerified { get; private set; }

    /// <summary>
    /// True when <c>AVSubtitle</c>, <c>AVSubtitleRect</c> and the subtitle header
    /// of <c>AVCodecContext</c> were confirmed by decoding a subtitle. Subtitle
    /// conversion and subtitle reading are refused when this is false.
    /// </summary>
    public static bool SubtitleLayoutVerified { get; private set; }

    /// <summary>
    /// Reads <c>AVFormatContext::start_time</c>: where the input's timestamps
    /// begin, in AV_TIME_BASE units, or 0 when it states none. MPEG-TS commonly
    /// starts well above zero, and the transcoder measures trims from here.
    /// </summary>
    public static long StartTimeOfInput(void* formatContext)
    {
        var value = *(long*)((byte*)formatContext + FormatContextStartTime);
        return value == AVConstants.NoPtsValue ? 0 : value;
    }

    /// <summary>
    /// Reads <c>AVFormatContext::duration</c>: the length of the whole input, in
    /// AV_TIME_BASE units, or 0 when it states none. Matroska keeps per-stream
    /// durations only as tags, so for it this is often the only length there is.
    /// </summary>
    public static long DurationOfInput(void* formatContext)
    {
        var value = *(long*)((byte*)formatContext + FormatContextDuration);
        return value is AVConstants.NoPtsValue or < 0 ? 0 : value;
    }

    /// <summary>Reads <c>AVFormatContext::iformat</c>.</summary>
    public static void* InputFormatOf(void* formatContext) =>
        *(void**)((byte*)formatContext + FormatContextInputFormat);

    /// <summary>Reads <c>AVFormatContext::oformat</c>.</summary>
    public static void* OutputFormatOf(void* formatContext) =>
        *(void**)((byte*)formatContext + FormatContextOutputFormat);

    /// <summary>Reads the <c>name</c> of an <c>AVInputFormat</c> or <c>AVOutputFormat</c>.</summary>
    public static string NameOfFormat(void* format) =>
        format is null ? string.Empty : Utf8.ToManagedOrEmpty(*(byte**)((byte*)format + FormatName));

    /// <summary>Reads <c>AVOutputFormat::flags</c>.</summary>
    public static int FlagsOfOutputFormat(void* outputFormat) => *(int*)((byte*)outputFormat + OutputFormatFlags);

    /// <summary>The address of <c>AVFormatContext::metadata</c>, for the <c>av_dict_*</c> functions.</summary>
    public static void** MetadataOf(void* formatContext)
    {
        RequireMetadataLayout();
        return (void**)((byte*)formatContext + FormatContextMetadata);
    }

    /// <summary>The address of <c>AVStream::metadata</c>, for the <c>av_dict_*</c> functions.</summary>
    public static void** MetadataOfStream(void* stream)
    {
        RequireMetadataLayout();
        return (void**)((byte*)stream + StreamMetadata);
    }

    /// <summary>Reads <c>AVStream::disposition</c>.</summary>
    public static int DispositionOf(void* stream) => *(int*)((byte*)stream + StreamDisposition);

    /// <summary>Writes <c>AVStream::disposition</c>.</summary>
    public static void SetDisposition(void* stream, int value)
    {
        RequireMetadataLayout();
        *(int*)((byte*)stream + StreamDisposition) = value;
    }

    /// <summary>
    /// Tells the demuxer to drop every packet of a stream, so a reader that wants
    /// one stream does not pay for reading the others. <c>AVDISCARD_ALL</c> is 48.
    /// </summary>
    public static void DiscardStream(void* stream) => *(int*)((byte*)stream + StreamDiscard) = AVConstants.DiscardAll;

    /// <summary>Reads <c>AVStream::sample_aspect_ratio</c>.</summary>
    public static AVRationalNative SampleAspectRatioOf(void* stream) =>
        *(AVRationalNative*)((byte*)stream + StreamSampleAspectRatio);

    /// <summary>Reads <c>AVFormatContext::nb_chapters</c>.</summary>
    public static int ChapterCountOf(void* formatContext) =>
        (int)*(uint*)((byte*)formatContext + FormatContextNbChapters);

    /// <summary>Reads <c>AVFormatContext::chapters[index]</c>.</summary>
    public static void* ChapterAt(void* formatContext, int index) =>
        (*(void***)((byte*)formatContext + FormatContextChapters))[index];

    /// <summary>Reads the time span of an <c>AVChapter</c>.</summary>
    public static (long Start, long End, AVRationalNative TimeBase) SpanOfChapter(void* chapter) =>
        (*(long*)((byte*)chapter + ChapterStart),
         *(long*)((byte*)chapter + ChapterEnd),
         *(AVRationalNative*)((byte*)chapter + ChapterTimeBase));

    /// <summary>The address of <c>AVChapter::metadata</c>, for the <c>av_dict_*</c> functions.</summary>
    public static void** MetadataOfChapter(void* chapter)
    {
        RequireMetadataLayout();
        return (void**)((byte*)chapter + ChapterMetadata);
    }

    /// <summary>
    /// Appends a chapter to an output, the way ffmpeg does: libavformat has no
    /// function for it, so the chapter is allocated at its documented size and
    /// added to the array the muxer reads.
    /// </summary>
    /// <returns>Returns the chapter, whose metadata the caller may fill.</returns>
    public static void* AddChapter(void* formatContext, long id, AVRationalNative timeBase, long start, long end)
    {
        RequireMetadataLayout();

        var chapter = (byte*)AV.av_mallocz(ChapterSize);
        if (chapter is null)
        {
            return null;
        }

        *(long*)(chapter + ChapterId) = id;
        *(AVRationalNative*)(chapter + ChapterTimeBase) = timeBase;
        *(long*)(chapter + ChapterStart) = start;
        *(long*)(chapter + ChapterEnd) = end;

        var raw = (byte*)formatContext;
        if (AV.av_dynarray_add_nofree(raw + FormatContextChapters, (int*)(raw + FormatContextNbChapters), chapter) < 0)
        {
            AV.av_free(chapter);
            return null;
        }

        return chapter;
    }

    /// <summary>Reads every entry of an <c>AVDictionary</c>, which may be null.</summary>
    public static Dictionary<string, string> ReadDictionary(void* dictionary)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (dictionary is null || !MetadataLayoutVerified)
        {
            return result;
        }

        Span<byte> empty = stackalloc byte[1];
        fixed (byte* key = empty)
        {
            void* entry = null;
            while ((entry = AV.av_dict_get(dictionary, key, entry, AVConstants.DictIgnoreSuffix)) is not null)
            {
                var name = Utf8.ToManagedOrEmpty(*(byte**)((byte*)entry + DictionaryEntryKey));
                result[name] = Utf8.ToManagedOrEmpty(*(byte**)((byte*)entry + DictionaryEntryValue));
            }
        }

        return result;
    }

    /// <summary>Reads <c>AVCodecDescriptor::props</c> for a codec, or 0 when there is no descriptor.</summary>
    public static int PropertiesOfCodec(int codecId)
    {
        if (!DescriptorLayoutVerified)
        {
            return 0;
        }

        var descriptor = AV.avcodec_descriptor_get(codecId);
        return descriptor is null ? 0 : *(int*)((byte*)descriptor + DescriptorProps);
    }

    /// <summary>Reads the ASS header a subtitle decoder produced, or <see langword="null"/>.</summary>
    public static byte[]? SubtitleHeaderOf(void* codecContext)
    {
        RequireSubtitleLayout();

        var size = *(int*)((byte*)codecContext + CodecContextSubtitleHeaderSize);
        var header = *(byte**)((byte*)codecContext + CodecContextSubtitleHeader);
        return size <= 0 || header is null ? null : new ReadOnlySpan<byte>(header, size).ToArray();
    }

    /// <summary>
    /// Gives an encoder the ASS header to write, copied into memory libavcodec
    /// frees with the context. ffmpeg copies the decoder's header the same way;
    /// the ASS encoder writes nothing usable without one.
    /// </summary>
    public static void SetSubtitleHeader(void* codecContext, ReadOnlySpan<byte> header)
    {
        RequireSubtitleLayout();

        var copy = (byte*)AV.av_mallocz((nuint)header.Length + 1);
        if (copy is null)
        {
            throw Fail("could not allocate the subtitle header.");
        }

        header.CopyTo(new Span<byte>(copy, header.Length));
        *(byte**)((byte*)codecContext + CodecContextSubtitleHeader) = copy;
        *(int*)((byte*)codecContext + CodecContextSubtitleHeaderSize) = header.Length;
    }

    /// <summary>Reads the timing of a decoded <c>AVSubtitle</c>: its pts in AV_TIME_BASE units and its display window in milliseconds.</summary>
    public static (long Pts, uint StartMs, uint EndMs) TimingOfSubtitle(void* subtitle)
    {
        RequireSubtitleLayout();
        var raw = (byte*)subtitle;
        return (*(long*)(raw + SubtitlePts), *(uint*)(raw + SubtitleStartDisplayTime), *(uint*)(raw + SubtitleEndDisplayTime));
    }

    /// <summary>Writes the timing of an <c>AVSubtitle</c>.</summary>
    public static void SetTimingOfSubtitle(void* subtitle, long pts, uint startMs, uint endMs)
    {
        RequireSubtitleLayout();
        var raw = (byte*)subtitle;
        *(long*)(raw + SubtitlePts) = pts;
        *(uint*)(raw + SubtitleStartDisplayTime) = startMs;
        *(uint*)(raw + SubtitleEndDisplayTime) = endMs;
    }

    /// <summary>Reads <c>AVSubtitle::num_rects</c>.</summary>
    public static int RectCountOf(void* subtitle) => (int)*(uint*)((byte*)subtitle + SubtitleNumRects);

    /// <summary>Reads the type, text and ASS event of <c>AVSubtitle::rects[index]</c>.</summary>
    public static (int Type, string? Text, string? Ass) RectOf(void* subtitle, int index)
    {
        RequireSubtitleLayout();
        var rects = *(byte***)((byte*)subtitle + SubtitleRects);
        var rect = rects is null ? null : rects[index];
        if (rect is null)
        {
            return (0, null, null);
        }

        var text = *(byte**)(rect + SubtitleRectText);
        var ass = *(byte**)(rect + SubtitleRectAss);
        return (*(int*)(rect + SubtitleRectType),
                text is null ? null : Utf8.ToManagedOrEmpty(text),
                ass is null ? null : Utf8.ToManagedOrEmpty(ass));
    }

    /// <summary>
    /// Fills a caller-owned <c>AVSubtitle</c> with one ASS event, for
    /// <c>avcodec_encode_subtitle</c>, which only reads it.
    /// </summary>
    /// <param name="subtitle">A zeroed block of <see cref="SubtitleSize"/> bytes.</param>
    /// <param name="rect">A zeroed block of <see cref="SubtitleRectSize"/> bytes.</param>
    /// <param name="rectArray">One pointer's worth of memory to hold the rect array.</param>
    /// <param name="assEvent">The event, NUL-terminated.</param>
    /// <param name="pts">Presentation time in AV_TIME_BASE units.</param>
    /// <param name="durationMs">How long the event stays up.</param>
    public static void FillAssSubtitle(void* subtitle, void* rect, void** rectArray, byte* assEvent, long pts, uint durationMs)
    {
        RequireSubtitleLayout();

        *(int*)((byte*)rect + SubtitleRectType) = AVConstants.SubtitleAss;
        *(byte**)((byte*)rect + SubtitleRectAss) = assEvent;
        rectArray[0] = rect;

        var raw = (byte*)subtitle;
        *(ushort*)(raw + SubtitleFormat) = 1;
        *(uint*)(raw + SubtitleNumRects) = 1;
        *(void***)(raw + SubtitleRects) = rectArray;
        SetTimingOfSubtitle(subtitle, pts, 0, durationMs);
    }

    private static void RequireMetadataLayout()
    {
        if (!MetadataLayoutVerified)
        {
            throw Fail("the AVDictionary and metadata fields were not confirmed, so tags are unavailable on this FFmpeg build.");
        }
    }

    /// <summary>Throws unless the subtitle layouts were confirmed.</summary>
    public static void RequireSubtitleLayout()
    {
        if (!SubtitleLayoutVerified)
        {
            throw Fail("the AVSubtitle layout was not confirmed, so subtitle conversion is unavailable on this FFmpeg build.");
        }
    }

    /// <summary>
    /// Confirms <c>AVDictionaryEntry</c> against an entry set through the API,
    /// and <c>AVFormatContext::metadata</c> by the two option-backed fields right
    /// after it, written through AVOptions and read back at their offsets.
    /// </summary>
    private static bool ProbeMetadataLayout()
    {
        void* dictionary = null;
        try
        {
            if (AV.DictionarySet(&dictionary, "mediatoolkitnet", "probe") < 0 || dictionary is null)
            {
                return false;
            }

            Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var key = new Utf8Scoped("mediatoolkitnet", scratch);
            var entry = AV.av_dict_get(dictionary, key.Pointer, null, 0);
            if (entry is null
                || Utf8.ToManagedOrEmpty(*(byte**)((byte*)entry + DictionaryEntryKey)) != "mediatoolkitnet"
                || Utf8.ToManagedOrEmpty(*(byte**)((byte*)entry + DictionaryEntryValue)) != "probe")
            {
                return false;
            }
        }
        finally
        {
            AV.av_dict_free(&dictionary);
        }

        var context = (byte*)AV.avformat_alloc_context();
        if (context is null)
        {
            return false;
        }

        try
        {
            const int fpsProbe = 0x2345;
            const long realtime = 0x0123_4567_89AB;
            return *(void**)(context + FormatContextMetadata) is null
                && AV.SetOption(context, "fpsprobesize", fpsProbe) >= 0
                && AV.SetOption(context, "start_time_realtime", realtime) >= 0
                && *(int*)(context + FormatContextFpsProbeSize) == fpsProbe
                && *(long*)(context + FormatContextStartTimeRealtime) == realtime;
        }
        finally
        {
            AV.avformat_free_context(context);
        }
    }

    /// <summary>
    /// Confirms <c>AVCodecDescriptor</c> against two codecs whose kind is known:
    /// SubRip is text and DVD subtitles are bitmaps.
    /// </summary>
    private static bool ProbeDescriptorLayout()
    {
        return Check("subrip", AVConstants.CodecPropTextSub) && Check("dvd_subtitle", AVConstants.CodecPropBitmapSub);

        static bool Check(string name, int expectedProp)
        {
            Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var utf8 = new Utf8Scoped(name, scratch);
            var descriptor = (byte*)AV.avcodec_descriptor_get_by_name(utf8.Pointer);
            return descriptor is not null
                && Utf8.ToManagedOrEmpty(*(byte**)(descriptor + DescriptorName)) == name
                && *(int*)(descriptor + DescriptorType) == (int)AVMediaType.Subtitle
                && (*(int*)(descriptor + DescriptorProps) & expectedProp) != 0;
        }
    }

    /// <summary>
    /// Confirms <c>AVSubtitle</c>, <c>AVSubtitleRect</c> and the subtitle header
    /// by decoding one SubRip packet whose text, time and duration are known.
    /// </summary>
    /// <remarks>
    /// The SubRip decoder produces a default ASS header when it opens, and turns
    /// the packet into one rect of type SUBTITLE_ASS holding the text, with the
    /// packet's pts moved to AV_TIME_BASE and its duration into
    /// <c>end_display_time</c>. Finding all of those where the offsets say pins
    /// every field this binding touches.
    /// </remarks>
    private static bool ProbeSubtitleLayout()
    {
        const string text = "mediatoolkitnet probe";
        const long ptsMs = 2000;
        const long durationMs = 500;

        Span<byte> nameScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var name = new Utf8Scoped("subrip", nameScratch);
        var codec = AV.avcodec_find_decoder_by_name(name.Pointer);
        if (codec is null)
        {
            return false;
        }

        var context = AV.avcodec_alloc_context3(codec);
        if (context is null)
        {
            return false;
        }

        var packet = AV.av_packet_alloc();
        var subtitle = stackalloc byte[SubtitleSize];
        new Span<byte>(subtitle, SubtitleSize).Clear();
        var decoded = false;
        try
        {
            if (packet is null
                || AV.SetOption(context, "pkt_timebase", "1/1000") < 0
                || AV.avcodec_open2(context, codec, null) < 0)
            {
                return false;
            }

            var headerSize = *(int*)((byte*)context + CodecContextSubtitleHeaderSize);
            var header = *(byte**)((byte*)context + CodecContextSubtitleHeader);
            if (headerSize is <= 0 or > 1 << 20 || header is null
                || !Utf8.ToManagedOrEmpty(header).StartsWith("[Script Info]", StringComparison.Ordinal))
            {
                return false;
            }

            var payload = System.Text.Encoding.UTF8.GetBytes(text);
            if (AV.av_new_packet(packet, payload.Length) < 0)
            {
                return false;
            }

            payload.CopyTo(new Span<byte>(packet->Data, payload.Length));
            packet->Pts = ptsMs;
            packet->Dts = ptsMs;
            packet->Duration = durationMs;

            int got;
            if (AV.avcodec_decode_subtitle2(context, subtitle, &got, packet) < 0 || got == 0)
            {
                return false;
            }

            decoded = true;
            var rects = *(byte***)(subtitle + SubtitleRects);
            return *(ushort*)(subtitle + SubtitleFormat) == 1
                && *(uint*)(subtitle + SubtitleNumRects) == 1
                && *(uint*)(subtitle + SubtitleEndDisplayTime) == durationMs
                && *(long*)(subtitle + SubtitlePts) == ptsMs * 1000
                && rects is not null && rects[0] is not null
                && *(int*)(rects[0] + SubtitleRectType) == AVConstants.SubtitleAss
                && *(byte**)(rects[0] + SubtitleRectAss) is not null
                && Utf8.ToManagedOrEmpty(*(byte**)(rects[0] + SubtitleRectAss)).Contains(text, StringComparison.Ordinal);
        }
        finally
        {
            if (decoded)
            {
                AV.avsubtitle_free(subtitle);
            }

            if (packet is not null)
            {
                AV.av_packet_free(&packet);
            }

            AV.avcodec_free_context(&context);
        }
    }

    // ------------------------------------------------------------ validation

    /// <summary>
    /// Verifies the mapped offsets against the documented defaults of a freshly
    /// allocated AVFrame, AVPacket and AVCodecContext.
    /// </summary>
    /// <exception cref="MediaBackendUnavailableException">A required offset does not match.</exception>
    internal static void Validate()
    {
        ValidateFrame();
        ValidatePacket();
        CodecParametersLayoutVerified = ProbeCodecParameters();
        EncoderLayoutVerified = CodecParametersLayoutVerified && ValidateCodecContext();
        AudioFrameLayoutVerified = ProbeAudioFrameFields() && ConfirmAudioFrameFields();
        FilterLayoutVerified = ProbeFilterLayout();
        MetadataLayoutVerified = ProbeMetadataLayout();
        DescriptorLayoutVerified = ProbeDescriptorLayout();
        SubtitleLayoutVerified = DescriptorLayoutVerified && ProbeSubtitleLayout();
    }

    /// <summary>
    /// Prepares an AVFrame to receive raw audio for an encoder.
    /// </summary>
    /// <remarks>
    /// FFmpeg validates the frame properties against the encoder context, so
    /// <c>sample_rate</c> and <c>ch_layout</c> have to be set even though they
    /// live past the stable head of the struct. Their offsets are discovered by
    /// <see cref="ProbeAudioFrameFields"/> rather than hard-coded.
    /// </remarks>
    public static void PrepareAudioFrame(
        AVFrameHead* frame,
        int sampleRate,
        int channels,
        ulong channelMask,
        int sampleFormat,
        int samples)
    {
        if (!AudioFrameLayoutVerified)
        {
            throw Fail("the AVFrame audio field offsets could not be determined, so audio encoding is unavailable.");
        }

        frame->Format = sampleFormat;
        frame->NbSamples = samples;
        *(int*)((byte*)frame + FrameSampleRate) = sampleRate;
        *(AVChannelLayoutNative*)((byte*)frame + FrameChannelLayout) = AVChannelLayoutNative.Of(channels, channelMask);
    }

    /// <summary>
    /// Discovers the <c>AVCodecParameters</c> field offsets at runtime instead of
    /// hard-coding them.
    /// </summary>
    /// <remarks>
    /// A freshly allocated <c>AVCodecParameters</c> has <c>profile</c> and
    /// <c>level</c> both set to FF_PROFILE_UNKNOWN / FF_LEVEL_UNKNOWN, which is
    /// -99. No other field defaults to that value, so a pair of adjacent -99
    /// integers pins the position of <c>profile</c>, and every other field this
    /// binding writes sits at a fixed distance from it:
    /// <c>level</c> = P+4, <c>width</c> = P+8, <c>height</c> = P+12, and
    /// <c>bit_rate</c> = P-16 regardless of whether the compiler inserted
    /// padding before it. <c>format</c> is then either P-20 or P-24, and it is
    /// the one holding -1.
    /// </remarks>
    private static bool ProbeCodecParameters()
    {
        const int unknownProfile = -99;
        const int searchLimit = 256;

        var parameters = AV.avcodec_parameters_alloc();
        if (parameters is null)
        {
            return false;
        }

        try
        {
            var raw = (byte*)parameters;
            for (var offset = 32; offset + 16 <= searchLimit; offset += 4)
            {
                if (*(int*)(raw + offset) != unknownProfile || *(int*)(raw + offset + 4) != unknownProfile)
                {
                    continue;
                }

                var format = *(int*)(raw + offset - 20) == -1 ? offset - 20
                    : *(int*)(raw + offset - 24) == -1 ? offset - 24
                    : -1;

                if (format < 8)
                {
                    continue;
                }

                // width and height are zero on a fresh allocation; use that as a
                // final cross-check before trusting the offsets.
                if (*(int*)(raw + offset + 8) != 0 || *(int*)(raw + offset + 12) != 0)
                {
                    continue;
                }

                CodecParametersFormat = format;
                CodecParametersBitRate = offset - 16;
                CodecParametersWidth = offset + 8;
                CodecParametersHeight = offset + 12;
                return true;
            }

            return false;
        }
        finally
        {
            var local = parameters;
            AV.avcodec_parameters_free(&local);
        }
    }

    /// <summary>
    /// Locates <c>AVFrame::sample_rate</c> and <c>AVFrame::ch_layout</c> without
    /// writing anything.
    /// </summary>
    /// <remarks>
    /// A PCM decoder is opened with a deliberately unusual sample rate and
    /// channel count, then fed one packet. FFmpeg stamps those values onto the
    /// frame it returns, so scanning the frame past its stable head for the
    /// marker rate and for a native-order channel layout with the marker channel
    /// count reveals both offsets.
    /// </remarks>
    private static bool ProbeAudioFrameFields()
    {
        const int markerRate = 12_345;
        const int markerChannels = 3;
        const int headSize = 160;
        const int scanLimit = 1024;

        Span<byte> scratch = stackalloc byte[32];
        using var codecName = new Utf8Scoped("pcm_s16le", scratch);
        var codec = AV.avcodec_find_decoder_by_name(codecName.Pointer);
        if (codec is null)
        {
            return false;
        }

        var context = AV.avcodec_alloc_context3(codec);
        if (context is null)
        {
            return false;
        }

        var packet = AV.av_packet_alloc();
        var frame = AV.av_frame_alloc();
        var payload = stackalloc byte[markerChannels * 2 * 16];

        try
        {
            if (packet is null || frame is null)
            {
                return false;
            }

            AV.SetOption(context, "ar", markerRate);

            // The layout, not the channel count: "ac" is a command line option and
            // not an AVOption, so a decoder told only that opens with no layout at
            // all and refuses. Asking for "3c" gives the conventional layout of
            // three channels, in native order, which is what the scan looks for.
            AV.SetOption(context, "ch_layout", FormattableString.Invariant($"{markerChannels}c"));
            if (AV.avcodec_open2(context, codec, null) < 0)
            {
                return false;
            }

            packet->Data = payload;
            packet->Size = markerChannels * 2 * 16;
            if (AV.avcodec_send_packet(context, packet) < 0 || AV.avcodec_receive_frame(context, frame) < 0)
            {
                return false;
            }

            var raw = (byte*)frame;
            var sampleRateOffset = -1;
            for (var offset = headSize; offset + 4 <= scanLimit; offset += 4)
            {
                if (*(int*)(raw + offset) == markerRate)
                {
                    sampleRateOffset = offset;
                    break;
                }
            }

            if (sampleRateOffset < 0)
            {
                return false;
            }

            // AVChannelLayout starts with AV_CHANNEL_ORDER_NATIVE (1) followed by
            // the channel count, and is 8-byte aligned because of the union that
            // follows.
            for (var offset = sampleRateOffset + 4; offset + 16 <= scanLimit; offset += 8)
            {
                var aligned = (offset + 7) & ~7;
                if (aligned + 16 > scanLimit)
                {
                    break;
                }

                if (*(int*)(raw + aligned) == 1 && *(int*)(raw + aligned + 4) == markerChannels)
                {
                    FrameSampleRate = sampleRateOffset;
                    FrameChannelLayout = aligned;
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (frame is not null)
            {
                var localFrame = frame;
                AV.av_frame_free(&localFrame);
            }

            if (packet is not null)
            {
                // The payload is stack memory that the packet never owned.
                packet->Data = null;
                packet->Size = 0;
                var localPacket = packet;
                AV.av_packet_free(&localPacket);
            }

            var localContext = context;
            AV.avcodec_free_context(&localContext);
        }
    }

    /// <summary>
    /// Writes into the offsets found by <see cref="ProbeAudioFrameFields"/> and
    /// checks that <c>av_frame_get_buffer</c> allocates exactly the expected
    /// amount, which confirms FFmpeg read the values back from where they were put.
    /// </summary>
    private static bool ConfirmAudioFrameFields()
    {
        if (FrameSampleRate < 0 || FrameChannelLayout < 0)
        {
            return false;
        }

        const int samples = 1024;
        const int channels = 2;
        var frame = AV.av_frame_alloc();
        if (frame is null)
        {
            return false;
        }

        try
        {
            frame->Format = (int)AVSampleFormat.FltP;
            frame->NbSamples = samples;
            *(int*)((byte*)frame + FrameSampleRate) = 48_000;
            *(AVChannelLayoutNative*)((byte*)frame + FrameChannelLayout) = AVChannelLayoutNative.Default(channels);

            if (AV.av_frame_get_buffer(frame, 0) < 0)
            {
                return false;
            }

            // Planar float: one plane per channel, four bytes per sample.
            return frame->Linesize[0] == samples * 4 && frame->Data[1] != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            var local = frame;
            AV.av_frame_free(&local);
        }
    }

    private static void RequireCodecParametersLayout()
    {
        if (!CodecParametersLayoutVerified)
        {
            throw Fail("the AVCodecParameters layout could not be determined.");
        }
    }

    private static void ValidateFrame()
    {
        var frame = AV.av_frame_alloc();
        if (frame is null)
        {
            throw Fail("av_frame_alloc returned NULL.");
        }

        try
        {
            // av_frame_alloc leaves the frame unreferenced: format is -1, the
            // geometry is zero and both timestamps are AV_NOPTS_VALUE.
            if (frame->Format != -1 || frame->Width != 0 || frame->Height != 0 || frame->NbSamples != 0)
            {
                throw Fail("the AVFrame layout does not match (format/width/height/nb_samples).");
            }

            if (frame->Pts != AVConstants.NoPtsValue || frame->PktDts != AVConstants.NoPtsValue)
            {
                throw Fail("the AVFrame layout does not match (pts/pkt_dts).");
            }
        }
        finally
        {
            var local = frame;
            AV.av_frame_free(&local);
        }
    }

    private static void ValidatePacket()
    {
        var packet = AV.av_packet_alloc();
        if (packet is null)
        {
            throw Fail("av_packet_alloc returned NULL.");
        }

        try
        {
            // av_packet_alloc initialises through av_packet_unref: null payload,
            // AV_NOPTS_VALUE timestamps and pos = -1.
            if (packet->Data is not null || packet->Size != 0 || packet->Pos != -1)
            {
                throw Fail("the AVPacket layout does not match (data/size/pos).");
            }

            if (packet->Pts != AVConstants.NoPtsValue || packet->Dts != AVConstants.NoPtsValue)
            {
                throw Fail("the AVPacket layout does not match (pts/dts).");
            }
        }
        finally
        {
            var local = packet;
            AV.av_packet_free(&local);
        }
    }

    private static bool ValidateCodecContext()
    {
        // Any video encoder will do; try a few so the check still runs on
        // minimal builds.
        Span<byte> scratch = stackalloc byte[64];
        foreach (var name in (string[])["mjpeg", "mpeg4", "rawvideo", "png", "ffv1"])
        {
            using var utf8 = new Utf8Scoped(name, scratch);
            var codec = AV.avcodec_find_encoder_by_name(utf8.Pointer);
            if (codec is null)
            {
                continue;
            }

            var context = AV.avcodec_alloc_context3(codec);
            if (context is null)
            {
                continue;
            }

            try
            {
                var codecType = *(int*)((byte*)context + CodecContextCodecType);
                var timeBase = *(AVRationalNative*)((byte*)context + CodecContextTimeBase);
                var pixFmt = *(int*)((byte*)context + CodecContextPixFmt);

                // avcodec_alloc_context3 sets codec_type from the codec,
                // time_base to 0/1 and pix_fmt to AV_PIX_FMT_NONE.
                return codecType == (int)AVMediaType.Video
                       && timeBase is { Num: 0, Den: 1 }
                       && pixFmt == -1;
            }
            finally
            {
                var local = context;
                AV.avcodec_free_context(&local);
            }
        }

        return false;
    }

    /// <summary>
    /// Verifies the AVStream and AVFormatContext offsets against an open input.
    /// Called once per demuxer, which is cheap and catches a wrong build early.
    /// </summary>
    internal static void ValidateOpenInput(void* formatContext, string url)
    {
        // url sits just after the chapter fields, and avformat_open_input stores
        // exactly the string it was given, so finding it where it belongs pins them.
        var storedUrl = *(byte**)((byte*)formatContext + FormatContextUrl);
        if (storedUrl is null || Utf8.ToManagedOrEmpty(storedUrl) != url)
        {
            throw Fail("the AVFormatContext layout does not match: url is not the one that was opened.");
        }

        var chapters = ChapterCountOf(formatContext);
        if (chapters is < 0 or > 100_000
            || (chapters > 0 && *(void**)((byte*)formatContext + FormatContextChapters) is null))
        {
            throw Fail($"the AVFormatContext layout does not match: nb_chapters = {chapters}.");
        }

        var count = NbStreams(formatContext);
        if (count is < 0 or > 4096)
        {
            throw Fail($"the AVFormatContext layout does not match: nb_streams = {count}.");
        }

        for (var i = 0; i < count; i++)
        {
            var stream = Stream(formatContext, i);
            if (stream is null || StreamIndexOf(stream) != i)
            {
                throw Fail("the AVStream layout does not match: index differs from its position in the streams array.");
            }

            var parameters = CodecParametersOf(stream);
            if (parameters is null)
            {
                throw Fail("the AVStream layout does not match: codecpar is NULL.");
            }

            var type = CodecTypeOf(parameters);
            if (type is < AVMediaType.Unknown or > AVMediaType.Attachment)
            {
                throw Fail($"the AVStream layout does not match: codecpar->codec_type = {(int)type}.");
            }

            var timeBase = TimeBaseOf(stream);
            if (timeBase.Den <= 0 || timeBase.Num <= 0)
            {
                throw Fail($"the AVStream layout does not match: time_base = {timeBase.Num}/{timeBase.Den}.");
            }

            // Demuxers may set a stream aside on open, as the MOV demuxer does its
            // chapter track, but only ever to one of the seven AVDiscard values.
            if (*(int*)((byte*)stream + StreamDiscard) is not (-16 or 0 or 8 or 16 or 24 or 32 or 48))
            {
                throw Fail("the AVStream layout does not match: discard holds no AVDiscard value.");
            }
        }
    }

    /// <summary>
    /// Verifies the AVStream offsets against a stream just added to an output,
    /// before anything is written through them.
    /// </summary>
    /// <param name="stream">The stream <c>avformat_new_stream</c> returned.</param>
    /// <param name="expectedIndex">The position it was added at.</param>
    /// <exception cref="MediaBackendUnavailableException">The offsets do not match this build.</exception>
    /// <remarks>
    /// The muxing path writes codec parameters through <c>codecpar</c>, so a
    /// wrong offset there would not read rubbish but write through it. A fresh
    /// stream knows its own index and owns allocated parameters whose type is
    /// still unknown, and that is enough to catch the case.
    /// </remarks>
    internal static void ValidateNewStream(void* stream, int expectedIndex)
    {
        var index = StreamIndexOf(stream);
        if (index != expectedIndex)
        {
            throw Fail(
                $"the AVStream layout does not match: a stream added at {expectedIndex} reports index {index}.");
        }

        var parameters = CodecParametersOf(stream);
        if (parameters is null)
        {
            throw Fail("the AVStream layout does not match: codecpar of a new stream is NULL.");
        }

        var type = CodecTypeOf(parameters);
        if (type != AVMediaType.Unknown)
        {
            throw Fail(
                $"the AVStream layout does not match: codecpar->codec_type of a new stream is {(int)type} " +
                $"rather than {(int)AVMediaType.Unknown}.");
        }

        // avformat_new_stream sets the aspect ratio to 0/1 and leaves the tags
        // and disposition empty, which pins the fields tags are written through.
        var aspect = SampleAspectRatioOf(stream);
        if (aspect.Num != 0 || aspect.Den != 1
            || *(void**)((byte*)stream + StreamMetadata) is not null
            || DispositionOf(stream) != 0)
        {
            throw Fail("the AVStream layout does not match: a new stream's aspect ratio, tags or disposition are not empty.");
        }
    }

    private static void RequireEncoderLayout()
    {
        if (!EncoderLayoutVerified)
        {
            throw Fail("the AVCodecContext layout was not confirmed, so encoding is unavailable on this FFmpeg build.");
        }
    }

    private static MediaBackendUnavailableException Fail(string reason) =>
        new(FFmpegLibraries.BackendName,
            $"{reason} The series listed in FFmpegGeneration are expected; adjust the offsets in AbiLayout for your build.");
}
