using MediaToolkitNet.Abstractions;
using MediaToolkitNet.FFmpeg.Native;

namespace MediaToolkitNet.FFmpeg;

/// <summary>Turns AVERROR codes into exceptions carrying FFmpeg's own message.</summary>
public static unsafe class FFmpegError
{
    /// <summary>Formats an AVERROR code using <c>av_strerror</c>.</summary>
    public static string Describe(int error)
    {
        const int bufferSize = 256;
        var buffer = stackalloc byte[bufferSize];
        return AV.av_strerror(error, buffer, bufferSize) == 0
            ? Interop.Utf8.ToManagedOrEmpty(buffer)
            : $"AVERROR {error}";
    }

    /// <summary>Throws when <paramref name="result"/> is negative; otherwise returns it.</summary>
    public static int Check(int result, string operation)
    {
        if (result < 0)
        {
            throw new MediaToolkitNetException(
                FFmpegLibraries.BackendName, $"{operation}: {Describe(result)}", result);
        }

        return result;
    }

    /// <summary>Throws when <paramref name="pointer"/> is null.</summary>
    public static void* CheckAlloc(void* pointer, string operation)
    {
        return pointer is null
            ? throw new MediaToolkitNetException(
                FFmpegLibraries.BackendName, $"{operation}: allocation failed", AVConstants.ErrorNoMemory)
            : pointer;
    }
}
