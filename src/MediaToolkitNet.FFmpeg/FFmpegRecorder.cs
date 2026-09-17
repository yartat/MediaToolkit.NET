using System.Diagnostics;
using MediaToolkitNet.Core;
using MediaToolkitNet.Core.Formats;
using MediaToolkitNet.Core.Frames;
using MediaToolkitNet.Core.Recording;
using MediaToolkitNet.FFmpeg.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg;

/// <summary>
/// Encodes pushed frames and muxes them into a container using libavformat and
/// libavcodec.
/// </summary>
/// <remarks>
/// Incoming frames are converted to whatever the chosen encoder accepts:
/// swscale handles pixel format and scaling, swresample plus an
/// <c>AVAudioFifo</c> handle sample format, rate and the fixed frame size most
/// audio encoders require.
/// </remarks>
public sealed unsafe class FFmpegRecorder : IMediaRecorder
{
    private readonly string _outputPath;
    private readonly List<EncoderStream> _streams = [];
    private readonly Stopwatch _elapsed = new();

    private void* _output;
    private AVPacketNative* _packet;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates a recorder writing to <paramref name="outputPath"/>. The container is inferred from the extension.</summary>
    public FFmpegRecorder(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        FFmpegLibraries.EnsureLoaded();

        if (!AbiLayout.EncoderLayoutVerified)
        {
            throw new MediaBackendUnavailableException(
                FFmpegLibraries.BackendName,
                "encoding is unavailable: the struct layout of this FFmpeg build was not confirmed.");
        }

        _outputPath = outputPath;

        void* output = null;
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var path = new Utf8Scoped(outputPath, scratch);
        FFmpegError.Check(
            AV.avformat_alloc_output_context2(&output, null, null, path.Pointer),
            $"avformat_alloc_output_context2({outputPath})");
        _output = output;

        _packet = AV.av_packet_alloc();
        FFmpegError.CheckAlloc(_packet, "av_packet_alloc");
    }

    /// <inheritdoc />
    public string Backend => FFmpegLibraries.BackendName;

    /// <inheritdoc />
    public bool IsRecording => _started;

    /// <inheritdoc />
    public TimeSpan Elapsed => _elapsed.Elapsed;

    /// <summary>
    /// Private encoder options passed straight to <c>avcodec_open2</c>, e.g.
    /// <c>preset=veryfast</c> or <c>crf=23</c> for libx264.
    /// </summary>
    public Dictionary<string, string> EncoderOptions { get; } = [];

    /// <inheritdoc />
    public int AddVideoStream(VideoEncodingSettings settings)
    {
        EnsureNotStarted();

        if (!settings.Format.IsValid)
        {
            throw new ArgumentException("No valid video format was supplied.", nameof(settings));
        }

        var frameRate = settings.Format.FrameRate.Denominator == 0
            ? new Rational(30, 1)
            : settings.Format.FrameRate;

        var codec = FindEncoder(settings.Codec, MediaCodec.H264);
        var candidates = settings.Codec == MediaCodec.Mjpeg
            ? (AVPixelFormat[])[AVPixelFormat.Yuvj420P, AVPixelFormat.Yuv420P, AVPixelFormat.Yuv422P]
            : [AVPixelFormat.Yuv420P, AVPixelFormat.Nv12, AVPixelFormat.Yuv422P, AVPixelFormat.Yuv444P];

        void* context = null;
        var chosen = AVPixelFormat.None;
        foreach (var pixelFormat in candidates)
        {
            context = TryOpenEncoder(
                codec,
                AVMediaType.Video,
                (int)pixelFormat,
                settings.BitrateBitsPerSecond,
                settings.Format.Width,
                settings.Format.Height,
                new AVRationalNative(frameRate.Denominator, frameRate.Numerator),
                configure: ctx =>
                {
                    if (settings.KeyFrameInterval > 0)
                    {
                        AV.SetOption((void*)ctx, "g", settings.KeyFrameInterval);
                    }
                });

            if (context is not null)
            {
                chosen = pixelFormat;
                break;
            }
        }

        if (context is null)
        {
            throw new MediaToolkitNetException(
                Backend, $"could not open a video encoder for {settings.Codec}", AVConstants.ErrorInvalid);
        }

        var stream = CreateStream(context, new AVRationalNative(frameRate.Denominator, frameRate.Numerator));
        var entry = new EncoderStream(context, stream, AVMediaType.Video)
        {
            EncoderTimeBase = new AVRationalNative(frameRate.Denominator, frameRate.Numerator),
            VideoFormat = new VideoFormat(settings.Format.Width, settings.Format.Height, FFmpegFormatMap.FromAV(chosen), frameRate),
            EncoderPixelFormat = chosen,
        };

        entry.AllocateVideoFrame();
        _streams.Add(entry);
        return _streams.Count - 1;
    }

