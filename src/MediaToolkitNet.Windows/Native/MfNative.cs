using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Interop.Com;

namespace MediaToolkitNet.Windows.Native;

/// <summary>Exported Media Foundation functions.</summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class MfApi
{
    /// <summary>MF_VERSION for Windows 7 and later.</summary>
    public const uint Version = 0x00020070;

    /// <summary>MFSTARTUP_LITE, which skips the sockets layer.</summary>
    public const uint StartupLite = 1;

    /// <summary>MF_SOURCE_READER_FIRST_VIDEO_STREAM.</summary>
    public const uint FirstVideoStream = 0xFFFFFFFC;

    /// <summary>MF_SOURCE_READER_FIRST_AUDIO_STREAM.</summary>
    public const uint FirstAudioStream = 0xFFFFFFFD;

    /// <summary>MF_SOURCE_READERF_ENDOFSTREAM.</summary>
    public const uint EndOfStream = 0x00000002;

    /// <summary>MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED.</summary>
    public const uint MediaTypeChanged = 0x00000010;

    /// <summary>MFVideoInterlace_Progressive.</summary>
    public const uint InterlaceProgressive = 2;

    private static readonly Lock Gate = new();
    private static bool _started;

    [LibraryImport("mfplat.dll")]
    private static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    private static partial int MFShutdown();

    /// <summary>MFCreateAttributes.</summary>
    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateAttributes(void** attributes, uint initialSize);

    /// <summary>MFCreateMediaType.</summary>
    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateMediaType(void** mediaType);

    /// <summary>MFCreateSample.</summary>
    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateSample(void** sample);

    /// <summary>MFCreateMemoryBuffer.</summary>
    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateMemoryBuffer(uint maxLength, void** buffer);

    /// <summary>MFT_CATEGORY_VIDEO_ENCODER.</summary>
    public static readonly Guid CategoryVideoEncoder = new("f79eac7d-e545-4c6d-4cd5-2b1d0fbc5d2c");

    /// <summary>MFT_CATEGORY_AUDIO_ENCODER.</summary>
    public static readonly Guid CategoryAudioEncoder = new("91c64bd0-f91e-4d8c-9276-db248279d975");

    /// <summary>MFT_ENUM_FLAG_ALL.</summary>
    public const uint EnumFlagAll = 0x0000003F;

    /// <summary>Mirrors <c>MFT_REGISTER_TYPE_INFO</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RegisterTypeInfo
    {
        /// <summary>Major type.</summary>
        public Guid MajorType;

        /// <summary>Subtype.</summary>
        public Guid Subtype;
    }

    /// <summary>MFTEnumEx.</summary>
    [LibraryImport("mfplat.dll")]
    public static partial int MFTEnumEx(
        Guid category, uint flags, RegisterTypeInfo* inputType, RegisterTypeInfo* outputType,
        void*** activates, uint* count);

    /// <summary>MFEnumDeviceSources.</summary>
    [LibraryImport("mf.dll")]
    public static partial int MFEnumDeviceSources(void* attributes, void*** sources, uint* count);

    /// <summary>MFCreateSourceReaderFromMediaSource.</summary>
    [LibraryImport("mfreadwrite.dll")]
    public static partial int MFCreateSourceReaderFromMediaSource(void* source, void* attributes, void** reader);

    /// <summary>MFCreateSourceReaderFromURL.</summary>
    [LibraryImport("mfreadwrite.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MFCreateSourceReaderFromURL(string url, void* attributes, void** reader);

    /// <summary>MFCreateSinkWriterFromURL.</summary>
    [LibraryImport("mfreadwrite.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MFCreateSinkWriterFromURL(string url, void* byteStream, void* attributes, void** writer);

    /// <summary>
    /// Starts the Media Foundation platform once per process. Media Foundation
    /// reference counts its own startups, but doing it once keeps shutdown simple.
    /// </summary>
    public static void Startup()
    {
        if (_started)
        {
            return;
        }

        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            HResult.ThrowIfFailed(MFStartup(Version, StartupLite), "MFStartup");
            _started = true;
        }
    }

    /// <summary>Shuts the platform down. Only call this when nothing else is using it.</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (!_started)
            {
                return;
            }

            MFShutdown();
            _started = false;
        }
    }

    /// <summary>True when Media Foundation could be started on this machine.</summary>
    public static bool TryStartup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            Startup();
            return true;
        }
        catch (Exception ex) when (ex is ComException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows N editions without the Media Feature Pack land here.
            return false;
        }
    }
}

