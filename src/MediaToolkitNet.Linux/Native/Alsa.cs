using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.Linux.Native;

/// <summary>SND_PCM_STREAM_*.</summary>
public enum AlsaStream
{
    /// <summary>SND_PCM_STREAM_PLAYBACK.</summary>
    Playback = 0,

    /// <summary>SND_PCM_STREAM_CAPTURE.</summary>
    Capture = 1,
}

/// <summary>SND_PCM_FORMAT_*, restricted to the little-endian layouts this toolkit exposes.</summary>
public enum AlsaFormat
{
    /// <summary>SND_PCM_FORMAT_UNKNOWN.</summary>
    Unknown = -1,

    /// <summary>SND_PCM_FORMAT_U8.</summary>
    U8 = 1,

    /// <summary>SND_PCM_FORMAT_S16_LE.</summary>
    S16Le = 2,

    /// <summary>SND_PCM_FORMAT_S32_LE.</summary>
    S32Le = 10,

    /// <summary>SND_PCM_FORMAT_FLOAT_LE.</summary>
    FloatLe = 14,

    /// <summary>SND_PCM_FORMAT_FLOAT64_LE.</summary>
    Float64Le = 16,
}

/// <summary>
/// Bindings for the libasound PCM API.
/// </summary>
/// <remarks>
/// ALSA hides its structures behind opaque pointers and accessor functions, so
/// this binding needs no struct layouts at all: the whole configuration goes
/// through <c>snd_pcm_hw_params_*</c>.
/// </remarks>
public static unsafe class Alsa
{
    /// <summary>Backend name used in error messages.</summary>
    public const string BackendName = "alsa";

    /// <summary>SND_PCM_ACCESS_RW_INTERLEAVED.</summary>
    public const int AccessRwInterleaved = 3;

    /// <summary>SND_PCM_NONBLOCK.</summary>
    public const int NonBlock = 1;

    private static readonly Lock Gate = new();
    private static bool _initialised;
    private static Exception? _failure;

    /// <summary>The loaded libasound module.</summary>
    public static NativeModule Module { get; private set; } = null!;

    /// <summary><c>int snd_pcm_open(snd_pcm_t **, const char *name, snd_pcm_stream_t, int mode)</c></summary>
    public static delegate* unmanaged[Cdecl]<void**, byte*, int, int, int> snd_pcm_open;

    /// <summary><c>int snd_pcm_close(snd_pcm_t *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int> snd_pcm_close;

    /// <summary><c>int snd_pcm_hw_params_malloc(snd_pcm_hw_params_t **)</c></summary>
    public static delegate* unmanaged[Cdecl]<void**, int> snd_pcm_hw_params_malloc;

    /// <summary><c>void snd_pcm_hw_params_free(snd_pcm_hw_params_t *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void> snd_pcm_hw_params_free;

    /// <summary><c>int snd_pcm_hw_params_any(snd_pcm_t *, snd_pcm_hw_params_t *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, int> snd_pcm_hw_params_any;

    /// <summary><c>int snd_pcm_hw_params_set_access(snd_pcm_t *, snd_pcm_hw_params_t *, snd_pcm_access_t)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, int, int> snd_pcm_hw_params_set_access;

    /// <summary><c>int snd_pcm_hw_params_set_format(snd_pcm_t *, snd_pcm_hw_params_t *, snd_pcm_format_t)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, int, int> snd_pcm_hw_params_set_format;

    /// <summary><c>int snd_pcm_hw_params_set_channels(snd_pcm_t *, snd_pcm_hw_params_t *, unsigned int)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, uint, int> snd_pcm_hw_params_set_channels;

    /// <summary><c>int snd_pcm_hw_params_set_rate_near(snd_pcm_t *, snd_pcm_hw_params_t *, unsigned int *, int *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, uint*, int*, int> snd_pcm_hw_params_set_rate_near;

    /// <summary><c>int snd_pcm_hw_params_set_buffer_time_near(snd_pcm_t *, snd_pcm_hw_params_t *, unsigned int *, int *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, uint*, int*, int> snd_pcm_hw_params_set_buffer_time_near;

    /// <summary><c>int snd_pcm_hw_params_set_period_time_near(snd_pcm_t *, snd_pcm_hw_params_t *, unsigned int *, int *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, uint*, int*, int> snd_pcm_hw_params_set_period_time_near;

