#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Mpv;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>mpv's hooks (it waits for you at <c>on_load</c> and the like) and its asynchronous commands.</summary>
public class MpvHookAndAsyncTests
{
    // ------------------------------------------------------------------- hooks

    [MpvFact]
    public void OnLoadCanRedirectAUrlBeforeAnythingIsOpened()
    {
        using var media = new TestMedia();
        var path = media.WriteMovie("movie.mkv");
        using var player = HeadlessPlayer();
        player.AddHook("on_load", hook =>
        {
            hook.Name.Should().Be("on_load");
            if (hook.Player.GetPropertyString("stream-open-filename") == "alias://movie")
            {
                hook.Player.SetProperty("stream-open-filename", path);
            }
        });

        player.Open("alias://movie");

        WaitForTracks(player).Should().HaveCountGreaterThanOrEqualTo(3);
    }

    [MpvFact]
    public void ADeferredHookKeepsMpvWaitingUntilItIsContinued()
    {
        using var media = new TestMedia();
        var path = media.WriteMovie("movie.mkv");
        using var player = HeadlessPlayer();
        MpvHook? held = null;
        player.AddHook("on_load", hook =>
        {
            hook.Defer();
            held = hook;
        });

        player.Open(path);
        Thread.Sleep(600);

        held.Should().NotBeNull("the hook was reached");
        player.GetTracks().Should().BeEmpty("mpv has not opened the file yet");

        Task.Run(() => held!.Continue()).Wait();
        WaitForTracks(player).Should().HaveCountGreaterThanOrEqualTo(3);
        held!.Continue(); // a second call changes nothing
    }

    [MpvFact]
    public void AHandlerThatThrowsDoesNotFreezeMpvAndIsReported()
    {
        using var media = new TestMedia();
        var path = media.WriteMovie("movie.mkv");
        using var player = HeadlessPlayer();
        var failures = new List<MediaToolkitNetException>();
        player.Failed += (_, error) =>
        {
            lock (failures)
            {
                failures.Add(error);
            }
        };
        player.AddHook("on_load", _ => throw new InvalidOperationException("boom"));

        player.Open(path);

        WaitForTracks(player).Should().HaveCountGreaterThanOrEqualTo(3);
        lock (failures)
        {
            failures.Should().ContainSingle().Which.Message.Should().Contain("on_load").And.Contain("boom");
        }
    }

    [MpvFact]
    public void HandlersRunInPriorityOrder()
    {
        using var media = new TestMedia();
        var path = media.WriteMovie("movie.mkv");
        using var player = HeadlessPlayer();
        var order = new List<string>();
        player.AddHook("on_load", _ => order.Add("late"), priority: 10);
        player.AddHook("on_load", _ => order.Add("early"), priority: -5);

        player.Open(path);
        WaitForTracks(player);

        order.Should().Equal("early", "late");
    }

    // ---------------------------------------------------------- async commands

    [MpvFact]
    public async Task ACommandFinishesAsynchronouslyWithItsResult()
    {
        using var player = HeadlessPlayer();

        var result = await player.CommandNodeAsync(MpvNode.Command("expand-text", ("text", "plain text")));

        result.AsString().Should().Be("plain text");
        await player.CommandAsync("set", "pause", "yes");
    }

    [MpvFact]
    public async Task TheCallerIsNotHeldUpWhileTheCommandRuns()
    {
        using var player = HeadlessPlayer();
        var clock = Stopwatch.StartNew();

        var running = player.CommandNodeAsync(Sleep(2));
        clock.ElapsedMilliseconds.Should().BeLessThan(1000, "the call returns at once");
        running.IsCompleted.Should().BeFalse();

        var result = await running;
        clock.ElapsedMilliseconds.Should().BeGreaterThan(1000);
        result["status"]!.AsInt64().Should().Be(0);
    }

    [MpvFact]
    public async Task CancellingStopsACommandThatCanBeAborted()
    {
        using var player = HeadlessPlayer();
        using var cancel = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();

        var running = player.CommandNodeAsync(Sleep(60), cancel.Token);
        await Task.Delay(300);
        await cancel.CancelAsync();

        var wait = async () => await running;
        await wait.Should().ThrowAsync<OperationCanceledException>();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "mpv killed the process instead of letting it run for a minute");
    }

    [MpvFact]
    public async Task ACommandThatFailsFaultsTheTask()
    {
        using var player = HeadlessPlayer();

        var run = async () => await player.CommandNodeAsync(MpvNode.Command("no-such-command"));

        await run.Should().ThrowAsync<MediaToolkitNetException>();
    }

    [MpvFact]
    public async Task DisposingThePlayerFailsWhatIsStillRunning()
    {
        var player = HeadlessPlayer();
        var running = player.CommandNodeAsync(Sleep(60));
        await Task.Delay(300);

        player.Dispose();

        var wait = async () => await running.WaitAsync(TimeSpan.FromSeconds(20));
        await wait.Should().ThrowAsync<ObjectDisposedException>();
    }

    // The mpv command that runs a process and returns when it ends, which can be aborted.
    private static MpvNode Sleep(int seconds)
    {
        var command = OperatingSystem.IsWindows()
            ? new[] { "ping", "-n", (seconds + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "127.0.0.1" }
            : ["sleep", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        return MpvNode.Command(
            "subprocess",
            ("args", MpvNode.Array(command.Select(MpvNode.From))),
            ("playback_only", false));
    }

    private static IReadOnlyList<MpvTrack> WaitForTracks(MpvPlayer player)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        IReadOnlyList<MpvTrack> tracks;
        do
        {
            tracks = player.GetTracks();
            if (tracks.Count >= 3)
            {
                break;
            }

            Thread.Sleep(50);
        }
        while (DateTime.UtcNow < deadline);

        return tracks;
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
