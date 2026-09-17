using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg.Native;

/// <summary>
/// Function pointers into the loaded FFmpeg libraries.
/// </summary>
/// <remarks>
/// Every entry is resolved once by <see cref="Bind"/> and then called through an
/// unmanaged function pointer with the C calling convention. Opaque handles are
/// <c>void*</c> on purpose: FFmpeg owns their memory and this binding never
/// allocates them itself.
/// </remarks>
public static unsafe class AV
{
    private static bool _bound;

    // ---------------------------------------------------------------- avutil

    /// <summary><c>unsigned avutil_version(void)</c></summary>
    public static delegate* unmanaged[Cdecl]<uint> avutil_version;

    /// <summary><c>AVFrame *av_frame_alloc(void)</c></summary>
    public static delegate* unmanaged[Cdecl]<AVFrameHead*> av_frame_alloc;

    /// <summary><c>void av_frame_free(AVFrame **)</c></summary>
    public static delegate* unmanaged[Cdecl]<AVFrameHead**, void> av_frame_free;

    /// <summary><c>void av_frame_unref(AVFrame *)</c></summary>
    public static delegate* unmanaged[Cdecl]<AVFrameHead*, void> av_frame_unref;

    /// <summary><c>int av_frame_get_buffer(AVFrame *, int align)</c></summary>
    public static delegate* unmanaged[Cdecl]<AVFrameHead*, int, int> av_frame_get_buffer;

    /// <summary><c>int av_frame_make_writable(AVFrame *)</c></summary>
    public static delegate* unmanaged[Cdecl]<AVFrameHead*, int> av_frame_make_writable;

    /// <summary><c>int av_strerror(int errnum, char *buf, size_t bufsize)</c></summary>
    public static delegate* unmanaged[Cdecl]<int, byte*, nuint, int> av_strerror;

    /// <summary><c>int av_dict_set(AVDictionary **, const char *key, const char *value, int flags)</c></summary>
    public static delegate* unmanaged[Cdecl]<void**, byte*, byte*, int, int> av_dict_set;

    /// <summary><c>void av_dict_free(AVDictionary **)</c></summary>
    public static delegate* unmanaged[Cdecl]<void**, void> av_dict_free;

    /// <summary><c>int av_opt_set(void *obj, const char *name, const char *val, int search_flags)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, byte*, int, int> av_opt_set;

    /// <summary><c>int av_opt_set_int(void *obj, const char *name, int64_t val, int search_flags)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, long, int, int> av_opt_set_int;

    /// <summary><c>int av_opt_get_int(void *obj, const char *name, int search_flags, int64_t *out)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, int, long*, int> av_opt_get_int;

    /// <summary><c>int64_t av_rescale_q(int64_t a, AVRational bq, AVRational cq)</c></summary>
    public static delegate* unmanaged[Cdecl]<long, AVRationalNative, AVRationalNative, long> av_rescale_q;

    /// <summary><c>void av_log_set_level(int)</c></summary>
    public static delegate* unmanaged[Cdecl]<int, void> av_log_set_level;

    /// <summary><c>int av_get_bytes_per_sample(enum AVSampleFormat)</c></summary>
    public static delegate* unmanaged[Cdecl]<int, int> av_get_bytes_per_sample;

    /// <summary><c>int av_samples_get_buffer_size(int *linesize, int nb_channels, int nb_samples, enum AVSampleFormat, int align)</c></summary>
    public static delegate* unmanaged[Cdecl]<int*, int, int, int, int, int> av_samples_get_buffer_size;

    /// <summary><c>int av_image_get_buffer_size(enum AVPixelFormat, int width, int height, int align)</c></summary>
    public static delegate* unmanaged[Cdecl]<int, int, int, int, int> av_image_get_buffer_size;

    /// <summary><c>void av_freep(void *ptr)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void> av_freep;

