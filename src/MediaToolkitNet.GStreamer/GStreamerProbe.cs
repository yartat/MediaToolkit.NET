#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.GStreamer.Native;

namespace MediaToolkitNet.GStreamer;

/// <summary>What a probe is told about the thing passing its pad.</summary>
/// <param name="Type">What it is: a buffer, a list of them, an event; or only that the pad is idle or blocked.</param>
/// <param name="Event">The kind of the event, or <see langword="null"/> when it is not one.</param>
/// <param name="Timestamp">The buffer's presentation time, or <see langword="null"/> when there is none.</param>
/// <param name="Size">The buffer's size in bytes; zero for anything else.</param>
public readonly record struct GStreamerProbeInfo(GstPadProbeType Type, GstEventType? Event, TimeSpan? Timestamp, long Size)
{
    /// <summary>For a custom event: what it carries.</summary>
    public GStreamerStructure? Details { get; init; }
}

/// <summary>
/// Handles what passes a probed pad, on the thread that carries it, usually a streaming thread, which waits for the
/// handler to return.
/// </summary>
/// <param name="info">What is passing.</param>
/// <returns>
/// <see cref="GstPadProbeReturn.Ok"/> to let it go on (a blocking probe keeps the pad blocked),
/// <see cref="GstPadProbeReturn.Drop"/> to discard it, <see cref="GstPadProbeReturn.Pass"/> to let this one through a
/// blocked pad, or <see cref="GstPadProbeReturn.Remove"/> to take the probe off.
/// </returns>
public delegate GstPadProbeReturn GStreamerProbeHandler(GStreamerProbeInfo info);

/// <summary>Handles one buffer passing a pad, with its bytes, which are valid only for the call.</summary>
/// <param name="data">The buffer's bytes with their times and flags.</param>
/// <returns><see cref="GstPadProbeReturn.Ok"/>, <see cref="GstPadProbeReturn.Drop"/> or <see cref="GstPadProbeReturn.Remove"/>.</returns>
public delegate GstPadProbeReturn GStreamerDataProbeHandler(in GStreamerData data);

