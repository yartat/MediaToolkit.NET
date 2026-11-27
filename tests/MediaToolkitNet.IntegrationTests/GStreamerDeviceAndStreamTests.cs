#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.GStreamer;
using MediaToolkitNet.GStreamer.Native;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>A fact that is skipped unless GStreamer reports at least one device.</summary>
public sealed class GStreamerDeviceFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="GStreamerDeviceFactAttribute"/> class.</summary>
    public GStreamerDeviceFactAttribute()
    {
        Skip = new GStreamerOnlyFactAttribute().Skip;
        if (Skip is null && OperatingSystem.IsLinux())
        {
            using var monitor = new GStreamerDeviceMonitor();
            Skip = monitor.GetDevices().Count == 0 ? "GStreamer reports no device here." : null;
        }
    }
}

/// <summary>The device monitor, stream collections, and what the bus mask lets through.</summary>
[SupportedOSPlatform("linux")]
public class GStreamerDeviceAndStreamTests
{
    [GStreamerFact]
    public void AStreamCollectionListsTheStreamsOfAFile()
    {
        using var media = new TestMedia();
        var file = media.WriteVideoAndAudio("two.mkv");
        using var pipeline = GStreamerPipeline.Parse($"filesrc location={file} ! decodebin3 ! fakesink");
        var collections = new List<IReadOnlyList<GStreamerStream>>();
        pipeline.Bus.MessageReceived += (_, m) =>
        {
            if (m.Type == GstMessageType.StreamCollection)
            {
                lock (collections)
                {
                    collections.Add(m.Streams);
                }
            }
        };

        pipeline.Pause();

        SpinWait.SpinUntil(() => { lock (collections) { return collections.Any(c => c.Count == 2); } }, TimeSpan.FromSeconds(5))
            .Should().BeTrue("the file has a picture and a sound");
        lock (collections)
        {
            var streams = collections.First(c => c.Count == 2);
            streams.Select(s => s.Type).Should().BeEquivalentTo([GstStreamType.Video, GstStreamType.Audio]);
            streams.Should().OnlyContain(s => s.Id.Length > 0 && s.Caps != null);
        }
    }

    [GStreamerOnlyFact]
    public void AMaskOfWarningsLetsNothingElseThrough()
    {
        using var pipeline = GStreamerPipeline.Parse(
            "videotestsrc num-buffers=5 ! video/x-raw,width=32,height=32 ! jpegenc ! decodebin3 ! fakesink");
        var seen = new List<GstMessageType>();
        pipeline.Bus.Mask = GstMessageType.Warning;
        pipeline.Bus.MessageReceived += (_, m) =>
        {
            lock (seen)
            {
                seen.Add(m.Type);
            }
        };

        pipeline.Play();
        pipeline.WaitForCompletion(TimeSpan.FromSeconds(10)).Should().BeTrue();
        Thread.Sleep(200);

        lock (seen)
        {
            seen.Where(t => t != GstMessageType.Warning).Should().BeEmpty();
        }
    }

    [GStreamerOnlyFact]
    public void ASelectionOfStreamsIsNotTakenForTheEnd()
    {
        using var pipeline = GStreamerPipeline.Parse(
            "videotestsrc is-live=true ! video/x-raw,width=32,height=32 ! jpegenc ! decodebin3 ! fakesink");
        pipeline.Play();

        pipeline.WaitForCompletion(TimeSpan.FromSeconds(2)).Should().BeFalse("a live source never ends by itself");
    }

    [GStreamerDeviceFact]
    public void TheMonitorListsDevicesAndMakesElementsOfThem()
    {
        using var monitor = new GStreamerDeviceMonitor();

        var devices = monitor.GetDevices();

        devices.Should().OnlyContain(d => d.DisplayName.Length > 0 && d.DeviceClass.Contains('/'));
        using var pipeline = GStreamerPipeline.Create();
        var element = pipeline.Add(devices[0], "device");
        element.Name.Should().Be("device");
    }

    [GStreamerDeviceFact]
    public void AFilterOfClassesKeepsTheOthersOut()
    {
        using var sinks = new GStreamerDeviceMonitor("Audio/Sink");
        using var nothing = new GStreamerDeviceMonitor("Video/Nonexistent");

        sinks.Start().Should().BeTrue();

        sinks.GetDevices().Should().OnlyContain(d => d.DeviceClass.StartsWith("Audio/Sink", StringComparison.Ordinal));
        nothing.GetDevices().Should().BeEmpty();
        sinks.Stop();
    }
}
