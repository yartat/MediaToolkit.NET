#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg.Native;

namespace MediaToolkitNet.FFmpeg.Transcoding;

/// <summary>
/// The stretch of one input that the output covers, in that input's own
/// timestamps.
/// </summary>
/// <param name="Origin">Where the input's timestamps begin: its container start time.</param>
/// <param name="Start">How far past the origin the output starts.</param>
/// <param name="End">How far past the origin the output stops, if it stops early.</param>
internal readonly record struct TimeWindow(TimeSpan Origin, TimeSpan Start, TimeSpan? End)
{
    /// <summary>The input timestamp that becomes zero in the output.</summary>
    public TimeSpan From => Origin + Start;

    /// <summary>The input timestamp at which the output stops, if it stops early.</summary>
    public TimeSpan? To => End is { } end ? Origin + end : null;
}

/// <summary>
/// One output stream and everything between the input packets and the muxer
/// that produces it.
/// </summary>
internal abstract unsafe class StreamPipeline : IDisposable
{
    private static readonly AVRationalNative Ticks = new(1, (int)TimeSpan.TicksPerSecond);

    protected StreamPipeline(PlannedStream plan, FFmpegDemuxer demuxer, TimeWindow window)
    {
        Plan = plan;
        Demuxer = demuxer;
        Window = window;
        InputTimeBase = AbiLayout.TimeBaseOf(demuxer.StreamHandle(plan.Source.Index));
    }

    /// <summary>What this pipeline was planned to do.</summary>
    public PlannedStream Plan { get; }

    /// <summary>The input stream it reads.</summary>
    public int SourceIndex => Plan.Source.Index;

    /// <summary>The output stream it writes, once <see cref="Open"/> has run.</summary>
    public int OutputIndex { get; protected set; } = -1;

    /// <summary>True once the input has passed the end of the window, so no more packets are needed.</summary>
    public bool Done { get; protected set; }

    /// <summary>How far into the output this stream has written.</summary>
    public TimeSpan Written { get; private set; }

    /// <summary>A short description for messages, such as <c>output #1 (aac)</c>.</summary>
    public string Name => $"output #{Plan.OutputIndex} ({Plan.Source.CodecName})";

    protected FFmpegDemuxer Demuxer { get; }

    protected TimeWindow Window { get; }

    protected AVRationalNative InputTimeBase { get; }

    protected FFmpegMuxer Muxer { get; private set; } = null!;

    /// <summary>The output stream's time base, final once the header is written.</summary>
    protected AVRationalNative OutputTimeBase { get; private set; }

    /// <summary>Opens whatever decoding and encoding the stream needs, and adds it to the output.</summary>
    public void Open(FFmpegMuxer muxer)
    {
        Muxer = muxer;
        OutputIndex = OpenCore(muxer);
        CopyTags();
    }

    /// <summary>Called once the header is written, when the output time base is final.</summary>
    public virtual void Start() => OutputTimeBase = Muxer.TimeBaseOf(OutputIndex);

    /// <summary>Takes one packet of the input stream.</summary>
    public abstract void Process(AVPacketNative* packet);

    /// <summary>Drains whatever decoders, filters and encoders still hold.</summary>
    public virtual void Flush()
    {
    }

    /// <inheritdoc />
    public virtual void Dispose()
    {
    }

    /// <summary>Adds the output stream and returns its index.</summary>
    protected abstract int OpenCore(FFmpegMuxer muxer);

    /// <summary>Converts a time into input timestamps.</summary>
    protected long ToInput(TimeSpan time) => AV.av_rescale_q(time.Ticks, Ticks, InputTimeBase);

    /// <summary>Hands a packet, already in the output time base, to the muxer, which consumes it.</summary>
    protected void Write(AVPacketNative* packet)
    {
        packet->StreamIndex = OutputIndex;
        packet->Pos = -1;

        var timestamp = packet->Pts != AVConstants.NoPtsValue ? packet->Pts : packet->Dts;
        if (timestamp != AVConstants.NoPtsValue)
        {
            var end = FFmpegStreamFormats.ToTimeSpan(timestamp + Math.Max(packet->Duration, 0), OutputTimeBase);
            if (end > Written)
            {
                Written = end;
            }
        }

        Muxer.WritePacket(packet);
    }