    /// <summary><c>int av_samples_alloc(uint8_t **audio_data, int *linesize, int nb_channels, int nb_samples, enum AVSampleFormat, int align)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte**, int*, int, int, int, int, int> av_samples_alloc;

    /// <summary><c>AVAudioFifo *av_audio_fifo_alloc(enum AVSampleFormat, int channels, int nb_samples)</c></summary>
    public static delegate* unmanaged[Cdecl]<int, int, int, void*> av_audio_fifo_alloc;

    /// <summary><c>int av_audio_fifo_write(AVAudioFifo *, void **data, int nb_samples)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void**, int, int> av_audio_fifo_write;

    /// <summary><c>int av_audio_fifo_read(AVAudioFifo *, void **data, int nb_samples)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void**, int, int> av_audio_fifo_read;

    /// <summary><c>int av_audio_fifo_size(AVAudioFifo *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int> av_audio_fifo_size;

    /// <summary><c>void av_audio_fifo_free(AVAudioFifo *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void> av_audio_fifo_free;

    // --------------------------------------------------------------- avcodec

    /// <summary><c>unsigned avcodec_version(void)</c></summary>
    public static delegate* unmanaged[Cdecl]<uint> avcodec_version;

    /// <summary><c>const AVCodec *avcodec_find_decoder(enum AVCodecID)</c></summary>
    public static delegate* unmanaged[Cdecl]<int, void*> avcodec_find_decoder;

    /// <summary><c>const AVCodec *avcodec_find_encoder_by_name(const char *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, void*> avcodec_find_encoder_by_name;

    /// <summary><c>const AVCodec *avcodec_find_decoder_by_name(const char *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, void*> avcodec_find_decoder_by_name;

    /// <summary><c>const char *avcodec_get_name(enum AVCodecID)</c></summary>
    public static delegate* unmanaged[Cdecl]<int, byte*> avcodec_get_name;

    /// <summary><c>AVCodecContext *avcodec_alloc_context3(const AVCodec *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> avcodec_alloc_context3;

    /// <summary><c>void avcodec_free_context(AVCodecContext **)</c></summary>
    public static delegate* unmanaged[Cdecl]<void**, void> avcodec_free_context;

    /// <summary><c>AVCodecParameters *avcodec_parameters_alloc(void)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*> avcodec_parameters_alloc;

    /// <summary><c>void avcodec_parameters_free(AVCodecParameters **)</c></summary>
    public static delegate* unmanaged[Cdecl]<void**, void> avcodec_parameters_free;

    /// <summary><c>int avcodec_parameters_to_context(AVCodecContext *, const AVCodecParameters *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, int> avcodec_parameters_to_context;

    /// <summary><c>int avcodec_parameters_from_context(AVCodecParameters *, const AVCodecContext *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, int> avcodec_parameters_from_context;

    /// <summary><c>int avcodec_parameters_copy(AVCodecParameters *dst, const AVCodecParameters *src)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, int> avcodec_parameters_copy;

    /// <summary><c>int avcodec_open2(AVCodecContext *, const AVCodec *, AVDictionary **)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, void**, int> avcodec_open2;

    /// <summary><c>int avcodec_send_packet(AVCodecContext *, const AVPacket *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, AVPacketNative*, int> avcodec_send_packet;

    /// <summary><c>int avcodec_receive_frame(AVCodecContext *, AVFrame *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, AVFrameHead*, int> avcodec_receive_frame;

    /// <summary><c>int avcodec_send_frame(AVCodecContext *, const AVFrame *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, AVFrameHead*, int> avcodec_send_frame;

    /// <summary><c>int avcodec_receive_packet(AVCodecContext *, AVPacket *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, AVPacketNative*, int> avcodec_receive_packet;

    /// <summary><c>void avcodec_flush_buffers(AVCodecContext *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void> avcodec_flush_buffers;

    /// <summary><c>AVPacket *av_packet_alloc(void)</c></summary>
    public static delegate* unmanaged[Cdecl]<AVPacketNative*> av_packet_alloc;

