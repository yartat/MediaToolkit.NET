#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using FluentAssertions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg.Transcoding;
using Xunit;

namespace MediaToolkitNet.Tests;

/// <summary>
/// How settings become libavfilter chains, which FFmpeg and mpv both read.
/// </summary>
/// <remarks>
/// The escaping expectations are what a burn-in from a file named
/// <c>odd name, with: colon's.srt</c> needed when it was run against FFmpeg
/// 7.1.5; these tests keep the function from drifting away from it.
/// </remarks>
public class FilterChainsTests
{
    [Theory]
    [InlineData("plain.srt", @"plain.srt")]
    [InlineData("a:b.srt", @"a\\:b.srt")]
    [InlineData("x,y.srt", @"x\,y.srt")]
    [InlineData("it's.srt", @"it\\\'s.srt")]
    [InlineData(@"C:\subs\a.srt", @"C\\:\\\\subs\\\\a.srt")]
    [InlineData("[1].srt", @"\[1\].srt")]
    public void AValueIsEscapedForTheOptionParserAndThenForTheGraphParser(string value, string expected) =>
        FilterChains.EscapeOptionValue(value).Should().Be(expected);

    [Fact]
    public void TheTypedFiltersBecomeLibavfilterFilters()
    {
        FilterChains.Describe(new VideoFilter.Scale(640, -1)).Should().Be("scale=640:-2");
        FilterChains.Describe(new VideoFilter.Crop(100, 50, 10, 20)).Should().Be("crop=100:50:10:20");
        FilterChains.Describe(new VideoFilter.FrameRate(new Rational(30000, 1001))).Should().Be("fps=30000/1001");
        FilterChains.Describe(new VideoFilter.Rotate(90)).Should().Be("transpose=clock");
        FilterChains.Describe(new VideoFilter.Rotate(270)).Should().Be("transpose=cclock");
        FilterChains.Describe(new VideoFilter.Rotate(180)).Should().Be("hflip,vflip");
        FilterChains.Describe(new VideoFilter.Flip(Horizontal: true, Vertical: false)).Should().Be("hflip");
        FilterChains.Describe(new VideoFilter.Deinterlace()).Should().Be("bwdif");
        FilterChains.Describe(new AudioFilter.Volume(0.5)).Should().Be("volume=0.5");
    }

    [Fact]
    public void ACustomFilterIsUsedOnlyByTheBackendItWasWrittenFor()
    {
        FilterChains.Describe(new VideoFilter.Custom("ffmpeg", "eq=contrast=2")).Should().Be("eq=contrast=2");
        FilterChains.Describe(new VideoFilter.Custom("gstreamer", "videobalance")).Should().BeNull();
        FilterChains.Describe(new VideoFilter.Custom("mpv", "eq=contrast=2"), alsoAccept: "mpv").Should().Be("eq=contrast=2");
    }

    [Fact]
    public void SizeAndRateFollowTheFiltersInOrder()
    {
        var settings = new VideoOutputSettings
        {
            Width = 320,
            FrameRate = new Rational(25, 1),
            Filters = [new VideoFilter.Deinterlace()],
        };

        FilterChains.Join(FilterChains.VideoSettings(settings)).Should().Be("bwdif,scale=320:-2,fps=25/1");
    }

    [Theory]
    [InlineData(@"0,0,Default,,0,0,0,,Hello\Nworld", "Hello\nworld")]
    [InlineData(@"3,0,Default,,0,0,0,,{\i1}Hi{\i0} there", "Hi there")]
    [InlineData(@"0,0,Default,,0,0,0,,one, two, three", "one, two, three")]
    [InlineData("not an event", "not an event")]
    public void TheTextOfAnAssEventIsTheLastFieldWithoutStyling(string assEvent, string expected) =>
        SubtitleCue.TextOfAssEvent(assEvent).Should().Be(expected);
}
