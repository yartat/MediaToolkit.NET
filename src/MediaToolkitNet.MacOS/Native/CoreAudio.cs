using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Core.Formats;

namespace MediaToolkitNet.MacOS.Native;

/// <summary>Mirrors <c>AudioStreamBasicDescription</c> (40 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AudioStreamBasicDescription
{
    /// <summary>Frames per second.</summary>
    public double SampleRate;

    /// <summary>Four character format id; <c>lpcm</c> for linear PCM.</summary>
    public uint FormatId;

    /// <summary>kAudioFormatFlag* bits.</summary>
    public uint FormatFlags;

    /// <summary>Bytes in one packet.</summary>
    public uint BytesPerPacket;

    /// <summary>Frames in one packet; 1 for uncompressed PCM.</summary>
    public uint FramesPerPacket;

    /// <summary>Bytes in one frame.</summary>
    public uint BytesPerFrame;

    /// <summary>Channels in one frame.</summary>
    public uint ChannelsPerFrame;

    /// <summary>Bits in one sample of one channel.</summary>
    public uint BitsPerChannel;

    /// <summary>Reserved; must be zero.</summary>
    public uint Reserved;

    /// <summary>Builds an interleaved linear PCM description for <paramref name="format"/>.</summary>
    [SupportedOSPlatform("macos")]
    public static AudioStreamBasicDescription From(AudioFormat format)
    {
        var bytesPerSample = format.SampleFormat.BytesPerSample();
        var bytesPerFrame = (uint)(bytesPerSample * format.Channels);

        var flags = CoreAudio.FlagIsPacked;
        flags |= format.SampleFormat.IsFloat() ? CoreAudio.FlagIsFloat : CoreAudio.FlagIsSignedInteger;

        return new AudioStreamBasicDescription
        {
            SampleRate = format.SampleRate,
            FormatId = CoreAudio.FormatLinearPcm,
            FormatFlags = flags,
            BytesPerPacket = bytesPerFrame,
            FramesPerPacket = 1,
            BytesPerFrame = bytesPerFrame,
            ChannelsPerFrame = (uint)format.Channels,
            BitsPerChannel = (uint)(bytesPerSample * 8),
            Reserved = 0,
        };
    }
}

/// <summary>Mirrors <c>AudioObjectPropertyAddress</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AudioObjectPropertyAddress
{
    /// <summary>Four character property selector.</summary>
    public uint Selector;

    /// <summary>Scope: global, input or output.</summary>
    public uint Scope;

    /// <summary>Element; 0 is the master element.</summary>
    public uint Element;

    /// <summary>Creates an address in the given scope.</summary>
    public AudioObjectPropertyAddress(uint selector, uint scope)
    {
        Selector = selector;
        Scope = scope;
        Element = 0;
    }
}

/// <summary>Mirrors <c>AudioQueueBuffer</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct AudioQueueBuffer
{
    /// <summary>Capacity of <see cref="AudioData"/> in bytes.</summary>
    public uint AudioDataBytesCapacity;

    /// <summary>Padding before the pointer field.</summary>
    private readonly uint _padding;

    /// <summary>Sample payload.</summary>
    public void* AudioData;

    /// <summary>Bytes currently valid in <see cref="AudioData"/>.</summary>
    public uint AudioDataByteSize;

    /// <summary>Padding before the next pointer field.</summary>
    private readonly uint _padding2;

    /// <summary>Caller data.</summary>
    public void* UserData;

    /// <summary>Capacity of the packet description array.</summary>
    public uint PacketDescriptionCapacity;

    /// <summary>Padding before the next pointer field.</summary>
    private readonly uint _padding3;

    /// <summary>Packet descriptions, unused for PCM.</summary>
    public void* PacketDescriptions;

    /// <summary>Number of valid packet descriptions.</summary>
    public uint PacketDescriptionCount;
}

/// <summary>
/// CoreAudio bindings: device enumeration through the AudioObject property API
/// and PCM transport through AudioQueue.
/// </summary>
/// <remarks>
/// AudioQueue is chosen over AudioUnit because its callbacks are plain C
/// function pointers, which pair cleanly with
/// <see cref="UnmanagedCallersOnlyAttribute"/>, and because it does its own
/// buffering.
/// </remarks>
[SupportedOSPlatform("macos")]
public static unsafe partial class CoreAudio
{
    private const string Framework = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    /// <summary>Backend name used in error messages.</summary>
    public const string BackendName = "coreaudio";

    /// <summary>kAudioObjectSystemObject.</summary>
    public const uint SystemObject = 1;

