using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions.Formats;

namespace MediaToolkitNet.MacOS.Native;

/// <summary>Mirrors <c>CMTime</c> (24 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CMTime
{
    /// <summary>Numerator of the time value.</summary>
    public long Value;

    /// <summary>Units per second.</summary>
    public int TimeScale;

    /// <summary>kCMTimeFlags_*; bit 0 marks the value as valid.</summary>
    public uint Flags;

    /// <summary>Epoch, used to disambiguate looping timelines.</summary>
    public long Epoch;

    /// <summary>kCMTimeFlags_Valid.</summary>
    public const uint Valid = 1 << 0;

    /// <summary>Builds a valid CMTime.</summary>
    public static CMTime FromSeconds(double seconds, int timeScale = 600) => new()
    {
        Value = (long)(seconds * timeScale),
        TimeScale = timeScale,
        Flags = Valid,
        Epoch = 0,
    };

    /// <summary>Converts to seconds, or <see cref="TimeSpan.Zero"/> when the value is invalid.</summary>
    public readonly TimeSpan ToTimeSpan() =>
        (Flags & Valid) == 0 || TimeScale == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)Value / TimeScale);
}

/// <summary>
/// Bindings for the C parts of the AVFoundation stack: CoreMedia, CoreVideo and
/// the Grand Central Dispatch queue the capture delegate runs on.
/// </summary>
[SupportedOSPlatform("macos")]
public static unsafe partial class AVFoundationNative
{
    private const string CoreMedia = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
    private const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
    private const string AVFoundationFramework = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";

    /// <summary>kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange.</summary>
    public const uint PixelFormatNv12VideoRange = 0x34323076;

    /// <summary>kCVPixelFormatType_420YpCbCr8BiPlanarFullRange.</summary>
    public const uint PixelFormatNv12FullRange = 0x34323066;

    /// <summary>kCVPixelFormatType_422YpCbCr8 ('2vuy'), which is UYVY in memory.</summary>
    public const uint PixelFormatUyvy = 0x32767579;

    /// <summary>kCVPixelFormatType_422YpCbCr8_yuvs ('yuvs'), which is YUYV in memory.</summary>
    public const uint PixelFormatYuyv = 0x79757673;

    /// <summary>kCVPixelFormatType_32BGRA.</summary>
    public const uint PixelFormatBgra = 0x42475241;

    /// <summary>kCVPixelFormatType_24RGB.</summary>
    public const uint PixelFormatRgb24 = 24;

    /// <summary>kCVPixelBufferLock_ReadOnly.</summary>
    public const ulong LockReadOnly = 1;

    /// <summary><c>CVImageBufferRef CMSampleBufferGetImageBuffer(CMSampleBufferRef)</c></summary>
    [LibraryImport(CoreMedia)]
    public static partial nint CMSampleBufferGetImageBuffer(nint sampleBuffer);

    /// <summary><c>CMTime CMSampleBufferGetPresentationTimeStamp(CMSampleBufferRef)</c></summary>
    [LibraryImport(CoreMedia)]
    public static partial CMTime CMSampleBufferGetPresentationTimeStamp(nint sampleBuffer);

    /// <summary><c>CVReturn CVPixelBufferLockBaseAddress(CVPixelBufferRef, CVPixelBufferLockFlags)</c></summary>
    [LibraryImport(CoreVideo)]
    public static partial int CVPixelBufferLockBaseAddress(nint pixelBuffer, ulong flags);

    /// <summary><c>CVReturn CVPixelBufferUnlockBaseAddress(CVPixelBufferRef, CVPixelBufferLockFlags)</c></summary>
    [LibraryImport(CoreVideo)]
    public static partial int CVPixelBufferUnlockBaseAddress(nint pixelBuffer, ulong flags);

    /// <summary><c>size_t CVPixelBufferGetWidth(CVPixelBufferRef)</c></summary>
    [LibraryImport(CoreVideo)]
    public static partial nuint CVPixelBufferGetWidth(nint pixelBuffer);