    /// <summary>
    /// Carries the input stream's tags over, then applies the planned
    /// language, title and flags on top.
    /// </summary>
    /// <remarks>
    /// Matroska statistics tags (DURATION, BPS, NUMBER_OF_FRAMES and their
    /// like) describe the input's encoding and would be wrong for the output,
    /// so they are left behind, as mkvmerge does.
    /// </remarks>
    private void CopyTags()
    {
        if (!AbiLayout.MetadataLayoutVerified)
        {
            return;
        }

        var tags = AbiLayout.ReadDictionary(*AbiLayout.MetadataOfStream(Demuxer.StreamHandle(SourceIndex)));
        foreach (var (key, value) in tags)
        {
            if (!IsStatistic(key) && !key.Equals("encoder", StringComparison.OrdinalIgnoreCase))
            {
                Muxer.SetStreamTag(OutputIndex, key, value);
            }
        }

        Muxer.SetStreamTag(OutputIndex, "language", Plan.Language);
        Muxer.SetStreamTag(OutputIndex, "title", Plan.Title);
        Muxer.SetDisposition(OutputIndex, Plan.IsDefault, Plan.IsForced);
    }

    private static bool IsStatistic(string key) =>
        key.StartsWith("DURATION", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("BPS", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("NUMBER_OF_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("_STATISTICS_", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Copies packets without decoding them.</summary>
internal sealed unsafe class CopyPipeline : StreamPipeline
{
    private readonly bool _isVideo;
    private readonly bool _isSubtitle;
    private AVPacketNative* _packet;
    private long _from;
    private long? _to;
    private bool _sawKeyFrame;

    public CopyPipeline(PlannedStream plan, FFmpegDemuxer demuxer, TimeWindow window)
        : base(plan, demuxer, window)
    {
        _isVideo = plan.Source.Kind == MediaStreamKind.Video && !plan.Source.IsAttachedPicture;
        _isSubtitle = plan.Source.Kind == MediaStreamKind.Subtitle;
    }

    public override void Start()
    {
        base.Start();
        _from = ToInput(Window.From);
        _to = Window.To is { } to ? ToInput(to) : null;
    }

    public override void Process(AVPacketNative* packet)
    {
        var timestamp = packet->Pts != AVConstants.NoPtsValue ? packet->Pts : packet->Dts;
        if (_to is { } to && timestamp != AVConstants.NoPtsValue && timestamp >= to)
        {
            Done = true;
            return;
        }

        if (_isVideo)
        {
            // After a seek the demuxer lands on a key frame, but a stream can also
            // start with frames that refer to one before it; those cannot be
            // decoded by whoever plays the output, so they are left out.
            if (!_sawKeyFrame)
            {
                if ((packet->Flags & AVConstants.PacketFlagKey) == 0)
                {
                    return;
                }

                _sawKeyFrame = true;
            }
        }
        else if (timestamp != AVConstants.NoPtsValue && timestamp + Math.Max(packet->Duration, 0) < _from)
        {
            // Audio and subtitles that end before the start have nothing to show.
            return;
        }

        FFmpegError.Check(AV.av_packet_ref(_packet, packet), "av_packet_ref");

        // A subtitle already on screen at the start is cut to begin there. Left
        // as it was, its negative timestamp would make libavformat move the
        // whole output later to keep it, and every stream with it.
        if (_isSubtitle && _packet->Pts != AVConstants.NoPtsValue && _packet->Pts < _from)
        {
            _packet->Duration -= _from - _packet->Pts;
            _packet->Pts = _from;
            _packet->Dts = _from;
        }

        // And one still up at the end is cut there, so it does not stretch the output.
        if (_isSubtitle && _to is { } end && _packet->Pts != AVConstants.NoPtsValue
            && _packet->Duration > 0 && _packet->Pts + _packet->Duration > end)
        {
            _packet->Duration = end - _packet->Pts;
        }

        // Everything is shifted by the same amount, so the streams stay in sync;
        // video from the key frame before the start comes out slightly negative,
        // and libavformat moves the whole output up when the container needs it.
        if (_packet->Pts != AVConstants.NoPtsValue)
        {
            _packet->Pts -= _from;
        }

        if (_packet->Dts != AVConstants.NoPtsValue)
        {
            _packet->Dts -= _from;
        }

        AV.av_packet_rescale_ts(_packet, InputTimeBase, OutputTimeBase);
        Write(_packet);
    }

    public override void Dispose()
    {
        if (_packet is not null)
        {
            fixed (AVPacketNative** packet = &_packet)
            {
                AV.av_packet_free(packet);
            }
        }
    }

    protected override int OpenCore(FFmpegMuxer muxer)
    {
        _packet = (AVPacketNative*)FFmpegError.CheckAlloc(AV.av_packet_alloc(), "av_packet_alloc");
        return muxer.AddStream(Demuxer.CodecParameters(SourceIndex), InputTimeBase);
    }
}
