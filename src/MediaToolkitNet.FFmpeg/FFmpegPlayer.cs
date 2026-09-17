using System.Diagnostics;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Playback;
using MediaToolkitNet.FFmpeg.Native;

namespace MediaToolkitNet.FFmpeg;

/// <summary>
/// Decode-only player built on <see cref="FFmpegDemuxer"/> and
/// <see cref="FFmpegDecoder"/>.
/// </summary>
/// <remarks>
/// FFmpeg has no renderer, so this player never draws anything and never opens
/// an audio device: it walks the file on a background thread and hands every
/// decoded frame to <see cref="VideoFrameDecoded"/> and
/// <see cref="AudioFrameDecoded"/>. Pair it with a renderer of your choice, or
/// use the mpv backend when you want playback that renders itself.
/// </remarks>
public sealed unsafe class FFmpegPlayer : IMediaPlayer
{
    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _resume = new(false);

    private FFmpegDemuxer? _demuxer;
    private FFmpegDecoder? _videoDecoder;
    private FFmpegDecoder? _audioDecoder;
    private int _videoStreamIndex = -1;
    private int _audioStreamIndex = -1;
    private Rational _videoFrameRate;

    private Thread? _worker;
    private CancellationTokenSource? _cancellation;
    private PlaybackState _state = PlaybackState.Closed;
    private long _positionTicks;
    private TimeSpan? _pendingSeek;
    private double _volume = 1.0;
    private bool _disposed;

    /// <inheritdoc />
    public string Backend => FFmpegLibraries.BackendName;

    /// <inheritdoc />
    public PlaybackState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public TimeSpan Duration => _demuxer?.Duration ?? TimeSpan.Zero;

    /// <inheritdoc />
    public TimeSpan Position => new(Interlocked.Read(ref _positionTicks));

