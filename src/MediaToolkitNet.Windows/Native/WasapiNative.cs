using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Core.Formats;
using MediaToolkitNet.Interop.Com;

namespace MediaToolkitNet.Windows.Native;

/// <summary>EDataFlow.</summary>
public enum DataFlow
{
    /// <summary>eRender: playback endpoints.</summary>
    Render = 0,

    /// <summary>eCapture: recording endpoints.</summary>
    Capture = 1,

    /// <summary>eAll.</summary>
    All = 2,
}

/// <summary>ERole.</summary>
public enum DeviceRole
{
    /// <summary>eConsole.</summary>
    Console = 0,

    /// <summary>eMultimedia.</summary>
    Multimedia = 1,

    /// <summary>eCommunications.</summary>
    Communications = 2,
}

/// <summary>AUDCLNT_SHAREMODE.</summary>
public enum AudioShareMode
{
    /// <summary>AUDCLNT_SHAREMODE_SHARED.</summary>
    Shared = 0,

    /// <summary>AUDCLNT_SHAREMODE_EXCLUSIVE.</summary>
    Exclusive = 1,
}

/// <summary>Mirrors WAVEFORMATEX.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WaveFormatEx
{
    /// <summary>WAVE_FORMAT_* tag.</summary>
    public ushort FormatTag;

    /// <summary>Channel count.</summary>
    public ushort Channels;

    /// <summary>Samples per second.</summary>
    public uint SamplesPerSec;

    /// <summary>Bytes per second.</summary>
    public uint AvgBytesPerSec;

    /// <summary>Bytes per sample frame.</summary>
    public ushort BlockAlign;

    /// <summary>Bits per sample of one channel.</summary>
    public ushort BitsPerSample;

    /// <summary>Size of the extension that follows, in bytes.</summary>
    public ushort Size;

    /// <summary>WAVE_FORMAT_PCM.</summary>
    public const ushort Pcm = 0x0001;

    /// <summary>WAVE_FORMAT_IEEE_FLOAT.</summary>
    public const ushort IeeeFloat = 0x0003;

    /// <summary>WAVE_FORMAT_EXTENSIBLE.</summary>
    public const ushort Extensible = 0xFFFE;

    /// <summary>Builds a plain WAVEFORMATEX describing <paramref name="format"/>.</summary>
    public static WaveFormatEx From(AudioFormat format)
    {
        var bits = (ushort)(format.SampleFormat.BytesPerSample() * 8);
        var blockAlign = (ushort)(bits / 8 * format.Channels);
        return new WaveFormatEx
        {
            FormatTag = format.SampleFormat.IsFloat() ? IeeeFloat : Pcm,
            Channels = (ushort)format.Channels,
            SamplesPerSec = (uint)format.SampleRate,
            BitsPerSample = bits,
            BlockAlign = blockAlign,
            AvgBytesPerSec = (uint)(blockAlign * format.SampleRate),
            Size = 0,
        };
    }

    /// <summary>
    /// Interprets the structure as a toolkit audio format. WAVE_FORMAT_EXTENSIBLE
    /// is resolved through its sub-format GUID by <see cref="Wasapi.ReadFormat"/>.
    /// </summary>
    public readonly AudioFormat ToAudioFormat(bool isFloat) => new(
        (int)SamplesPerSec,
        Channels,
        (isFloat, BitsPerSample) switch
        {
            (true, 32) => SampleFormat.F32,
            (true, 64) => SampleFormat.F64,
            (false, 8) => SampleFormat.U8,
            (false, 16) => SampleFormat.S16,
            (false, 32) => SampleFormat.S32,
            _ => SampleFormat.Unknown,
        });
}

