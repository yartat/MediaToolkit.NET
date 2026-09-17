using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Core;
using MediaToolkitNet.Core.Capture;
using MediaToolkitNet.Core.Devices;
using MediaToolkitNet.Core.Formats;
using MediaToolkitNet.Core.Frames;
using MediaToolkitNet.MacOS.Native;

namespace MediaToolkitNet.MacOS;

/// <summary>Selectors and helpers shared by the AVFoundation capture and player.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class AVFoundation
{
    /// <summary><c>AVMediaTypeVideo</c>, which is the literal string "vide".</summary>
    public const string MediaTypeVideo = "vide";

    /// <summary><c>AVMediaTypeAudio</c>, which is the literal string "soun".</summary>
    public const string MediaTypeAudio = "soun";

    /// <summary>True when AVFoundation could be loaded into this process.</summary>
    public static bool IsAvailable => AVFoundationNative.EnsureFrameworkLoaded();

    /// <summary>Lists the camera-like devices AVFoundation can see.</summary>
    public static IReadOnlyList<MediaDevice> EnumerateVideoDevices()
    {
        if (!IsAvailable)
        {
            return [];
        }

        var deviceClass = ObjC.objc_getClass("AVCaptureDevice");
        if (deviceClass == 0)
        {
            return [];
        }

        // devicesWithMediaType: is deprecated but still the only call that works
        // without naming every device type the current macOS knows about.
        var devices = ObjC.Send(
            deviceClass, ObjC.Sel("devicesWithMediaType:"), ObjC.NSString(MediaTypeVideo));

        if (devices == 0)
        {
            return [];
        }

        var count = ObjC.ArrayCount(devices);
        var result = new List<MediaDevice>((int)count);
        for (nint i = 0; i < count; i++)
        {
            var device = ObjC.ArrayItem(devices, i);
            var id = ObjC.FromNSString(ObjC.Send(device, ObjC.Sel("uniqueID")));
            var name = ObjC.FromNSString(ObjC.Send(device, ObjC.Sel("localizedName")));
            if (id is null)
            {
                continue;
            }

            result.Add(new MediaDevice(id, name ?? id, MediaDeviceKind.VideoCapture, MacDeviceEnumerator.BackendName));
        }

        return result;
    }

    /// <summary>Resolves an <c>AVCaptureDevice</c> by unique id, or the default video device.</summary>
    public static nint ResolveVideoDevice(MediaDevice? device)
    {
        var deviceClass = ObjC.objc_getClass("AVCaptureDevice");
        if (deviceClass == 0)
        {
            throw new MediaBackendUnavailableException(
                MacDeviceEnumerator.BackendName, "AVFoundation is not loaded into this process.");
        }

        if (device is not null)
        {
            var resolved = ObjC.Send(deviceClass, ObjC.Sel("deviceWithUniqueID:"), ObjC.NSString(device.Id));
            if (resolved != 0)
            {
                return resolved;
            }
        }

        var fallback = ObjC.Send(deviceClass, ObjC.Sel("defaultDeviceWithMediaType:"), ObjC.NSString(MediaTypeVideo));
        return fallback != 0
            ? fallback
            : throw new MediaToolkitNetException(MacDeviceEnumerator.BackendName, "no camera found", 0);
    }
}