    /// <summary>
    /// Volume is tracked but not applied: this player does not own an audio
    /// output. Apply it in your own renderer.
    /// </summary>
    public double Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0d, 1d);
    }

    /// <inheritdoc />
    public bool CanSeek => _demuxer is not null;

    /// <summary>
    /// When true (the default) the decode thread paces itself against the frame
    /// timestamps, so frames arrive roughly in real time. Set it to false to
    /// decode as fast as possible, e.g. for transcoding or thumbnailing.
    /// </summary>
    public bool RealTimePacing { get; set; } = true;

    /// <summary>Number of decode threads to request; 0 lets FFmpeg pick.</summary>
    public int ThreadCount { get; set; }

    /// <inheritdoc />
    public event EventHandler<PlaybackStateChange>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <inheritdoc />
    public event VideoFrameHandler? VideoFrameDecoded;

    /// <inheritdoc />
    public event AudioFrameHandler? AudioFrameDecoded;

    /// <inheritdoc />
    public void Open(string uri)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        Stop();
        CloseMedia();

        var demuxer = FFmpegDemuxer.Open(uri);
        try
        {
            var video = demuxer.FindStream(AVMediaType.Video);
            if (video is { } videoInfo)
            {
                _videoDecoder = FFmpegDecoder.Open(demuxer, videoInfo.Index, ThreadCount);
                _videoStreamIndex = videoInfo.Index;
                _videoFrameRate = videoInfo.AverageFrameRate;
            }

            var audio = demuxer.FindStream(AVMediaType.Audio);
            if (audio is { } audioInfo)
            {
                _audioDecoder = FFmpegDecoder.Open(demuxer, audioInfo.Index, ThreadCount);
                _audioStreamIndex = audioInfo.Index;
            }

            if (_videoDecoder is null && _audioDecoder is null)
            {
                throw new MediaToolkitNetException(
                    Backend, "the container has neither a video nor an audio stream", AVConstants.ErrorInvalid);
            }
        }
        catch
        {
            CloseMedia();
            demuxer.Dispose();
            throw;
        }

        _demuxer = demuxer;
        Interlocked.Exchange(ref _positionTicks, 0);
        TransitionTo(PlaybackState.Stopped);
    }

    /// <inheritdoc />
    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_demuxer is null)
        {
            throw new InvalidOperationException("Open the media first.");
        }

        lock (_gate)
        {
            if (_state == PlaybackState.Playing)
            {
                return;
            }

            if (_worker is null || !_worker.IsAlive)
            {
                _cancellation = new CancellationTokenSource();
                _worker = new Thread(() => DecodeLoop(_cancellation.Token))
                {
                    IsBackground = true,
                    Name = "MediaToolkitNet.FFmpeg decode",
                };
                _worker.Start();
            }
        }

        _resume.Set();
        TransitionTo(PlaybackState.Playing);
    }

    /// <inheritdoc />
    public void Pause()
    {
        if (State != PlaybackState.Playing)
        {
            return;
        }

        _resume.Reset();
        TransitionTo(PlaybackState.Paused);
    }

    /// <inheritdoc />
    public void Stop()
    {
        Thread? worker;
        lock (_gate)
        {
            worker = _worker;
            _worker = null;
            _cancellation?.Cancel();
        }

        _resume.Set();
        worker?.Join(TimeSpan.FromSeconds(2));

        lock (_gate)
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }

        _resume.Reset();
        Interlocked.Exchange(ref _positionTicks, 0);

        if (_demuxer is not null)
        {
            _pendingSeek = TimeSpan.Zero;
            TransitionTo(PlaybackState.Stopped);
        }
    }

    /// <inheritdoc />
    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanSeek)
        {
            throw new InvalidOperationException("Seeking is unavailable: no media is open.");
        }

        lock (_gate)
        {
            _pendingSeek = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        }

        if (State is PlaybackState.Stopped or PlaybackState.Paused or PlaybackState.Ended)
        {
            // Nothing is running, so apply the seek straight away.
            ApplyPendingSeek();
            Interlocked.Exchange(ref _positionTicks, position.Ticks);
        }
    }

    private void DecodeLoop(CancellationToken token)
    {
        var packet = AV.av_packet_alloc();
        var frame = AV.av_frame_alloc();
        var clock = Stopwatch.StartNew();
        var clockOrigin = TimeSpan.Zero;
        var clockValid = false;

        try
        {
            FFmpegError.CheckAlloc(packet, "av_packet_alloc");
            FFmpegError.CheckAlloc(frame, "av_frame_alloc");

            while (!token.IsCancellationRequested)
            {
                if (!_resume.Wait(50, token))
                {
                    clockValid = false;
                    continue;
                }

                if (ApplyPendingSeek())
                {
                    clockValid = false;
                }

                if (!_demuxer!.ReadPacket(packet))
                {
                    DrainDecoders(frame);
                    TransitionTo(PlaybackState.Ended);
                    return;
                }

                try
                {
                    var decoder = SelectDecoder(packet->StreamIndex);
                    if (decoder is null)
                    {
                        continue;
                    }

                    if (!decoder.SendPacket(packet))
                    {
                        continue;
                    }

                    while (decoder.ReceiveFrame(frame) == FFmpegDecoder.ReceiveResult.Frame)
                    {
                        var timestamp = decoder.TimestampOf(frame);
                        Interlocked.Exchange(ref _positionTicks, timestamp.Ticks);

                        if (RealTimePacing)
                        {
                            if (!clockValid)
                            {
                                clockOrigin = timestamp;
                                clock.Restart();
                                clockValid = true;
                            }
                            else
                            {
                                var target = timestamp - clockOrigin;
                                var ahead = target - clock.Elapsed;
                                if (ahead > TimeSpan.FromMilliseconds(2))
                                {
                                    token.WaitHandle.WaitOne(ahead);
                                }
                            }
                        }

                        Dispatch(decoder, frame);
                        AV.av_frame_unref(frame);
                    }
                }
                finally
                {
                    AV.av_packet_unref(packet);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() was called; nothing to report.
        }
        catch (Exception ex)
        {
            var error = ex as MediaToolkitNetException
                        ?? new MediaToolkitNetException("FFmpeg decoding error.", ex);
            TransitionTo(PlaybackState.Faulted);
            Failed?.Invoke(this, error);
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
                var localPacket = packet;
                AV.av_packet_free(&localPacket);
            }
        }
    }

    private void DrainDecoders(AVFrameHead* frame)
    {
        foreach (var decoder in new[] { _videoDecoder, _audioDecoder })
        {
            if (decoder is null)
            {
                continue;
            }

            decoder.SendPacket(null);
            while (decoder.ReceiveFrame(frame) == FFmpegDecoder.ReceiveResult.Frame)
            {
                Dispatch(decoder, frame);
                AV.av_frame_unref(frame);
            }
        }
    }

    private void Dispatch(FFmpegDecoder decoder, AVFrameHead* frame)
    {
        if (decoder.MediaType == AVMediaType.Video)
        {
            var handler = VideoFrameDecoded;
            if (handler is null)
            {
                return;
            }

            Span<nint> planes = stackalloc nint[MediaPlanes.MaxPlanes];
            Span<int> strides = stackalloc int[MediaPlanes.MaxPlanes];
            var planeCount = 0;
            for (var i = 0; i < MediaPlanes.MaxPlanes; i++)
            {
                if (frame->Data[i] == 0)
                {
                    break;
                }

                planes[i] = (nint)frame->Data[i];
                strides[i] = frame->Linesize[i];
                planeCount++;
            }

            var format = decoder.DescribeVideo(frame, _videoFrameRate);
            var videoFrame = new VideoFrame(format, decoder.TimestampOf(frame), planes, strides, planeCount);
            handler(in videoFrame);
        }
        else
        {
            var handler = AudioFrameDecoded;
            if (handler is null)
            {
                return;
            }

            var format = decoder.DescribeAudio(frame);
            var planeCount = format.SampleFormat.IsPlanar() ? format.Channels : 1;
            planeCount = Math.Min(planeCount, MediaPlanes.MaxPlanes);

            Span<nint> planes = stackalloc nint[MediaPlanes.MaxPlanes];
            for (var i = 0; i < planeCount; i++)
            {
                // extended_data is the only safe source when a planar frame has
                // more channels than AVFrame::data can hold.
                planes[i] = (nint)frame->ExtendedData[i];
            }

            var audioFrame = new AudioFrame(format, decoder.TimestampOf(frame), frame->NbSamples, planes, planeCount);
            handler(in audioFrame);
        }
    }

    private FFmpegDecoder? SelectDecoder(int streamIndex)
    {
        if (streamIndex == _videoStreamIndex)
        {
            return _videoDecoder;
        }

        return streamIndex == _audioStreamIndex ? _audioDecoder : null;
    }

    private bool ApplyPendingSeek()
    {
        TimeSpan? target;
        lock (_gate)
        {
            target = _pendingSeek;
            _pendingSeek = null;
        }

        if (target is not { } position || _demuxer is null)
        {
            return false;
        }

        _demuxer.Seek(position);
        _videoDecoder?.Flush();
        _audioDecoder?.Flush();
        Interlocked.Exchange(ref _positionTicks, position.Ticks);
        return true;
    }

    private void TransitionTo(PlaybackState next)
    {
        PlaybackState previous;
        lock (_gate)
        {
            if (_state == next)
            {
                return;
            }

            previous = _state;
            _state = next;
        }

        StateChanged?.Invoke(this, new PlaybackStateChange(previous, next));
    }

    private void CloseMedia()
    {
        _videoDecoder?.Dispose();
        _videoDecoder = null;
        _audioDecoder?.Dispose();
        _audioDecoder = null;
        _videoStreamIndex = -1;
        _audioStreamIndex = -1;
        _demuxer?.Dispose();
        _demuxer = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        CloseMedia();
        _resume.Dispose();
        TransitionTo(PlaybackState.Closed);
    }
}