    /// <inheritdoc />
    public int AddAudioStream(AudioEncodingSettings settings)
    {
        EnsureNotStarted();

        if (!settings.Format.IsValid)
        {
            throw new ArgumentException("No valid audio format was supplied.", nameof(settings));
        }

        if (!AbiLayout.AudioFrameLayoutVerified)
        {
            throw new MediaBackendUnavailableException(
                Backend, "audio encoding is unavailable: the AVFrame audio fields were not recognised in this FFmpeg build.");
        }

        var codec = FindEncoder(settings.Codec, MediaCodec.Aac);
        var timeBase = new AVRationalNative(1, settings.Format.SampleRate);

        void* context = null;
        var chosen = AVSampleFormat.None;
        foreach (var sampleFormat in (AVSampleFormat[])
                 [AVSampleFormat.FltP, AVSampleFormat.S16, AVSampleFormat.Flt, AVSampleFormat.S16P, AVSampleFormat.S32])
        {
            context = TryOpenEncoder(
                codec,
                AVMediaType.Audio,
                (int)sampleFormat,
                settings.BitrateBitsPerSecond,
                width: 0,
                height: 0,
                timeBase,
                configure: ctx =>
                {
                    AV.SetOption((void*)ctx, "ar", settings.Format.SampleRate);
                    AV.SetOption((void*)ctx, "ac", settings.Format.Channels);
                });

            if (context is not null)
            {
                chosen = sampleFormat;
                break;
            }
        }

        if (context is null)
        {
            throw new MediaToolkitNetException(
                Backend, $"could not open an audio encoder for {settings.Codec}", AVConstants.ErrorInvalid);
        }

        var stream = CreateStream(context, timeBase);

        // Most encoders demand a fixed number of samples per call; those that do
        // not report 0, in which case a round buffer size keeps the FIFO simple.
        var frameSize = (int)AV.GetOption(context, "frame_size");
        if (frameSize <= 0)
        {
            frameSize = 1024;
        }

        var entry = new EncoderStream(context, stream, AVMediaType.Audio)
        {
            EncoderTimeBase = timeBase,
            AudioFormat = settings.Format with { SampleFormat = FFmpegFormatMap.FromAV(chosen) },
            EncoderSampleFormat = chosen,
            FrameSize = frameSize,
        };

        entry.AllocateAudioFrame();
        _streams.Add(entry);
        return _streams.Count - 1;
    }

    /// <inheritdoc />
    public void Start()
    {
        EnsureNotStarted();
        if (_streams.Count == 0)
        {
            throw new InvalidOperationException("Add at least one stream before starting.");
        }

        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var path = new Utf8Scoped(_outputPath, scratch);

        void* pb = null;
        FFmpegError.Check(AV.avio_open(&pb, path.Pointer, AVConstants.AvioFlagWrite), $"avio_open({_outputPath})");
        AbiLayout.SetPb(_output, pb);

        FFmpegError.Check(AV.avformat_write_header(_output, null), "avformat_write_header");

        _started = true;
        _elapsed.Restart();
    }

