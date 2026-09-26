#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions;
using MediaToolkitNet.FFmpeg.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg;

/// <summary>
/// Thin wrapper over <c>AVFormatContext</c> in muxing mode: the write side of
/// <see cref="FFmpegDemuxer"/>. Streams are added, tagged, then packets go in.
/// </summary>
/// <remarks>
/// <para>
/// This is the packet level: nothing is decoded or encoded here, which is what
/// makes a container change lossless. Copying a stream means adding it with the
/// input's codec parameters and writing the input's packets, rescaled to the
/// output stream's time base.
/// </para>
/// <para>
/// Read each stream's time base with <see cref="TimeBaseOf"/> only after
/// <see cref="WriteHeader"/>: muxers are free to replace the one they were given,
/// Matroska with 1/1000 and MP4 with one of its own.
/// </para>
/// </remarks>
public sealed unsafe class FFmpegMuxer : IDisposable
{
    private readonly string _path;
    private void* _context;
    private bool _headerWritten;
    private bool _finished;
    private bool _disposed;

    private FFmpegMuxer(void* context, string path)
    {
        _context = context;
        _path = path;
    }

    /// <summary>Raw <c>AVFormatContext</c> pointer, for callers that need to go lower.</summary>
    public void* Handle => _context;

    /// <summary>The muxer libavformat chose, for example <c>matroska</c> or <c>mp4</c>.</summary>
    public string FormatName => AbiLayout.NameOfFormat(AbiLayout.OutputFormatOf(_context));

    /// <summary>
    /// True when this container wants codec headers out of band, so encoders
    /// feeding it must be opened with <c>AV_CODEC_FLAG_GLOBAL_HEADER</c>.
    /// </summary>
    public bool WantsGlobalHeader =>
        (AbiLayout.FlagsOfOutputFormat(AbiLayout.OutputFormatOf(_context)) & AVConstants.FormatGlobalHeader) != 0;

    /// <summary>Number of streams added so far.</summary>
    public int StreamCount => AbiLayout.NbStreams(_context);

    /// <summary>
    /// Creates an output.
    /// </summary>
    /// <param name="path">The file to write. Nothing is opened until <see cref="WriteHeader"/>.</param>
    /// <param name="formatName">A muxer name such as <c>mp4</c>; <see langword="null"/> guesses from the extension.</param>
    /// <exception cref="MediaToolkitNetException">No muxer matches.</exception>
    public static FFmpegMuxer Create(string path, string? formatName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FFmpegLibraries.EnsureLoaded();

        Span<byte> pathScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> formatScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var pathUtf8 = new Utf8Scoped(path, pathScratch);
        using var formatUtf8 = new Utf8Scoped(formatName, formatScratch);

        void* context = null;
        var result = AV.avformat_alloc_output_context2(&context, null, formatUtf8.Pointer, pathUtf8.Pointer);
        if (result < 0 || context is null)
        {
            throw new MediaToolkitNetException(
                FFmpegLibraries.BackendName,
                formatName is null
                    ? $"no muxer is known for the extension of {path}"
                    : $"no muxer is named {formatName}",
                result);
        }

        return new FFmpegMuxer(context, path);
    }

    /// <summary>
    /// Asks the muxer whether it can hold a codec.
    /// </summary>
    /// <param name="codecId">The codec, as the demuxer or encoder reports it.</param>
    /// <returns>
    /// True or false when the muxer knows, and <see langword="null"/> when it
    /// keeps no list and the answer only comes from trying.
    /// </returns>
    public bool? CanHold(int codecId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var answer = AV.avformat_query_codec(AbiLayout.OutputFormatOf(_context), codecId, AVConstants.ComplianceNormal);
        return answer switch
        {
            1 => true,
            0 => false,
            _ => null,
        };
    }

