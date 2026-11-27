#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.GStreamer.Transcoding;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>The prober, asked often enough for what GstDiscoverer only sometimes leaves out.</summary>
[SupportedOSPlatform("linux")]
public class GStreamerProberTests
{
    // GstDiscoverer finished without any tags for a subtitle stream about once in two hundred reads.
    [GStreamerFact]
    public void EveryReadHasTheLanguageOfEverySubtitleTrack()
    {
        using var media = new TestMedia();
        var cue = media.PathTo("cue.srt");
        File.WriteAllText(cue, "1\n00:00:00,100 --> 00:00:00,900\nhello\n");
        var file = media.PathTo("two-subtitles.mkv");
        FfmpegProgram.Run(
            $"-v error -f lavfi -i testsrc=size=64x48:rate=25:duration=2 -f lavfi -i sine=duration=2 -i {cue} " +
            $"-map 0 -map 1 -map 2 -map 2 -metadata:s:s:0 language=eng -metadata:s:s:1 language=fre -c:v mjpeg -c:a flac -c:s srt {file}");
        var prober = new GStreamerProber();

        for (var read = 0; read < 1000; read++)
        {
            prober.Probe(file).OfKind(MediaStreamKind.Subtitle).Select(s => s.Language)
                .Should().Equal(["eng", "fre"], $"read {read} is of the same file as all the others");
        }
    }
}