    /// <summary><c>size_t CVPixelBufferGetHeight(CVPixelBufferRef)</c></summary>
    [LibraryImport(CoreVideo)]
    public static partial nuint CVPixelBufferGetHeight(nint pixelBuffer);

    /// <summary><c>OSType CVPixelBufferGetPixelFormatType(CVPixelBufferRef)</c></summary>
    [LibraryImport(CoreVideo)]
    public static partial uint CVPixelBufferGetPixelFormatType(nint pixelBuffer);

    /// <summary><c>size_t CVPixelBufferGetPlaneCount(CVPixelBufferRef)</c></summary>
    [LibraryImport(CoreVideo)]
    public static partial nuint CVPixelBufferGetPlaneCount(nint pixelBuffer);

    /// <summary><c>void *CVPixelBufferGetBaseAddress(CVPixelBufferRef)</c></summary>
    [LibraryImport(CoreVideo)]
    public static partial nint CVPixelBufferGetBaseAddress(nint pixelBuffer);

    /// <summary><c>size_t CVPixelBufferGetBytesPerRow(CVPixelBufferRef)</c></summary>
    [LibraryImport(CoreVideo)]
    public static partial nuint CVPixelBufferGetBytesPerRow(nint pixelBuffer);

    /// <summary><c>void *CVPixelBufferGetBaseAddressOfPlane(CVPixelBufferRef, size_t)</c></summary>
    [LibraryImport(CoreVideo)]
    public static partial nint CVPixelBufferGetBaseAddressOfPlane(nint pixelBuffer, nuint plane);

    /// <summary><c>size_t CVPixelBufferGetBytesPerRowOfPlane(CVPixelBufferRef, size_t)</c></summary>
    [LibraryImport(CoreVideo)]
    public static partial nuint CVPixelBufferGetBytesPerRowOfPlane(nint pixelBuffer, nuint plane);

    /// <summary><c>dispatch_queue_t dispatch_queue_create(const char *label, dispatch_queue_attr_t attr)</c></summary>
    [LibraryImport("/usr/lib/libSystem.dylib", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint dispatch_queue_create(string label, nint attributes);

    /// <summary><c>void dispatch_release(dispatch_object_t)</c></summary>
    [LibraryImport("/usr/lib/libSystem.dylib")]
    public static partial void dispatch_release(nint obj);

    /// <summary><c>void *object_getIndexedIvars(id)</c></summary>
    [LibraryImport("/usr/lib/libobjc.A.dylib")]
    public static partial void* object_getIndexedIvars(nint obj);

    /// <summary>
    /// Forces the AVFoundation framework to load so its classes appear in the
    /// Objective-C runtime. The framework is normally pulled in by the app
    /// bundle, which a plain console process does not have.
    /// </summary>
    public static bool EnsureFrameworkLoaded()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        return NativeLibrary.TryLoad(AVFoundationFramework, out _);
    }

    /// <summary>Maps a CoreVideo pixel format type onto a toolkit pixel format.</summary>
    public static PixelFormat ToPixelFormat(uint type) => type switch
    {
        PixelFormatNv12VideoRange or PixelFormatNv12FullRange => PixelFormat.Nv12,
        PixelFormatUyvy => PixelFormat.Uyvy422,
        PixelFormatYuyv => PixelFormat.Yuyv422,
        PixelFormatBgra => PixelFormat.Bgra32,
        PixelFormatRgb24 => PixelFormat.Rgb24,
        _ => PixelFormat.Unknown,
    };

    /// <summary>Maps a toolkit pixel format onto a CoreVideo pixel format type, or 0 when there is none.</summary>
    public static uint FromPixelFormat(PixelFormat format) => format switch
    {
        PixelFormat.Nv12 => PixelFormatNv12FullRange,
        PixelFormat.Uyvy422 => PixelFormatUyvy,
        PixelFormat.Yuyv422 => PixelFormatYuyv,
        PixelFormat.Bgra32 => PixelFormatBgra,
        PixelFormat.Rgb24 => PixelFormatRgb24,
        _ => 0,
    };
}
