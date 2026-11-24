#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer;

/// <summary>
/// Lays several video layers over each other into one picture, with <c>compositor</c>: a camera in a corner of a screen
/// capture, a tiled wall, a logo with some transparency. Each layer is pushed frame by frame and has a place, a size, an alpha and
/// a stacking order; what comes out is <see cref="PixelFormat.Bgra32"/> at the size and rate of the output format.
/// </summary>
/// <remarks>
/// <para>
/// Layers are added before <see cref="Start"/>; their places, sizes, alphas and orders can be changed while it runs, and apply to the
/// frames pushed after. A layer's frames are scaled to its size by the compositor, so a layer need not be pushed at that size.
/// Timestamps decide which frames are laid together: push every layer with the same clock.
/// </para>
/// <para>
/// Composed frames are raised on a thread GStreamer owns and are valid for the call only; they can be pushed straight on, to a
/// <see cref="GStreamerRecorder"/> for one. A handler may dispose the compositor, which is then taken down from another thread.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerCompositor : IDisposable
{
    [ThreadStatic]
    private static bool _inHandler;

    private readonly VideoFormat _output;
    private readonly List<Layer> _layers = [];
    private readonly List<GStreamerSource> _sources = [];
    private readonly Lock _gate = new();
    private GStreamerPipeline? _pipeline;
    private GStreamerElement? _mixer;
    private GStreamerSink? _sink;
    private volatile bool _delivering;
    private bool _disposed;

    /// <summary>Creates a compositor whose picture has this size and frame rate.</summary>
    /// <param name="output">The size and rate of what comes out; the pixel format is always <c>Bgra32</c>.</param>
    public GStreamerCompositor(VideoFormat output)
    {
        if (output.Width <= 0 || output.Height <= 0 || output.FrameRate.Numerator <= 0 || output.FrameRate.Denominator <= 0)
        {
            throw new ArgumentException("The output needs a size and a frame rate.", nameof(output));
        }

        _output = output with { PixelFormat = PixelFormat.Bgra32 };
        Gst.EnsureLoaded();
    }

    /// <summary>Raised for each composed frame, on a thread of GStreamer's, valid for the call only.</summary>
    public event VideoFrameHandler? FrameComposed;

    /// <summary>Raised when the pipeline fails; a handler that throws is reported here too.</summary>
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <summary>True between <see cref="Start"/> and <see cref="Dispose"/>.</summary>
    public bool IsRunning => _pipeline is not null;

    /// <summary>Adds a layer, which must be done before <see cref="Start"/>.</summary>
    /// <param name="input">The format of the frames that will be pushed for this layer.</param>
    /// <param name="x">Where the layer's left edge is in the output.</param>
    /// <param name="y">Where its top edge is.</param>
    /// <param name="width">The width the layer is scaled to; the input's when not given.</param>
    /// <param name="height">The height it is scaled to; the input's when not given.</param>
    /// <param name="alpha">0 for fully transparent to 1 for opaque.</param>
    /// <returns>The layer's index, which the other calls take. A later layer is above an earlier one unless the z-order says otherwise.</returns>
    /// <exception cref="InvalidOperationException">The compositor has started.</exception>
    public int AddLayer(VideoFormat input, int x = 0, int y = 0, int? width = null, int? height = null, double alpha = 1.0)
    {
        if (_pipeline is not null)
        {
            throw new InvalidOperationException("The layers are fixed once the compositor has started.");
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!input.IsValid)
        {
            throw new ArgumentException("No valid video format was supplied.", nameof(input));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(alpha, 0.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(alpha, 1.0);
        _layers.Add(new Layer(input, x, y, width ?? input.Width, height ?? input.Height, alpha, _layers.Count));
        return _layers.Count - 1;
    }

    /// <summary>Builds the pipeline and starts it.</summary>
    /// <exception cref="InvalidOperationException">There is no layer, or it has started already.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipeline is not null)
        {
            throw new InvalidOperationException("The compositor has started already.");
        }

        if (_layers.Count == 0)
        {
            throw new InvalidOperationException("Add at least one layer before Start.");
        }

        var pipeline = GStreamerPipeline.Parse(Describe(), strict: true);
        try
        {
            _mixer = pipeline.FindElement("mix")!;
            _sink = pipeline.GetSink("out");
            _sink.Element.Failed += (_, e) => Raise(e);
            _mixer.Failed += (_, e) => Raise(e);
            _sink.Element.Connect("new-sample", _ => Deliver());
            pipeline.Bus.MessageReceived += OnMessage;

            for (var i = 0; i < _layers.Count; i++)
            {
                var source = pipeline.GetSource($"in{i}");
                source.SetFormat(_layers[i].Input);
                _sources.Add(source);
            }

            _delivering = true;
            pipeline.Play();
        }
        catch
        {
            _delivering = false;
            pipeline.Dispose();
            _sources.Clear();
            _mixer = null;
            _sink = null;
            throw;
        }

        _pipeline = pipeline;
    }

    /// <summary>Pushes one frame of a layer.</summary>
    /// <returns>False when the pipeline takes no more (the layer has ended, or it is flushing).</returns>
    /// <exception cref="InvalidOperationException">The compositor has not started.</exception>
    /// <exception cref="ArgumentOutOfRangeException">There is no such layer.</exception>
    /// <exception cref="MediaToolkitNetException">The frame is not in the format the layer was added with.</exception>
    public bool Push(int layer, in VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipeline is null)
        {
            throw new InvalidOperationException("Call Start before pushing frames.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(layer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(layer, _sources.Count);
        return _sources[layer].PushVideo(frame);
    }

    /// <summary>Moves a layer; it applies to the frames pushed after.</summary>
    public void SetPosition(int layer, int x, int y)
    {
        SetPad(layer, "xpos", x);
        SetPad(layer, "ypos", y);
    }

    /// <summary>Scales a layer to a new size.</summary>
    public void SetSize(int layer, int width, int height)
    {
        SetPad(layer, "width", width);
        SetPad(layer, "height", height);
    }

    /// <summary>Sets how opaque a layer is, 0 to 1.</summary>
    public void SetAlpha(int layer, double alpha)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(alpha, 0.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(alpha, 1.0);
        SetPad(layer, "alpha", alpha);
    }

    /// <summary>Sets a layer's stacking order: the higher is on top. Layers begin with their index.</summary>
    public void SetZOrder(int layer, int zOrder) => SetPad(layer, "zorder", (uint)Math.Max(zOrder, 0));

    /// <summary>Ends one layer: its last frame stops being laid and the others go on.</summary>
    public void EndOfStream(int layer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipeline is null)
        {
            throw new InvalidOperationException("Call Start first.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(layer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(layer, _sources.Count);
        _sources[layer].EndOfStream();
    }

    /// <summary>Ends every layer, which ends the picture after what is already in the pipeline.</summary>
    public void EndOfStream()
    {
        for (var i = 0; i < _sources.Count; i++)
        {
            EndOfStream(i);
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
        _delivering = false;
        if (_inHandler)
        {
            // Taking a pipeline to NULL waits for its streaming threads, and this is one of them.
            ThreadPool.QueueUserWorkItem(_ => Teardown());
        }
        else
        {
            Teardown();
        }
    }

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private string Describe()
    {
        var mixer = new StringBuilder("compositor name=mix background=black");
        for (var i = 0; i < _layers.Count; i++)
        {
            var l = _layers[i];
            mixer.Append(CultureInfo.InvariantCulture, $" sink_{i}::xpos={l.X} sink_{i}::ypos={l.Y} sink_{i}::width={l.Width} sink_{i}::height={l.Height}")
                .Append($" sink_{i}::alpha={Number(l.Alpha)} sink_{i}::zorder={l.ZOrder}");
        }

        var description = new StringBuilder(mixer.ToString());
        description.Append(CultureInfo.InvariantCulture,
            $" ! video/x-raw,format=BGRA,width={_output.Width},height={_output.Height},framerate={_output.FrameRate.Numerator}/{_output.FrameRate.Denominator} ! ")
            .Append("appsink name=out emit-signals=true sync=false max-buffers=16");
        for (var i = 0; i < _layers.Count; i++)
        {
            description.Append(CultureInfo.InvariantCulture, $"  appsrc name=in{i} ! mix.sink_{i}");
        }

        return description.ToString();
    }

    private void SetPad(int layer, string property, object value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mixer = _mixer ?? throw new InvalidOperationException("Call Start first.");
        ArgumentOutOfRangeException.ThrowIfNegative(layer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(layer, _layers.Count);
        mixer.SetPadValue($"sink_{layer}", property, value);
    }

    private void Deliver()
    {
        if (!_delivering || _sink is not { } sink)
        {
            return;
        }

        _inHandler = true;
        try
        {
            sink.TryPullVideo(TimeSpan.Zero, (in VideoFrame frame) => FrameComposed?.Invoke(in frame));
        }
        finally
        {
            _inHandler = false;
        }
    }

    private void OnMessage(object? sender, GStreamerMessage message)
    {
        if (message.Type == GstMessageType.Error && !_disposed)
        {
            Raise(new MediaToolkitNetException(GstConstants.BackendName, $"{message.Text} ({message.Debug})", 0));
        }
    }

    private void Raise(MediaToolkitNetException ex) => SafeEvents.RaiseFailed(Failed, this, ex);

    // NULL first, which ends the streaming threads, and only then the handlers and the objects.
    private void Teardown()
    {
        var pipeline = _pipeline;
        _pipeline = null;
        if (pipeline is null)
        {
            return;
        }

        try
        {
            pipeline.Stop();
        }
        catch (MediaToolkitNetException)
        {
            // Going to NULL fails only for a pipeline already broken, which is being thrown away.
        }

        _sink?.Dispose();
        pipeline.Dispose();
    }

    private sealed record Layer(VideoFormat Input, int X, int Y, int Width, int Height, double Alpha, int ZOrder);
}
