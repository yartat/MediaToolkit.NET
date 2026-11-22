#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Mpv;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>mpv's <c>screenshot-raw</c>: the picture as mpv shows it, taken into memory.</summary>
public class MpvScreenshotTests
{
    [MpvFact]
    public void WithoutAFrameThereIsNothingToTake()
    {
        using var player = HeadlessPlayer();

        var take = () => player.TakeScreenshot();

        take.Should().Throw<MediaToolkitNetException>().WithMessage("*mpv_command_node*");
    }

    [MpvFact]
    public void TheVideoIsTakenAtItsOwnSize()
    {
        using var media = new TestMedia();
        using var player = HeadlessPlayer();
        var shot = Take(player, media, MpvScreenshotContent.Video, MpvScreenshotFormat.Bgra);

        (shot.Width, shot.Height).Should().Be((320, 240));
        shot.Stride.Should().BeGreaterThanOrEqualTo(320 * 4);
        shot.BytesPerPixel.Should().Be(4);
        shot.Data.Length.Should().BeGreaterThanOrEqualTo(239 * shot.Stride + 320 * 4);
        shot.GetRow(100).Length.Should().Be(320 * 4);
        // The first frame is almost black and the frames get brighter (the recorder writes a grey ramp).
        shot.GetRow(100)[0].Should().BeLessThan(40);

        player.Seek(TimeSpan.FromSeconds(0.8));
        var brightness = 0;
        for (var i = 0; i < 100 && brightness < 120; i++)
        {
            Thread.Sleep(50);
            brightness = player.TakeScreenshot(MpvScreenshotContent.Video).GetRow(100)[0];
        }

        brightness.Should().BeGreaterThan(120, "a frame from later in the clip is lighter");
    }

    [MpvFact]
    public void BgraAndRgbaAreTheSamePictureWithRedAndBlueSwapped()
    {
        using var media = new TestMedia();
        using var player = HeadlessPlayer();
        var bgra = Take(player, media, MpvScreenshotContent.Video, MpvScreenshotFormat.Bgra);
        var rgba = player.TakeScreenshot(MpvScreenshotContent.Video, MpvScreenshotFormat.Rgba);

        var pixelsChecked = 0;
        for (var y = 0; y < bgra.Height; y += 17)
        {
            var b = bgra.GetRow(y);
            var r = rgba.GetRow(y);
            for (var x = 0; x < bgra.Width; x += 13)
            {
                var i = x * 4;
                (r[i], r[i + 1], r[i + 2]).Should().Be((b[i + 2], b[i + 1], b[i]), $"pixel {x},{y}");
                pixelsChecked++;
            }
        }

        pixelsChecked.Should().BeGreaterThan(100);
    }

    [MpvFact]
    public void Rgba64HasEightBytesPerPixel()
    {
        using var media = new TestMedia();
        using var player = HeadlessPlayer();
        var shot = Take(player, media, MpvScreenshotContent.Video, MpvScreenshotFormat.Rgba64);

        shot.BytesPerPixel.Should().Be(8);
        shot.Stride.Should().BeGreaterThanOrEqualTo(320 * 8);
        shot.GetRow(0).Length.Should().Be(320 * 8);
    }

    private static MpvScreenshot Take(MpvPlayer player, TestMedia media, MpvScreenshotContent content, MpvScreenshotFormat format)
    {
        player.Open(media.WriteMovie("movie.mkv"));

        // The first frame is on screen once mpv has its parameters.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (player.GetPropertyNode("video-params") is null && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        return player.TakeScreenshot(content, format);
    }

    private static MpvPlayer HeadlessPlayer()
    {
        var player = new MpvPlayer();
        player.Options["vo"] = "null";
        player.Options["ao"] = "null";
        player.Options["pause"] = "yes";
        return player;
    }
}
