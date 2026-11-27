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
/// Adds several audio inputs into one stream, with <c>audiomixer</c>: a microphone over a music track, the sound of two
/// sources recorded together. Each input is pushed block by block in its own format, which is converted and resampled to the
/// output's, and has a volume and a mute of its own.
/// </summary>
/// <remarks>
/// <para>
/// Inputs can be added and removed before or after <see cref="Start"/>; volume and mute can be changed while it runs and apply to
/// what is pushed after. A running mixer waits for every input it has, so push a new one from the time the others have reached.
/// Timestamps decide which blocks are added together, so push every input with the same clock. A sum above full scale clips
/// at the top, it does not wrap around; lower the volumes to keep headroom.
/// </para>
/// <para>
/// Mixed blocks are raised on a thread GStreamer owns and are valid for the call only; they can be written straight to a
/// <see cref="GStreamerRecorder"/>. A handler may dispose the mixer, which is then taken down from another thread.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerAudioMixer : IDisposable
{
    private const double MaxVolume = 10.0;

    [ThreadStatic]
    private static bool _inHandler;

    private readonly AudioFormat _output;
    // A removed input leaves its place empty, so the indices of the others stay what they were.
    private readonly List<(AudioFormat Format, double Volume)?> _inputs = [];
    private readonly List<GStreamerSource?> _sources = [];
    private GStreamerPipeline? _pipeline;
    private GStreamerElement? _mixer;
    private GStreamerSink? _sink;
    private volatile bool _delivering;
    private bool _disposed;

    /// <summary>Creates a mixer that puts out this format.</summary>
    /// <param name="output">The rate, channels and sample format of the mix; interleaved only.</param>
    /// <exception cref="ArgumentException">The format is not usable: no rate or channels, or planar samples.</exception>
    public GStreamerAudioMixer(AudioFormat output)
    {
        if (output.SampleRate <= 0 || output.Channels <= 0 || output.SampleFormat.IsPlanar())
        {
            throw new ArgumentException("The output needs a sample rate, channels and an interleaved sample format.", nameof(output));
        }

        _output = output;
        Gst.EnsureLoaded();
    }

    /// <summary>Raised for each mixed block, on a thread of GStreamer's, valid for the call only.</summary>
    public event AudioFrameHandler? FrameMixed;

    /// <summary>Raised when the pipeline fails; a handler that throws is reported here too.</summary>
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <summary>True between <see cref="Start"/> and <see cref="Dispose"/>.</summary>
    public bool IsRunning => _pipeline is not null;

    /// <summary>Adds an input, before or while the mixer runs.</summary>
    /// <param name="input">The format of the blocks that will be pushed for this input.</param>
    /// <param name="volume">Linear volume: 1 leaves it as it is, 0 silences it, at most 10.</param>
    /// <returns>The input's index, which the other calls take.</returns>
    public int AddInput(AudioFormat input, double volume = 1.0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (input.SampleRate <= 0 || input.Channels <= 0)
        {
            throw new ArgumentException("No valid audio format was supplied.", nameof(input));
        }

        CheckVolume(volume);
        var index = _inputs.Count;
        if (_pipeline is { } running)
        {
            Attach(running, index, input, volume);
        }
        else
        {
            _sources.Add(null);
        }

        _inputs.Add((input, volume));
        return index;
    }

    /// <summary>Takes an input away; the others keep their indices.</summary>
    /// <exception cref="ArgumentOutOfRangeException">There is no such input.</exception>
    public void RemoveInput(int input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Check(input);
        if (_pipeline is { } running)
        {
            var source = _sources[input]!;
            _sources[input] = null;
            GStreamerInputs.Detach(
                running, _mixer!, $"sink_{input}", source.Element,
                running.FindElement($"convert{input}")!, running.FindElement($"resample{input}")!, running.FindElement($"caps{input}")!);
        }

        _inputs[input] = null;
    }

    /// <summary>Builds the pipeline and starts it.</summary>
    /// <exception cref="InvalidOperationException">There is no input, or it has started already.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipeline is not null)
        {
            throw new InvalidOperationException("The mixer has started already.");
        }

        if (_inputs.All(i => i is null))
        {
            throw new InvalidOperationException("Add at least one input before Start.");
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

            for (var i = 0; i < _inputs.Count; i++)
            {
                if (_inputs[i] is { } input)
                {
                    var source = pipeline.GetSource($"in{i}");
                    source.SetFormat(input.Format);
                    _sources[i] = source;
                }
            }

            _delivering = true;
            pipeline.Play();
        }
        catch
        {
            _delivering = false;
            pipeline.Dispose();
            for (var i = 0; i < _sources.Count; i++)
            {
                _sources[i] = null;
            }

            _mixer = null;
            _sink = null;
            throw;
        }

        _pipeline = pipeline;
    }

    /// <summary>Pushes one block of an input.</summary>
    /// <returns>False when the pipeline takes no more (the input has ended, or it is flushing).</returns>
    /// <exception cref="InvalidOperationException">The mixer has not started.</exception>
    /// <exception cref="ArgumentOutOfRangeException">There is no such input.</exception>
    /// <exception cref="MediaToolkitNetException">The block is not in the format the input was added with.</exception>
    public bool Push(int input, in AudioFrame frame) => Source(input).PushAudio(frame);

    /// <summary>Sets an input's volume (0 to 10, 1 as it is) for what is pushed after.</summary>
    public void SetVolume(int input, double volume)
    {
        CheckVolume(volume);
        SetPad(input, "volume", volume);
    }

    /// <summary>Mutes or unmutes an input, keeping its volume.</summary>
    public void SetMute(int input, bool muted) => SetPad(input, "mute", muted);

    /// <summary>Ends one input; the others go on.</summary>
    public void EndOfStream(int input) => Source(input).EndOfStream();

    /// <summary>Ends every input, which ends the mix after what is already in the pipeline.</summary>
    public void EndOfStream()
    {
        for (var i = 0; i < _inputs.Count; i++)
        {
            if (_inputs[i] is not null)
            {
                EndOfStream(i);
            }
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

    private static void CheckVolume(double volume)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(volume, 0.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(volume, MaxVolume);
    }

    // Every input is converted to the output's format before the mixer, which adds what it is given.
    private string Describe()
    {
        var format = GstMediaFormat.CapsOf(_output);
        var description = new StringBuilder("audiomixer name=mix");
        for (var i = 0; i < _inputs.Count; i++)
        {
            if (_inputs[i] is { } input)
            {
                description.Append(CultureInfo.InvariantCulture, $" sink_{i}::volume={input.Volume:0.######}");
            }
        }

        description.Append(" ! ").Append(format).Append(" ! appsink name=out emit-signals=true sync=false max-buffers=64");
        for (var i = 0; i < _inputs.Count; i++)
        {
            if (_inputs[i] is not null)
            {
                // Named, so that the input can be taken out again.
                description.Append(CultureInfo.InvariantCulture,
                        $"  appsrc name=in{i} ! audioconvert name=convert{i} ! audioresample name=resample{i} ! capsfilter name=caps{i} caps=\"")
                    .Append(format).Append(CultureInfo.InvariantCulture, $"\" ! mix.sink_{i}");
            }
        }

        return description.ToString();
    }

    private GStreamerSource Source(int input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipeline is null)
        {
            throw new InvalidOperationException("Call Start first.");
        }

        Check(input);
        return _sources[input]!;
    }

    private void SetPad(int input, string property, object value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mixer = _mixer ?? throw new InvalidOperationException("Call Start first.");
        Check(input);
        mixer.SetPadValue($"sink_{input}", property, value);
    }

    private void Check(int input)
    {
        if (input < 0 || input >= _inputs.Count || _inputs[input] is null)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input, "There is no such input.");
        }
    }

    private void Attach(GStreamerPipeline pipeline, int index, AudioFormat format, double volume)
    {
        var element = pipeline.Add("appsrc", $"in{index}");
        var convert = pipeline.Add("audioconvert", $"convert{index}");
        var resample = pipeline.Add("audioresample", $"resample{index}");
        var caps = pipeline.Add("capsfilter", $"caps{index}");
        caps.Set("caps", GstMediaFormat.CapsOf(_output));
        var source = pipeline.GetSource($"in{index}");
        source.SetFormat(format);

        var pad = $"sink_{index}";
        element.Link(convert);
        convert.Link(resample);
        resample.Link(caps);
        caps.Link(_mixer!, "src", pad);
        _mixer!.SetPadValue(pad, "volume", volume);
        foreach (var added in new[] { caps, resample, convert, element })
        {
            added.SyncStateWithPipeline();
        }

        _sources.Add(source);
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
            sink.TryPullAudio(TimeSpan.Zero, (in AudioFrame frame) => FrameMixed?.Invoke(in frame));
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
}