/// <summary>
/// Vtable wrappers for the Media Foundation interfaces this backend uses.
/// </summary>
/// <remarks>
/// Slot numbers count every method of every base interface first, so
/// <c>IMFAttributes</c> occupies slots 3 to 32 and the interfaces deriving from
/// it start at slot 33.
/// </remarks>
[SupportedOSPlatform("windows")]
public static unsafe class Mf
{
    // IMFAttributes : IUnknown, slots 3..32.
    private const int AttributesGetUint32 = 7;
    private const int AttributesGetUint64 = 8;
    private const int AttributesGetGuid = 10;
    private const int AttributesGetAllocatedString = 13;
    private const int AttributesSetUint32 = 21;
    private const int AttributesSetUint64 = 22;
    private const int AttributesSetGuid = 24;
    private const int AttributesSetUnknown = 27;
    private const int AttributesGetCount = 30;

    // IMFActivate : IMFAttributes.
    private const int ActivateActivateObject = 33;
    private const int ActivateShutdownObject = 34;

    // IMFSourceReader : IUnknown.
    private const int ReaderGetStreamSelection = 3;
    private const int ReaderSetStreamSelection = 4;
    private const int ReaderGetNativeMediaType = 5;
    private const int ReaderGetCurrentMediaType = 6;
    private const int ReaderSetCurrentMediaType = 7;
    private const int ReaderSetCurrentPosition = 8;
    private const int ReaderReadSample = 9;
    private const int ReaderFlush = 10;

    // IMFSinkWriter : IUnknown.
    private const int WriterAddStream = 3;
    private const int WriterSetInputMediaType = 4;
    private const int WriterBeginWriting = 5;
    private const int WriterWriteSample = 6;
    private const int WriterFlush = 10;
    private const int WriterFinalize = 11;

    // IMFSample : IMFAttributes.
    private const int SampleSetSampleTime = 36;
    private const int SampleSetSampleDuration = 38;
    private const int SampleGetSampleTime = 35;
    private const int SampleGetBufferCount = 39;
    private const int SampleConvertToContiguousBuffer = 41;
    private const int SampleAddBuffer = 42;

    // IMFMediaBuffer : IUnknown.
    private const int BufferLock = 3;
    private const int BufferUnlock = 4;
    private const int BufferSetCurrentLength = 6;

    // IMFMediaSource : IMFMediaEventGenerator : IUnknown, so its own methods
    // start after the four event generator methods.
    private const int SourceShutdown = 12;

    // ------------------------------------------------------------- attributes

