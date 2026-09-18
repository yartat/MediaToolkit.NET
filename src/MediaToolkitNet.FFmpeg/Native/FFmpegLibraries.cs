using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg.Native;

/// <summary>
/// Locates and loads the five FFmpeg shared libraries this backend needs, plus
/// the optional libavdevice.
/// </summary>
/// <remarks>
/// <para>
/// FFmpeg 7.x, 8.x and 9.x are supported. FFmpeg does not keep a stable ABI for
/// its public structs across major releases, and this binding reads a small
/// number of struct fields directly, so the series that was loaded is
/// established first and the offsets that differ between series come from
/// <see cref="FFmpegGeneration"/>; see <see cref="AbiLayout"/> for the full list
/// and for the self-check that runs before any of them is used.
/// </para>
/// <para>
/// Place the native libraries in <c>src/FFMpeg/{Windows,Linux,MacOS}</c>, next
/// to the application, or anywhere on the system search path. Extra directories
/// can be registered through <see cref="NativeSearchPaths.Prepend"/>.
/// </para>
/// </remarks>
public static class FFmpegLibraries
{
    /// <summary>Backend name used in error messages.</summary>
    public const string BackendName = "ffmpeg";

    private static readonly Lock Gate = new();
    private static bool _initialised;
    private static Exception? _failure;

    /// <summary>libavutil, once loaded.</summary>
    public static NativeModule AvUtil { get; private set; } = null!;

    /// <summary>libavcodec, once loaded.</summary>
    public static NativeModule AvCodec { get; private set; } = null!;

    /// <summary>libavformat, once loaded.</summary>
    public static NativeModule AvFormat { get; private set; } = null!;

    /// <summary>libswscale, once loaded.</summary>
    public static NativeModule SwScale { get; private set; } = null!;

    /// <summary>libswresample, once loaded.</summary>
    public static NativeModule SwResample { get; private set; } = null!;

    /// <summary>libavdevice, or <see langword="null"/> when the build does not ship it.</summary>
    public static NativeModule? AvDevice { get; private set; }

    /// <summary>libavfilter, or <see langword="null"/> when the build does not ship it.</summary>
    public static NativeModule? AvFilter { get; private set; }

    /// <summary>Version string of the loaded stack, e.g. <c>avutil 59.39.100 / avcodec 61.19.101</c>.</summary>
    public static string? VersionString { get; private set; }

    /// <summary>The release series that was loaded, once the versions have been read.</summary>
    public static FFmpegGeneration? Generation { get; private set; }

    /// <summary>True once every required library has been loaded and the ABI self-check has passed.</summary>
    public static bool IsAvailable
    {
        get
        {
            TryInitialise();
            return _failure is null;
        }
    }

    /// <summary>Loads the libraries if needed and throws when they are not usable.</summary>
    /// <exception cref="MediaBackendUnavailableException">FFmpeg is missing or has the wrong ABI.</exception>
    public static void EnsureLoaded()
    {
        TryInitialise();
        if (_failure is not null)
        {
            throw _failure as MediaBackendUnavailableException
                  ?? new MediaBackendUnavailableException(BackendName, "could not initialise FFmpeg.", _failure);
        }
    }

    private static void TryInitialise()
    {
        if (_initialised)
        {
            return;
        }

        lock (Gate)
        {
            if (_initialised)
            {
                return;
            }

            try
            {
                Initialise();
            }
            catch (Exception ex)
            {
                _failure = ex;
            }
            finally
            {
                _initialised = true;
            }
        }
    }

    private static unsafe void Initialise()
    {
        if (nint.Size != 8)
        {
            throw new MediaBackendUnavailableException(
                BackendName, "only 64-bit processes are supported: the FFmpeg struct layout assumes 64 bits.");
        }

        AvUtil = NativeModule.Load(BackendName, Candidates("avutil", g => g.AvUtil));
        AvCodec = NativeModule.Load(BackendName, Candidates("avcodec", g => g.AvCodec));
        AvFormat = NativeModule.Load(BackendName, Candidates("avformat", g => g.AvFormat));
        SwScale = NativeModule.Load(BackendName, Candidates("swscale", g => g.SwScale));
        SwResample = NativeModule.Load(BackendName, Candidates("swresample", g => g.SwResample));
        _ = NativeModule.TryLoad(Candidates("avdevice", g => g.AvDevice), out var avdevice);
        AvDevice = avdevice;

        AV.Bind();

        var utilVersion = AV.avutil_version();
        var codecVersion = AV.avcodec_version();
        var formatVersion = AV.avformat_version();
        VersionString =
            $"avutil {Describe(utilVersion)} / avcodec {Describe(codecVersion)} / avformat {Describe(formatVersion)}";

        // Which series was loaded decides the offsets, and libraries from two of
        // them in one process would be read with the offsets of neither.
        var generation = FFmpegGeneration.Find(MajorOf(utilVersion), MajorOf(codecVersion), MajorOf(formatVersion))
            ?? throw new MediaBackendUnavailableException(
                BackendName,
                $"the FFmpeg libraries found are {VersionString}, which is not a release series these bindings " +
                $"know. Supported: {string.Join(", ", FFmpegGeneration.Known)}. Install one of those, or add the " +
                "series to FFmpegGeneration together with its offsets.");

        Generation = generation;
        AbiLayout.Use(generation);

        // libavfilter is optional and is looked for only in the series that was
        // loaded: a graph built by one major against the structs of another is
        // exactly the mismatch the version gate above exists to prevent.
        _ = NativeModule.TryLoad([Named("avfilter", generation.AvFilter)], out var avfilter);
        AvFilter = avfilter;
        AV.BindFilters();

        AbiLayout.Validate();

        if (AvDevice is not null)
        {
            AV.avdevice_register_all();
        }
    }

    private static int MajorOf(uint version) => (int)(version >> 16);

    private static string Describe(uint version) =>
        $"{version >> 16}.{(version >> 8) & 0xFF}.{version & 0xFF}";

    /// <summary>
    /// Builds the file name candidates for one FFmpeg library, one per supported
    /// series with the newest first, then the unversioned name. Windows uses
    /// <c>avcodec-63.dll</c>, Linux <c>libavcodec.so.63</c> and macOS
    /// <c>libavcodec.63.dylib</c>.
    /// </summary>
    /// <param name="name">The library, without prefix or extension.</param>
    /// <param name="major">Picks that library's major version out of a series.</param>
    /// <returns>Returns the candidates, in the order they should be tried.</returns>
    private static List<string> Candidates(string name, Func<FFmpegGeneration, int> major)
    {
        var result = new List<string>(FFmpegGeneration.Known.Count + 1);
        foreach (var generation in FFmpegGeneration.Known)
        {
            result.Add(Named(name, major(generation)));
        }

        result.Add(Unversioned(name));
        return result;
    }

    private static string Named(string name, int major) =>
        OperatingSystem.IsWindows() ? $"{name}-{major}.dll"
        : OperatingSystem.IsMacOS() ? $"lib{name}.{major}.dylib"
        : $"lib{name}.so.{major}";

    private static string Unversioned(string name) =>
        OperatingSystem.IsWindows() ? $"{name}.dll"
        : OperatingSystem.IsMacOS() ? $"lib{name}.dylib"
        : $"lib{name}.so";
}
