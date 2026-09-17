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
    public static IReadOnlyList<string> Resolve()
    {
        var result = new List<string>();
        lock (Extra)
        {
            result.AddRange(Extra);
        }

        var baseDir = AppContext.BaseDirectory;
        var platform = PlatformFolder;

        result.Add(baseDir);
        result.Add(Path.Combine(baseDir, "native"));
        result.Add(Path.Combine(baseDir, "native", platform));
        result.Add(Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"));

        // Development convenience: when running straight out of bin/, walk up to
        // the repository root and pick up the checked-in native folders.
        var probe = new DirectoryInfo(baseDir);
        for (var depth = 0; probe is not null && depth < 8; depth++, probe = probe.Parent)
        {
            var ffmpeg = Path.Combine(probe.FullName, "src", "FFMpeg", platform);
            if (Directory.Exists(ffmpeg))
            {
                result.Add(ffmpeg);
                break;
            }
        }

        return result;
    }
}