    /// <summary>
    /// Adds a stream whose packets will be copied: its codec parameters are
    /// taken from the input as they are.
    /// </summary>
    /// <param name="codecParameters">The input stream's <c>AVCodecParameters</c>.</param>
    /// <param name="timeBase">The input stream's time base, which the muxer may replace.</param>
    /// <returns>Returns the output stream index.</returns>
    public int AddStream(void* codecParameters, AVRationalNative timeBase)
    {
        var stream = NewStream();
        FFmpegError.Check(
            AV.avcodec_parameters_copy(AbiLayout.CodecParametersOf(stream), codecParameters),
            "avcodec_parameters_copy");

        // The codec tag belongs to the container the parameters came from: an MKV
        // fourcc is meaningless to MP4, so the new muxer picks its own.
        AbiLayout.ClearCodecTag(AbiLayout.CodecParametersOf(stream));
        AbiLayout.SetTimeBase(stream, timeBase);
        return AbiLayout.StreamIndexOf(stream);
    }

    /// <summary>Adds a stream fed by an open encoder.</summary>
    /// <param name="encoderContext">The encoder's <c>AVCodecContext</c>, already open.</param>
    /// <param name="timeBase">The encoder's time base, which the muxer may replace.</param>
    /// <returns>Returns the output stream index.</returns>
    public int AddEncodedStream(void* encoderContext, AVRationalNative timeBase)
    {
        var stream = NewStream();
        FFmpegError.Check(
            AV.avcodec_parameters_from_context(AbiLayout.CodecParametersOf(stream), encoderContext),
            "avcodec_parameters_from_context");
        AbiLayout.SetTimeBase(stream, timeBase);
        return AbiLayout.StreamIndexOf(stream);
    }

