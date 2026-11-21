#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.FFmpeg.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg;

/// <summary>Description of one stream inside a container.</summary>
/// <param name="Index">Stream index, matching <c>AVStream::index</c>.</param>
/// <param name="MediaType">Video, audio or something else.</param>
/// <param name="CodecId">Raw <c>AVCodecID</c>; treated as an opaque value.</param>
/// <param name="CodecName">Human-readable codec name from <c>avcodec_get_name</c>.</param>
/// <param name="TimeBase">Time base the stream timestamps are expressed in.</param>
/// <param name="AverageFrameRate">Average frame rate, or 0/0 for audio and variable rate streams.</param>
/// <param name="Duration">Stream duration, or <see cref="TimeSpan.Zero"/> when the container does not state one.</param>
public readonly record struct FFmpegStreamInfo(
    int Index,
    AVMediaType MediaType,
    int CodecId,
    string CodecName,
    Rational TimeBase,
    Rational AverageFrameRate,
    TimeSpan Duration);

/// <summary>
/// Thin wrapper over <c>AVFormatContext</c> in demuxing mode: opens an input,
/// exposes its streams and pulls packets out of it.
/// </summary>
public sealed unsafe class FFmpegDemuxer : IDisposable
{
    private void* _context;
    private void* _pb;
    private InterruptState? _interrupt;
    private System.Runtime.InteropServices.GCHandle _interruptHandle;
    private FFmpegStreamInfo[] _streams = [];
    private bool _disposed;

    private FFmpegDemuxer(void* context, void* pb, InterruptState? interrupt, System.Runtime.InteropServices.GCHandle interruptHandle)
    {
        _context = context;
        _pb = pb;
        _interrupt = interrupt;
        _interruptHandle = interruptHandle;
    }

    /// <summary>What the native side asks, from any thread, whether to give up waiting.</summary>
    private sealed class InterruptState
    {
        public volatile bool Requested;
    }

    /// <summary>
    /// Makes a read or connect that is waiting on the network return with an
    /// error, so the thread blocked in it can be stopped. Until
    /// <see cref="ClearInterrupt"/> every network read fails. Inputs that are
    /// files cannot block and are not affected.
    /// </summary>
    public void Interrupt()
    {
        if (_interrupt is not null)
        {
            _interrupt.Requested = true;
        }
    }

