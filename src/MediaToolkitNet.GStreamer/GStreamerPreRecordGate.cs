#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.GStreamer.Native;

namespace MediaToolkitNet.GStreamer;

/// <summary>
/// Holds back a branch at a <c>queue</c> that keeps only the latest stretch of the stream, so that what is recorded once
/// something happens begins a few seconds before it.
/// </summary>
/// <remarks>
/// Put the queue after the encoder, and what follows it must not wait for data to start (<c>async=false</c> on its sink).
/// Opening starts at the first key frame inside the window, so what comes out reaches back by the window less at most one
/// key frame interval. The encoded stretch is held in memory. The gate opens once.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class GStreamerPreRecordGate : IDisposable
{
    private readonly GStreamerElement _queue;
    private readonly TimeSpan _window;
    private readonly GStreamerProbe _watch;
    private readonly GStreamerProbe _block;
    private long _newest = -1;
    private bool _opened;

    /// <summary>Shuts the gate. Do it before the pipeline starts.</summary>
    /// <param name="queue">The <c>queue</c> element; its limits and leaking are set here.</param>
    /// <param name="window">How much of the stream to keep.</param>
    public GStreamerPreRecordGate(GStreamerElement queue, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        _queue = queue;
        _window = window;

        queue.SetValue("max-size-buffers", 0U);
        queue.SetValue("max-size-bytes", 0U);
        queue.SetValue("max-size-time", (ulong)(window.Ticks * GstConstants.NanosecondsPerTick));
        queue.Set("leaky", "downstream");

        _watch = queue.AddDataProbe("sink", (in GStreamerData data) =>
        {
            if (data.Timestamp is { } time)
            {
                Interlocked.Exchange(ref _newest, time.Ticks);
            }

            return GstPadProbeReturn.Ok;
        });
        _block = queue.AddProbe("src", GstPadProbeType.Block | GstPadProbeType.Buffer, _ => GstPadProbeReturn.Ok);
    }

    /// <summary>Lets the kept stretch out, followed by what comes after it.</summary>
    public void Open()
    {
        if (_opened)
        {
            return;
        }

        _opened = true;

        // The buffer that was waiting at the shut gate is as old as the gate; it and anything before a key frame goes.
        var newest = Interlocked.Read(ref _newest);
        var oldest = newest < 0 ? TimeSpan.Zero : new TimeSpan(newest) - _window - TimeSpan.FromMilliseconds(100);
        _queue.AddDataProbe("src", (in GStreamerData data) =>
            data.IsKeyFrame && !(data.Timestamp < oldest) ? GstPadProbeReturn.Remove : GstPadProbeReturn.Drop);
        _block.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _watch.Dispose();
        _block.Dispose();
    }
}
