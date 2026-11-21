#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>Runs a scenario of the CrashProbe program in a child process.</summary>
public static class ChildProbe
{
    /// <summary>What a scenario printed and how it ended.</summary>
    public sealed record Outcome(int ExitCode, string Output)
    {
        /// <summary>The value of a <c>RESULT key=value</c> line.</summary>
        public string? Value(string key) => Output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("RESULT " + key + "=", StringComparison.Ordinal))
            .Select(l => l[("RESULT " + key + "=").Length..])
            .FirstOrDefault();
    }

    /// <summary>Runs a scenario.</summary>
    public static Outcome Run(string scenario, string media)
    {
        var dll = Find("MediaToolkitNet.CrashProbe")
            ?? throw new InvalidOperationException("The CrashProbe program is not built; build the solution first.");
        using var process = Process.Start(new ProcessStartInfo("dotnet")
        {
            ArgumentList = { dll, scenario, media },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromSeconds(120)))
        {
            process.Kill(entireProcessTree: true);
        }

        return new Outcome(process.ExitCode, output + error.Result);
    }

    private static string? Find(string project)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var bin = Path.Combine(dir.FullName, "tests", project, "bin");
            if (Directory.Exists(bin))
            {
                return Directory.EnumerateFiles(bin, project + ".dll", SearchOption.AllDirectories).FirstOrDefault();
            }
        }

        return null;
    }
}