    /// <summary>Lets reads wait again after an <see cref="Interrupt"/> has done its work.</summary>
    public void ClearInterrupt()
    {
        if (_interrupt is not null)
        {
            _interrupt.Requested = false;
        }
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int InterruptCallback(void* opaque)
    {
        try
        {
            var handle = System.Runtime.InteropServices.GCHandle.FromIntPtr((nint)opaque);
            return handle.Target is InterruptState { Requested: true } ? 1 : 0;
        }
        catch (Exception)
        {
            return 0; // nothing may be thrown into libavformat
        }
    }

    /// <summary>True for an input that goes over the network, which is where a read can block.</summary>
    private static bool IsNetworkUrl(string url) =>
        url.Contains("://", StringComparison.Ordinal) && !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Streams found in the container.</summary>
    public IReadOnlyList<FFmpegStreamInfo> Streams => _streams;

    /// <summary>Raw <c>AVFormatContext</c> pointer, for callers that need to go lower.</summary>
    public void* Handle => _context;

    /// <summary>
    /// Opens a file, a URL or a capture device.
    /// </summary>
    /// <param name="url">Path, URL, or device name when <paramref name="inputFormat"/> is set.</param>
    /// <param name="inputFormat">
    /// Forced demuxer name such as <c>dshow</c>, <c>v4l2</c> or <c>avfoundation</c>.
    /// Requires libavdevice for the device demuxers.
    /// </param>
    /// <param name="options">Demuxer options, passed through as an AVDictionary.</param>
    /// <param name="allowNetwork">
    /// Whether the input may be a URL on the network. By default only files and
    /// pipes are opened, so a path someone else supplied cannot make this process
    /// connect to a host of their choosing; <see cref="AllowNetworkByDefault"/>
    /// changes the default for the whole process. Devices are not affected.
    /// </param>
    /// <param name="allowPlaylists">
    /// Playlist demuxers (HLS, DASH, concat) read other files and URLs named by
    /// the input, so a file someone else supplied can make this process read
    /// anything it can reach. They are refused unless the caller trusts the input.
    /// </param>
    public static FFmpegDemuxer Open(
        string url,
        string? inputFormat = null,
        IReadOnlyDictionary<string, string>? options = null,
        bool allowPlaylists = false,
        bool? allowNetwork = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        FFmpegLibraries.EnsureLoaded();

        void* format = null;
        if (!string.IsNullOrEmpty(inputFormat))
        {
            Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var name = new Utf8Scoped(inputFormat, scratch);
            format = AV.av_find_input_format(name.Pointer);
            if (format is null)
            {
                throw new Abstractions.MediaToolkitNetException(
                    FFmpegLibraries.BackendName, $"demuxer \"{inputFormat}\" not found", AVConstants.ErrorInvalid);
            }
        }

        void* dictionary = null;
        if (options is not null)
        {
            foreach (var (key, value) in options)
            {
                AV.DictionarySet(&dictionary, key, value);
            }
        }

        // A device name is not a protocol, and a caller's own whitelist wins.
        if (string.IsNullOrEmpty(inputFormat)
            && !(allowNetwork ?? AllowNetworkByDefault)
            && options?.ContainsKey("protocol_whitelist") != true)
        {
            AV.DictionarySet(&dictionary, "protocol_whitelist", "file,pipe,crypto,data");
        }

        void* context = null;
        void* pb = null;
        InterruptState? interrupt = null;
        var interruptHandle = default(System.Runtime.InteropServices.GCHandle);
        var owned = false;
        try
        {
            Span<byte> urlScratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var urlUtf8 = new Utf8Scoped(url, urlScratch);

            if (string.IsNullOrEmpty(inputFormat) && IsNetworkUrl(url))
            {
                // The input is opened here, with a callback the demuxer can be
                // interrupted through, and handed to libavformat ready-made.
                interrupt = new InterruptState();
                interruptHandle = System.Runtime.InteropServices.GCHandle.Alloc(interrupt);
                var callback = stackalloc nint[2];
                callback[0] = (nint)(delegate* unmanaged[Cdecl]<void*, int>)&InterruptCallback;
                callback[1] = System.Runtime.InteropServices.GCHandle.ToIntPtr(interruptHandle);

                void* ioOptions = null;
                try
                {
                    if (options is not null)
                    {
                        foreach (var (key, value) in options)
                        {
                            AV.DictionarySet(&ioOptions, key, value);
                        }
                    }

                    if (!(allowNetwork ?? AllowNetworkByDefault) && options?.ContainsKey("protocol_whitelist") != true)
                    {
                        AV.DictionarySet(&ioOptions, "protocol_whitelist", "file,pipe,crypto,data");
                    }

                    FFmpegError.Check(
                        AV.avio_open2(&pb, urlUtf8.Pointer, AVConstants.AvioFlagRead, callback, &ioOptions),
                        $"avformat_open_input({url})");
                }
                finally
                {
                    if (ioOptions is not null)
                    {
                        AV.av_dict_free(&ioOptions);
                    }
                }

                context = AV.avformat_alloc_context();
                if (context is null)
                {
                    throw new Abstractions.MediaToolkitNetException(
                        FFmpegLibraries.BackendName, "avformat_alloc_context returned null", AVConstants.ErrorInvalid);
                }

                AbiLayout.SetPb(context, pb);
            }

            FFmpegError.Check(AV.avformat_open_input(&context, urlUtf8.Pointer, format, &dictionary), $"avformat_open_input({url})");
            if (!allowPlaylists && string.IsNullOrEmpty(inputFormat) && IsPlaylistFormat(AbiLayout.NameOfFormat(AbiLayout.InputFormatOf(context))))
            {
                throw new Abstractions.MediaToolkitNetException(
                    FFmpegLibraries.BackendName,
                    $"{url} is a playlist ({AbiLayout.NameOfFormat(AbiLayout.InputFormatOf(context))}), which names other files to read; " +
                    "open the media it lists, or allow playlists for input you trust",
                    AVConstants.ErrorInvalid);
            }

            FFmpegError.Check(AV.avformat_find_stream_info(context, null), "avformat_find_stream_info");
            AbiLayout.ValidateOpenInput(context, url);

            var opened = new FFmpegDemuxer(context, pb, interrupt, interruptHandle);
            owned = true;
            opened.ReadStreamInfo();
            return opened;
        }
        catch
        {
            if (!owned)
            {
                if (context is not null)
                {
                    AV.avformat_close_input(&context);
                }

                // A pre-opened input is not closed by libavformat, whether it
                // went on to open or not.
                if (pb is not null)
                {
                    AV.avio_closep(&pb);
                }

                if (interruptHandle.IsAllocated)
                {
                    interruptHandle.Free();
                }
            }

            throw;
        }
        finally
        {
            if (dictionary is not null)
            {
                AV.av_dict_free(&dictionary);
            }
        }
    }

    /// <summary>
    /// Whether inputs may be network URLs when the caller does not say. Off, so
    /// that a path taken from an untrusted source stays on the local machine;
    /// an application that handles URLs on purpose turns it on once at start-up.
    /// </summary>
    public static bool AllowNetworkByDefault { get; set; }

    private static readonly string[] PlaylistFormats = ["hls", "applehttp", "concat", "dash"];

    private static bool IsPlaylistFormat(string names) =>
        names.Split(',').Any(n => PlaylistFormats.Contains(n, StringComparer.OrdinalIgnoreCase));

    private void ReadStreamInfo()
    {
        var count = AbiLayout.NbStreams(_context);
        var result = new FFmpegStreamInfo[count];
        for (var i = 0; i < count; i++)
        {
            var stream = AbiLayout.Stream(_context, i);
            var parameters = AbiLayout.CodecParametersOf(stream);
            var codecId = AbiLayout.CodecIdOf(parameters);
            var timeBase = AbiLayout.TimeBaseOf(stream).ToRational();
            var rawDuration = AbiLayout.DurationOf(stream);

            result[i] = new FFmpegStreamInfo(
                i,
                AbiLayout.CodecTypeOf(parameters),
                codecId,
                Interop.Utf8.ToManagedOrEmpty(AV.avcodec_get_name(codecId)),
                timeBase,
                AbiLayout.AvgFrameRateOf(stream).ToRational(),
                rawDuration == AVConstants.NoPtsValue || timeBase.Denominator == 0
                    ? TimeSpan.Zero
                    : TimeSpan.FromSeconds(rawDuration * timeBase.Value));
        }

        _streams = result;
    }

    /// <summary>Longest stream duration, or <see cref="TimeSpan.Zero"/> when nothing reports one.</summary>
    public TimeSpan Duration
    {
        get
        {
            var longest = TimeSpan.Zero;
            foreach (var stream in _streams)
            {
                if (stream.Duration > longest)
                {
                    longest = stream.Duration;
                }
            }

            return longest;
        }
    }

    /// <summary>Returns the first stream of the given type, or <see langword="null"/>.</summary>
    public FFmpegStreamInfo? FindStream(AVMediaType type)
    {
        foreach (var stream in _streams)
        {
            if (stream.MediaType == type)
            {
                return stream;
            }
        }

        return null;
    }

    /// <summary>Raw <c>AVStream</c> pointer for the given index.</summary>
    public void* StreamHandle(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _streams.Length);
        return AbiLayout.Stream(_context, index);
    }

