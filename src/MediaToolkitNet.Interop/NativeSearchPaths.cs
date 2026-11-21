#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.InteropServices;

namespace MediaToolkitNet.Interop;

/// <summary>
/// Directories probed when a backend loads its native libraries, in order.
/// </summary>
/// <remarks>
/// Applications can insert their own directories before any backend is first
/// used, e.g. to point at a private FFmpeg build. The defaults cover the layout
/// this repository ships with as well as the standard NuGet runtimes folder.
/// </remarks>
public static class NativeSearchPaths
{
    private const string RepositoryMarker = "MediaToolkit.NET.slnx";

    private static readonly List<string> Extra = [];

    /// <summary>
    /// Folder name used by this repository for the current operating system:
    /// <c>Windows</c>, <c>Linux</c> or <c>MacOS</c>.
    /// </summary>
    public static string PlatformFolder =>
        OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsMacOS() ? "MacOS"
        : "Linux";

    /// <summary>Adds a directory to the front of the probe order. Ignored when it does not exist.</summary>
    public static void Prepend(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        lock (Extra)
        {
            Extra.Insert(0, Path.GetFullPath(directory));
        }
    }

    /// <summary>Removes every directory added through <see cref="Prepend"/>.</summary>
    public static void ClearUserPaths()
    {
        lock (Extra)
        {
            Extra.Clear();
        }
    }

    /// <summary>Returns the probe order: user directories first, then the built-in defaults.</summary>
    public static IReadOnlyList<string> Resolve() => Resolve(AppContext.BaseDirectory);

    internal static IReadOnlyList<string> Resolve(string baseDir)
    {
        var result = new List<string>();
        lock (Extra)
        {
            result.AddRange(Extra);
        }

        var platform = PlatformFolder;

        result.Add(baseDir);
        result.Add(Path.Combine(baseDir, "native"));
        result.Add(Path.Combine(baseDir, "native", platform));
        result.Add(Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"));

        // Development convenience: when running straight out of bin/, walk up to
        // the repository root and pick up the checked-in native folders. Only a
        // folder that is this repository counts: an installed application sits
        // under directories other users may write to, and a library planted in
        // <ancestor>/src/FFMpeg would be loaded into it.
        var probe = new DirectoryInfo(baseDir);
        for (var depth = 0; probe is not null && depth < 8; depth++, probe = probe.Parent)
        {
            var ffmpeg = Path.Combine(probe.FullName, "src", "FFMpeg", platform);
            if (File.Exists(Path.Combine(probe.FullName, RepositoryMarker)) && Directory.Exists(ffmpeg))
            {
                result.Add(ffmpeg);
                break;
            }
        }

        return result;
    }

    /// <summary>The entries of a <c>PATH</c>-style variable that can be loaded from.</summary>
    /// <remarks>Empty and relative entries mean the current directory, which is not trusted.</remarks>
    internal static IReadOnlyList<string> PathEntries(string? pathVariable) =>
        [.. (pathVariable?.Split(Path.PathSeparator) ?? []).Where(Path.IsPathFullyQualified)];
}