    /// <inheritdoc />
    public void WriteVideo(int streamIndex, in VideoFrame frame)
    {
        var entry = Require(streamIndex, AVMediaType.Video);
        var target = entry.Frame;

        FFmpegError.Check(AV.av_frame_make_writable(target), "av_frame_make_writable");

        var source = FFmpegFormatMap.ToAV(frame.Format.PixelFormat);
        if (source == AVPixelFormat.None)
        {
            throw new NotSupportedException($"Pixel format {frame.Format.PixelFormat} is not supported by the encoder.");
        }

        entry.EnsureScaler(frame.Format.Width, frame.Format.Height, source);

        var sourcePlanes = stackalloc byte*[MediaPlanes.MaxPlanes];
        var sourceStrides = stackalloc int[MediaPlanes.MaxPlanes];
        for (var i = 0; i < MediaPlanes.MaxPlanes; i++)
        {
            sourcePlanes[i] = i < frame.PlaneCount ? (byte*)frame.PlanePointer(i) : null;
            sourceStrides[i] = i < frame.PlaneCount ? frame.Stride(i) : 0;
        }

        var destinationPlanes = stackalloc byte*[MediaPlanes.MaxPlanes];
        var destinationStrides = stackalloc int[MediaPlanes.MaxPlanes];
        for (var i = 0; i < MediaPlanes.MaxPlanes; i++)
        {
            destinationPlanes[i] = (byte*)target->Data[i];
            destinationStrides[i] = target->Linesize[i];
        }

        AV.sws_scale(
            entry.Scaler, sourcePlanes, sourceStrides, 0, frame.Format.Height, destinationPlanes, destinationStrides);

        target->Pts = entry.NextPts++;
        Encode(entry, target);
    }

    /// <inheritdoc />
    public void WriteAudio(int streamIndex, in AudioFrame frame)
    {
        var entry = Require(streamIndex, AVMediaType.Audio);

        var source = FFmpegFormatMap.ToAV(frame.Format.SampleFormat);
        if (source == AVSampleFormat.None)
        {
            throw new NotSupportedException($"Sample format {frame.Format.SampleFormat} is not supported by the encoder.");
        }

        entry.EnsureResampler(frame.Format, source);

        var inputPlanes = stackalloc byte*[MediaPlanes.MaxPlanes];
        for (var i = 0; i < MediaPlanes.MaxPlanes; i++)
        {
            inputPlanes[i] = i < frame.PlaneCount ? (byte*)frame.PlanePointer(i) : null;
        }

        entry.ResampleIntoFifo(inputPlanes, frame.SampleCount);
        DrainFifo(entry, flush: false);
    }

    private void DrainFifo(EncoderStream entry, bool flush)
    {
        var target = entry.Frame;
        var planes = stackalloc void*[MediaPlanes.MaxPlanes];
        while (true)
        {
            var available = AV.av_audio_fifo_size(entry.Fifo);
            if (available < entry.FrameSize && !(flush && available > 0))
            {
                return;
            }

            var take = Math.Min(entry.FrameSize, available);
            FFmpegError.Check(AV.av_frame_make_writable(target), "av_frame_make_writable");

            for (var i = 0; i < MediaPlanes.MaxPlanes; i++)
            {
                planes[i] = (void*)target->Data[i];
            }

            FFmpegError.Check(AV.av_audio_fifo_read(entry.Fifo, planes, take), "av_audio_fifo_read");

            target->NbSamples = take;
            target->Pts = entry.NextPts;
            entry.NextPts += take;
            Encode(entry, target);

            // Restore the configured frame size for the next round.
            target->NbSamples = entry.FrameSize;
        }
    }

    private void Encode(EncoderStream entry, AVFrameHead* frame)
    {
        if (!_started)
        {
            throw new InvalidOperationException("Recording has not started: call Start.");
        }

        FFmpegError.Check(AV.avcodec_send_frame(entry.Context, frame), "avcodec_send_frame");
        MuxPending(entry);
    }