    /// <summary><c>void av_packet_free(AVPacket **)</c></summary>
    public static delegate* unmanaged[Cdecl]<AVPacketNative**, void> av_packet_free;

    /// <summary><c>void av_packet_unref(AVPacket *)</c></summary>
    public static delegate* unmanaged[Cdecl]<AVPacketNative*, void> av_packet_unref;

    /// <summary><c>void av_packet_rescale_ts(AVPacket *, AVRational src, AVRational dst)</c></summary>
    public static delegate* unmanaged[Cdecl]<AVPacketNative*, AVRationalNative, AVRationalNative, void> av_packet_rescale_ts;

    // -------------------------------------------------------------- avformat

    /// <summary><c>unsigned avformat_version(void)</c></summary>
    public static delegate* unmanaged[Cdecl]<uint> avformat_version;

    /// <summary><c>int avformat_open_input(AVFormatContext **, const char *url, const AVInputFormat *, AVDictionary **)</c></summary>
    public static delegate* unmanaged[Cdecl]<void**, byte*, void*, void**, int> avformat_open_input;

    /// <summary><c>void avformat_close_input(AVFormatContext **)</c></summary>
    public static delegate* unmanaged[Cdecl]<void**, void> avformat_close_input;

    /// <summary><c>int avformat_find_stream_info(AVFormatContext *, AVDictionary **)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void**, int> avformat_find_stream_info;

    /// <summary><c>int av_read_frame(AVFormatContext *, AVPacket *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, AVPacketNative*, int> av_read_frame;

    /// <summary><c>int av_seek_frame(AVFormatContext *, int stream_index, int64_t timestamp, int flags)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int, long, int, int> av_seek_frame;

    /// <summary><c>const AVInputFormat *av_find_input_format(const char *)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, void*> av_find_input_format;

    /// <summary><c>int avformat_alloc_output_context2(AVFormatContext **, const AVOutputFormat *, const char *format, const char *filename)</c></summary>
    public static delegate* unmanaged[Cdecl]<void**, void*, byte*, byte*, int> avformat_alloc_output_context2;

    /// <summary><c>AVStream *avformat_new_stream(AVFormatContext *, const AVCodec *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, void*> avformat_new_stream;

    /// <summary><c>int avformat_write_header(AVFormatContext *, AVDictionary **)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void**, int> avformat_write_header;

    /// <summary><c>int av_interleaved_write_frame(AVFormatContext *, AVPacket *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, AVPacketNative*, int> av_interleaved_write_frame;

    /// <summary><c>int av_write_trailer(AVFormatContext *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int> av_write_trailer;

    /// <summary><c>void avformat_free_context(AVFormatContext *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void> avformat_free_context;

    /// <summary><c>int avio_open(AVIOContext **, const char *url, int flags)</c></summary>
    public static delegate* unmanaged[Cdecl]<void**, byte*, int, int> avio_open;

    /// <summary><c>int avio_closep(AVIOContext **)</c></summary>
    public static delegate* unmanaged[Cdecl]<void**, int> avio_closep;

    // -------------------------------------------------------------- avdevice

    /// <summary><c>void avdevice_register_all(void)</c></summary>
    public static delegate* unmanaged[Cdecl]<void> avdevice_register_all;

    /// <summary><c>const AVInputFormat *av_input_video_device_next(const AVInputFormat *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> av_input_video_device_next;

    /// <summary><c>const AVInputFormat *av_input_audio_device_next(const AVInputFormat *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> av_input_audio_device_next;

    /// <summary><c>int avdevice_list_input_sources(const AVInputFormat *, const char *, AVDictionary *, AVDeviceInfoList **)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, void*, void**, int> avdevice_list_input_sources;

    /// <summary><c>void avdevice_free_list_devices(AVDeviceInfoList **)</c></summary>
    public static delegate* unmanaged[Cdecl]<void**, void> avdevice_free_list_devices;