/// <summary>
/// Captures frames from an AVFoundation device.
/// </summary>
/// <remarks>
/// AVFoundation delivers frames to an Objective-C delegate, so this class builds
/// one at runtime with <c>objc_allocateClassPair</c> and stores a
/// <see cref="GCHandle"/> to itself in the instance's indexed ivars. The
/// delegate method is a static <see cref="UnmanagedCallersOnlyAttribute"/>
/// function, which is the only shape the runtime can hand to Objective-C.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed unsafe class AVFoundationVideoCapture : IVideoCapture
{
    private static readonly nint DelegateClass = CreateDelegateClass();
    private static readonly nint SampleSelector =
        ObjC.Sel("captureOutput:didOutputSampleBuffer:fromConnection:");

    private readonly GCHandle _self;
    private readonly List<VideoFormat> _supported = [];

    private nint _session;
    private nint _output;
    private nint _delegate;
    private nint _queue;
    private bool _running;
    private bool _disposed;

    /// <summary>Opens the device described by <paramref name="settings"/>.</summary>
    public AVFoundationVideoCapture(VideoCaptureSettings settings)
    {
        if (!AVFoundation.IsAvailable || DelegateClass == 0)
        {
            throw new MediaBackendUnavailableException(
                MacDeviceEnumerator.BackendName, "AVFoundation is unavailable in this process.");
        }

        _self = GCHandle.Alloc(this, GCHandleType.Normal);

        try
        {
            var device = AVFoundation.ResolveVideoDevice(settings.Device);
            _session = ObjC.New("AVCaptureSession");

            var inputClass = ObjC.objc_getClass("AVCaptureDeviceInput");
            var input = ObjC.Send(inputClass, ObjC.Sel("deviceInputWithDevice:error:"), device, 0);
            if (input == 0 || !ObjC.SendBool(_session, ObjC.Sel("canAddInput:")))
            {
                throw new MediaToolkitNetException(MacDeviceEnumerator.BackendName, "could not open the camera input", 0);
            }

            ObjC.Send(_session, ObjC.Sel("addInput:"), input);

            _output = ObjC.New("AVCaptureVideoDataOutput");
            ObjC.SendSetBool(_output, ObjC.Sel("setAlwaysDiscardsLateVideoFrames:"), true);
            ApplyPixelFormat(settings.Format.PixelFormat);

            _delegate = ObjC.Send(ObjC.Send(DelegateClass, Selectors.Alloc), Selectors.Init);
            *(nint*)AVFoundationNative.object_getIndexedIvars(_delegate) = GCHandle.ToIntPtr(_self);

            _queue = AVFoundationNative.dispatch_queue_create("net.mediatoolkit.capture", 0);
            ObjC.Send(_output, ObjC.Sel("setSampleBufferDelegate:queue:"), _delegate, _queue);

            if (!ObjC.SendBool(_session, ObjC.Sel("canAddOutput:")))
            {
                throw new MediaToolkitNetException(MacDeviceEnumerator.BackendName, "the session will not accept the frame output", 0);
            }

            ObjC.Send(_session, ObjC.Sel("addOutput:"), _output);
            Format = settings.Format;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public string Backend => MacDeviceEnumerator.BackendName;

    /// <inheritdoc />
    public VideoFormat Format { get; private set; }

    /// <summary>
    /// AVFoundation describes modes through <c>AVCaptureDeviceFormat</c> objects
    /// rather than a flat list; this backend reports the negotiated format only.
    /// </summary>
    public IReadOnlyList<VideoFormat> SupportedFormats => _supported;

    /// <inheritdoc />
    public bool IsRunning => _running;

    /// <inheritdoc />
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <inheritdoc />
    public event VideoFrameHandler? FrameCaptured;

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
        {
            return;
        }

        ObjC.Send(_session, ObjC.Sel("startRunning"));
        _running = true;
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        ObjC.Send(_session, ObjC.Sel("stopRunning"));
    }

    private void ApplyPixelFormat(PixelFormat pixelFormat)
    {
        var type = AVFoundationNative.FromPixelFormat(pixelFormat);
        if (type == 0)
        {
            type = AVFoundationNative.PixelFormatNv12FullRange;
        }

        // videoSettings is an NSDictionary keyed by kCVPixelBufferPixelFormatTypeKey,
        // built here as @{ @"PixelFormatType": @(type) }, which is the string
        // value of that key.
        var number = ObjC.Send(
            ObjC.objc_getClass("NSNumber"), ObjC.Sel("numberWithUnsignedInt:"), (nint)type);
        var key = ObjC.NSString("PixelFormatType");
        var dictionary = ObjC.Send(
            ObjC.objc_getClass("NSDictionary"), ObjC.Sel("dictionaryWithObject:forKey:"), number, key);

        ObjC.Send(_output, ObjC.Sel("setVideoSettings:"), dictionary);
    }

    private static nint CreateDelegateClass()
    {
        if (!OperatingSystem.IsMacOS() || !AVFoundationNative.EnsureFrameworkLoaded())
        {
            return 0;
        }

        var super = ObjC.objc_getClass("NSObject");
        if (super == 0)
        {
            return 0;
        }

        // The extra bytes hold the GCHandle that links the Objective-C instance
        // back to its managed owner.
        var cls = ObjC.objc_allocateClassPair(super, "MediaToolkitNetSampleBufferDelegate", (nuint)sizeof(nint));
        if (cls == 0)
        {
            // Already registered by an earlier instance of this library.
            return ObjC.objc_getClass("MediaToolkitNetSampleBufferDelegate");
        }

        var protocol = ObjC.objc_getProtocol("AVCaptureVideoDataOutputSampleBufferDelegate");
        if (protocol != 0)
        {
            ObjC.class_addProtocol(cls, protocol);
        }

        // "v@:@@@": returns void, takes self, _cmd and three objects.
        ObjC.class_addMethod(
            cls,
            ObjC.Sel("captureOutput:didOutputSampleBuffer:fromConnection:"),
            (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&OnSampleBuffer,
            "v@:@@@");

        ObjC.objc_registerClassPair(cls);
        return cls;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnSampleBuffer(nint self, nint selector, nint output, nint sampleBuffer, nint connection)
    {
        var slot = (nint*)AVFoundationNative.object_getIndexedIvars(self);
        if (slot is null || *slot == 0)
        {
            return;
        }

        var handle = GCHandle.FromIntPtr(*slot);
        if (handle.Target is not AVFoundationVideoCapture capture)
        {
            return;
        }

        try
        {
            capture.Deliver(sampleBuffer);
        }
        catch (Exception ex)
        {
            capture.Failed?.Invoke(
                capture, ex as MediaToolkitNetException ?? new MediaToolkitNetException("AVFoundation capture error.", ex));
        }
    }

    private void Deliver(nint sampleBuffer)
    {
        var handler = FrameCaptured;
        if (handler is null)
        {
            return;
        }

        var pixelBuffer = AVFoundationNative.CMSampleBufferGetImageBuffer(sampleBuffer);
        if (pixelBuffer == 0)
        {
            return;
        }

        AVFoundationNative.CVPixelBufferLockBaseAddress(pixelBuffer, AVFoundationNative.LockReadOnly);
        try
        {
            var width = (int)AVFoundationNative.CVPixelBufferGetWidth(pixelBuffer);
            var height = (int)AVFoundationNative.CVPixelBufferGetHeight(pixelBuffer);
            var pixelFormat = AVFoundationNative.ToPixelFormat(
                AVFoundationNative.CVPixelBufferGetPixelFormatType(pixelBuffer));

            var format = new VideoFormat(width, height, pixelFormat, Format.FrameRate);
            Format = format;

            var planeCount = (int)AVFoundationNative.CVPixelBufferGetPlaneCount(pixelBuffer);
            Span<nint> planes = stackalloc nint[MediaPlanes.MaxPlanes];
            Span<int> strides = stackalloc int[MediaPlanes.MaxPlanes];

            if (planeCount == 0)
            {
                // A packed buffer reports no planes; its data is addressed directly.
                planes[0] = AVFoundationNative.CVPixelBufferGetBaseAddress(pixelBuffer);
                strides[0] = (int)AVFoundationNative.CVPixelBufferGetBytesPerRow(pixelBuffer);
                planeCount = 1;
            }
            else
            {
                planeCount = Math.Min(planeCount, MediaPlanes.MaxPlanes);
                for (var i = 0; i < planeCount; i++)
                {
                    planes[i] = AVFoundationNative.CVPixelBufferGetBaseAddressOfPlane(pixelBuffer, (nuint)i);
                    strides[i] = (int)AVFoundationNative.CVPixelBufferGetBytesPerRowOfPlane(pixelBuffer, (nuint)i);
                }
            }

            var timestamp = AVFoundationNative.CMSampleBufferGetPresentationTimeStamp(sampleBuffer).ToTimeSpan();
            var frame = new VideoFrame(format, timestamp, planes, strides, planeCount);
            handler(in frame);
        }
        finally
        {
            AVFoundationNative.CVPixelBufferUnlockBaseAddress(pixelBuffer, AVFoundationNative.LockReadOnly);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

        if (_output != 0 && _delegate != 0)
        {
            // Detach the delegate before the GCHandle goes away, so a frame in
            // flight cannot reach freed managed state.
            ObjC.Send(_output, ObjC.Sel("setSampleBufferDelegate:queue:"), 0, 0);
        }

        if (_delegate != 0)
        {
            var slot = (nint*)AVFoundationNative.object_getIndexedIvars(_delegate);
            if (slot is not null)
            {
                *slot = 0;
            }

            ObjC.Send(_delegate, Selectors.Release);
            _delegate = 0;
        }

        if (_queue != 0)
        {
            AVFoundationNative.dispatch_release(_queue);
            _queue = 0;
        }

        if (_output != 0)
        {
            ObjC.Send(_output, Selectors.Release);
            _output = 0;
        }

        if (_session != 0)
        {
            ObjC.Send(_session, Selectors.Release);
            _session = 0;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}
