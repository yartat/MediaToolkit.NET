#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Playback;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer;

/// <summary>
/// Plays a file or URL with <c>playbin3</c> and hands the decoded frames back, the way the FFmpeg player does: video as
/// <c>Bgra32</c> and audio as interleaved <c>S16</c> (in the media's own rate and
/// channel count), each paced by the pipeline's clock, so playing a one-second file takes a second.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is drawn or played on its own: the sinks are <c>appsink</c>s. For a window or a sound card use a pipeline of
/// your own (<see cref="GStreamerPipeline"/>), or take the frames from here.
/// </para>
/// <para>
/// The events are raised on GStreamer's streaming threads, the frames valid for the call only. A handler may call
/// <see cref="Dispose"/>: the pipeline is then taken down from another thread, which it could not be from its own.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerPlayer : IMediaPlayer
{
    private const string VideoSinkDescription = "videoconvert ! video/x-raw,format=BGRA ! appsink name=vsink emit-signals=true sync=true";
    private const string AudioSinkDescription = "audioconvert ! audio/x-raw,format=S16LE,layout=interleaved ! appsink name=asink emit-signals=true sync=true";

    [ThreadStatic]
    private static bool _inHandler;

    private readonly Lock _gate = new();
    private readonly List<GStreamerSink> _sinks = [];
    private GStreamerPipeline? _pipeline;
    private GStreamerElement? _root;
    private PlaybackState _state = PlaybackState.Closed;
    private double _volume = 1.0;
    private bool _disposed;

    /// <summary>Creates a player; GStreamer is loaded when the first file is opened.</summary>
    public GStreamerPlayer()
    {
    }

    /// <summary>
    /// Where the picture goes, as a <c>gst-launch</c> description of a sink (<c>ximagesink</c>, <c>autovideosink</c>,
    /// <c>glimagesink</c>). Left <see langword="null"/>, the frames come back through <see cref="VideoFrameDecoded"/>
    /// and nothing is drawn; set, the sink draws and the event is not raised. Set it before <see cref="Open"/>.
    /// </summary>
    public string? VideoSink { get; set; }

    /// <summary>
    /// Where the sound goes, as a description of a sink (<c>pulsesink</c>, <c>autoaudiosink</c>, <c>alsasink</c>). Left
    /// <see langword="null"/>, the blocks come back through <see cref="AudioFrameDecoded"/> and nothing is played; set,
    /// the sink plays and the event is not raised. Set it before <see cref="Open"/>.
    /// </summary>
    public string? AudioSink { get; set; }

    /// <summary>
    /// An X11 window the picture is drawn into instead of a window of the sink's own; the sink must be one that can
    /// (<c>ximagesink</c>, <c>xvimagesink</c>, <c>glimagesink</c>), which is why <see cref="VideoSink"/> has to be set as
    /// well. Zero lets the sink open its window. Set it before <see cref="Open"/>.
    /// </summary>
    public nuint VideoWindowHandle { get; set; }

    /// <inheritdoc />
    public string Backend => GstConstants.BackendName;

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
    public TimeSpan Duration => _pipeline?.Duration ?? TimeSpan.Zero;

    /// <inheritdoc />
    public TimeSpan Position => _pipeline?.Position ?? TimeSpan.Zero;

    /// <inheritdoc />
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0.0, 1.0);
            _root?.SetValue("volume", _volume);
        }
    }

    /// <inheritdoc />
    public bool CanSeek => _pipeline is not null && Duration > TimeSpan.Zero;

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

        Close();
        var location = uri.Contains("://", StringComparison.Ordinal) ? uri : new Uri(Path.GetFullPath(uri)).AbsoluteUri;
        var pipeline = GStreamerPipeline.Parse(GStreamerElements.Exists("playbin3") ? "playbin3" : "playbin", strict: true);
        try
        {
            var root = pipeline.AsElement();
            root.Failed += (_, e) => Raise(e);
            root.Set("uri", location);
            root.SetValue("volume", _volume);
            AttachSink(root, "video-sink", VideoSink ?? VideoSinkDescription, "vsink", deliver: VideoSink is null);
            AttachSink(root, "audio-sink", AudioSink ?? AudioSinkDescription, "asink", deliver: AudioSink is null);
            if (VideoWindowHandle != 0 && !Gst.SetVideoOverlayWindow(root.Handle, VideoWindowHandle))
            {
                throw new MediaToolkitNetException(Backend, "the player cannot draw into a window: it is not a video overlay", 0);
            }

            pipeline.Bus.MessageReceived += OnMessage;
            _pipeline = pipeline;
            _root = root;

            // Paused is where the file prerolls, so a bad one fails here and the duration is known afterwards.
            pipeline.Pause();
        }
        catch (MediaToolkitNetException ex)
        {
            Close();
            pipeline.Dispose();
            throw new MediaToolkitNetException(Backend, $"could not open {uri}: {ex.Message}", 0);
        }
        catch
        {
            Close();
            pipeline.Dispose();
            throw;
        }

        TransitionTo(PlaybackState.Stopped);
    }

    /// <inheritdoc />
    public void Play()
    {
        var pipeline = Required();
        RequireNotFaulted();
        if (State == PlaybackState.Ended)
        {
            if (!pipeline.Seek(TimeSpan.Zero))
            {
                throw new InvalidOperationException("The media cannot start over: it cannot seek back to its beginning.");
            }
        }

        pipeline.Play();
        TransitionTo(PlaybackState.Playing);
    }

    /// <inheritdoc />
    public void Pause()
    {
        if (_pipeline is not { } pipeline || State != PlaybackState.Playing)
        {
            return;
        }

        pipeline.Pause();
        TransitionTo(PlaybackState.Paused);
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_pipeline is not { } pipeline || State == PlaybackState.Faulted)
        {
            return;
        }

        pipeline.Pause();
        Seek(TimeSpan.Zero);
        TransitionTo(PlaybackState.Stopped);
    }

    /// <inheritdoc />
    public void Seek(TimeSpan position)
    {
        var pipeline = Required();
        RequireNotFaulted();
        if (!CanSeek)
        {
            throw new InvalidOperationException("Seeking is unavailable: the media has no known length.");
        }

        // A pipeline that reached its end is still PLAYING, so a seek would set it running again; and Play from the end
        // starts over, so a place seeked to must leave the Ended state or Play would rewind it.
        var wasEnded = State == PlaybackState.Ended;
        if (wasEnded)
        {
            pipeline.Pause();
        }

        if (!pipeline.Seek(position < TimeSpan.Zero ? TimeSpan.Zero : position))
        {
            throw new MediaToolkitNetException(Backend, $"the media cannot seek to {position}", 0);
        }

        // A flushing seek finishes asynchronously; the position is the target once it has.
        pipeline.WaitForState(TimeSpan.FromSeconds(2));
        if (wasEnded)
        {
            TransitionTo(PlaybackState.Paused);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_inHandler)
        {
            // Taking a pipeline to NULL waits for its streaming threads, and this is one of them.
            ThreadPool.QueueUserWorkItem(_ => Close());
        }
        else
        {
            Close();
        }

        TransitionTo(PlaybackState.Closed);
    }

    private void RequireNotFaulted()
    {
        if (State == PlaybackState.Faulted)
        {
            throw new InvalidOperationException("The player faulted; open the media again.");
        }
    }

    private GStreamerPipeline Required() =>
        _pipeline ?? throw new InvalidOperationException("Open the media first.");

    // With deliver the description ends in an appsink named `name`, whose samples are pulled for the events; without it
    // the description is a sink that renders, and it is set as it is.
    private void AttachSink(GStreamerElement root, string property, string description, string name, bool deliver)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(description, scratch);
        void* error = null;
        var bin = Gst.gst_parse_bin_from_description(utf8.Pointer, 1, &error);
        if (bin is null)
        {
            throw new MediaToolkitNetException(Backend, $"the sink {name} could not be built: {Gst.TakeError(error)}", 0);
        }

        Gst.gst_object_ref_sink(bin);
        try
        {
            if (!deliver)
            {
                root.SetObject(property, bin);
                return;
            }

            Span<byte> nameScratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var nameUtf8 = new Utf8Scoped(name, nameScratch);
            var element = Gst.gst_bin_get_by_name(bin, nameUtf8.Pointer);
            var sink = new GStreamerSink(new GStreamerElement(element, name));
            _sinks.Add(sink);
            sink.Element.Failed += (_, e) => Raise(e);

            if (name == "vsink")
            {
                sink.Element.Connect("new-sample", _ => Deliver(sink, video: true));
            }
            else
            {
                sink.Element.Connect("new-sample", _ => Deliver(sink, video: false));
            }

            root.SetObject(property, bin);
        }
        finally
        {
            Gst.gst_object_unref(bin);
        }
    }

    // The signal says a sample is waiting; it is pulled here so that the frame is mapped for the handler's call only.
    private void Deliver(GStreamerSink sink, bool video)
    {
        _inHandler = true;
        try
        {
            if (video)
            {
                sink.TryPull(TimeSpan.Zero, (in VideoFrame frame) => VideoFrameDecoded?.Invoke(in frame), null);
            }
            else
            {
                sink.TryPull(TimeSpan.Zero, null, (in AudioFrame frame) => AudioFrameDecoded?.Invoke(in frame));
            }
        }
        finally
        {
            _inHandler = false;
        }
    }

    private void OnMessage(object? sender, GStreamerMessage message)
    {
        switch (message.Type)
        {
            case GstMessageType.Eos:
                TransitionTo(PlaybackState.Ended);
                break;
            case GstMessageType.Error:
                // A failing element brings the others down with it, and each of them posts an error of its own: the first
                // is the cause and the only one reported, the rest are what it left behind.
                bool first;
                lock (_gate)
                {
                    first = _state != PlaybackState.Faulted;
                }

                if (first)
                {
                    // The error first: whoever waits for the Faulted state finds the reason already reported.
                    Raise(new MediaToolkitNetException(Backend, $"{message.Text} ({message.Debug})", 0));
                    TransitionTo(PlaybackState.Faulted);
                }

                break;
        }
    }

    private void TransitionTo(PlaybackState next)
    {
        PlaybackState previous;
        lock (_gate)
        {
            if (_state == next || (_state == PlaybackState.Closed && _disposed && next != PlaybackState.Closed))
            {
                return;
            }

            previous = _state;
            _state = next;
        }

        var handler = StateChanged;
        if (handler is null)
        {
            return;
        }

        _inHandler = true;
        try
        {
            handler(this, new PlaybackStateChange(previous, next));
        }
        catch (Exception ex)
        {
            Raise(new MediaToolkitNetException(Backend, $"a StateChanged handler threw: {ex.Message}", 0));
        }
        finally
        {
            _inHandler = false;
        }
    }

    private void Raise(MediaToolkitNetException ex) => SafeEvents.RaiseFailed(Failed, this, ex);

    // Takes the pipeline down: NULL first, which ends the streaming threads, and only then the handlers and the objects.
    private void Close()
    {
        var pipeline = _pipeline;
        _pipeline = null;
        _root = null;
        if (pipeline is not null)
        {
            try
            {
                pipeline.Stop();
            }
            catch (MediaToolkitNetException)
            {
                // Going to NULL fails only for a pipeline already broken, which is being thrown away.
            }
        }

        foreach (var sink in _sinks)
        {
            sink.Dispose();
        }

        _sinks.Clear();
        pipeline?.Dispose();
        lock (_gate)
        {
            if (_state != PlaybackState.Closed && !_disposed)
            {
                _state = PlaybackState.Closed;
            }
        }
    }
}