/// <summary>
/// Vtable wrappers for the Core Audio interfaces used by the WASAPI backend.
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe class Wasapi
{
    /// <summary>AUDCLNT_STREAMFLAGS_EVENTCALLBACK.</summary>
    public const uint StreamFlagsEventCallback = 0x00040000;

    /// <summary>AUDCLNT_STREAMFLAGS_LOOPBACK, which turns a render endpoint into a capture source.</summary>
    public const uint StreamFlagsLoopback = 0x00020000;

    /// <summary>AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM.</summary>
    public const uint StreamFlagsAutoConvertPcm = 0x80000000;

    /// <summary>AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY.</summary>
    public const uint StreamFlagsSrcDefaultQuality = 0x08000000;

    /// <summary>AUDCLNT_BUFFERFLAGS_SILENT.</summary>
    public const uint BufferFlagsSilent = 0x2;

    /// <summary>DEVICE_STATE_ACTIVE.</summary>
    public const uint DeviceStateActive = 0x1;

    /// <summary>AUDCLNT_E_DEVICE_INVALIDATED.</summary>
    public const int DeviceInvalidated = unchecked((int)0x88890004);

    /// <summary>AUDCLNT_E_UNSUPPORTED_FORMAT.</summary>
    public const int UnsupportedFormat = unchecked((int)0x88890008);

    // IMMDeviceEnumerator : IUnknown.
    private const int EnumeratorEnumAudioEndpoints = 3;
    private const int EnumeratorGetDefaultAudioEndpoint = 4;
    private const int EnumeratorGetDevice = 5;

    // IMMDeviceCollection : IUnknown.
    private const int CollectionGetCount = 3;
    private const int CollectionItem = 4;

    // IMMDevice : IUnknown.
    private const int DeviceActivate = 3;
    private const int DeviceOpenPropertyStore = 4;
    private const int DeviceGetId = 5;
    private const int DeviceGetState = 6;

    // IPropertyStore : IUnknown.
    private const int StoreGetValue = 5;

    // IAudioClient : IUnknown.
    private const int ClientInitialize = 3;
    private const int ClientGetBufferSize = 4;
    private const int ClientGetCurrentPadding = 6;
    private const int ClientIsFormatSupported = 7;
    private const int ClientGetMixFormat = 8;
    private const int ClientGetDevicePeriod = 9;
    private const int ClientStart = 10;
    private const int ClientStop = 11;
    private const int ClientReset = 12;
    private const int ClientSetEventHandle = 13;
    private const int ClientGetService = 14;

    // IAudioRenderClient : IUnknown.
    private const int RenderGetBuffer = 3;
    private const int RenderReleaseBuffer = 4;

    // IAudioCaptureClient : IUnknown.
    private const int CaptureGetBuffer = 3;
    private const int CaptureReleaseBuffer = 4;
    private const int CaptureGetNextPacketSize = 5;

    /// <summary>PKEY_Device_FriendlyName, as a PROPERTYKEY.</summary>
    public static readonly Guid DeviceFriendlyNameCategory = new("a45c254e-df1c-4efd-8020-67d146a850e0");

    /// <summary>Property id of PKEY_Device_FriendlyName.</summary>
    public const uint DeviceFriendlyNameId = 14;

    /// <summary>Creates the device enumerator.</summary>
    public static ComPtr CreateDeviceEnumerator() =>
        Ole32.CreateInstance(WinGuids.MMDeviceEnumerator, WinGuids.IMMDeviceEnumerator);

    /// <summary>IMMDeviceEnumerator::EnumAudioEndpoints.</summary>
    public static int EnumAudioEndpoints(void* self, DataFlow flow, uint stateMask, out void* collection)
    {
        fixed (void** pCollection = &collection)
        {
            return ((delegate* unmanaged[Stdcall]<void*, int, uint, void**, int>)
                Com.Slot(self, EnumeratorEnumAudioEndpoints))(self, (int)flow, stateMask, pCollection);
        }
    }

    /// <summary>IMMDeviceEnumerator::GetDefaultAudioEndpoint.</summary>
    public static int GetDefaultAudioEndpoint(void* self, DataFlow flow, DeviceRole role, out void* device)
    {
        fixed (void** pDevice = &device)
        {
            return ((delegate* unmanaged[Stdcall]<void*, int, int, void**, int>)
                Com.Slot(self, EnumeratorGetDefaultAudioEndpoint))(self, (int)flow, (int)role, pDevice);
        }
    }

    /// <summary>IMMDeviceEnumerator::GetDevice.</summary>
    public static int GetDevice(void* self, string id, out void* device)
    {
        fixed (char* pId = id)
        fixed (void** pDevice = &device)
        {
            return ((delegate* unmanaged[Stdcall]<void*, char*, void**, int>)
                Com.Slot(self, EnumeratorGetDevice))(self, pId, pDevice);
        }
    }

    /// <summary>IMMDeviceCollection::GetCount.</summary>
    public static int GetCount(void* self, out uint count)
    {
        fixed (uint* pCount = &count)
        {
            return ((delegate* unmanaged[Stdcall]<void*, uint*, int>)Com.Slot(self, CollectionGetCount))(self, pCount);
        }
    }

    /// <summary>IMMDeviceCollection::Item.</summary>
    public static int Item(void* self, uint index, out void* device)
    {
        fixed (void** pDevice = &device)
        {
            return ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Com.Slot(self, CollectionItem))(
                self, index, pDevice);
        }
    }

    /// <summary>IMMDevice::Activate.</summary>
    public static int Activate(void* self, in Guid iid, out void* result)
    {
        fixed (Guid* pIid = &iid)
        fixed (void** pResult = &result)
        {
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, uint, void*, void**, int>)
                Com.Slot(self, DeviceActivate))(self, pIid, Ole32.ClsCtxAll, null, pResult);
        }
    }

    /// <summary>IMMDevice::GetState.</summary>
    public static int GetState(void* self, out uint state)
    {
        fixed (uint* pState = &state)
        {
            return ((delegate* unmanaged[Stdcall]<void*, uint*, int>)Com.Slot(self, DeviceGetState))(self, pState);
        }
    }

    /// <summary>IMMDevice::GetId, returning a managed copy.</summary>
    public static string? GetId(void* self)
    {
        char* id;
        var hr = ((delegate* unmanaged[Stdcall]<void*, char**, int>)Com.Slot(self, DeviceGetId))(self, &id);
        if (HResult.Failed(hr) || id is null)
        {
            return null;
        }

        try
        {
            return new string(id);
        }
        finally
        {
            Ole32.CoTaskMemFree(id);
        }
    }

    /// <summary>Reads PKEY_Device_FriendlyName through IMMDevice::OpenPropertyStore.</summary>
    public static string? GetFriendlyName(void* device)
    {
        void* store;
        // STGM_READ is 0.
        var hr = ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Com.Slot(device, DeviceOpenPropertyStore))(
            device, 0, &store);
        if (HResult.Failed(hr))
        {
            return null;
        }

        try
        {
            // PROPERTYKEY is a GUID followed by a DWORD property id.
            var key = stackalloc byte[24];
            *(Guid*)key = DeviceFriendlyNameCategory;
            *(uint*)(key + 16) = DeviceFriendlyNameId;

            // PROPVARIANT is 24 bytes on 64-bit; VT_LPWSTR stores its pointer at offset 8.
            var value = stackalloc byte[24];
            new Span<byte>(value, 24).Clear();

            hr = ((delegate* unmanaged[Stdcall]<void*, void*, void*, int>)Com.Slot(store, StoreGetValue))(
                store, key, value);
            if (HResult.Failed(hr))
            {
                return null;
            }

            try
            {
                const ushort vtLpwstr = 31;
                if (*(ushort*)value != vtLpwstr)
                {
                    return null;
                }

                var text = *(char**)(value + 8);
                return text is null ? null : new string(text);
            }
            finally
            {
                Ole32.PropVariantClear(value);
            }
        }
        finally
        {
            Com.Release(store);
        }
    }

    /// <summary>IAudioClient::GetMixFormat. The caller frees the returned block with CoTaskMemFree.</summary>
    public static int GetMixFormat(void* self, out WaveFormatEx* format)
    {
        fixed (WaveFormatEx** pFormat = &format)
        {
            return ((delegate* unmanaged[Stdcall]<void*, WaveFormatEx**, int>)Com.Slot(self, ClientGetMixFormat))(
                self, pFormat);
        }
    }

    /// <summary>IAudioClient::IsFormatSupported.</summary>
    public static int IsFormatSupported(void* self, AudioShareMode mode, WaveFormatEx* format, WaveFormatEx** closest) =>
        ((delegate* unmanaged[Stdcall]<void*, int, WaveFormatEx*, WaveFormatEx**, int>)
            Com.Slot(self, ClientIsFormatSupported))(self, (int)mode, format, closest);

    /// <summary>IAudioClient::Initialize.</summary>
    public static int Initialize(
        void* self, AudioShareMode mode, uint flags, long bufferDuration, long periodicity, WaveFormatEx* format) =>
        ((delegate* unmanaged[Stdcall]<void*, int, uint, long, long, WaveFormatEx*, Guid*, int>)
            Com.Slot(self, ClientInitialize))(self, (int)mode, flags, bufferDuration, periodicity, format, null);

    /// <summary>IAudioClient::GetBufferSize, in frames.</summary>
    public static int GetBufferSize(void* self, out uint frames)
    {
        fixed (uint* pFrames = &frames)
        {
            return ((delegate* unmanaged[Stdcall]<void*, uint*, int>)Com.Slot(self, ClientGetBufferSize))(self, pFrames);
        }
    }

    /// <summary>IAudioClient::GetCurrentPadding, in frames.</summary>
    public static int GetCurrentPadding(void* self, out uint frames)
    {
        fixed (uint* pFrames = &frames)
        {
            return ((delegate* unmanaged[Stdcall]<void*, uint*, int>)Com.Slot(self, ClientGetCurrentPadding))(
                self, pFrames);
        }
    }

    /// <summary>IAudioClient::GetDevicePeriod, in 100-nanosecond units.</summary>
    public static int GetDevicePeriod(void* self, out long defaultPeriod, out long minimumPeriod)
    {
        fixed (long* pDefault = &defaultPeriod)
        fixed (long* pMinimum = &minimumPeriod)
        {
            return ((delegate* unmanaged[Stdcall]<void*, long*, long*, int>)Com.Slot(self, ClientGetDevicePeriod))(
                self, pDefault, pMinimum);
        }
    }

    /// <summary>IAudioClient::Start.</summary>
    public static int Start(void* self) => ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(self, ClientStart))(self);

    /// <summary>IAudioClient::Stop.</summary>
    public static int Stop(void* self) => ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(self, ClientStop))(self);

    /// <summary>IAudioClient::Reset.</summary>
    public static int Reset(void* self) => ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(self, ClientReset))(self);

    /// <summary>IAudioClient::SetEventHandle.</summary>
    public static int SetEventHandle(void* self, nint handle) =>
        ((delegate* unmanaged[Stdcall]<void*, nint, int>)Com.Slot(self, ClientSetEventHandle))(self, handle);

    /// <summary>IAudioClient::GetService.</summary>
    public static int GetService(void* self, in Guid iid, out void* service)
    {
        fixed (Guid* pIid = &iid)
        fixed (void** pService = &service)
        {
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Com.Slot(self, ClientGetService))(
                self, pIid, pService);
        }
    }

    /// <summary>IAudioRenderClient::GetBuffer.</summary>
    public static int GetRenderBuffer(void* self, uint frames, out byte* data)
    {
        fixed (byte** pData = &data)
        {
            return ((delegate* unmanaged[Stdcall]<void*, uint, byte**, int>)Com.Slot(self, RenderGetBuffer))(
                self, frames, pData);
        }
    }

    /// <summary>IAudioRenderClient::ReleaseBuffer.</summary>
    public static int ReleaseRenderBuffer(void* self, uint frames, uint flags) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, uint, int>)Com.Slot(self, RenderReleaseBuffer))(self, frames, flags);

    /// <summary>IAudioCaptureClient::GetBuffer.</summary>
    public static int GetCaptureBuffer(
        void* self, out byte* data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition)
    {
        fixed (byte** pData = &data)
        fixed (uint* pFrames = &frames)
        fixed (uint* pFlags = &flags)
        fixed (ulong* pDevice = &devicePosition)
        fixed (ulong* pQpc = &qpcPosition)
        {
            return ((delegate* unmanaged[Stdcall]<void*, byte**, uint*, uint*, ulong*, ulong*, int>)
                Com.Slot(self, CaptureGetBuffer))(self, pData, pFrames, pFlags, pDevice, pQpc);
        }
    }

    /// <summary>IAudioCaptureClient::ReleaseBuffer.</summary>
    public static int ReleaseCaptureBuffer(void* self, uint frames) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, int>)Com.Slot(self, CaptureReleaseBuffer))(self, frames);

    /// <summary>IAudioCaptureClient::GetNextPacketSize.</summary>
    public static int GetNextPacketSize(void* self, out uint frames)
    {
        fixed (uint* pFrames = &frames)
        {
            return ((delegate* unmanaged[Stdcall]<void*, uint*, int>)Com.Slot(self, CaptureGetNextPacketSize))(
                self, pFrames);
        }
    }

    /// <summary>
    /// Interprets a WAVEFORMATEX, resolving WAVE_FORMAT_EXTENSIBLE through its
    /// sub-format GUID, which sits 8 bytes past the end of the base structure.
    /// </summary>
    public static AudioFormat ReadFormat(WaveFormatEx* format)
    {
        var isFloat = format->FormatTag == WaveFormatEx.IeeeFloat;
        if (format->FormatTag == WaveFormatEx.Extensible && format->Size >= 22)
        {
            // WAVEFORMATEXTENSIBLE: union (2) + dwChannelMask (4) + SubFormat GUID.
            var subFormat = *(Guid*)((byte*)format + sizeof(WaveFormatEx) + 6);
            isFloat = subFormat == WinGuids.AudioFloat;
        }

        return format->ToAudioFormat(isFloat);
    }
}
