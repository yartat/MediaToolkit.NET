using MediaToolkitNet.FFmpeg.Native;
using MediaToolkitNet.Interop;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// Finds the FFmpeg libraries these tests run against and loads them once.
/// </summary>
/// <remarks>
/// Point <c>MEDIATOOLKITNET_FFMPEG_DIR</c> at a directory holding the shared
/// libraries to test a build that is not on the system search path, e.g. a
/// checkout of FFmpeg 9 beside one of FFmpeg 7. Without it the usual search
/// applies: next to the test assembly, in its <c>native</c> folder, and on the
/// system path.
/// </remarks>
public static class FFmpegEnvironment
{
    /// <summary>The environment variable that points the tests at a build.</summary>
    public const string DirectoryVariable = "MEDIATOOLKITNET_FFMPEG_DIR";

    private static readonly Lock Gate = new();
    private static bool _attempted;
    private static string? _failure;

    /// <summary>True when FFmpeg loaded and its ABI self-check passed.</summary>
    public static bool IsAvailable
    {
        get
        {
            Load();
            return _failure is null;
        }
    }

    /// <summary>Why the tests cannot run, or <see langword="null"/> when they can.</summary>
    public static string? SkipReason
    {
        get
        {
            Load();
            return _failure;
        }
    }

    /// <summary>The release series that was loaded.</summary>
    public static FFmpegGeneration Series
    {
        get
        {
            Load();
            return FFmpegLibraries.Generation
                ?? throw new InvalidOperationException("FFmpeg is not loaded; the test should have been skipped.");
        }
    }

    /// <summary>The versions that were loaded, for a failure message.</summary>
    public static string Versions
    {
        get
        {
            Load();
            return FFmpegLibraries.VersionString ?? "nothing";
        }
    }

    private static void Load()
    {
        if (_attempted)
        {
            return;
        }

        lock (Gate)
        {
            if (_attempted)
            {
                return;
            }

            _attempted = true;

            var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                if (!Directory.Exists(directory))
                {
                    _failure = $"{DirectoryVariable} points at {directory}, which does not exist.";
                    return;
                }

                NativeSearchPaths.Prepend(directory);
            }

            try
            {
                FFmpegLibraries.EnsureLoaded();
            }
            catch (Exception exception)
            {
                _failure =
                    $"FFmpeg is not usable here: {exception.Message} " +
                    $"Install FFmpeg 7.x, 8.x or 9.x, or set {DirectoryVariable} to the directory holding it.";
            }
        }
    }
}

/// <summary>A fact that is skipped unless FFmpeg is loadable.</summary>
public sealed class FFmpegFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="FFmpegFactAttribute"/> class.</summary>
    public FFmpegFactAttribute() => Skip = FFmpegEnvironment.SkipReason;
}

/// <summary>A theory that is skipped unless FFmpeg is loadable.</summary>
public sealed class FFmpegTheoryAttribute : TheoryAttribute
{
    /// <summary>Initializes a new instance of the <see cref="FFmpegTheoryAttribute"/> class.</summary>
    public FFmpegTheoryAttribute() => Skip = FFmpegEnvironment.SkipReason;
}
