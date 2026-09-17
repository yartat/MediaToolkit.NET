using MediaToolkitNet.Core;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg.Native;

/// <summary>
/// Locates and loads the five FFmpeg shared libraries this backend needs, plus
/// the optional libavdevice.
/// </summary>
/// <remarks>
/// <para>
/// Only FFmpeg 7.x is supported. FFmpeg does not keep a stable ABI for its
/// public structs across major releases, and this binding reads a small number
/// of struct fields directly; see <see cref="AbiLayout"/> for the full list and
/// for the self-check that runs before any of them is used.
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

    /// <summary>libavutil major version this binding targets.</summary>
    public const int AvUtilMajor = 59;

    /// <summary>libavcodec major version this binding targets.</summary>
    public const int AvCodecMajor = 61;

    /// <summary>libavformat major version this binding targets.</summary>
    public const int AvFormatMajor = 61;

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

    /// <summary>Version string of the loaded stack, e.g. <c>avutil 59.39.100 / avcodec 61.19.101</c>.</summary>
    public static string? VersionString { get; private set; }

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

        AvUtil = NativeModule.Load(BackendName, Candidates("avutil", AvUtilMajor));
        AvCodec = NativeModule.Load(BackendName, Candidates("avcodec", AvCodecMajor));
        AvFormat = NativeModule.Load(BackendName, Candidates("avformat", AvFormatMajor));
        SwScale = NativeModule.Load(BackendName, Candidates("swscale", 8));
        SwResample = NativeModule.Load(BackendName, Candidates("swresample", 5));
        _ = NativeModule.TryLoad(Candidates("avdevice", AvDeviceMajor), out var avdevice);
        AvDevice = avdevice;

        AV.Bind();

        var utilVersion = AV.avutil_version();
        var codecVersion = AV.avcodec_version();
        var formatVersion = AV.avformat_version();
        VersionString =
            $"avutil {Describe(utilVersion)} / avcodec {Describe(codecVersion)} / avformat {Describe(formatVersion)}";

        RequireMajor("libavutil", utilVersion, AvUtilMajor);
        RequireMajor("libavcodec", codecVersion, AvCodecMajor);
        RequireMajor("libavformat", formatVersion, AvFormatMajor);

        AbiLayout.Validate();

        if (AvDevice is not null)
        {
            AV.avdevice_register_all();
        }
    }

    // libavdevice tracks libavformat's major version offset by one.
    private const int AvDeviceMajor = 61;

    private static void RequireMajor(string library, uint version, int expected)
    {
        var major = (int)(version >> 16);
        if (major != expected)
        {
            throw new MediaBackendUnavailableException(
                BackendName,
                $"{library} has major version {major}, but these bindings target {expected}. " +
                "Install FFmpeg 7.x, or update the offsets in AbiLayout.");
        }
    }

    private static string Describe(uint version) =>
        $"{version >> 16}.{(version >> 8) & 0xFF}.{version & 0xFF}";

    /// <summary>
    /// Builds the file name candidates for one FFmpeg library, newest major
    /// first. Windows uses <c>avcodec-61.dll</c>, Linux <c>libavcodec.so.61</c>
    /// and macOS <c>libavcodec.61.dylib</c>.
    /// </summary>
    private static List<string> Candidates(string name, int major)
    {
        var result = new List<string>(4);
        if (OperatingSystem.IsWindows())
        {
            result.Add($"{name}-{major}.dll");
            result.Add($"{name}.dll");
        }
        else if (OperatingSystem.IsMacOS())
        {
            result.Add($"lib{name}.{major}.dylib");
            result.Add($"lib{name}.dylib");
        }
        else
        {
            result.Add($"lib{name}.so.{major}");
            result.Add($"lib{name}.so");
        }

        return result;
    }
}