    /// <summary>kAudioFormatLinearPCM.</summary>
    public const uint FormatLinearPcm = 0x6C70636D;

    /// <summary>kAudioFormatFlagIsFloat.</summary>
    public const uint FlagIsFloat = 1 << 0;

    /// <summary>kAudioFormatFlagIsSignedInteger.</summary>
    public const uint FlagIsSignedInteger = 1 << 2;

    /// <summary>kAudioFormatFlagIsPacked.</summary>
    public const uint FlagIsPacked = 1 << 3;

    /// <summary>kAudioObjectPropertyScopeGlobal ('glob').</summary>
    public const uint ScopeGlobal = 0x676C6F62;

    /// <summary>kAudioObjectPropertyScopeInput ('inpt').</summary>
    public const uint ScopeInput = 0x696E7074;

    /// <summary>kAudioObjectPropertyScopeOutput ('outp').</summary>
    public const uint ScopeOutput = 0x6F757470;

    /// <summary>kAudioHardwarePropertyDevices ('dev#').</summary>
    public const uint PropertyDevices = 0x64657623;

    /// <summary>kAudioHardwarePropertyDefaultInputDevice ('dIn ').</summary>
    public const uint PropertyDefaultInput = 0x64496E20;

    /// <summary>kAudioHardwarePropertyDefaultOutputDevice ('dOut').</summary>
    public const uint PropertyDefaultOutput = 0x644F7574;

    /// <summary>kAudioObjectPropertyName ('lnam').</summary>
    public const uint PropertyName = 0x6C6E616D;

    /// <summary>kAudioDevicePropertyDeviceUID ('uid ').</summary>
    public const uint PropertyDeviceUid = 0x75696420;

    /// <summary>kAudioDevicePropertyStreamConfiguration ('slay').</summary>
    public const uint PropertyStreamConfiguration = 0x736C6179;

    /// <summary><c>OSStatus AudioObjectGetPropertyDataSize(AudioObjectID, const AudioObjectPropertyAddress *, UInt32, const void *, UInt32 *)</c></summary>
    [LibraryImport("/System/Library/Frameworks/CoreAudio.framework/CoreAudio")]
    public static partial int AudioObjectGetPropertyDataSize(
        uint objectId, AudioObjectPropertyAddress* address, uint qualifierSize, void* qualifier, uint* dataSize);

    /// <summary><c>OSStatus AudioObjectGetPropertyData(AudioObjectID, const AudioObjectPropertyAddress *, UInt32, const void *, UInt32 *, void *)</c></summary>
    [LibraryImport("/System/Library/Frameworks/CoreAudio.framework/CoreAudio")]
    public static partial int AudioObjectGetPropertyData(
        uint objectId, AudioObjectPropertyAddress* address, uint qualifierSize, void* qualifier, uint* dataSize, void* data);

    /// <summary><c>OSStatus AudioQueueNewOutput(...)</c></summary>
    [LibraryImport(Framework)]
    public static partial int AudioQueueNewOutput(
        AudioStreamBasicDescription* format,
        delegate* unmanaged[Cdecl]<void*, nint, AudioQueueBuffer*, void> callback,
        void* userData,
        nint runLoop,
        nint runLoopMode,
        uint flags,
        nint* queue);

    /// <summary><c>OSStatus AudioQueueNewInput(...)</c></summary>
    [LibraryImport(Framework)]
    public static partial int AudioQueueNewInput(
        AudioStreamBasicDescription* format,
        delegate* unmanaged[Cdecl]<void*, nint, AudioQueueBuffer*, void*, uint, void*, void> callback,
        void* userData,
        nint runLoop,
        nint runLoopMode,
        uint flags,
        nint* queue);

    /// <summary><c>OSStatus AudioQueueAllocateBuffer(AudioQueueRef, UInt32, AudioQueueBufferRef *)</c></summary>
    [LibraryImport(Framework)]
    public static partial int AudioQueueAllocateBuffer(nint queue, uint byteSize, AudioQueueBuffer** buffer);

    /// <summary><c>OSStatus AudioQueueFreeBuffer(AudioQueueRef, AudioQueueBufferRef)</c></summary>
    [LibraryImport(Framework)]
    public static partial int AudioQueueFreeBuffer(nint queue, AudioQueueBuffer* buffer);

    /// <summary><c>OSStatus AudioQueueEnqueueBuffer(AudioQueueRef, AudioQueueBufferRef, UInt32, const AudioStreamPacketDescription *)</c></summary>
    [LibraryImport(Framework)]
    public static partial int AudioQueueEnqueueBuffer(nint queue, AudioQueueBuffer* buffer, uint packetCount, void* packets);

