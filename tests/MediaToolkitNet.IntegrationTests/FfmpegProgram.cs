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

    /// <summary>Starts the <c>ffmpeg</c> program and leaves it running, for output that goes to a pipe the test reads; the caller disposes it.</summary>
    public static Process Start(string arguments)
    {
        var directory = Environment.GetEnvironmentVariable(FFmpegEnvironment.DirectoryVariable);
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var program = directory is not null && File.Exists(Path.Combine(directory, exe)) ? Path.Combine(directory, exe) : "ffmpeg";
        return Process.Start(new ProcessStartInfo(program, "-v error -y " + arguments)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
    }

    /// <summary>Counts the video frames <c>ffmpeg</c> decodes from a file, with input options such as <c>-skip_frame nokey</c>.</summary>
    public static int Count(string path, string inputOptions)
    {
        var directory = Environment.GetEnvironmentVariable(FFmpegEnvironment.DirectoryVariable);
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var program = directory is not null && File.Exists(Path.Combine(directory, exe)) ? Path.Combine(directory, exe) : "ffmpeg";
        using var process = Process.Start(new ProcessStartInfo(program, $"-v error -progress pipe:1 {inputOptions} -i \"{path}\" -map 0:v:0 -f null -")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, errors.Result);
        var last = output.Split('\n').Last(l => l.StartsWith("frame=", StringComparison.Ordinal));
        return int.Parse(last["frame=".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