/// <summary>A probe on a pad. Disposing it takes it off.</summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerProbe : IDisposable
{
    private readonly GStreamerProbeHandler? _handler;
    private readonly GStreamerDataProbeHandler? _data;
    private readonly Action<Exception> _failed;
    private readonly Lock _gate = new();
    private void* _pad;
    private nuint _id;
    private bool _removed;

    private GStreamerProbe(GStreamerProbeHandler? handler, GStreamerDataProbeHandler? data, Action<Exception> failed)
    {
        _handler = handler;
        _data = data;
        _failed = failed;
    }

    // Takes over the caller's reference to the pad.
    internal static GStreamerProbe Install(void* pad, GstPadProbeType mask, GStreamerProbeHandler handler, Action<Exception> failed) =>
        Add(pad, mask, new GStreamerProbe(handler, null, failed));

    internal static GStreamerProbe Install(void* pad, GStreamerDataProbeHandler handler, Action<Exception> failed) =>
        Add(pad, GstPadProbeType.Buffer, new GStreamerProbe(null, handler, failed));

    private static GStreamerProbe Add(void* pad, GstPadProbeType mask, GStreamerProbe probe)
    {
        probe._pad = pad;
        var handle = GCHandle.Alloc(probe);

        // An idle probe can run, and even remove itself, before this call returns; the id is then zero.
        var id = Gst.gst_pad_add_probe(pad, (int)mask, &OnProbe, (void*)GCHandle.ToIntPtr(handle), (void*)(delegate* unmanaged[Cdecl]<void*, void>)&OnDestroyed);
        lock (probe._gate)
        {
            probe._id = id;
        }

        return probe;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        void* pad;
        nuint id;
        lock (_gate)
        {
            pad = _pad;
            id = _removed ? 0 : _id;
            _pad = null;
            _removed = true;
        }

        // Outside the lock: removing waits for nothing, but it runs the destroy notifier.
        if (pad is not null)
        {
            if (id != 0)
            {
                Gst.gst_pad_remove_probe(pad, id);
            }

            Gst.gst_object_unref(pad);
        }
    }

    private GstPadProbeReturn Invoke(void* pad, void* info)
    {
        lock (_gate)
        {
            if (_removed)
            {
                return GstPadProbeReturn.Ok;
            }
        }

        var type = (GstPadProbeType)(*(int*)info);
        if (_data is not null)
        {
            return Settle(InvokeData(pad, info), type);
        }

        GstEventType? kind = null;
        GStreamerStructure? details = null;
        TimeSpan? timestamp = null;
        long size = 0;
        if ((type & GstPadProbeType.Buffer) != 0)
        {
            var buffer = Gst.gst_pad_probe_info_get_buffer(info);
            if (buffer is not null)
            {
                var pts = GstLayout.PtsOfBuffer(buffer);
                timestamp = pts == GstConstants.ClockTimeNone ? null : new TimeSpan((long)pts / GstConstants.NanosecondsPerTick);
                size = (long)Gst.gst_buffer_get_size(buffer);
            }
        }
        else if ((type & (GstPadProbeType.EventDownstream | GstPadProbeType.EventUpstream | GstPadProbeType.EventFlush)) != 0)
        {
            var passing = Gst.gst_pad_probe_info_get_event(info);
            if (passing is not null)
            {
                kind = GstLayout.TypeOfEvent(passing);
                if (kind is GstEventType.CustomUpstream or GstEventType.CustomDownstream
                    or GstEventType.CustomDownstreamOutOfBand or GstEventType.CustomBoth)
                {
                    var carried = Gst.gst_event_get_structure(passing);
                    details = carried is null ? null : GStreamerStructure.From(carried);
                }
            }
        }

        GstPadProbeReturn answer;
        try
        {
            answer = _handler!(new GStreamerProbeInfo(type, kind, timestamp, size) { Details = details });
        }
        catch (Exception ex)
        {
            _failed(ex);

            // A pad left blocked by a handler that failed would stop the stream for good.
            answer = (type & (GstPadProbeType.Block | GstPadProbeType.Idle)) != 0 ? GstPadProbeReturn.Remove : GstPadProbeReturn.Ok;
        }

        return Settle(answer, type);
    }

    private GstPadProbeReturn Settle(GstPadProbeReturn answer, GstPadProbeType type)
    {
        if (answer == GstPadProbeReturn.Remove)
        {
            lock (_gate)
            {
                _removed = true;
            }
        }

        return answer;
    }

    private GstPadProbeReturn InvokeData(void* pad, void* info)
    {
        var buffer = Gst.gst_pad_probe_info_get_buffer(info);
        if (buffer is null)
        {
            return GstPadProbeReturn.Ok;
        }

        var map = stackalloc byte[GstLayout.MapInfoSize];
        new Span<byte>(map, GstLayout.MapInfoSize).Clear();
        if (Gst.gst_buffer_map(buffer, map, (int)GstMapFlags.Read) == 0)
        {
            return GstPadProbeReturn.Ok;
        }

        var caps = Gst.gst_pad_get_current_caps(pad);
        try
        {
            return _data!(GStreamerData.Of(buffer, map, caps));
        }
        catch (Exception ex)
        {
            _failed(ex);
            return GstPadProbeReturn.Ok;
        }
        finally
        {
            if (caps is not null)
            {
                Gst.gst_mini_object_unref(caps);
            }

            Gst.gst_buffer_unmap(buffer, map);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnProbe(void* pad, void* info, void* data)
    {
        try
        {
            return (int)((GStreamerProbe)GCHandle.FromIntPtr((nint)data).Target!).Invoke(pad, info);
        }
        catch
        {
            // Nothing may throw into GStreamer.
            return (int)GstPadProbeReturn.Ok;
        }
    }

    // GDestroyNotify: GStreamer has let go of the probe, so its handle can go.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDestroyed(void* data)
    {
        try
        {
            GCHandle.FromIntPtr((nint)data).Free();
        }
        catch
        {
            // A handle freed twice has nothing left to free.
        }
    }
}
