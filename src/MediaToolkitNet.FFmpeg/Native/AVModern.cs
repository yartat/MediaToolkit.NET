#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg.Native;

/// <summary>
/// Entry points that only the newer FFmpeg releases export (8.x, 9.0, 9.1). Each
/// one is optional: a library that lacks it leaves the pointer null, and
/// <see cref="FFmpegFeatures"/> says so, so older series keep working unchanged.
/// </summary>
internal static unsafe class AVModern
{
    // ---------------------------------------------------------------- libavcodec

    /// <summary><c>int avcodec_receive_frame_flags(AVCodecContext *, AVFrame *, unsigned flags)</c> (libavcodec 62.22, FFmpeg 8.1).</summary>
    public static delegate* unmanaged[Cdecl]<void*, AVFrameHead*, uint, int> avcodec_receive_frame_flags;

    /// <summary><c>AVBitStreamFilterGraph *av_bsf_graph_alloc(void)</c> (libavcodec 63.4, FFmpeg 9.1).</summary>
    public static delegate* unmanaged[Cdecl]<void*> av_bsf_graph_alloc;

    /// <summary><c>const AVBitStreamFilter *av_bsf_get_by_name(const char *name)</c> (FFmpeg 9.1).</summary>
    public static delegate* unmanaged[Cdecl]<byte*, void*> av_bsf_get_by_name;

    /// <summary><c>int av_bsf_graph_alloc_filter(AVBitStreamFilterContext **, const AVBitStreamFilter *, const char *name, AVBitStreamFilterGraph *)</c> (FFmpeg 9.1).</summary>
    public static delegate* unmanaged[Cdecl]<void**, void*, byte*, void*, int> av_bsf_graph_alloc_filter;

    /// <summary><c>int av_bsf_graph_create_filter(AVBitStreamFilterContext **, const AVBitStreamFilter *, const char *name, AVDictionary **options, AVBitStreamFilterGraph *)</c> (FFmpeg 9.1).</summary>
    public static delegate* unmanaged[Cdecl]<void**, void*, byte*, void**, void*, int> av_bsf_graph_create_filter;

    /// <summary><c>int av_bsf_link(AVBitStreamFilterContext *src, unsigned srcpad, AVBitStreamFilterContext *dst, unsigned dstpad)</c> (FFmpeg 9.1).</summary>
    public static delegate* unmanaged[Cdecl]<void*, uint, void*, uint, int> av_bsf_link;

    /// <summary><c>int av_bsf_source_parameters_set(AVBitStreamFilterContext *, const AVCodecParameters *)</c> (FFmpeg 9.1).</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, int> av_bsf_source_parameters_set;

    /// <summary><c>int av_bsf_source_add_packet(AVBitStreamFilterContext *, AVPacket *, int flags)</c> (FFmpeg 9.1).</summary>
    public static delegate* unmanaged[Cdecl]<void*, AVPacketNative*, int, int> av_bsf_source_add_packet;

    /// <summary><c>int av_bsf_sink_get_packet(AVBitStreamFilterContext *, AVPacket *, int flags)</c> (FFmpeg 9.1).</summary>
    public static delegate* unmanaged[Cdecl]<void*, AVPacketNative*, int, int> av_bsf_sink_get_packet;

    /// <summary><c>AVRational av_bsf_sink_get_time_base(const AVBitStreamFilterContext *)</c> (FFmpeg 9.1).</summary>
    public static delegate* unmanaged[Cdecl]<void*, AVRationalNative> av_bsf_sink_get_time_base;

    /// <summary><c>int av_bsf_graph_config(AVBitStreamFilterGraph *, void *log_ctx)</c> (FFmpeg 9.1).</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, int> av_bsf_graph_config;

    /// <summary><c>void av_bsf_graph_free(AVBitStreamFilterGraph **)</c> (FFmpeg 9.1).</summary>
    public static delegate* unmanaged[Cdecl]<void**, void> av_bsf_graph_free;

