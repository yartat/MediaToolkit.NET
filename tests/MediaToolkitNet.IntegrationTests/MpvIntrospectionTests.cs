#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Mpv;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>Watching any property, asking mpv what it can do, and hearing that a file could not be played.</summary>
public class MpvIntrospectionTests
{
    [MpvFact]
    public void AnyPropertyCanBeWatchedAndTheWatchStopsWhenDisposed()
    {
        using var media = new TestMedia();
        var first = media.WriteMovie("first.mkv");
        var second = media.WriteMovie("second.mkv");
        using var player = HeadlessPlayer();
        var seen = new List<int?>();
        var observation = player.ObserveProperty("track-list", (name, value) =>
        {
            name.Should().Be("track-list");
            lock (seen)
            {
                seen.Add(value?.Items.Count);
            }
        });

        player.Open(first);
        WaitUntil(() => { lock (seen) { return seen.Contains(3); } }).Should().BeTrue("the tracks of the file were reported");

        observation.Dispose();
        observation.Dispose(); // a second call changes nothing
        int before;
        lock (seen)
        {
            before = seen.Count;
        }

        player.Open(second);
        WaitForTracks(player);
        Thread.Sleep(300);
        lock (seen)
        {
            seen.Count.Should().Be(before, "nothing is reported after the watch was disposed");
        }
    }

    [MpvFact]
    public void ThePlayerKnowsItsCommandsPropertiesAndOptions()
    {
        using var player = HeadlessPlayer();

        var commands = player.GetCommands();
        var load = commands.Single(c => c.Name == "loadfile");
        load.Arguments[0].Name.Should().Be("url");
        load.Arguments[0].IsOptional.Should().BeFalse();
        load.Arguments.Select(a => a.Name).Should().Contain("flags");
        commands.Should().Contain(c => c.Name == "screenshot-raw");

        player.HasCommand("expand-text").Should().BeTrue();
        player.HasCommand("no-such-command").Should().BeFalse();
        player.GetPropertyNames().Should().Contain(["time-pos", "track-list", "command-list"]);
        player.HasOption("vo").Should().BeTrue();
        player.HasOption("no-such-option").Should().BeFalse();
        player.GetMpvVersion().Should().StartWith("mpv ");
        MpvPlayer.ClientApiVersion.Should().MatchRegex(@"^\d+\.\d+$");
    }

    [MpvFact]
    public void AFileThatCannotBePlayedRaisesFailed()
    {
        using var media = new TestMedia();
        using var player = HeadlessPlayer();
        var failed = new List<MediaToolkitNetException>();
        player.Failed += (_, error) =>
        {
            lock (failed)
            {
                failed.Add(error);
            }
        };

        player.Open(media.PathTo("does-not-exist.mkv"));

        WaitUntil(() => { lock (failed) { return failed.Count > 0; } }).Should().BeTrue("mpv said it could not play the file");
        lock (failed)
        {
            failed[0].Message.Should().Contain("could not play");
        }
    }

    [MpvFact]
    public void AFileThatPlaysToTheEndDoesNotRaiseFailed()
    {
        using var media = new TestMedia();
        using var player = HeadlessPlayer();
        player.Options["pause"] = "no";
        var failures = 0;
        var ended = new ManualResetEventSlim();
        player.Failed += (_, _) => Interlocked.Increment(ref failures);
        player.StateChanged += (_, change) =>
        {
            if (change.Current == MediaToolkitNet.Abstractions.Playback.PlaybackState.Ended)
            {
                ended.Set();
            }
        };

        player.Open(media.WriteMovie("movie.mkv"));

        ended.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue();
        failures.Should().Be(0);
    }

    private static bool WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return condition();
    }

    private static void WaitForTracks(MpvPlayer player) => WaitUntil(() => player.GetTracks().Count >= 3);

    private static MpvPlayer HeadlessPlayer()
    {
        var player = new MpvPlayer();
        player.Options["vo"] = "null";
        player.Options["ao"] = "null";
        player.Options["pause"] = "yes";
        return player;
    }
}
