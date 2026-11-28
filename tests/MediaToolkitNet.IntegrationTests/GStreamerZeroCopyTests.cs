#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.GStreamer;
using MediaToolkitNet.GStreamer.Native;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// Buffers that are not copied: the application's own memory, and memory behind a file descriptor as a DMABuf is. The
/// descriptors here are <c>memfd</c> files, not buffers of a graphics device.
/// </summary>
[SupportedOSPlatform("linux")]
public unsafe partial class GStreamerZeroCopyTests
{
    private const int Size = 64 * 1024;

    [LibraryImport("libc", EntryPoint = "memfd_create", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MemfdCreate(string name, uint flags);

    [LibraryImport("libc", EntryPoint = "pwrite")]
    private static partial nint PWrite(int fd, byte* data, nint count, nint offset);

    private static (nint Address, byte First, byte Last, int Length) Describe(in GStreamerData data)
    {
        fixed (byte* p = data.Bytes)
        {
            return ((nint)p, data.Bytes[0], data.Bytes[^1], data.Bytes.Length);
        }
    }

    [GStreamerOnlyFact]
    public void TheApplicationsMemoryReachesTheEndWithoutBeingCopied()
    {
        var memory = (byte*)NativeMemory.Alloc(Size);
        var released = 0;
        try
        {
            new Span<byte>(memory, Size).Fill(0xA5);
            using (var pipeline = GStreamerPipeline.Parse("appsrc name=src ! queue ! appsink name=sink sync=false"))
            {
                var source = pipeline.GetSource("src");
                var sink = pipeline.GetSink("sink");
                source.SetCaps("application/x-mediatoolkitnet-test");
                pipeline.Play();

                source.PushNoCopy((nint)memory, Size, TimeSpan.Zero, released: () => Interlocked.Increment(ref released))
                    .Should().BeTrue();

                (nint Address, byte First, byte Last, int Length) seen = default;
                sink.TryPullData(TimeSpan.FromSeconds(3), (in GStreamerData data) =>
                {
                    seen = Describe(data);
                    data.DmaBufFd.Should().BeNull("this is ordinary memory");
                    Volatile.Read(ref released).Should().Be(0, "the buffer is still in use");
                }).Should().BeTrue();
                seen.Should().Be(((nint)memory, (byte)0xA5, (byte)0xA5, Size), "the very same bytes arrive");
            }

            SpinWait.SpinUntil(() => Volatile.Read(ref released) == 1, TimeSpan.FromSeconds(3))
                .Should().BeTrue("the memory is given back once nothing uses it");
        }
        finally
        {
            NativeMemory.Free(memory);
        }
    }

    [GStreamerOnlyFact]
    public void MemoryComesBackWhileThePipelineRunsWhenNothingKeepsIt()
    {
        var memory = (byte*)NativeMemory.Alloc(Size);
        var released = 0;
        try
        {
            using var pipeline = GStreamerPipeline.Parse("appsrc name=src ! fakesink sync=false enable-last-sample=false");
            var source = pipeline.GetSource("src");
            source.SetCaps("application/x-mediatoolkitnet-test");
            pipeline.Play();

            for (var i = 0; i < 5; i++)
            {
                source.PushNoCopy((nint)memory, Size, TimeSpan.FromMilliseconds(i), released: () => Interlocked.Increment(ref released));
            }

            SpinWait.SpinUntil(() => Volatile.Read(ref released) == 5, TimeSpan.FromSeconds(3))
                .Should().BeTrue("each buffer is given back as the sink is done with it");
        }
        finally
        {
            NativeMemory.Free(memory);
        }
    }

    [GStreamerOnlyFact]
    public void EveryBufferIsGivenBackOnceEvenWhenRefusedOrNeverPlayed()
    {
        var memory = (byte*)NativeMemory.Alloc(Size);
        var released = 0;
        var failures = new List<string>();
        try
        {
            using (var pipeline = GStreamerPipeline.Parse("appsrc name=src ! fakesink"))
            {
                var source = pipeline.GetSource("src");
                source.Element.Failed += (_, e) => failures.Add(e.Message);
                source.SetCaps("application/x-mediatoolkitnet-test");
                pipeline.Play();
                source.PushNoCopy((nint)memory, Size, TimeSpan.Zero, released: () => Interlocked.Increment(ref released));
                source.PushNoCopy((nint)memory, Size, TimeSpan.Zero, released: () =>
                {
                    Interlocked.Increment(ref released);
                    throw new InvalidOperationException("boom");
                });
                source.EndOfStream();

                source.PushNoCopy((nint)memory, Size, TimeSpan.Zero, released: () => Interlocked.Increment(ref released))
                    .Should().BeFalse("the stream has ended");
                pipeline.WaitForCompletion(TimeSpan.FromSeconds(5)).Should().BeTrue();
            }

            SpinWait.SpinUntil(() => Volatile.Read(ref released) == 3, TimeSpan.FromSeconds(3)).Should().BeTrue();
            Thread.Sleep(100);
            released.Should().Be(3);
            failures.Should().ContainSingle().Which.Should().Contain("boom");
        }
        finally
        {
            NativeMemory.Free(memory);
        }
    }

    [GStreamerOnlyFact]
    public void ASinkThatKeepsItsLastSampleKeepsTheMemoryUntilItStops()
    {
        var memory = (byte*)NativeMemory.Alloc(Size);
        var released = 0;
        try
        {
            using (var pipeline = GStreamerPipeline.Parse("appsrc name=src ! fakesink sync=false"))
            {
                var source = pipeline.GetSource("src");
                source.SetCaps("application/x-mediatoolkitnet-test");
                pipeline.Play();
                source.PushNoCopy((nint)memory, Size, TimeSpan.Zero, released: () => Interlocked.Increment(ref released));
                Thread.Sleep(300);

                released.Should().Be(0, "the sink still holds the last buffer");
            }

            SpinWait.SpinUntil(() => Volatile.Read(ref released) == 1, TimeSpan.FromSeconds(3)).Should().BeTrue();
        }
        finally
        {
            NativeMemory.Free(memory);
        }
    }

    [GStreamerOnlyFact]
    public void ADescriptorPushedAsADmaBufArrivesAsOne()
    {
        var fd = MemfdCreate("mediatoolkitnet-test", 0);
        fd.Should().BeGreaterThan(0);
        var content = new byte[Size];
        Array.Fill(content, (byte)0x3C);
        fixed (byte* p = content)
        {
            ((long)PWrite(fd, p, Size, 0)).Should().Be(Size);
        }

        var pipeline = GStreamerPipeline.Parse("appsrc name=src ! queue ! appsink name=sink sync=false");
        var source = pipeline.GetSource("src");
        var sink = pipeline.GetSink("sink");
        source.SetCaps("application/x-mediatoolkitnet-test");
        pipeline.Play();
        var released = 0;

        source.PushDmaBuf(fd, Size, TimeSpan.FromMilliseconds(40), released: () => Interlocked.Increment(ref released))
            .Should().BeTrue();

        int? arrived = null;
        sink.TryPullData(TimeSpan.FromSeconds(3), (in GStreamerData data) =>
        {
            arrived = data.DmaBufFd;
            data.Bytes.Length.Should().Be(Size);
            data.Bytes[0].Should().Be(0x3C);
            data.Timestamp.Should().Be(TimeSpan.FromMilliseconds(40));
        }).Should().BeTrue();
        arrived.Should().Be(fd, "the descriptor itself travels, not a copy of what it holds");
        pipeline.Dispose();
        SpinWait.SpinUntil(() => Volatile.Read(ref released) == 1, TimeSpan.FromSeconds(3))
            .Should().BeTrue("the descriptor is given back");

        // The number may be reused at once by something else, so what is asked is whether it still names this memory.
        bool StillOpen() => new FileInfo($"/proc/self/fd/{fd}").LinkTarget?.Contains("mediatoolkitnet-test", StringComparison.Ordinal) == true;
        SpinWait.SpinUntil(() => !StillOpen(), TimeSpan.FromSeconds(3))
            .Should().BeTrue("GStreamer took the descriptor over and closed it");
    }

    [GStreamerOnlyFact]
    public void WhatIsNoMemoryAndNoDescriptorIsRefused()
    {
        using var pipeline = GStreamerPipeline.Parse("appsrc name=src ! fakesink");
        var source = pipeline.GetSource("src");

        var empty = () => source.PushNoCopy(0, Size, TimeSpan.Zero);
        var noBytes = () => source.PushNoCopy(1, 0, TimeSpan.Zero);
        var noFile = () => source.PushDmaBuf(-1, Size, TimeSpan.Zero);

        empty.Should().Throw<ArgumentNullException>();
        noBytes.Should().Throw<ArgumentOutOfRangeException>();
        noFile.Should().Throw<ArgumentOutOfRangeException>();
    }
}