    // --------------------------------------------------------------- swscale

    /// <summary><c>struct SwsContext *sws_getContext(int srcW, int srcH, enum AVPixelFormat srcFormat, int dstW, int dstH, enum AVPixelFormat dstFormat, int flags, SwsFilter *, SwsFilter *, const double *)</c></summary>
    public static delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int, void*, void*, double*, void*> sws_getContext;

    /// <summary><c>int sws_scale(struct SwsContext *, const uint8_t *const srcSlice[], const int srcStride[], int srcSliceY, int srcSliceH, uint8_t *const dst[], const int dstStride[])</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte**, int*, int, int, byte**, int*, int> sws_scale;

    /// <summary><c>void sws_freeContext(struct SwsContext *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void> sws_freeContext;

    // ------------------------------------------------------------ swresample

    /// <summary><c>int swr_alloc_set_opts2(SwrContext **, const AVChannelLayout *out_ch, enum AVSampleFormat out_fmt, int out_rate, const AVChannelLayout *in_ch, enum AVSampleFormat in_fmt, int in_rate, int log_offset, void *log_ctx)</c></summary>
    public static delegate* unmanaged[Cdecl]<void**, AVChannelLayoutNative*, int, int, AVChannelLayoutNative*, int, int, int, void*, int> swr_alloc_set_opts2;

    /// <summary><c>int swr_init(SwrContext *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int> swr_init;

    /// <summary><c>int swr_convert(SwrContext *, uint8_t **out, int out_count, const uint8_t **in, int in_count)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte**, int, byte**, int, int> swr_convert;

    /// <summary><c>int64_t swr_get_delay(SwrContext *, int64_t base)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, long, long> swr_get_delay;

    /// <summary><c>int swr_get_out_samples(SwrContext *, int in_samples)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int, int> swr_get_out_samples;

    /// <summary><c>void swr_free(SwrContext **)</c></summary>
    public static delegate* unmanaged[Cdecl]<void**, void> swr_free;

