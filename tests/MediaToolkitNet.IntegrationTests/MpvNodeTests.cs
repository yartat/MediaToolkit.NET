#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Mpv;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>mpv's node API: structured property values and commands with named arguments or a result.</summary>
public class MpvNodeTests
{
    [Fact]
    public void ANodeKeepsItsShapeAndPrintsAsJson()
    {
        var node = MpvNode.Map(
        [
            new("name", "a\"b"),
            new("count", 3L),
            new("ratio", 0.5),
            new("on", true),
            new("list", MpvNode.Array(1L, "x", MpvNode.None)),
        ]);

        node.ToString().Should().Be("{\"name\":\"a\\\"b\",\"count\":3,\"ratio\":0.5,\"on\":true,\"list\":[1,\"x\",null]}");
        node["count"]!.AsInt64().Should().Be(3);
        node["ratio"]!.AsDouble().Should().Be(0.5);
        node["count"]!.AsDouble().Should().Be(3, "an integer reads as a number too");
        node["list"]![1]!.AsString().Should().Be("x");
        node["missing"].Should().BeNull();
        node["name"]!.AsInt64().Should().BeNull("a string is not an integer");
        node.Keys.Should().Equal("name", "count", "ratio", "on", "list");
    }

    [Fact]
    public void ACommandNodeCarriesItsNameUnderName()
    {
        var node = MpvNode.Command("screenshot-raw", ("flags", "video"));

        node.ToString().Should().Be("{\"name\":\"screenshot-raw\",\"flags\":\"video\"}");
    }

    [MpvFact]
    public void AStructuredValueSurvivesTheTripThroughLibmpv()
    {
        using var player = HeadlessPlayer();
        var value = MpvNode.Map(
        [
            new("text", "naïve — 日本語"),
            new("int", -42L),
            new("double", 2.25),
            new("flag", false),
            new("list", MpvNode.Array(1L, MpvNode.Map([new("inner", "deep")]), MpvNode.Array())),
        ]);

        player.SetPropertyNode("user-data/mtn/test", value);
        var back = player.GetPropertyNode("user-data/mtn/test");

        back.Should().NotBeNull();
        back!.ToString().Should().Be(value.ToString());
    }

    [MpvFact]
    public void ACommandTakesNamedArgumentsAndReturnsData()
    {
        using var player = HeadlessPlayer();

        var result = player.CommandNode(MpvNode.Command("expand-text", ("text", "plain text")));

        result.AsString().Should().Be("plain text");
    }

    [MpvFact]
    public void ACommandThatFailsThrowsWithMpvsReason()
    {
        using var player = HeadlessPlayer();

        var run = () => player.CommandNode(MpvNode.Command("no-such-command"));

        run.Should().Throw<MediaToolkitNetException>().WithMessage("*mpv_command_node*");
    }

    [MpvFact]
    public void AnUnavailablePropertyIsNullNotAnError()
    {
        using var player = HeadlessPlayer();

        player.GetPropertyNode("no-such-property").Should().BeNull();
    }

    [MpvFact]
    public void TheTracksOfAFileAreListedWithTheirIdsLanguagesAndFormats()
    {
        using var media = new TestMedia();
        var path = media.WriteMovie("movie.mkv");
        using var player = HeadlessPlayer();

        player.Open(path);
        var tracks = WaitFor(player, () => player.GetTracks(), t => t.Count >= 3);

        tracks.Select(t => t.Type).Should().BeEquivalentTo([MpvTrackType.Video, MpvTrackType.Audio, MpvTrackType.Subtitle]);
        var video = tracks.Single(t => t.Type == MpvTrackType.Video);
        video.Id.Should().Be(1);
        (video.Width, video.Height).Should().Be((320L, 240L));
        video.Codec.Should().Be("mjpeg");
        var audio = tracks.Single(t => t.Type == MpvTrackType.Audio);
        (audio.Channels, audio.SampleRate).Should().Be((2L, 48000L));
        audio.Codec.Should().Be("aac");
        tracks.Single(t => t.Type == MpvTrackType.Subtitle).Language.Should().Be("eng");
        video.Raw["type"]!.AsString().Should().Be("video");
    }

    [MpvFact]
    public void TheChaptersAndTagsOfAFileAreRead()
    {
        using var media = new TestMedia();
        var movie = media.WriteMovie("movie.mkv");
        var meta = media.PathTo("meta.txt");
        File.WriteAllText(
            meta,
            ";FFMETADATA1\ntitle=My Movie\nartist=Somebody\n" +
            "[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1000\ntitle=One\n" +
            "[CHAPTER]\nTIMEBASE=1/1000\nSTART=1000\nEND=2000\ntitle=Two\n");
        var tagged = media.PathTo("tagged.mkv");
        FfmpegProgram.Run($"-i \"{movie}\" -i \"{meta}\" -map 0 -map_metadata 1 -map_chapters 1 -c copy \"{tagged}\"");
        using var player = HeadlessPlayer();

        player.Open(tagged);
        var chapters = WaitFor(player, () => player.GetChapters(), c => c.Count == 2);
        var tags = player.GetMetadata();

        // The audio's priming shifts the container's zero by a few hundredths of a second on some FFmpeg versions.
        chapters.Select(c => c.Title).Should().Equal("One", "Two");
        chapters[0].Start.TotalSeconds.Should().BeApproximately(0.0, 0.05);
        chapters[1].Start.TotalSeconds.Should().BeApproximately(1.0, 0.05);
        tags.Should().Contain("TITLE", "My Movie").And.Contain("ARTIST", "Somebody"); // Matroska spells tags in capitals
    }

    private static MpvPlayer HeadlessPlayer()
    {
        var player = new MpvPlayer();
        player.Options["vo"] = "null";
        player.Options["ao"] = "null";
        player.Options["pause"] = "yes";
        return player;
    }

    private static T WaitFor<T>(MpvPlayer player, Func<T> read, Func<T, bool> done)
    {
        _ = player;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        T value;
        do
        {
            value = read();
            if (done(value))
            {
                return value;
            }

            Thread.Sleep(50);
        }
        while (DateTime.UtcNow < deadline);

        return value;
    }
}
