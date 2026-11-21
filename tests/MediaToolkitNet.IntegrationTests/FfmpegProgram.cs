#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using FluentAssertions;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>Runs the <c>ffmpeg</c> program, found beside the libraries the tests load or on <c>PATH</c>.</summary>
public static class FfmpegProgram
{
    /// <summary>Runs it with the arguments; the test fails when it does.</summary>
    public static void Run(string arguments)
    {
        var directory = Environment.GetEnvironmentVariable(FFmpegEnvironment.DirectoryVariable);
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var program = directory is not null && File.Exists(Path.Combine(directory, exe)) ? Path.Combine(directory, exe) : "ffmpeg";
        using var process = Process.Start(new ProcessStartInfo(program, "-v error -y " + arguments)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"the ffmpeg program is needed to make the test media: {error}");
    }
}
