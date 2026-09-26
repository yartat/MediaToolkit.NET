#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.GStreamer.Native;

namespace MediaToolkitNet.GStreamer.Transcoding;

/// <summary>
/// Holds back everything a branch produces until the seek that trims the job
/// has flushed it, so nothing downstream sees data from before the start.
/// </summary>
/// <remarks>
/// <para>
/// A GStreamer pipeline can only seek once its demuxers are running, and by then
/// they have pushed data. Seeking the whole pipeline after preroll is the usual
/// answer, but a muxer that has already written its header does not survive
/// the flush: <c>mp4mux</c> loses its <c>moov</c> and <c>matroskamux</c> refuses
/// the seek. So the seek goes to each <c>parsebin</c> instead, and a probe on
/// the first pad of every branch drops buffers, the end-of-stream that a short
/// input reaches before the seek, and the flush itself. The flush-stop is the
/// marker: whatever follows it on the pad comes from after the seek, so the
/// gate opens there and stays open.
/// </para>
/// <para>
/// Stream-start, caps, segment and tag events pass throughout, so decoders,
/// encoders and the muxer negotiate as usual. The callbacks run on GStreamer's streaming threads and
/// touch nothing but an unmanaged flag, so nothing managed can throw across the
/// boundary; the flag outlives every callback because it is freed only after
/// the pipeline has gone to NULL.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed unsafe class PipelineGate : IDisposable
{
    private int* _open;

    private PipelineGate(int* open) => _open = open;

    /// <summary>True once the flush of the seek has passed.</summary>
    public bool IsOpen => Volatile.Read(ref *_open) != 0;

    /// <summary>Installs a closed gate on the source pad of an element.</summary>
    /// <exception cref="MediaToolkitNetException">The element has no <c>src</c> pad.</exception>
    public static PipelineGate Install(GStreamerElement element)
    {
        var pad = element.GetStaticPad("src");
        if (pad is null)
        {
            throw new MediaToolkitNetException(GstConstants.BackendName, $"{element.Name} has no src pad to gate", 0);
        }

        var open = (int*)NativeMemory.AllocZeroed(sizeof(int));
        try
        {
            Gst.gst_pad_add_probe(
                pad,
                (int)(GstPadProbeType.Buffer | GstPadProbeType.BufferList),
                &OnBuffer,
                open,
                null);

            Gst.gst_pad_add_probe(
                pad,
                (int)(GstPadProbeType.EventDownstream | GstPadProbeType.EventFlush),
                &OnEvent,
                open,
                null);
        }
        finally
        {
            Gst.gst_object_unref(pad);
        }

        return new PipelineGate(open);
    }

    /// <summary>Frees the flag. Call only once the pipeline is in NULL, when no callback can run.</summary>
    public void Dispose()
    {
        if (_open is not null)
        {
            NativeMemory.Free(_open);
            _open = null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnBuffer(void* pad, void* info, void* open) =>
        (int)(Volatile.Read(ref *(int*)open) != 0 ? GstPadProbeReturn.Ok : GstPadProbeReturn.Drop);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnEvent(void* pad, void* info, void* open)
    {
        var flag = (int*)open;
        if (Volatile.Read(ref *flag) != 0)
        {
            return (int)GstPadProbeReturn.Ok;
        }

        var evt = Gst.gst_pad_probe_info_get_event(info);
        if (evt is null)
        {
            return (int)GstPadProbeReturn.Ok;
        }

        switch (GstLayout.TypeOfEvent(evt))
        {
            case GstEventType.FlushStop:
                Volatile.Write(ref *flag, 1);
                return (int)GstPadProbeReturn.Drop;
            case GstEventType.FlushStart:
            case GstEventType.Eos:
                return (int)GstPadProbeReturn.Drop;
            default:
                return (int)GstPadProbeReturn.Ok;
        }
    }
}
