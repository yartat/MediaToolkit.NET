#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.GStreamer.Native;

namespace MediaToolkitNet.GStreamer;

/// <summary>What the mixer and the compositor share when an input leaves a running pipeline.</summary>
[SupportedOSPlatform("linux")]
internal static unsafe class GStreamerInputs
{
    /// <summary>Takes the chain of elements feeding one request pad out of a pipeline, upstream first.</summary>
    public static void Detach(GStreamerPipeline pipeline, GStreamerElement mixer, string pad, params ReadOnlySpan<GStreamerElement> chain)
    {
        var handle = mixer.GetStaticPad(pad);
        if (handle is not null)
        {
            // A thread of the chain may be waiting in the pad for room; flushing lets it go, or stopping it would wait for ever.
            Gst.gst_pad_send_event(handle, Gst.gst_event_new_flush_start());
            Gst.gst_object_unref(handle);
        }

        foreach (var element in chain)
        {
            Gst.gst_element_set_state(element.Handle, (int)GstState.Null);
        }

        if (handle is not null)
        {
            mixer.ReleasePad(pad);
        }

        foreach (var element in chain)
        {
            pipeline.Remove(element);
        }
    }
}