    /// <summary><c>OSStatus AudioQueueStart(AudioQueueRef, const AudioTimeStamp *)</c></summary>
    [LibraryImport(Framework)]
    public static partial int AudioQueueStart(nint queue, void* startTime);

    /// <summary><c>OSStatus AudioQueueStop(AudioQueueRef, Boolean immediate)</c></summary>
    [LibraryImport(Framework)]
    public static partial int AudioQueueStop(nint queue, [MarshalAs(UnmanagedType.U1)] bool immediate);

    /// <summary><c>OSStatus AudioQueueFlush(AudioQueueRef)</c></summary>
    [LibraryImport(Framework)]
    public static partial int AudioQueueFlush(nint queue);

    /// <summary><c>OSStatus AudioQueueDispose(AudioQueueRef, Boolean immediate)</c></summary>
    [LibraryImport(Framework)]
    public static partial int AudioQueueDispose(nint queue, [MarshalAs(UnmanagedType.U1)] bool immediate);

    /// <summary><c>OSStatus AudioQueueSetParameter(AudioQueueRef, AudioQueueParameterID, AudioQueueParameterValue)</c></summary>
    [LibraryImport(Framework)]
    public static partial int AudioQueueSetParameter(nint queue, uint parameterId, float value);

    /// <summary>kAudioQueueParam_Volume.</summary>
    public const uint ParameterVolume = 1;

    /// <summary>Throws when an OSStatus indicates failure.</summary>
    public static void Check(int status, string operation)
    {
        if (status != 0)
        {
            throw new Core.MediaToolkitNetException(BackendName, $"{operation} returned OSStatus {status}", status);
        }
    }

    /// <summary>
    /// Reads a CoreAudio property whose size is fixed and known.
    /// </summary>
    public static bool TryGetProperty<T>(uint objectId, uint selector, uint scope, out T value)
        where T : unmanaged
    {
        var address = new AudioObjectPropertyAddress(selector, scope);
        var size = (uint)sizeof(T);
        T local;
        var status = AudioObjectGetPropertyData(objectId, &address, 0, null, &size, &local);
        value = status == 0 ? local : default;
        return status == 0;
    }

    /// <summary>Reads a CFString property and converts it to a managed string.</summary>
    public static string? GetStringProperty(uint objectId, uint selector, uint scope)
    {
        if (!TryGetProperty<nint>(objectId, selector, scope, out var cfString) || cfString == 0)
        {
            return null;
        }

        try
        {
            // CFString and NSString are toll-free bridged, so the Objective-C
            // accessor works directly on the CoreFoundation object.
            return ObjC.FromNSString(cfString);
        }
        finally
        {
            CFRelease(cfString);
        }
    }

    /// <summary>Counts the channels a device exposes in the given scope.</summary>
    public static int CountChannels(uint deviceId, uint scope)
    {
        var address = new AudioObjectPropertyAddress(PropertyStreamConfiguration, scope);
        uint size;
        if (AudioObjectGetPropertyDataSize(deviceId, &address, 0, null, &size) != 0 || size == 0)
        {
            return 0;
        }

        var buffer = NativeMemory.AllocZeroed(size);
        try
        {
            if (AudioObjectGetPropertyData(deviceId, &address, 0, null, &size, buffer) != 0)
            {
                return 0;
            }

            // AudioBufferList: UInt32 mNumberBuffers, then AudioBuffer[]
            // { UInt32 mNumberChannels; UInt32 mDataByteSize; void *mData; }
            // padded to 8-byte alignment.
            var count = *(uint*)buffer;
            var channels = 0;
            var entry = (byte*)buffer + 8;
            for (var i = 0u; i < count; i++)
            {
                channels += (int)(*(uint*)entry);
                entry += 16;
            }

            return channels;
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    /// <summary>Lists the ids of every audio device the system knows about.</summary>
    public static uint[] ListDevices()
    {
        var address = new AudioObjectPropertyAddress(PropertyDevices, ScopeGlobal);
        uint size;
        if (AudioObjectGetPropertyDataSize(SystemObject, &address, 0, null, &size) != 0 || size == 0)
        {
            return [];
        }

        var result = new uint[size / sizeof(uint)];
        fixed (uint* data = result)
        {
            return AudioObjectGetPropertyData(SystemObject, &address, 0, null, &size, data) == 0 ? result : [];
        }
    }

    /// <summary><c>void CFRelease(CFTypeRef)</c></summary>
    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    public static partial void CFRelease(nint reference);
}