    /// <summary><c>int snd_pcm_hw_params(snd_pcm_t *, snd_pcm_hw_params_t *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, int> snd_pcm_hw_params;

    /// <summary><c>int snd_pcm_hw_params_get_period_size(const snd_pcm_hw_params_t *, snd_pcm_uframes_t *, int *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, nuint*, int*, int> snd_pcm_hw_params_get_period_size;

    /// <summary><c>int snd_pcm_hw_params_get_buffer_size(const snd_pcm_hw_params_t *, snd_pcm_uframes_t *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, nuint*, int> snd_pcm_hw_params_get_buffer_size;

    /// <summary><c>int snd_pcm_prepare(snd_pcm_t *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int> snd_pcm_prepare;

    /// <summary><c>int snd_pcm_start(snd_pcm_t *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int> snd_pcm_start;

    /// <summary><c>int snd_pcm_drop(snd_pcm_t *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int> snd_pcm_drop;

    /// <summary><c>int snd_pcm_drain(snd_pcm_t *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int> snd_pcm_drain;

    /// <summary><c>snd_pcm_sframes_t snd_pcm_writei(snd_pcm_t *, const void *, snd_pcm_uframes_t)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, nuint, nint> snd_pcm_writei;

    /// <summary><c>snd_pcm_sframes_t snd_pcm_readi(snd_pcm_t *, void *, snd_pcm_uframes_t)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void*, nuint, nint> snd_pcm_readi;

    /// <summary><c>int snd_pcm_recover(snd_pcm_t *, int err, int silent)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int, int, int> snd_pcm_recover;

    /// <summary><c>const char *snd_strerror(int)</c></summary>
    public static delegate* unmanaged[Cdecl]<int, byte*> snd_strerror;

    /// <summary><c>int snd_device_name_hint(int card, const char *iface, void ***hints)</c></summary>
    public static delegate* unmanaged[Cdecl]<int, byte*, void***, int> snd_device_name_hint;

    /// <summary><c>char *snd_device_name_get_hint(const void *hint, const char *id)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, byte*> snd_device_name_get_hint;

    /// <summary><c>int snd_device_name_free_hint(void **hints)</c></summary>
    public static delegate* unmanaged[Cdecl]<void**, int> snd_device_name_free_hint;

    /// <summary>True once libasound has been found and every symbol resolved.</summary>
    public static bool IsAvailable
    {
        get
        {
            TryInitialise();
            return _failure is null;
        }
    }

