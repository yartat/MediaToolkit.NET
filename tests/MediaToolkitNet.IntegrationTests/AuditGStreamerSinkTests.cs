#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// A buffer shorter than its caps describe must not be read
/// past its end. The crash cannot be caught, so the pipeline runs in a child
/// process: the sample's <c>gst-run</c> command.
/// </summary>
public class AuditGStreamerSinkTests
{
    private const string ShortBuffers =
        "fakesrc num-buffers=3 sizetype=fixed sizemax=100 filltype=zero ! " +
        "capsfilter caps=video/x-raw,format=I420,width=1920,height=1080,framerate=25/1 ! appsink name=sink";

    [GStreamerFact]
    public void ABufferShorterThanItsCapsIsRefusedNotRead()
    {
        var sample = FindSample();
        if (sample is null)
        {
            Assert.Fail("The sample CLI is not built; build the solution first.");
        }

        using var process = Process.Start(new ProcessStartInfo("dotnet")
        {
            ArgumentList = { sample!, "gst-run", ShortBuffers, "3" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        output.Should().NotContain("AccessViolationException");
        process.ExitCode.Should().NotBe(134, "the process must not abort");
    }

    private static string? FindSample()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var bin = Path.Combine(dir.FullName, "samples", "MediaToolkitNet.Sample.Cli", "bin");
            if (!Directory.Exists(bin))
            {
                continue;
            }

            return Directory.EnumerateFiles(bin, "MediaToolkitNet.Sample.Cli.dll", SearchOption.AllDirectories).FirstOrDefault();
        }

        return null;
    }
}
