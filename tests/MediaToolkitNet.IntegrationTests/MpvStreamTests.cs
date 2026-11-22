#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Mpv;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>mpv reading from a .NET <see cref="Stream"/> through <c>stream_cb</c>.</summary>
public class MpvStreamTests
{
    [MpvFact]
    public void AStreamIsPlayedLikeTheFileItHolds()
    {
        using var media = new TestMedia();
        var path = media.WriteMovie("movie.mkv");
        using var player = HeadlessPlayer();
        using var stream = new Tracked(File.OpenRead(path));

        player.Open(stream, leaveOpen: true);
        var tracks = WaitForTracks(player);

        tracks.Select(t => t.Type).Should().BeEquivalentTo([MpvTrackType.Video, MpvTrackType.Audio, MpvTrackType.Subtitle]);
        stream.Reads.Should().BeGreaterThan(0, "mpv read it");
        stream.Seeks.Should().BeGreaterThan(0, "mpv tests for seekability and reads the index");
    }

    [MpvFact]
    public void AStreamThatCannotSeekPlaysFromStartToEnd()
    {
        using var media = new TestMedia();
        var path = media.WriteMovie("movie.mkv");
        using var player = HeadlessPlayer();
        using var stream = new Tracked(File.OpenRead(path)) { Seekable = false };

        player.Open(stream, leaveOpen: true);
        var tracks = WaitForTracks(player);

        tracks.Should().HaveCountGreaterThanOrEqualTo(3);
        stream.Seeks.Should().Be(0);
    }

    [MpvFact]
    public void ALentStreamIsLeftOpenAndAnOwnedOneIsClosed()
    {
        using var media = new TestMedia();
        var path = media.WriteMovie("movie.mkv");
        var lent = new Tracked(File.OpenRead(path));
        var owned = new Tracked(File.OpenRead(path));
        try
        {
            using (var player = HeadlessPlayer())
            {
                player.Open(lent, leaveOpen: true);
                WaitForTracks(player);
            }

            lent.Disposed.Should().BeFalse("the caller kept ownership");

            using (var player = HeadlessPlayer())
            {
                player.Open(owned);
                WaitForTracks(player);
            }

            owned.Disposed.Should().BeTrue("mpv closed it when the player went away");
        }
        finally
        {
            lent.Dispose();
            owned.Dispose();
        }
    }

    [MpvFact]
    public void AProtocolOfYourOwnOpensWhateverItsUriNames()
    {
        using var media = new TestMedia();
        var path = media.WriteMovie("movie.mkv");
        var asked = new List<string>();
        using var player = HeadlessPlayer();
        player.RegisterStreamProtocol("mtntest", uri =>
        {
            lock (asked)
            {
                asked.Add(uri);
            }

            return uri == "mtntest://the-movie" ? File.OpenRead(path) : null;
        });

        player.Open("mtntest://the-movie");
        var tracks = WaitForTracks(player);

        tracks.Should().HaveCountGreaterThanOrEqualTo(3);
        lock (asked)
        {
            asked.Should().Contain("mtntest://the-movie");
        }
    }

    [MpvFact]
    public void AnOpenerThatReturnsNothingEndsThePlaybackWithoutAnything()
    {
        using var player = HeadlessPlayer();
        var ended = new ManualResetEventSlim();
        var asked = 0;
        player.StateChanged += (_, change) =>
        {
            if (change.Current == MediaToolkitNet.Abstractions.Playback.PlaybackState.Ended)
            {
                ended.Set();
            }
        };
        player.RegisterStreamProtocol("mtnnone", _ =>
        {
            Interlocked.Increment(ref asked);
            return null;
        });

        player.Open("mtnnone://missing");

        ended.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("mpv gave up on the file");
        asked.Should().BeGreaterThan(0);
        player.GetTracks().Should().BeEmpty();
    }

    [MpvFact]
    public void AProtocolNameMustBeAScheme()
    {
        using var player = HeadlessPlayer();

        var register = () => player.RegisterStreamProtocol("bad name://", _ => null);

        register.Should().Throw<ArgumentException>();
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

    /// <summary>A stream that counts what is done to it and can pretend not to seek.</summary>
    private sealed class Tracked(Stream inner) : Stream
    {
        private int _reads;
        private int _seeks;

        public bool Seekable { get; set; } = true;

        public bool Disposed { get; private set; }

        public int Reads => Volatile.Read(ref _reads);

        public int Seeks => Volatile.Read(ref _seeks);

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => Seekable && inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            Interlocked.Increment(ref _reads);
            return inner.Read(buffer);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Interlocked.Increment(ref _seeks);
            return inner.Seek(offset, origin);
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