    /// <summary>Loads libasound if needed and throws when it is not usable.</summary>
    public static void EnsureLoaded()
    {
        TryInitialise();
        if (_failure is not null)
        {
            throw _failure as MediaBackendUnavailableException
                  ?? new MediaBackendUnavailableException(BackendName, "could not initialise libasound.", _failure);
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
                    throw new MediaBackendUnavailableException(BackendName, "ALSA is only available on Linux.");
                }

                Module = NativeModule.Load(BackendName, ["libasound.so.2", "libasound.so"]);
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
        snd_pcm_open = (delegate* unmanaged[Cdecl]<void**, byte*, int, int, int>)Module.GetExport(nameof(snd_pcm_open));
        snd_pcm_close = (delegate* unmanaged[Cdecl]<void*, int>)Module.GetExport(nameof(snd_pcm_close));
        snd_pcm_hw_params_malloc = (delegate* unmanaged[Cdecl]<void**, int>)Module.GetExport(nameof(snd_pcm_hw_params_malloc));
        snd_pcm_hw_params_free = (delegate* unmanaged[Cdecl]<void*, void>)Module.GetExport(nameof(snd_pcm_hw_params_free));
        snd_pcm_hw_params_any = (delegate* unmanaged[Cdecl]<void*, void*, int>)Module.GetExport(nameof(snd_pcm_hw_params_any));
        snd_pcm_hw_params_set_access = (delegate* unmanaged[Cdecl]<void*, void*, int, int>)Module.GetExport(nameof(snd_pcm_hw_params_set_access));
        snd_pcm_hw_params_set_format = (delegate* unmanaged[Cdecl]<void*, void*, int, int>)Module.GetExport(nameof(snd_pcm_hw_params_set_format));
        snd_pcm_hw_params_set_channels = (delegate* unmanaged[Cdecl]<void*, void*, uint, int>)Module.GetExport(nameof(snd_pcm_hw_params_set_channels));
        snd_pcm_hw_params_set_rate_near = (delegate* unmanaged[Cdecl]<void*, void*, uint*, int*, int>)Module.GetExport(nameof(snd_pcm_hw_params_set_rate_near));
        snd_pcm_hw_params_set_buffer_time_near = (delegate* unmanaged[Cdecl]<void*, void*, uint*, int*, int>)Module.GetExport(nameof(snd_pcm_hw_params_set_buffer_time_near));
        snd_pcm_hw_params_set_period_time_near = (delegate* unmanaged[Cdecl]<void*, void*, uint*, int*, int>)Module.GetExport(nameof(snd_pcm_hw_params_set_period_time_near));
        snd_pcm_hw_params = (delegate* unmanaged[Cdecl]<void*, void*, int>)Module.GetExport(nameof(snd_pcm_hw_params));
        snd_pcm_hw_params_get_period_size = (delegate* unmanaged[Cdecl]<void*, nuint*, int*, int>)Module.GetExport(nameof(snd_pcm_hw_params_get_period_size));
        snd_pcm_hw_params_get_buffer_size = (delegate* unmanaged[Cdecl]<void*, nuint*, int>)Module.GetExport(nameof(snd_pcm_hw_params_get_buffer_size));
        snd_pcm_prepare = (delegate* unmanaged[Cdecl]<void*, int>)Module.GetExport(nameof(snd_pcm_prepare));
        snd_pcm_start = (delegate* unmanaged[Cdecl]<void*, int>)Module.GetExport(nameof(snd_pcm_start));
        snd_pcm_drop = (delegate* unmanaged[Cdecl]<void*, int>)Module.GetExport(nameof(snd_pcm_drop));
        snd_pcm_drain = (delegate* unmanaged[Cdecl]<void*, int>)Module.GetExport(nameof(snd_pcm_drain));
        snd_pcm_writei = (delegate* unmanaged[Cdecl]<void*, void*, nuint, nint>)Module.GetExport(nameof(snd_pcm_writei));
        snd_pcm_readi = (delegate* unmanaged[Cdecl]<void*, void*, nuint, nint>)Module.GetExport(nameof(snd_pcm_readi));
        snd_pcm_recover = (delegate* unmanaged[Cdecl]<void*, int, int, int>)Module.GetExport(nameof(snd_pcm_recover));
        snd_strerror = (delegate* unmanaged[Cdecl]<int, byte*>)Module.GetExport(nameof(snd_strerror));
        snd_device_name_hint = (delegate* unmanaged[Cdecl]<int, byte*, void***, int>)Module.GetExport(nameof(snd_device_name_hint));
        snd_device_name_get_hint = (delegate* unmanaged[Cdecl]<void*, byte*, byte*>)Module.GetExport(nameof(snd_device_name_get_hint));
        snd_device_name_free_hint = (delegate* unmanaged[Cdecl]<void**, int>)Module.GetExport(nameof(snd_device_name_free_hint));
    }

    /// <summary>Maps a toolkit sample format onto an ALSA PCM format.</summary>
    public static AlsaFormat ToAlsa(SampleFormat format) => format.ToInterleaved() switch
    {
        SampleFormat.U8 => AlsaFormat.U8,
        SampleFormat.S16 => AlsaFormat.S16Le,
        SampleFormat.S32 => AlsaFormat.S32Le,
        SampleFormat.F32 => AlsaFormat.FloatLe,
        SampleFormat.F64 => AlsaFormat.Float64Le,
        _ => AlsaFormat.Unknown,
    };

    /// <summary>Formats an ALSA error code using <c>snd_strerror</c>.</summary>
    public static string Describe(int error) => Utf8.ToManagedOrEmpty(snd_strerror(error));

    /// <summary>Throws when <paramref name="result"/> is a negative ALSA error code.</summary>
    public static int Check(int result, string operation)
    {
        return result < 0
            ? throw new MediaToolkitNetException(BackendName, $"{operation}: {Describe(result)}", result)
            : result;
    }
}
