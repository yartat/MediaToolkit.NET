#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using FluentAssertions;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// AUDIT.md MEM-5, MEM-6 and MEM-7 on the FFmpeg player. Each scenario runs in
/// a child process, because the failures they look for end the process.
/// </summary>
public class AuditPlayerTests
{
    [FFmpegFact]
    public void AThrowingFailedHandlerDoesNotEndTheProcess()
    {
        using var media = new TestMedia();
        var outcome = ChildProbe.Run("failed-handler-throws", media.WriteMovie("movie.mkv"));

        outcome.ExitCode.Should().Be(0, outcome.Output);
        outcome.Value("state").Should().Be("Faulted");
    }

    [FFmpegFact]
    public void DisposingFromInsideAFrameHandlerDoesNotWaitOutTheJoinTimeout()
    {
        using var media = new TestMedia();
        var outcome = ChildProbe.Run("dispose-inside-handler", media.WriteMovie("movie.mkv"));

        outcome.ExitCode.Should().Be(0, outcome.Output);
        long.Parse(outcome.Value("dispose_ms")!).Should().BeLessThan(1000, "the worker cannot wait for itself");
    }

    [FFmpegFact]
    public void SeekingWhilePausedNeverTouchesTheContextsFromTwoThreads()
    {
        using var media = new TestMedia();
        var outcome = ChildProbe.Run("seek-while-paused", media.WriteMovie("movie.mkv"));

        outcome.ExitCode.Should().Be(0, outcome.Output);
        outcome.Value("failures").Should().Be("0");
    }

    [Fact]
    public void WritingBeforeStartReturnsWhatFitsInsteadOfWaitingForever()
    {
        var outcome = ChildProbe.Run("wasapi-prefill", string.Empty);

        if (outcome.Value("skipped") is not null)
        {
            return; // no Windows audio endpoint here
        }

        outcome.Value("returned").Should().Be("True", outcome.Output);
    }

    [MpvFact]
    public void StoppingWhileAnotherThreadDisposesTheMpvPlayerNeverTouchesAFreedHandle()
    {
        using var media = new TestMedia();
        var outcome = ChildProbe.Run("mpv-stop-vs-dispose", media.WriteMovie("movie.mkv"));

        outcome.ExitCode.Should().Be(0, outcome.Output);
    }

    [FFmpegFact]
    public void DisposingAPlayerThatIsWaitingForTheNetworkDoesNotWaitForTheServer()
    {
        using var media = new TestMedia();
        var clip = media.PathTo("clip.ts");
        FfmpegProgram.Run($"-f lavfi -i testsrc=d=1:s=160x120:r=25 -f lavfi -i sine=d=1 -c:v mpeg2video -c:a mp2 -f mpegts \"{clip}\"");

        var outcome = ChildProbe.Run("player-stalled-network", clip);

        outcome.ExitCode.Should().Be(0, outcome.Output);
        long.Parse(outcome.Value("dispose_ms")!).Should().BeLessThan(1000, "a read blocked on the socket has to be interruptible");
    }
}