    /// <summary>Raw <c>AVCodecParameters</c> pointer for the given stream.</summary>
    public void* CodecParameters(int index) => AbiLayout.CodecParametersOf(StreamHandle(index));

    /// <summary>
    /// Reads the next packet into <paramref name="packet"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a packet was read, <see langword="false"/> at
    /// end of stream. The caller owns the packet reference and must call
    /// <c>av_packet_unref</c>.
    /// </returns>
    public bool ReadPacket(AVPacketNative* packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = AV.av_read_frame(_context, packet);
        if (result == AVConstants.ErrorEof)
        {
            return false;
        }

        FFmpegError.Check(result, "av_read_frame");
        return true;
    }

    /// <summary>Seeks the container so that the next packets are at or before <paramref name="position"/>.</summary>
    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var timestamp = (long)(position.TotalSeconds * AVConstants.TimeBase);
        FFmpegError.Check(
            AV.av_seek_frame(_context, -1, timestamp, AVConstants.SeekFlagBackward),
            $"av_seek_frame({position})");
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
            fixed (void** context = &_context)
            {
                AV.avformat_close_input(context);
            }
        }

        // A pre-opened input is not closed with the context, and the callback it
        // holds must stay valid until it is.
        if (_pb is not null)
        {
            fixed (void** pb = &_pb)
            {
                AV.avio_closep(pb);
            }
        }

        if (_interruptHandle.IsAllocated)
        {
            _interruptHandle.Free();
        }
    }
}