    private void MuxPending(EncoderStream entry)
    {
        while (true)
        {
            var result = AV.avcodec_receive_packet(entry.Context, _packet);
            if (result is AVConstants.ErrorAgain or AVConstants.ErrorEof)
            {
                return;
            }

            FFmpegError.Check(result, "avcodec_receive_packet");

            try
            {
                AV.av_packet_rescale_ts(_packet, entry.EncoderTimeBase, AbiLayout.TimeBaseOf(entry.Stream));
                _packet->StreamIndex = AbiLayout.StreamIndexOf(entry.Stream);
                FFmpegError.Check(
                    AV.av_interleaved_write_frame(_output, _packet), "av_interleaved_write_frame");
            }
            finally
            {
                AV.av_packet_unref(_packet);
            }
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _elapsed.Stop();

        foreach (var entry in _streams)
        {
            if (entry.MediaType == AVMediaType.Audio)
            {
                // Feed whatever is still buffered before draining the encoder.
                DrainFifo(entry, flush: true);
            }

            AV.avcodec_send_frame(entry.Context, null);
            MuxPending(entry);
        }

        _started = false;

        FFmpegError.Check(AV.av_write_trailer(_output), "av_write_trailer");

        var pb = AbiLayout.GetPb(_output);
        if (pb is not null)
        {
            AV.avio_closep((void**)((byte*)_output + AbiLayout.FormatContextPb));
        }
    }

    private EncoderStream Require(int streamIndex, AVMediaType expected)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(streamIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(streamIndex, _streams.Count);

        var entry = _streams[streamIndex];
        if (entry.MediaType != expected)
        {
            throw new ArgumentException($"Stream {streamIndex} is {entry.MediaType}, expected {expected}.", nameof(streamIndex));
        }

        return entry;
    }

    private void EnsureNotStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("Streams cannot be added after Start.");
        }
    }

    private static void* FindEncoder(MediaCodec requested, MediaCodec fallback)
    {
        var names = FFmpegFormatMap.EncoderNames(requested == MediaCodec.Default ? fallback : requested);
        Span<byte> scratch = stackalloc byte[64];
        foreach (var name in names)
        {
            using var utf8 = new Utf8Scoped(name, scratch);
            var codec = AV.avcodec_find_encoder_by_name(utf8.Pointer);
            if (codec is not null)
            {
                return codec;
            }
        }

        throw new MediaToolkitNetException(
            FFmpegLibraries.BackendName,
            $"none of the encoders [{string.Join(", ", names)}] is available in this FFmpeg build",
            AVConstants.ErrorInvalid);
    }

    private void* TryOpenEncoder(
        void* codec,
        AVMediaType mediaType,
        int format,
        int bitrate,
        int width,
        int height,
        AVRationalNative timeBase,
        Action<nint> configure)
    {
        var context = AV.avcodec_alloc_context3(codec);
        if (context is null)
        {
            return null;
        }

        var parameters = AV.avcodec_parameters_alloc();
        if (parameters is null)
        {
            AV.avcodec_free_context(&context);
            return null;
        }

        void* options = null;
        try
        {
            AbiLayout.FillCodecParameters(
                parameters, mediaType, AbiLayout.IdOfCodec(codec), format, bitrate, width, height);

            if (AV.avcodec_parameters_to_context(context, parameters) < 0)
            {
                return Fail(context);
            }

            AbiLayout.SetCodecTimeBase(context, timeBase);
            if (bitrate > 0)
            {
                AV.SetOption(context, "b", bitrate);
            }

            if (FFmpegFormatMap.NeedsGlobalHeader(_outputPath))
            {
                AV.SetOption(context, "flags", AVConstants.CodecFlagGlobalHeader);
            }

            configure((nint)context);

            foreach (var (key, value) in EncoderOptions)
            {
                AV.DictionarySet(&options, key, value);
            }

            return AV.avcodec_open2(context, codec, &options) < 0 ? Fail(context) : context;
        }
        finally
        {
            var localParameters = parameters;
            AV.avcodec_parameters_free(&localParameters);
            if (options is not null)
            {
                AV.av_dict_free(&options);
            }
        }

        static void* Fail(void* context)
        {
            var local = context;
            AV.avcodec_free_context(&local);
            return null;
        }
    }

    private void* CreateStream(void* encoderContext, AVRationalNative timeBase)
    {
        var stream = AV.avformat_new_stream(_output, null);
        FFmpegError.CheckAlloc(stream, "avformat_new_stream");

        FFmpegError.Check(
            AV.avcodec_parameters_from_context(AbiLayout.CodecParametersOf(stream), encoderContext),
            "avcodec_parameters_from_context");

        AbiLayout.SetTimeBase(stream, timeBase);
        return stream;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            Stop();
        }
        catch (MediaToolkitNetException)
        {
            // Disposal must not throw over a broken output file.
        }

        foreach (var entry in _streams)
        {
            entry.Dispose();
        }

        _streams.Clear();

        if (_packet is not null)
        {
            fixed (AVPacketNative** packet = &_packet)
            {
                AV.av_packet_free(packet);
            }
        }

        if (_output is not null)
        {
            var pb = AbiLayout.GetPb(_output);
            if (pb is not null)
            {
                AV.avio_closep((void**)((byte*)_output + AbiLayout.FormatContextPb));
            }

            AV.avformat_free_context(_output);
            _output = null;
        }
    }

    /// <summary>One encoder plus the muxer stream it feeds.</summary>
    private sealed class EncoderStream(void* context, void* stream, AVMediaType mediaType) : IDisposable
    {
        private void* _context = context;
        private void* _scaler;
        private void* _resampler;
        private void* _fifo;
        private AVFrameHead* _frame;
        private byte** _resampleBuffer;
        private int _resampleCapacity;
        private int _scalerWidth;
        private int _scalerHeight;
        private AVPixelFormat _scalerFormat = AVPixelFormat.None;
        private AudioFormat _resamplerInput;

        public void* Context => _context;

        public void* Stream { get; } = stream;

        public AVMediaType MediaType { get; } = mediaType;

        public AVRationalNative EncoderTimeBase { get; init; }

        public VideoFormat VideoFormat { get; init; }

        public AudioFormat AudioFormat { get; init; }

        public AVPixelFormat EncoderPixelFormat { get; init; }

        public AVSampleFormat EncoderSampleFormat { get; init; }

        public int FrameSize { get; init; }

        public long NextPts { get; set; }

        public void* Scaler => _scaler;

        public void* Fifo => _fifo;

        public AVFrameHead* Frame => _frame;

        public void AllocateVideoFrame()
        {
            _frame = AV.av_frame_alloc();
            FFmpegError.CheckAlloc(_frame, "av_frame_alloc");
            _frame->Format = (int)EncoderPixelFormat;
            _frame->Width = VideoFormat.Width;
            _frame->Height = VideoFormat.Height;
            FFmpegError.Check(AV.av_frame_get_buffer(_frame, 0), "av_frame_get_buffer");
        }

        public void AllocateAudioFrame()
        {
            _frame = AV.av_frame_alloc();
            FFmpegError.CheckAlloc(_frame, "av_frame_alloc");
            AbiLayout.PrepareAudioFrame(
                _frame, AudioFormat.SampleRate, AudioFormat.Channels, (int)EncoderSampleFormat, FrameSize);
            FFmpegError.Check(AV.av_frame_get_buffer(_frame, 0), "av_frame_get_buffer");

            _fifo = AV.av_audio_fifo_alloc((int)EncoderSampleFormat, AudioFormat.Channels, FrameSize * 4);
            FFmpegError.CheckAlloc(_fifo, "av_audio_fifo_alloc");
        }

        public void EnsureScaler(int width, int height, AVPixelFormat format)
        {
            if (_scaler is not null && _scalerWidth == width && _scalerHeight == height && _scalerFormat == format)
            {
                return;
            }

            if (_scaler is not null)
            {
                AV.sws_freeContext(_scaler);
            }

            // SWS_BILINEAR is 2; good enough as a default and cheap.
            _scaler = AV.sws_getContext(
                width, height, (int)format,
                VideoFormat.Width, VideoFormat.Height, (int)EncoderPixelFormat,
                2, null, null, null);

            FFmpegError.CheckAlloc(_scaler, "sws_getContext");
            _scalerWidth = width;
            _scalerHeight = height;
            _scalerFormat = format;
        }

        public void EnsureResampler(AudioFormat input, AVSampleFormat inputFormat)
        {
            if (_resampler is not null && _resamplerInput == input)
            {
                return;
            }

            if (_resampler is not null)
            {
                fixed (void** existing = &_resampler)
                {
                    AV.swr_free(existing);
                }
            }

            var inputLayout = AVChannelLayoutNative.Default(input.Channels);
            var outputLayout = AVChannelLayoutNative.Default(AudioFormat.Channels);

            void* resampler = null;
            FFmpegError.Check(
                AV.swr_alloc_set_opts2(
                    &resampler,
                    &outputLayout, (int)EncoderSampleFormat, AudioFormat.SampleRate,
                    &inputLayout, (int)inputFormat, input.SampleRate,
                    0, null),
                "swr_alloc_set_opts2");

            _resampler = resampler;
            FFmpegError.Check(AV.swr_init(_resampler), "swr_init");
            _resamplerInput = input;
        }

        public void ResampleIntoFifo(byte** input, int sampleCount)
        {
            var capacity = AV.swr_get_out_samples(_resampler, sampleCount);
            if (capacity <= 0)
            {
                return;
            }

            EnsureResampleBuffer(capacity);

            var converted = FFmpegError.Check(
                AV.swr_convert(_resampler, _resampleBuffer, capacity, input, sampleCount), "swr_convert");

            if (converted <= 0)
            {
                return;
            }

            FFmpegError.Check(
                AV.av_audio_fifo_write(_fifo, (void**)_resampleBuffer, converted), "av_audio_fifo_write");
        }

        private void EnsureResampleBuffer(int samples)
        {
            if (_resampleBuffer is not null && _resampleCapacity >= samples)
            {
                return;
            }

            FreeResampleBuffer();

            _resampleBuffer = (byte**)System.Runtime.InteropServices.NativeMemory.AllocZeroed(
                (nuint)(MediaPlanes.MaxPlanes * sizeof(byte*)));

            int linesize;
            FFmpegError.Check(
                AV.av_samples_alloc(
                    _resampleBuffer, &linesize, AudioFormat.Channels, samples, (int)EncoderSampleFormat, 0),
                "av_samples_alloc");

            _resampleCapacity = samples;
        }

        private void FreeResampleBuffer()
        {
            if (_resampleBuffer is null)
            {
                return;
            }

            // av_samples_alloc allocates one block referenced by plane 0.
            AV.av_freep(_resampleBuffer);
            System.Runtime.InteropServices.NativeMemory.Free(_resampleBuffer);
            _resampleBuffer = null;
            _resampleCapacity = 0;
        }

        public void Dispose()
        {
            if (_frame is not null)
            {
                fixed (AVFrameHead** frame = &_frame)
                {
                    AV.av_frame_free(frame);
                }
            }

            if (_scaler is not null)
            {
                AV.sws_freeContext(_scaler);
                _scaler = null;
            }

            if (_resampler is not null)
            {
                fixed (void** resampler = &_resampler)
                {
                    AV.swr_free(resampler);
                }
            }

            if (_fifo is not null)
            {
                AV.av_audio_fifo_free(_fifo);
                _fifo = null;
            }

            FreeResampleBuffer();

            if (_context is not null)
            {
                fixed (void** context = &_context)
                {
                    AV.avcodec_free_context(context);
                }
            }
        }
    }
}
