#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using FluentAssertions;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg.Transcoding;
using MediaToolkitNet.GStreamer.Transcoding;
using MediaToolkitNet.Mpv.Transcoding;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// AUDIT.md DL-1 and DL-2: a transcoder must never destroy a file it was not
/// asked to replace. Each case runs against all three backends.
/// </summary>
public class AuditDataLossTests
{
    // ------------------------------------------------------------------ DL-1

    [FFmpegFact]
    public Task FFmpegLeavesTheInputAloneWhenTheOutputIsALinkToIt() => InputSurvivesLinkedOutput(new FFmpegTranscoder());

    [GStreamerFact]
    [SupportedOSPlatform("linux")]
    public Task GStreamerLeavesTheInputAloneWhenTheOutputIsALinkToIt() => InputSurvivesLinkedOutput(new GStreamerTranscoder());

    [MpvFact]
    public Task MpvLeavesTheInputAloneWhenTheOutputIsALinkToIt() => InputSurvivesLinkedOutput(new MpvTranscoder());

    private static async Task InputSurvivesLinkedOutput(IMediaTranscoder transcoder)
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var before = File.ReadAllBytes(input);
        var alias = media.PathTo("alias.mkv");
        try
        {
            File.CreateSymbolicLink(alias, input);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // this machine may not create links; the test cannot run here
        }

        try
        {
            await transcoder.RunAsync(new TranscodeRequest(alias) { Inputs = [input], Streams = [VideoStream()] });
        }
        catch (Exception)
        {
            // Refusing the job is a correct answer; destroying the input is not.
        }

        File.ReadAllBytes(input).Should().Equal(before, "the output path is a link to the input");
    }

    // ------------------------------------------------------------------ DL-2

    [FFmpegFact]
    public Task FFmpegKeepsAnExistingOutputWhenTheJobFails() => ExistingOutputSurvives(new FFmpegTranscoder(), "ffmpeg");

    [GStreamerFact]
    [SupportedOSPlatform("linux")]
    public Task GStreamerKeepsAnExistingOutputWhenTheJobIsRefused() => ExistingOutputSurvives(new GStreamerTranscoder(), "gstreamer");

    [MpvFact]
    public Task MpvKeepsAnExistingOutputWhenTheJobFails() => ExistingOutputSurvives(new MpvTranscoder(), "mpv");

    private static async Task ExistingOutputSurvives(IMediaTranscoder transcoder, string backend)
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");
        var output = media.PathTo("keep.mkv");
        File.WriteAllText(output, "precious bytes");

        var request = new TranscodeRequest(output)
        {
            Inputs = [input],
            Streams =
            [
                OutputStream.Video(
                    StreamSource.First(MediaStreamKind.Video),
                    new VideoOutputSettings(MediaCodec.Mjpeg)
                    {
                        Filters = [new VideoFilter.Custom(backend, "no_such_filter_xyz")],
                    }),
            ],
        };

        var act = () => transcoder.RunAsync(request);
        await act.Should().ThrowAsync<Exception>("the filter does not exist");

        File.Exists(output).Should().BeTrue("the job never opened the output");
        File.ReadAllText(output).Should().Be("precious bytes");
    }

    private static OutputStream VideoStream() => OutputStream.Video(
        StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings(MediaCodec.Mjpeg));
}