    /// <summary><c>int av_bsf_init_str(AVBitStreamFilterContext *, const char *args)</c> (FFmpeg 9.1).</summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, int> av_bsf_init_str;

    /// <summary>True when every entry point of the bitstream filter graph API was found.</summary>
    public static bool HasBitstreamFilterGraphs =>
        av_bsf_graph_alloc is not null && av_bsf_get_by_name is not null && av_bsf_graph_alloc_filter is not null
        && av_bsf_graph_create_filter is not null && av_bsf_link is not null && av_bsf_source_parameters_set is not null
        && av_bsf_source_add_packet is not null && av_bsf_sink_get_packet is not null
        && av_bsf_init_str is not null && av_bsf_sink_get_time_base is not null && av_bsf_graph_config is not null && av_bsf_graph_free is not null;

    /// <summary><c>int av_gain_map_params_validate(const AVGainMapParams *)</c> (libavutil 61.10, FFmpeg 9.1).</summary>
    public static delegate* unmanaged[Cdecl]<void*, int> av_gain_map_params_validate;

    /// <summary><c>int av_gain_map_params_from_iso21496(AVGainMapParams *, const uint8_t *, size_t)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, nuint, int> av_gain_map_params_from_iso21496;

    /// <summary><c>int av_gain_map_params_to_iso21496(const AVGainMapParams *, uint8_t *buf)</c>: the number of bytes written.</summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, int> av_gain_map_params_to_iso21496;

    /// <summary><c>AVDownmixMatrix *av_downmix_matrix_alloc(enum AVDownmixType, int in_ch_count, size_t *out_size)</c> (libavutil 61.3, FFmpeg 9.1).</summary>
    public static delegate* unmanaged[Cdecl]<int, int, nuint*, void*> av_downmix_matrix_alloc;

    /// <summary><c>int avcodec_encode_reconfigure(AVCodecContext *, AVDictionary **options)</c> (libavcodec 63.8, FFmpeg 9.1).</summary>
    public static delegate* unmanaged[Cdecl]<void*, void**, int> avcodec_encode_reconfigure;

    /// <summary><c>const AVOption *av_opt_find(void *obj, const char *name, const char *unit, int opt_flags, int search_flags)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, byte*, int, int, void*> av_opt_find;

    /// <summary><c>int av_exif_parse_buffer(void *logctx, const uint8_t *, size_t, AVExifMetadata *, enum AVExifHeaderMode)</c> (libavcodec 62.13, FFmpeg 8.1).</summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, nuint, void*, int, int> av_exif_parse_buffer;

    /// <summary><c>void av_exif_free(AVExifMetadata *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void> av_exif_free;

    /// <summary><c>const char *av_exif_get_tag_name(uint16_t id)</c></summary>
    public static delegate* unmanaged[Cdecl]<ushort, byte*> av_exif_get_tag_name;

    // --------------------------------------------------------------- libavformat

    /// <summary><c>int av_mime_codec_str(const AVCodecParameters *, AVRational frame_rate, struct AVBPrint *)</c> (libavformat 62.7, FFmpeg 8.1).</summary>
    public static delegate* unmanaged[Cdecl]<void*, AVRationalNative, void*, int> av_mime_codec_str;

    /// <summary><c>const char *avio_find_protocol_name(const char *url)</c>: null when no protocol takes the URL.</summary>
    public static delegate* unmanaged[Cdecl]<byte*, byte*> avio_find_protocol_name;

    // ------------------------------------------------------------------ libavutil

    /// <summary><c>AVFrameSideData *av_frame_get_side_data(const AVFrame *, enum AVFrameSideDataType)</c></summary>
    public static delegate* unmanaged[Cdecl]<AVFrameHead*, int, void*> av_frame_get_side_data;

    /// <summary><c>AVFrameSideData *av_frame_new_side_data(AVFrame *, enum AVFrameSideDataType, size_t size)</c></summary>
    public static delegate* unmanaged[Cdecl]<AVFrameHead*, int, nuint, void*> av_frame_new_side_data;

