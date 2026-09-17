using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Capture;
using MediaToolkitNet.Abstractions.Devices;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>
/// Captures frames from a camera or capture card through
/// <c>IMFSourceReader</c> in synchronous mode.
/// </summary>
/// <remarks>
/// The reader is created with video processing enabled, so Media Foundation
/// inserts a converter when the requested pixel format is not one the device
/// produces natively.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class MediaFoundationVideoCapture : IVideoCapture
{
    private readonly Lock _gate = new();
    private readonly List<VideoFormat> _supported = [];

    private ComPtr _source;
    private ComPtr _reader;
    private Thread? _worker;
    private CancellationTokenSource? _cancellation;
    private int _defaultStride;
    private bool _disposed;

    /// <summary>Opens the device described by <paramref name="settings"/>.</summary>
    public MediaFoundationVideoCapture(VideoCaptureSettings settings)
    {
        MfApi.Startup();

        _source = MediaFoundationDeviceEnumerator.ActivateSource(settings.Device, MediaDeviceKind.VideoCapture);
        try
        {
            void* attributes;
            HResult.ThrowIfFailed(MfApi.MFCreateAttributes(&attributes, 2), "MFCreateAttributes");
            try
            {
                Mf.SetUint32(attributes, WinGuids.EnableVideoProcessing, 1);
                Mf.SetUint32(attributes, WinGuids.EnableHardwareTransforms, 1);

                void* reader;
                HResult.ThrowIfFailed(
                    MfApi.MFCreateSourceReaderFromMediaSource(_source.Pointer, attributes, &reader),
                    "MFCreateSourceReaderFromMediaSource");
                _reader = new ComPtr(reader);
            }
            finally
            {
                Com.Release(attributes);
            }

            ReadSupportedFormats();
            Format = Negotiate(settings.Format);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public string Backend => MediaFoundationDeviceEnumerator.BackendName;

    /// <inheritdoc />
    public VideoFormat Format { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<VideoFormat> SupportedFormats => _supported;

    /// <inheritdoc />
    public bool IsRunning => _worker is not null;

    /// <inheritdoc />
    public event EventHandler<MediaToolkitNetException>? Failed;

    /// <inheritdoc />
    public event VideoFrameHandler? FrameCaptured;

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_worker is not null)
            {
                return;
            }

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _worker = new Thread(() => CaptureLoop(token))
            {
                IsBackground = true,
                Name = "MediaToolkitNet.MediaFoundation capture",
            };
            _worker.Start();
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        Thread? worker;
        lock (_gate)
        {
            worker = _worker;
            _worker = null;
            _cancellation?.Cancel();
        }

        worker?.Join(TimeSpan.FromSeconds(2));

        lock (_gate)
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private void ReadSupportedFormats()
    {
        for (var index = 0u; ; index++)
        {
            var hr = Mf.GetNativeMediaType(_reader.Pointer, MfApi.FirstVideoStream, index, out var mediaType);
            if (HResult.Failed(hr))
            {
                // MF_E_NO_MORE_TYPES ends the enumeration.
                break;
            }

            try
            {
                var format = DescribeMediaType(mediaType);
                if (format.IsValid)
                {
                    _supported.Add(format);
                }
            }
            finally
            {
                Com.Release(mediaType);
            }
        }
    }

    private static VideoFormat DescribeMediaType(void* mediaType)
    {
        if (HResult.Failed(Mf.GetGuid(mediaType, WinGuids.Subtype, out var subtype)))
        {
            return default;
        }

        var pixelFormat = MediaFoundationFormatMap.ToPixelFormat(subtype);
        if (HResult.Failed(Mf.GetUint64(mediaType, WinGuids.FrameSize, out var packedSize)))
        {
            return default;
        }

        var (width, height) = Mf.Unpack(packedSize);
        var frameRate = Rational.Zero;
        if (HResult.Succeeded(Mf.GetUint64(mediaType, WinGuids.FrameRate, out var packedRate)))
        {
            var (numerator, denominator) = Mf.Unpack(packedRate);
            frameRate = new Rational((int)numerator, (int)denominator);
        }

        return new VideoFormat((int)width, (int)height, pixelFormat, frameRate);
    }

    private VideoFormat Negotiate(VideoFormat requested)
    {
        var subtype = MediaFoundationFormatMap.ToSubtype(requested.PixelFormat);
        if (subtype == Guid.Empty)
        {
            subtype = WinGuids.VideoNv12;
        }

        void* mediaType;
        HResult.ThrowIfFailed(MfApi.MFCreateMediaType(&mediaType), "MFCreateMediaType");

        try
        {
            HResult.ThrowIfFailed(
                Mf.SetGuid(mediaType, WinGuids.MajorType, WinGuids.MediaTypeVideo), "SetGUID(MAJOR_TYPE)");
            HResult.ThrowIfFailed(Mf.SetGuid(mediaType, WinGuids.Subtype, subtype), "SetGUID(SUBTYPE)");
            Mf.SetUint64(mediaType, WinGuids.FrameSize, Mf.Pack((uint)requested.Width, (uint)requested.Height));
            Mf.SetUint32(mediaType, WinGuids.InterlaceMode, MfApi.InterlaceProgressive);

            if (requested.FrameRate.Denominator > 0)
            {
                Mf.SetUint64(
                    mediaType,
                    WinGuids.FrameRate,
                    Mf.Pack((uint)requested.FrameRate.Numerator, (uint)requested.FrameRate.Denominator));
            }

            // A device that cannot do the exact mode keeps whatever it already
            // has, which is more useful than refusing to open at all. The caller
            // sees the result through Format and SupportedFormats.
            _ = Mf.SetCurrentMediaType(_reader.Pointer, MfApi.FirstVideoStream, mediaType);
        }
        finally
        {
            Com.Release(mediaType);
        }

        HResult.ThrowIfFailed(
            Mf.SetStreamSelection(_reader.Pointer, MfApi.FirstVideoStream, true),
            "IMFSourceReader::SetStreamSelection");

        return ReadCurrentFormat();
    }

    private VideoFormat ReadCurrentFormat()
    {
        HResult.ThrowIfFailed(
            Mf.GetCurrentMediaType(_reader.Pointer, MfApi.FirstVideoStream, out var current),
            "IMFSourceReader::GetCurrentMediaType");

        try
        {
            var format = DescribeMediaType(current);
            _defaultStride = HResult.Succeeded(Mf.GetUint32(current, WinGuids.DefaultStride, out var stride))
                ? (int)stride
                : format.PixelFormat.MinimumStride(format.Width);
            return format;
        }
        finally
        {
            Com.Release(current);
        }
    }

    private void CaptureLoop(CancellationToken token)
    {
        // The source reader objects outlive this thread, so the apartment is
        // initialised but never torn back down here.
        Ole32.Initialize();

        try
        {
            while (!token.IsCancellationRequested)
            {
                var hr = Mf.ReadSample(
                    _reader.Pointer, MfApi.FirstVideoStream, out _, out var flags, out var timestamp, out var sample);
                HResult.ThrowIfFailed(hr, "IMFSourceReader::ReadSample");

                if ((flags & MfApi.MediaTypeChanged) != 0)
                {
                    Format = ReadCurrentFormat();
                }

                if (sample is null)
                {
                    if ((flags & MfApi.EndOfStream) != 0)
                    {
                        return;
                    }

                    // A null sample without end-of-stream simply means the device
                    // had nothing ready; loop again.
                    continue;
                }

                try
                {
                    Deliver(sample, timestamp);
                }
                finally
                {
                    Com.Release(sample);
                }
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex as MediaToolkitNetException ?? new MediaToolkitNetException("Video capture error.", ex));
        }
    }

    private void Deliver(void* sample, long timestamp)
    {
        var handler = FrameCaptured;
        if (handler is null)
        {
            return;
        }

        HResult.ThrowIfFailed(Mf.ConvertToContiguousBuffer(sample, out var buffer), "IMFSample::ConvertToContiguousBuffer");
        try
        {
            HResult.ThrowIfFailed(Mf.LockBuffer(buffer, out var data, out _, out _), "IMFMediaBuffer::Lock");
            try
            {
                var format = Format;
                var stride = _defaultStride != 0 ? _defaultStride : format.PixelFormat.MinimumStride(format.Width);

                Span<nint> planes = stackalloc nint[MediaPlanes.MaxPlanes];
                Span<int> strides = stackalloc int[MediaPlanes.MaxPlanes];
                var planeCount = BuildPlanes(format, (nint)data, stride, planes, strides);

                // Media Foundation timestamps are in 100-nanosecond units, which
                // is exactly one TimeSpan tick.
                var frame = new VideoFrame(format, new TimeSpan(timestamp), planes, strides, planeCount);
                handler(in frame);
            }
            finally
            {
                Mf.UnlockBuffer(buffer);
            }
        }
        finally
        {
            Com.Release(buffer);
        }
    }

    /// <summary>
    /// Splits a contiguous buffer into planes. Media Foundation hands over one
    /// block, so the planar formats need their offsets computed by hand.
    /// </summary>
    private static int BuildPlanes(VideoFormat format, nint data, int stride, Span<nint> planes, Span<int> strides)
    {
        switch (format.PixelFormat)
        {
            case PixelFormat.Nv12:
                planes[0] = data;
                strides[0] = stride;
                planes[1] = data + (nint)((long)stride * format.Height);
                strides[1] = stride;
                return 2;

            case PixelFormat.Yuv420P:
                var lumaSize = (long)stride * format.Height;
                var chromaStride = stride / 2;
                var chromaSize = (long)chromaStride * ((format.Height + 1) / 2);
                planes[0] = data;
                strides[0] = stride;
                planes[1] = data + (nint)lumaSize;
                strides[1] = chromaStride;
                planes[2] = data + (nint)(lumaSize + chromaSize);
                strides[2] = chromaStride;
                return 3;

            default:
                planes[0] = data;
                strides[0] = stride;
                return 1;
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

        _reader.Dispose();
        if (!_source.IsNull)
        {
            Mf.ShutdownSource(_source.Pointer);
            _source.Dispose();
        }
    }
}