    /// <summary>Raw <c>AVStream</c> pointer for an output stream.</summary>
    public void* StreamHandle(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, StreamCount);
        return AbiLayout.Stream(_context, index);
    }

    /// <summary>
    /// The time base packets for a stream must be in. Only final once
    /// <see cref="WriteHeader"/> has run.
    /// </summary>
    public AVRationalNative TimeBaseOf(int index) => AbiLayout.TimeBaseOf(StreamHandle(index));

    /// <summary>Sets, or with a null or empty value removes, a tag on a stream.</summary>
    public void SetStreamTag(int index, string key, string? value) =>
        SetTag(AbiLayout.MetadataOfStream(StreamHandle(index)), key, value);

    /// <summary>Copies every tag of a dictionary onto a stream.</summary>
    /// <param name="index">The output stream.</param>
    /// <param name="tags">Tags to copy.</param>
    public void SetStreamTags(int index, IEnumerable<KeyValuePair<string, string>> tags)
    {
        foreach (var (key, value) in tags)
        {
            SetStreamTag(index, key, value);
        }
    }

    /// <summary>Marks a stream as the default of its kind, forced, or neither.</summary>
    public void SetDisposition(int index, bool isDefault, bool isForced)
    {
        var stream = StreamHandle(index);
        var value = AbiLayout.DispositionOf(stream) & ~(AVConstants.DispositionDefault | AVConstants.DispositionForced);
        if (isDefault)
        {
            value |= AVConstants.DispositionDefault;
        }

        if (isForced)
        {
            value |= AVConstants.DispositionForced;
        }

        AbiLayout.SetDisposition(stream, value);
    }

    /// <summary>Sets, or with a null or empty value removes, a file-level tag.</summary>
    public void SetTag(string key, string? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetTag(AbiLayout.MetadataOf(_context), key, value);
    }

    /// <summary>Adds a chapter.</summary>
    /// <param name="start">Where it starts, in the output's time.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="title">Its title, if any.</param>
    public void AddChapter(TimeSpan start, TimeSpan end, string? title)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_headerWritten)
        {
            throw new InvalidOperationException("Chapters have to be added before the header is written.");
        }

        // Milliseconds are what Matroska and MP4 store anyway.
        var timeBase = new AVRationalNative(1, 1000);
        var chapter = AbiLayout.AddChapter(
            _context,
            AbiLayout.ChapterCountOf(_context) + 1,
            timeBase,
            (long)start.TotalMilliseconds,
            (long)end.TotalMilliseconds);
        FFmpegError.CheckAlloc(chapter, "AddChapter");

        if (!string.IsNullOrEmpty(title))
        {
            SetTag(AbiLayout.MetadataOfChapter(chapter), "title", title);
        }
    }

    /// <summary>
    /// Opens the file and writes the container header.
    /// </summary>
    /// <param name="options">Muxer options, for example <c>movflags=+faststart</c>.</param>
    /// <returns>Returns the options the muxer did not recognise, which a caller may want to report.</returns>
    public IReadOnlyList<string> WriteHeader(IReadOnlyDictionary<string, string>? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_headerWritten)
        {
            throw new InvalidOperationException("The header has already been written.");
        }

        if ((AbiLayout.FlagsOfOutputFormat(AbiLayout.OutputFormatOf(_context)) & AVConstants.FormatNoFile) == 0)
        {
            Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var path = new Utf8Scoped(_path, scratch);
            void* pb = null;
            FFmpegError.Check(AV.avio_open(&pb, path.Pointer, AVConstants.AvioFlagWrite), $"avio_open({_path})");
            AbiLayout.SetPb(_context, pb);
        }

        void* dictionary = null;
        try
        {
            if (options is not null)
            {
                foreach (var (key, value) in options)
                {
                    AV.DictionarySet(&dictionary, key, value);
                }
            }

            FFmpegError.Check(AV.avformat_write_header(_context, &dictionary), "avformat_write_header");
            _headerWritten = true;

            // Whatever avformat_write_header left in the dictionary, it did not use.
            return [.. AbiLayout.ReadDictionary(dictionary).Keys];
        }
        finally
        {
            AV.av_dict_free(&dictionary);
        }
    }

    /// <summary>
    /// Writes one packet, interleaving it with the other streams. The packet
    /// must already be in the output stream's time base and carry its index;
    /// the muxer takes over its reference.
    /// </summary>
    public void WritePacket(AVPacketNative* packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_headerWritten)
        {
            throw new InvalidOperationException("Write the header before any packet.");
        }

        FFmpegError.Check(AV.av_interleaved_write_frame(_context, packet), "av_interleaved_write_frame");
    }

    /// <summary>Flushes the interleaving queue, writes the trailer and closes the file.</summary>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_headerWritten || _finished)
        {
            return;
        }

        _finished = true;
        FFmpegError.Check(AV.av_write_trailer(_context), "av_write_trailer");
        ClosePb();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_context is not null)
        {
            ClosePb();
            AV.avformat_free_context(_context);
            _context = null;
        }
    }

    private void* NewStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_headerWritten)
        {
            throw new InvalidOperationException("Streams have to be added before the header is written.");
        }

        var expected = StreamCount;
        var stream = FFmpegError.CheckAlloc(AV.avformat_new_stream(_context, null), "avformat_new_stream");
        AbiLayout.ValidateNewStream(stream, expected);
        return stream;
    }

    private void ClosePb()
    {
        if (AbiLayout.GetPb(_context) is not null)
        {
            AV.avio_closep((void**)((byte*)_context + AbiLayout.FormatContextPb));
        }
    }

    private static void SetTag(void** dictionary, string key, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Span<byte> keyScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> valueScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var keyUtf8 = new Utf8Scoped(key, keyScratch);

        // av_dict_set with a null value removes the key.
        using var valueUtf8 = new Utf8Scoped(string.IsNullOrEmpty(value) ? null : value, valueScratch);
        FFmpegError.Check(AV.av_dict_set(dictionary, keyUtf8.Pointer, valueUtf8.Pointer, 0), $"av_dict_set({key})");
    }
}
