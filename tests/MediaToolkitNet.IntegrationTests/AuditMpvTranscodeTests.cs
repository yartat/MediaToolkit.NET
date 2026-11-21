#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using FluentAssertions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.Mpv.Transcoding;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>AUDIT.md BUG-3 for mpv: an encoder option nobody recognised must not vanish.</summary>
public class AuditMpvTranscodeTests
{
    [MpvFact]
    public async Task AnEncoderOptionNobodyUnderstoodIsReported()
    {
        using var media = new TestMedia();
        var input = media.WriteMovie("movie.mkv");

        TranscodeResult? result = null;
        Exception? failure = null;
        try
        {
            result = await new MpvTranscoder().RunAsync(new TranscodeRequest(media.PathTo("out.mkv"))
            {
                Inputs = [input],
                Streams =
                [
                    OutputStream.Video(
                        StreamSource.First(MediaStreamKind.Video),
                        new VideoOutputSettings(MediaCodec.Mjpeg)
                        {
                            Options = new Dictionary<string, string> { ["no_such_option"] = "1" },
                        }),
                ],
            });
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Either the job is refused naming the option, or it runs and warns about it.
        var said = failure?.Message ?? string.Join("; ", result!.Warnings.Select(w => w.Message));
        said.Should().Contain("no_such_option");
    }
}
