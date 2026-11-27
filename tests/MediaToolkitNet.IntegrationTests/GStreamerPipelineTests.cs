#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.GStreamer;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>Control of a whole pipeline. What a pipeline wrote is read back with FFmpeg.</summary>
[SupportedOSPlatform("linux")]
public class GStreamerPipelineTests
{
    [GStreamerFact]
    public void EndOfStreamLetsALivePipelineFinishItsFile()
    {
        using var media = new TestMedia();
        var path = media.PathTo("live.mov");
        using var pipeline = GStreamerPipeline.Parse(
            "videotestsrc is-live=true ! video/x-raw,width=64,height=48,framerate=25/1 ! videoconvert ! jpegenc ! " +
            $"qtmux ! filesink location={path}");
        pipeline.Play();
        Thread.Sleep(1000);

        pipeline.EndOfStream().Should().BeTrue();

        pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();
        pipeline.Stop();
        var info = new FFmpegProber().Probe(path);
        info.OfKind(MediaStreamKind.Video).Should().ContainSingle();
        info.Duration.Should().BeGreaterThan(TimeSpan.FromMilliseconds(500));
    }
}
