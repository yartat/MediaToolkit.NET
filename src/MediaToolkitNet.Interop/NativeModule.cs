using System.Runtime.InteropServices;
using MediaToolkitNet.Core;

namespace MediaToolkitNet.Interop;

/// <summary>
/// A loaded native shared library, resolved by probing several file name
/// candidates across <see cref="NativeSearchPaths"/> and then the operating
/// system loader.
/// </summary>
/// <remarks>
/// Modules are intentionally never unloaded: media stacks such as FFmpeg and
/// libmpv register global state on first use, and unloading them mid-process is
/// not safe.
/// </remarks>
public sealed class NativeModule
{
    private readonly Dictionary<string, nint> _exports = new(StringComparer.Ordinal);

    private NativeModule(string fileName, nint handle)
    {
        FileName = fileName;
        Handle = handle;
    }

    /// <summary>File name the loader accepted.</summary>
    public string FileName { get; }

    /// <summary>Raw module handle.</summary>
    public nint Handle { get; }

    /// <summary>
    /// Loads the first candidate that resolves.
    /// </summary>
    /// <param name="backend">Backend name, used in the error message.</param>
    /// <param name="candidates">
    /// File names to try in order, e.g. <c>avcodec-61.dll</c> then <c>avcodec-60.dll</c>.
    /// </param>
    /// <exception cref="MediaBackendUnavailableException">No candidate could be loaded.</exception>
    public static NativeModule Load(string backend, IEnumerable<string> candidates)
    {
        var list = candidates as IReadOnlyList<string> ?? [.. candidates];
        return TryLoad(list, out var module)
            ? module
            : throw new MediaBackendUnavailableException(
                backend,
                $"could not load the native library. Names tried: {string.Join(", ", list)}.");
    }

    /// <summary>Loads the first candidate that resolves, without throwing.</summary>
    public static bool TryLoad(IEnumerable<string> candidates, out NativeModule module)
    {
        var directories = NativeSearchPaths.Resolve();

        foreach (var candidate in candidates)
        {
            // Explicit paths first: they beat whatever the OS loader has cached.
            foreach (var directory in directories)
            {
                var full = Path.Combine(directory, candidate);
                if (File.Exists(full) && NativeLibrary.TryLoad(full, out var handle))
                {
                    module = new NativeModule(full, handle);
                    return true;
                }
            }

            // Then let the OS resolve it through its own search rules.
            if (NativeLibrary.TryLoad(candidate, out var systemHandle))
            {
                module = new NativeModule(candidate, systemHandle);
                return true;
            }
        }

        module = null!;
        return false;
    }

    /// <summary>Resolves an exported symbol.</summary>
    /// <exception cref="MediaToolkitNetException">The symbol is not exported by this module.</exception>
    public nint GetExport(string name)
    {
        return TryGetExport(name, out var address)
            ? address
            : throw new MediaToolkitNetException($"The library \"{FileName}\" does not export the symbol \"{name}\".");
    }

    /// <summary>Resolves an exported symbol without throwing.</summary>
    public bool TryGetExport(string name, out nint address)
    {
        lock (_exports)
        {
            if (_exports.TryGetValue(name, out address))
            {
                return address != 0;
            }

            NativeLibrary.TryGetExport(Handle, name, out address);
            _exports[name] = address;
            return address != 0;
        }
    }
}