    /// <summary><c>const char *av_frame_side_data_name(enum AVFrameSideDataType)</c>: null for a type the library does not know.</summary>
    public static delegate* unmanaged[Cdecl]<int, byte*> av_frame_side_data_name;

    /// <summary><c>void av_bprint_init(AVBPrint *, unsigned initial_size, unsigned max_size)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, uint, uint, void> av_bprint_init;

    /// <summary><c>int av_bprint_finalize(AVBPrint *, char **ret_str)</c>: frees what the buffer allocated.</summary>
    public static delegate* unmanaged[Cdecl]<void*, byte**, int> av_bprint_finalize;

    /// <summary>Resolves what the loaded libraries have. Called once, after the required entry points are bound.</summary>
    internal static void Bind(NativeModule util, NativeModule codec, NativeModule format)
    {
        avcodec_receive_frame_flags = codec.TryGetExport(nameof(avcodec_receive_frame_flags), out var receive)
            ? (delegate* unmanaged[Cdecl]<void*, AVFrameHead*, uint, int>)receive
            : null;

        avio_find_protocol_name = format.TryGetExport(nameof(avio_find_protocol_name), out var protocolName)
            ? (delegate* unmanaged[Cdecl]<byte*, byte*>)protocolName
            : null;

        av_opt_find = util.TryGetExport(nameof(av_opt_find), out var optFind)
            ? (delegate* unmanaged[Cdecl]<void*, byte*, byte*, int, int, void*>)optFind
            : null;
        av_bsf_graph_alloc = codec.TryGetExport(nameof(av_bsf_graph_alloc), out var bsfGraphAlloc)
            ? (delegate* unmanaged[Cdecl]<void*>)bsfGraphAlloc
            : null;
        av_bsf_get_by_name = codec.TryGetExport(nameof(av_bsf_get_by_name), out var v_av_bsf_get_by_name)
            ? (delegate* unmanaged[Cdecl]<byte*, void*>)v_av_bsf_get_by_name
            : null;
        av_bsf_graph_alloc_filter = codec.TryGetExport(nameof(av_bsf_graph_alloc_filter), out var v_av_bsf_graph_alloc_filter)
            ? (delegate* unmanaged[Cdecl]<void**, void*, byte*, void*, int>)v_av_bsf_graph_alloc_filter
            : null;
        av_bsf_graph_create_filter = codec.TryGetExport(nameof(av_bsf_graph_create_filter), out var v_av_bsf_graph_create_filter)
            ? (delegate* unmanaged[Cdecl]<void**, void*, byte*, void**, void*, int>)v_av_bsf_graph_create_filter
            : null;
        av_bsf_link = codec.TryGetExport(nameof(av_bsf_link), out var v_av_bsf_link)
            ? (delegate* unmanaged[Cdecl]<void*, uint, void*, uint, int>)v_av_bsf_link
            : null;
        av_bsf_source_parameters_set = codec.TryGetExport(nameof(av_bsf_source_parameters_set), out var v_av_bsf_source_parameters_set)
            ? (delegate* unmanaged[Cdecl]<void*, void*, int>)v_av_bsf_source_parameters_set
            : null;
        av_bsf_source_add_packet = codec.TryGetExport(nameof(av_bsf_source_add_packet), out var v_av_bsf_source_add_packet)
            ? (delegate* unmanaged[Cdecl]<void*, AVPacketNative*, int, int>)v_av_bsf_source_add_packet
            : null;
        av_bsf_sink_get_packet = codec.TryGetExport(nameof(av_bsf_sink_get_packet), out var v_av_bsf_sink_get_packet)
            ? (delegate* unmanaged[Cdecl]<void*, AVPacketNative*, int, int>)v_av_bsf_sink_get_packet
            : null;
        av_bsf_sink_get_time_base = codec.TryGetExport(nameof(av_bsf_sink_get_time_base), out var v_av_bsf_sink_get_time_base)
            ? (delegate* unmanaged[Cdecl]<void*, AVRationalNative>)v_av_bsf_sink_get_time_base
            : null;
        av_bsf_graph_config = codec.TryGetExport(nameof(av_bsf_graph_config), out var v_av_bsf_graph_config)
            ? (delegate* unmanaged[Cdecl]<void*, void*, int>)v_av_bsf_graph_config
            : null;
        av_bsf_graph_free = codec.TryGetExport(nameof(av_bsf_graph_free), out var v_av_bsf_graph_free)
            ? (delegate* unmanaged[Cdecl]<void**, void>)v_av_bsf_graph_free
            : null;
        av_bsf_init_str = codec.TryGetExport(nameof(av_bsf_init_str), out var bsfInitStr)
            ? (delegate* unmanaged[Cdecl]<void*, byte*, int>)bsfInitStr
            : null;
        av_gain_map_params_validate = util.TryGetExport(nameof(av_gain_map_params_validate), out var gainValidate)
            ? (delegate* unmanaged[Cdecl]<void*, int>)gainValidate
            : null;
        av_gain_map_params_from_iso21496 = util.TryGetExport(nameof(av_gain_map_params_from_iso21496), out var gainFrom)
            ? (delegate* unmanaged[Cdecl]<void*, byte*, nuint, int>)gainFrom
            : null;
        av_gain_map_params_to_iso21496 = util.TryGetExport(nameof(av_gain_map_params_to_iso21496), out var gainTo)
            ? (delegate* unmanaged[Cdecl]<void*, byte*, int>)gainTo
            : null;
        av_downmix_matrix_alloc = util.TryGetExport(nameof(av_downmix_matrix_alloc), out var downmixAlloc)
            ? (delegate* unmanaged[Cdecl]<int, int, nuint*, void*>)downmixAlloc
            : null;
        avcodec_encode_reconfigure = codec.TryGetExport(nameof(avcodec_encode_reconfigure), out var reconfigure)
            ? (delegate* unmanaged[Cdecl]<void*, void**, int>)reconfigure
            : null;

        av_exif_parse_buffer = codec.TryGetExport(nameof(av_exif_parse_buffer), out var exifParse)
            ? (delegate* unmanaged[Cdecl]<void*, byte*, nuint, void*, int, int>)exifParse
            : null;
        av_exif_free = codec.TryGetExport(nameof(av_exif_free), out var exifFree)
            ? (delegate* unmanaged[Cdecl]<void*, void>)exifFree
            : null;
        av_exif_get_tag_name = codec.TryGetExport(nameof(av_exif_get_tag_name), out var exifName)
            ? (delegate* unmanaged[Cdecl]<ushort, byte*>)exifName
            : null;

        av_frame_get_side_data = util.TryGetExport(nameof(av_frame_get_side_data), out var getSideData)
            ? (delegate* unmanaged[Cdecl]<AVFrameHead*, int, void*>)getSideData
            : null;
        av_frame_new_side_data = util.TryGetExport(nameof(av_frame_new_side_data), out var newSideData)
            ? (delegate* unmanaged[Cdecl]<AVFrameHead*, int, nuint, void*>)newSideData
            : null;
        av_frame_side_data_name = util.TryGetExport(nameof(av_frame_side_data_name), out var sideDataName)
            ? (delegate* unmanaged[Cdecl]<int, byte*>)sideDataName
            : null;

        av_bprint_init = util.TryGetExport(nameof(av_bprint_init), out var bprintInit)
            ? (delegate* unmanaged[Cdecl]<void*, uint, uint, void>)bprintInit
            : null;
        av_bprint_finalize = util.TryGetExport(nameof(av_bprint_finalize), out var bprintFinalize)
            ? (delegate* unmanaged[Cdecl]<void*, byte**, int>)bprintFinalize
            : null;

        // The string builder is part of the call, so the function is only usable when both are there.
        av_mime_codec_str = av_bprint_init is not null && av_bprint_finalize is not null
            && format.TryGetExport(nameof(av_mime_codec_str), out var mime)
            ? (delegate* unmanaged[Cdecl]<void*, AVRationalNative, void*, int>)mime
            : null;
    }
}