    /// <summary>Resolves every function pointer. Called once by <see cref="FFmpegLibraries"/>.</summary>
    internal static void Bind()
    {
        if (_bound)
        {
            return;
        }

        var util = FFmpegLibraries.AvUtil;
        var codec = FFmpegLibraries.AvCodec;
        var format = FFmpegLibraries.AvFormat;
        var scale = FFmpegLibraries.SwScale;
        var resample = FFmpegLibraries.SwResample;

        avutil_version = (delegate* unmanaged[Cdecl]<uint>)util.GetExport(nameof(avutil_version));
        av_frame_alloc = (delegate* unmanaged[Cdecl]<AVFrameHead*>)util.GetExport(nameof(av_frame_alloc));
        av_frame_free = (delegate* unmanaged[Cdecl]<AVFrameHead**, void>)util.GetExport(nameof(av_frame_free));
        av_frame_unref = (delegate* unmanaged[Cdecl]<AVFrameHead*, void>)util.GetExport(nameof(av_frame_unref));
        av_frame_get_buffer = (delegate* unmanaged[Cdecl]<AVFrameHead*, int, int>)util.GetExport(nameof(av_frame_get_buffer));
        av_frame_make_writable = (delegate* unmanaged[Cdecl]<AVFrameHead*, int>)util.GetExport(nameof(av_frame_make_writable));
        av_strerror = (delegate* unmanaged[Cdecl]<int, byte*, nuint, int>)util.GetExport(nameof(av_strerror));
        av_dict_set = (delegate* unmanaged[Cdecl]<void**, byte*, byte*, int, int>)util.GetExport(nameof(av_dict_set));
        av_dict_free = (delegate* unmanaged[Cdecl]<void**, void>)util.GetExport(nameof(av_dict_free));
        av_opt_set = (delegate* unmanaged[Cdecl]<void*, byte*, byte*, int, int>)util.GetExport(nameof(av_opt_set));
        av_opt_set_int = (delegate* unmanaged[Cdecl]<void*, byte*, long, int, int>)util.GetExport(nameof(av_opt_set_int));
        av_opt_get_int = (delegate* unmanaged[Cdecl]<void*, byte*, int, long*, int>)util.GetExport(nameof(av_opt_get_int));
        av_rescale_q = (delegate* unmanaged[Cdecl]<long, AVRationalNative, AVRationalNative, long>)util.GetExport(nameof(av_rescale_q));
        av_log_set_level = (delegate* unmanaged[Cdecl]<int, void>)util.GetExport(nameof(av_log_set_level));
        av_get_bytes_per_sample = (delegate* unmanaged[Cdecl]<int, int>)util.GetExport(nameof(av_get_bytes_per_sample));
        av_samples_get_buffer_size = (delegate* unmanaged[Cdecl]<int*, int, int, int, int, int>)util.GetExport(nameof(av_samples_get_buffer_size));
        av_image_get_buffer_size = (delegate* unmanaged[Cdecl]<int, int, int, int, int>)util.GetExport(nameof(av_image_get_buffer_size));
        av_freep = (delegate* unmanaged[Cdecl]<void*, void>)util.GetExport(nameof(av_freep));
        av_samples_alloc = (delegate* unmanaged[Cdecl]<byte**, int*, int, int, int, int, int>)util.GetExport(nameof(av_samples_alloc));
        av_audio_fifo_alloc = (delegate* unmanaged[Cdecl]<int, int, int, void*>)util.GetExport(nameof(av_audio_fifo_alloc));
        av_audio_fifo_write = (delegate* unmanaged[Cdecl]<void*, void**, int, int>)util.GetExport(nameof(av_audio_fifo_write));
        av_audio_fifo_read = (delegate* unmanaged[Cdecl]<void*, void**, int, int>)util.GetExport(nameof(av_audio_fifo_read));
        av_audio_fifo_size = (delegate* unmanaged[Cdecl]<void*, int>)util.GetExport(nameof(av_audio_fifo_size));
        av_audio_fifo_free = (delegate* unmanaged[Cdecl]<void*, void>)util.GetExport(nameof(av_audio_fifo_free));

        avcodec_version = (delegate* unmanaged[Cdecl]<uint>)codec.GetExport(nameof(avcodec_version));
        avcodec_find_decoder = (delegate* unmanaged[Cdecl]<int, void*>)codec.GetExport(nameof(avcodec_find_decoder));
        avcodec_find_encoder_by_name = (delegate* unmanaged[Cdecl]<byte*, void*>)codec.GetExport(nameof(avcodec_find_encoder_by_name));
        avcodec_find_decoder_by_name = (delegate* unmanaged[Cdecl]<byte*, void*>)codec.GetExport(nameof(avcodec_find_decoder_by_name));
        avcodec_get_name = (delegate* unmanaged[Cdecl]<int, byte*>)codec.GetExport(nameof(avcodec_get_name));
        avcodec_alloc_context3 = (delegate* unmanaged[Cdecl]<void*, void*>)codec.GetExport(nameof(avcodec_alloc_context3));
        avcodec_free_context = (delegate* unmanaged[Cdecl]<void**, void>)codec.GetExport(nameof(avcodec_free_context));
        avcodec_parameters_alloc = (delegate* unmanaged[Cdecl]<void*>)codec.GetExport(nameof(avcodec_parameters_alloc));
        avcodec_parameters_free = (delegate* unmanaged[Cdecl]<void**, void>)codec.GetExport(nameof(avcodec_parameters_free));
        avcodec_parameters_to_context = (delegate* unmanaged[Cdecl]<void*, void*, int>)codec.GetExport(nameof(avcodec_parameters_to_context));
        avcodec_parameters_from_context = (delegate* unmanaged[Cdecl]<void*, void*, int>)codec.GetExport(nameof(avcodec_parameters_from_context));
        avcodec_parameters_copy = (delegate* unmanaged[Cdecl]<void*, void*, int>)codec.GetExport(nameof(avcodec_parameters_copy));
        avcodec_open2 = (delegate* unmanaged[Cdecl]<void*, void*, void**, int>)codec.GetExport(nameof(avcodec_open2));
        avcodec_send_packet = (delegate* unmanaged[Cdecl]<void*, AVPacketNative*, int>)codec.GetExport(nameof(avcodec_send_packet));
        avcodec_receive_frame = (delegate* unmanaged[Cdecl]<void*, AVFrameHead*, int>)codec.GetExport(nameof(avcodec_receive_frame));
        avcodec_send_frame = (delegate* unmanaged[Cdecl]<void*, AVFrameHead*, int>)codec.GetExport(nameof(avcodec_send_frame));
        avcodec_receive_packet = (delegate* unmanaged[Cdecl]<void*, AVPacketNative*, int>)codec.GetExport(nameof(avcodec_receive_packet));
        avcodec_flush_buffers = (delegate* unmanaged[Cdecl]<void*, void>)codec.GetExport(nameof(avcodec_flush_buffers));
        av_packet_alloc = (delegate* unmanaged[Cdecl]<AVPacketNative*>)codec.GetExport(nameof(av_packet_alloc));
        av_packet_free = (delegate* unmanaged[Cdecl]<AVPacketNative**, void>)codec.GetExport(nameof(av_packet_free));
        av_packet_unref = (delegate* unmanaged[Cdecl]<AVPacketNative*, void>)codec.GetExport(nameof(av_packet_unref));
        av_packet_rescale_ts = (delegate* unmanaged[Cdecl]<AVPacketNative*, AVRationalNative, AVRationalNative, void>)codec.GetExport(nameof(av_packet_rescale_ts));

        avformat_version = (delegate* unmanaged[Cdecl]<uint>)format.GetExport(nameof(avformat_version));
        avformat_open_input = (delegate* unmanaged[Cdecl]<void**, byte*, void*, void**, int>)format.GetExport(nameof(avformat_open_input));
        avformat_close_input = (delegate* unmanaged[Cdecl]<void**, void>)format.GetExport(nameof(avformat_close_input));
        avformat_find_stream_info = (delegate* unmanaged[Cdecl]<void*, void**, int>)format.GetExport(nameof(avformat_find_stream_info));
        av_read_frame = (delegate* unmanaged[Cdecl]<void*, AVPacketNative*, int>)format.GetExport(nameof(av_read_frame));
        av_seek_frame = (delegate* unmanaged[Cdecl]<void*, int, long, int, int>)format.GetExport(nameof(av_seek_frame));
        av_find_input_format = (delegate* unmanaged[Cdecl]<byte*, void*>)format.GetExport(nameof(av_find_input_format));
        avformat_alloc_output_context2 = (delegate* unmanaged[Cdecl]<void**, void*, byte*, byte*, int>)format.GetExport(nameof(avformat_alloc_output_context2));
        avformat_new_stream = (delegate* unmanaged[Cdecl]<void*, void*, void*>)format.GetExport(nameof(avformat_new_stream));
        avformat_write_header = (delegate* unmanaged[Cdecl]<void*, void**, int>)format.GetExport(nameof(avformat_write_header));
        av_interleaved_write_frame = (delegate* unmanaged[Cdecl]<void*, AVPacketNative*, int>)format.GetExport(nameof(av_interleaved_write_frame));
        av_write_trailer = (delegate* unmanaged[Cdecl]<void*, int>)format.GetExport(nameof(av_write_trailer));
        avformat_free_context = (delegate* unmanaged[Cdecl]<void*, void>)format.GetExport(nameof(avformat_free_context));
        avio_open = (delegate* unmanaged[Cdecl]<void**, byte*, int, int>)format.GetExport(nameof(avio_open));
        avio_closep = (delegate* unmanaged[Cdecl]<void**, int>)format.GetExport(nameof(avio_closep));

        sws_getContext = (delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int, void*, void*, double*, void*>)scale.GetExport(nameof(sws_getContext));
        sws_scale = (delegate* unmanaged[Cdecl]<void*, byte**, int*, int, int, byte**, int*, int>)scale.GetExport(nameof(sws_scale));
        sws_freeContext = (delegate* unmanaged[Cdecl]<void*, void>)scale.GetExport(nameof(sws_freeContext));

        swr_alloc_set_opts2 = (delegate* unmanaged[Cdecl]<void**, AVChannelLayoutNative*, int, int, AVChannelLayoutNative*, int, int, int, void*, int>)resample.GetExport(nameof(swr_alloc_set_opts2));
        swr_init = (delegate* unmanaged[Cdecl]<void*, int>)resample.GetExport(nameof(swr_init));
        swr_convert = (delegate* unmanaged[Cdecl]<void*, byte**, int, byte**, int, int>)resample.GetExport(nameof(swr_convert));
        swr_get_delay = (delegate* unmanaged[Cdecl]<void*, long, long>)resample.GetExport(nameof(swr_get_delay));
        swr_get_out_samples = (delegate* unmanaged[Cdecl]<void*, int, int>)resample.GetExport(nameof(swr_get_out_samples));
        swr_free = (delegate* unmanaged[Cdecl]<void**, void>)resample.GetExport(nameof(swr_free));

        var device = FFmpegLibraries.AvDevice;
        if (device is not null)
        {
            avdevice_register_all = (delegate* unmanaged[Cdecl]<void>)device.GetExport(nameof(avdevice_register_all));
            av_input_video_device_next = (delegate* unmanaged[Cdecl]<void*, void*>)device.GetExport(nameof(av_input_video_device_next));
            av_input_audio_device_next = (delegate* unmanaged[Cdecl]<void*, void*>)device.GetExport(nameof(av_input_audio_device_next));
            avdevice_list_input_sources = (delegate* unmanaged[Cdecl]<void*, byte*, void*, void**, int>)device.GetExport(nameof(avdevice_list_input_sources));
            avdevice_free_list_devices = (delegate* unmanaged[Cdecl]<void**, void>)device.GetExport(nameof(avdevice_free_list_devices));
        }

        _bound = true;
    }