    /// <summary>IMFAttributes::SetGUID.</summary>
    public static int SetGuid(void* self, in Guid key, in Guid value)
    {
        fixed (Guid* pKey = &key)
        fixed (Guid* pValue = &value)
        {
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, Guid*, int>)Com.Slot(self, AttributesSetGuid))(self, pKey, pValue);
        }
    }

    /// <summary>IMFAttributes::GetGUID.</summary>
    public static int GetGuid(void* self, in Guid key, out Guid value)
    {
        fixed (Guid* pKey = &key)
        fixed (Guid* pValue = &value)
        {
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, Guid*, int>)Com.Slot(self, AttributesGetGuid))(self, pKey, pValue);
        }
    }

    /// <summary>IMFAttributes::SetUINT32.</summary>
    public static int SetUint32(void* self, in Guid key, uint value)
    {
        fixed (Guid* pKey = &key)
        {
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, uint, int>)Com.Slot(self, AttributesSetUint32))(self, pKey, value);
        }
    }

    /// <summary>IMFAttributes::GetUINT32.</summary>
    public static int GetUint32(void* self, in Guid key, out uint value)
    {
        fixed (Guid* pKey = &key)
        fixed (uint* pValue = &value)
        {
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, uint*, int>)Com.Slot(self, AttributesGetUint32))(self, pKey, pValue);
        }
    }

    /// <summary>IMFAttributes::SetUINT64.</summary>
    public static int SetUint64(void* self, in Guid key, ulong value)
    {
        fixed (Guid* pKey = &key)
        {
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, ulong, int>)Com.Slot(self, AttributesSetUint64))(self, pKey, value);
        }
    }

    /// <summary>IMFAttributes::GetUINT64.</summary>
    public static int GetUint64(void* self, in Guid key, out ulong value)
    {
        fixed (Guid* pKey = &key)
        fixed (ulong* pValue = &value)
        {
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, ulong*, int>)Com.Slot(self, AttributesGetUint64))(self, pKey, pValue);
        }
    }

    /// <summary>IMFAttributes::SetUnknown.</summary>
    public static int SetUnknown(void* self, in Guid key, void* value)
    {
        fixed (Guid* pKey = &key)
        {
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, void*, int>)Com.Slot(self, AttributesSetUnknown))(self, pKey, value);
        }
    }

    /// <summary>IMFAttributes::GetCount.</summary>
    public static int GetCount(void* self, out uint count)
    {
        fixed (uint* pCount = &count)
        {
            return ((delegate* unmanaged[Stdcall]<void*, uint*, int>)Com.Slot(self, AttributesGetCount))(self, pCount);
        }
    }

    /// <summary>
    /// IMFAttributes::GetAllocatedString, returning the managed copy and freeing
    /// the buffer Media Foundation allocated.
    /// </summary>
    public static string? GetString(void* self, in Guid key)
    {
        char* buffer;
        uint length;
        int hr;
        fixed (Guid* pKey = &key)
        {
            hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, char**, uint*, int>)
                Com.Slot(self, AttributesGetAllocatedString))(self, pKey, &buffer, &length);
        }

        if (HResult.Failed(hr) || buffer is null)
        {
            return null;
        }

        try
        {
            return new string(buffer, 0, (int)length);
        }
        finally
        {
            Ole32.CoTaskMemFree(buffer);
        }
    }

    // --------------------------------------------------------------- activate

    /// <summary>IMFActivate::ActivateObject.</summary>
    public static int ActivateObject(void* self, in Guid iid, out void* result)
    {
        fixed (Guid* pIid = &iid)
        fixed (void** pResult = &result)
        {
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)
                Com.Slot(self, ActivateActivateObject))(self, pIid, pResult);
        }
    }

    /// <summary>IMFActivate::ShutdownObject.</summary>
    public static int ShutdownObject(void* self) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(self, ActivateShutdownObject))(self);

    /// <summary>IMFMediaSource::Shutdown.</summary>
    public static int ShutdownSource(void* self) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(self, SourceShutdown))(self);

    // ---------------------------------------------------------- source reader

    /// <summary>IMFSourceReader::SetStreamSelection.</summary>
    public static int SetStreamSelection(void* self, uint streamIndex, bool selected) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, int, int>)Com.Slot(self, ReaderSetStreamSelection))(
            self, streamIndex, selected ? 1 : 0);

    /// <summary>IMFSourceReader::GetStreamSelection.</summary>
    public static int GetStreamSelection(void* self, uint streamIndex, out int selected)
    {
        fixed (int* pSelected = &selected)
        {
            return ((delegate* unmanaged[Stdcall]<void*, uint, int*, int>)Com.Slot(self, ReaderGetStreamSelection))(
                self, streamIndex, pSelected);
        }
    }

    /// <summary>IMFSourceReader::GetNativeMediaType.</summary>
    public static int GetNativeMediaType(void* self, uint streamIndex, uint typeIndex, out void* mediaType)
    {
        fixed (void** pType = &mediaType)
        {
            return ((delegate* unmanaged[Stdcall]<void*, uint, uint, void**, int>)
                Com.Slot(self, ReaderGetNativeMediaType))(self, streamIndex, typeIndex, pType);
        }
    }

    /// <summary>IMFSourceReader::GetCurrentMediaType.</summary>
    public static int GetCurrentMediaType(void* self, uint streamIndex, out void* mediaType)
    {
        fixed (void** pType = &mediaType)
        {
            return ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)
                Com.Slot(self, ReaderGetCurrentMediaType))(self, streamIndex, pType);
        }
    }

    /// <summary>IMFSourceReader::SetCurrentMediaType.</summary>
    public static int SetCurrentMediaType(void* self, uint streamIndex, void* mediaType) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, void*, void*, int>)Com.Slot(self, ReaderSetCurrentMediaType))(
            self, streamIndex, null, mediaType);

    /// <summary>IMFSourceReader::ReadSample, in synchronous mode.</summary>
    public static int ReadSample(
        void* self, uint streamIndex, out uint actualStreamIndex, out uint flags, out long timestamp, out void* sample)
    {
        fixed (uint* pActual = &actualStreamIndex)
        fixed (uint* pFlags = &flags)
        fixed (long* pTimestamp = &timestamp)
        fixed (void** pSample = &sample)
        {
            return ((delegate* unmanaged[Stdcall]<void*, uint, uint, uint*, uint*, long*, void**, int>)
                Com.Slot(self, ReaderReadSample))(self, streamIndex, 0, pActual, pFlags, pTimestamp, pSample);
        }
    }

    /// <summary>IMFSourceReader::Flush.</summary>
    public static int FlushReader(void* self, uint streamIndex) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, int>)Com.Slot(self, ReaderFlush))(self, streamIndex);

    /// <summary>IMFSourceReader::SetCurrentPosition, using the default time format.</summary>
    public static int SetCurrentPosition(void* self, long hundredNanoseconds)
    {
        // PROPVARIANT with VT_I8 (20) holding the position.
        var variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        *(ushort*)variant = 20;
        *(long*)(variant + 8) = hundredNanoseconds;

        var guidNull = Guid.Empty;
        return ((delegate* unmanaged[Stdcall]<void*, Guid*, void*, int>)Com.Slot(self, ReaderSetCurrentPosition))(
            self, &guidNull, variant);
    }

    // ------------------------------------------------------------ sink writer

    /// <summary>IMFSinkWriter::AddStream.</summary>
    public static int AddStream(void* self, void* targetMediaType, out uint streamIndex)
    {
        fixed (uint* pIndex = &streamIndex)
        {
            return ((delegate* unmanaged[Stdcall]<void*, void*, uint*, int>)Com.Slot(self, WriterAddStream))(
                self, targetMediaType, pIndex);
        }
    }

    /// <summary>IMFSinkWriter::SetInputMediaType.</summary>
    public static int SetInputMediaType(void* self, uint streamIndex, void* mediaType, void* parameters) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, void*, void*, int>)Com.Slot(self, WriterSetInputMediaType))(
            self, streamIndex, mediaType, parameters);

    /// <summary>IMFSinkWriter::BeginWriting.</summary>
    public static int BeginWriting(void* self) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(self, WriterBeginWriting))(self);

    /// <summary>IMFSinkWriter::WriteSample.</summary>
    public static int WriteSample(void* self, uint streamIndex, void* sample) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, void*, int>)Com.Slot(self, WriterWriteSample))(
            self, streamIndex, sample);

    /// <summary>IMFSinkWriter::Flush.</summary>
    public static int FlushWriter(void* self, uint streamIndex) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, int>)Com.Slot(self, WriterFlush))(self, streamIndex);

    /// <summary>IMFSinkWriter::Finalize.</summary>
    public static int FinalizeWriter(void* self) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(self, WriterFinalize))(self);

    // ----------------------------------------------------------- sample/buffer

    /// <summary>IMFSample::SetSampleTime, in 100-nanosecond units.</summary>
    public static int SetSampleTime(void* self, long time) =>
        ((delegate* unmanaged[Stdcall]<void*, long, int>)Com.Slot(self, SampleSetSampleTime))(self, time);

    /// <summary>IMFSample::GetSampleTime, in 100-nanosecond units.</summary>
    public static int GetSampleTime(void* self, out long time)
    {
        fixed (long* pTime = &time)
        {
            return ((delegate* unmanaged[Stdcall]<void*, long*, int>)Com.Slot(self, SampleGetSampleTime))(self, pTime);
        }
    }

    /// <summary>IMFSample::SetSampleDuration, in 100-nanosecond units.</summary>
    public static int SetSampleDuration(void* self, long duration) =>
        ((delegate* unmanaged[Stdcall]<void*, long, int>)Com.Slot(self, SampleSetSampleDuration))(self, duration);

    /// <summary>IMFSample::GetBufferCount.</summary>
    public static int GetBufferCount(void* self, out uint count)
    {
        fixed (uint* pCount = &count)
        {
            return ((delegate* unmanaged[Stdcall]<void*, uint*, int>)Com.Slot(self, SampleGetBufferCount))(self, pCount);
        }
    }

    /// <summary>IMFSample::ConvertToContiguousBuffer.</summary>
    public static int ConvertToContiguousBuffer(void* self, out void* buffer)
    {
        fixed (void** pBuffer = &buffer)
        {
            return ((delegate* unmanaged[Stdcall]<void*, void**, int>)
                Com.Slot(self, SampleConvertToContiguousBuffer))(self, pBuffer);
        }
    }

    /// <summary>IMFSample::AddBuffer.</summary>
    public static int AddBuffer(void* self, void* buffer) =>
        ((delegate* unmanaged[Stdcall]<void*, void*, int>)Com.Slot(self, SampleAddBuffer))(self, buffer);

    /// <summary>IMFMediaBuffer::Lock.</summary>
    public static int LockBuffer(void* self, out byte* data, out uint maxLength, out uint currentLength)
    {
        fixed (byte** pData = &data)
        fixed (uint* pMax = &maxLength)
        fixed (uint* pCurrent = &currentLength)
        {
            return ((delegate* unmanaged[Stdcall]<void*, byte**, uint*, uint*, int>)Com.Slot(self, BufferLock))(
                self, pData, pMax, pCurrent);
        }
    }

    /// <summary>IMFMediaBuffer::Unlock.</summary>
    public static int UnlockBuffer(void* self) =>
        ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(self, BufferUnlock))(self);

    /// <summary>IMFMediaBuffer::SetCurrentLength.</summary>
    public static int SetCurrentLength(void* self, uint length) =>
        ((delegate* unmanaged[Stdcall]<void*, uint, int>)Com.Slot(self, BufferSetCurrentLength))(self, length);

    /// <summary>Packs two 32-bit values the way MF_MT_FRAME_SIZE and MF_MT_FRAME_RATE expect.</summary>
    public static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    /// <summary>Splits a packed attribute back into its two halves.</summary>
    public static (uint High, uint Low) Unpack(ulong value) => ((uint)(value >> 32), (uint)(value & 0xFFFFFFFF));
}
