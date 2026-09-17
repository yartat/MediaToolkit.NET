using System.Runtime.InteropServices;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.Linux.Native;

/// <summary>pa_stream_direction_t.</summary>
public enum PulseDirection
{
    /// <summary>PA_STREAM_PLAYBACK.</summary>
    Playback = 1,

    /// <summary>PA_STREAM_RECORD.</summary>
    Record = 2,
}

/// <summary>pa_sample_format_t, restricted to the little-endian layouts this toolkit exposes.</summary>
public enum PulseSampleFormat
{
    /// <summary>PA_SAMPLE_INVALID.</summary>
    Invalid = -1,

    /// <summary>PA_SAMPLE_U8.</summary>
    U8 = 0,

    /// <summary>PA_SAMPLE_S16LE.</summary>
    S16Le = 3,

    /// <summary>PA_SAMPLE_FLOAT32LE.</summary>
    Float32Le = 5,

    /// <summary>PA_SAMPLE_S32LE.</summary>
    S32Le = 7,
}

/// <summary>Mirrors <c>pa_sample_spec</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PulseSampleSpec
{
    /// <summary>Sample layout.</summary>
    public int Format;

    /// <summary>Samples per second.</summary>
    public uint Rate;

    /// <summary>Channel count.</summary>
    public byte Channels;
}

/// <summary>
/// Bindings for the PulseAudio simple API.
/// </summary>
/// <remarks>
/// The simple API is synchronous and blocking, which matches the shape of
/// <see cref="Abstractions.Capture.IAudioRenderer"/> exactly. It also works unchanged on
/// PipeWire, whose PulseAudio server is a drop-in replacement.
/// </remarks>
public static unsafe class Pulse
{
    /// <summary>Backend name used in error messages.</summary>
    public const string BackendName = "pulseaudio";

    private static readonly Lock Gate = new();
    private static bool _initialised;
    private static Exception? _failure;

    /// <summary>The loaded libpulse-simple module.</summary>
    public static NativeModule Simple { get; private set; } = null!;

    /// <summary>The loaded libpulse module, which owns <c>pa_strerror</c>.</summary>
    public static NativeModule Core { get; private set; } = null!;

    /// <summary><c>pa_simple *pa_simple_new(const char *server, const char *name, pa_stream_direction_t, const char *dev, const char *stream_name, const pa_sample_spec *, const pa_channel_map *, const pa_buffer_attr *, int *error)</c></summary>
    public static delegate* unmanaged[Cdecl]<byte*, byte*, int, byte*, byte*, PulseSampleSpec*, void*, void*, int*, void*> pa_simple_new;

    /// <summary><c>void pa_simple_free(pa_simple *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void> pa_simple_free;

    /// <summary><c>int pa_simple_write(pa_simple *, const void *data, size_t bytes, int *error)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, nuint, int*, int> pa_simple_write;

    /// <summary><c>int pa_simple_read(pa_simple *, void *data, size_t bytes, int *error)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, nuint, int*, int> pa_simple_read;

    /// <summary><c>int pa_simple_drain(pa_simple *, int *error)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int*, int> pa_simple_drain;

    /// <summary><c>int pa_simple_flush(pa_simple *, int *error)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int*, int> pa_simple_flush;

    /// <summary><c>pa_usec_t pa_simple_get_latency(pa_simple *, int *error)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int*, ulong> pa_simple_get_latency;

    /// <summary><c>const char *pa_strerror(int error)</c></summary>
    public static delegate* unmanaged[Cdecl]<int, byte*> pa_strerror;

    /// <summary>True once libpulse has been found and every symbol resolved.</summary>
    public static bool IsAvailable
    {
        get
        {
            TryInitialise();
            return _failure is null;
        }
    }

    /// <summary>Loads libpulse if needed and throws when it is not usable.</summary>
    public static void EnsureLoaded()
    {
        TryInitialise();
        if (_failure is not null)
        {
            throw _failure as MediaBackendUnavailableException
                  ?? new MediaBackendUnavailableException(BackendName, "could not initialise libpulse.", _failure);
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
                if (!OperatingSystem.IsLinux())
                {
                    throw new MediaBackendUnavailableException(BackendName, "PulseAudio is only available on Linux.");
                }

                Simple = NativeModule.Load(BackendName, ["libpulse-simple.so.0", "libpulse-simple.so"]);
                Core = NativeModule.Load(BackendName, ["libpulse.so.0", "libpulse.so"]);
                Bind();
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

    private static void Bind()
    {
        pa_simple_new = (delegate* unmanaged[Cdecl]<byte*, byte*, int, byte*, byte*, PulseSampleSpec*, void*, void*, int*, void*>)
            Simple.GetExport(nameof(pa_simple_new));
        pa_simple_free = (delegate* unmanaged[Cdecl]<void*, void>)Simple.GetExport(nameof(pa_simple_free));
        pa_simple_write = (delegate* unmanaged[Cdecl]<void*, void*, nuint, int*, int>)Simple.GetExport(nameof(pa_simple_write));
        pa_simple_read = (delegate* unmanaged[Cdecl]<void*, void*, nuint, int*, int>)Simple.GetExport(nameof(pa_simple_read));
        pa_simple_drain = (delegate* unmanaged[Cdecl]<void*, int*, int>)Simple.GetExport(nameof(pa_simple_drain));
        pa_simple_flush = (delegate* unmanaged[Cdecl]<void*, int*, int>)Simple.GetExport(nameof(pa_simple_flush));
        pa_simple_get_latency = (delegate* unmanaged[Cdecl]<void*, int*, ulong>)Simple.GetExport(nameof(pa_simple_get_latency));
        pa_strerror = (delegate* unmanaged[Cdecl]<int, byte*>)Core.GetExport(nameof(pa_strerror));
    }

    /// <summary>Maps a toolkit sample format onto a PulseAudio sample format.</summary>
    public static PulseSampleFormat ToPulse(SampleFormat format) => format.ToInterleaved() switch
    {
        SampleFormat.U8 => PulseSampleFormat.U8,
        SampleFormat.S16 => PulseSampleFormat.S16Le,
        SampleFormat.S32 => PulseSampleFormat.S32Le,
        SampleFormat.F32 => PulseSampleFormat.Float32Le,
        _ => PulseSampleFormat.Invalid,
    };

    /// <summary>Formats a PulseAudio error code using <c>pa_strerror</c>.</summary>
    public static string Describe(int error) => Utf8.ToManagedOrEmpty(pa_strerror(error));

    /// <summary>Throws when <paramref name="result"/> is negative.</summary>
    public static void Check(int result, int error, string operation)
    {
        if (result < 0)
        {
            throw new MediaToolkitNetException(BackendName, $"{operation}: {Describe(error)}", error);
        }
    }
}