    /// <summary>Sets an AVOption by name on any object that starts with an AVClass pointer.</summary>
    public static int SetOption(void* obj, string name, string value)
    {
        Span<byte> nameScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> valueScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, nameScratch);
        using var valueUtf8 = new Utf8Scoped(value, valueScratch);
        return av_opt_set(obj, nameUtf8.Pointer, valueUtf8.Pointer, 0);
    }

    /// <summary>Sets an integer AVOption by name.</summary>
    public static int SetOption(void* obj, string name, long value)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, scratch);
        return av_opt_set_int(obj, nameUtf8.Pointer, value, 0);
    }

    /// <summary>Reads an integer AVOption by name, returning <paramref name="fallback"/> on failure.</summary>
    public static long GetOption(void* obj, string name, long fallback = 0)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, scratch);
        long value;
        return av_opt_get_int(obj, nameUtf8.Pointer, 0, &value) < 0 ? fallback : value;
    }

    /// <summary>Adds an entry to an AVDictionary, allocating it when needed.</summary>
    public static int DictionarySet(void** dictionary, string key, string value)
    {
        Span<byte> keyScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> valueScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var keyUtf8 = new Utf8Scoped(key, keyScratch);
        using var valueUtf8 = new Utf8Scoped(value, valueScratch);
        return av_dict_set(dictionary, keyUtf8.Pointer, valueUtf8.Pointer, 0);
    }
}
