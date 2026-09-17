using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Capture;
using MediaToolkitNet.Abstractions.Devices;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Interop;
using MediaToolkitNet.Linux.Native;

namespace MediaToolkitNet.Linux;

/// <summary>Translates between V4L2 FOURCCs and toolkit pixel formats.</summary>
[SupportedOSPlatform("linux")]
public static class V4l2FormatMap
{
    /// <summary>Maps a V4L2 FOURCC onto a toolkit pixel format.</summary>
    public static PixelFormat ToPixelFormat(uint fourCc) => V4l2.FourCcToString(fourCc) switch
    {
        "YUYV" => PixelFormat.Yuyv422,
        "UYVY" => PixelFormat.Uyvy422,
        "NV12" => PixelFormat.Nv12,
        "YU12" => PixelFormat.Yuv420P,
        "422P" => PixelFormat.Yuv422P,
        "444P" => PixelFormat.Yuv444P,
        "MJPG" or "JPEG" => PixelFormat.Mjpeg,
        "RGB3" => PixelFormat.Rgb24,
        "BGR3" => PixelFormat.Bgr24,
        "AB24" => PixelFormat.Rgba32,
        "AR24" => PixelFormat.Bgra32,
        _ => PixelFormat.Unknown,
    };

    /// <summary>Maps a toolkit pixel format onto a V4L2 FOURCC, or 0 when there is none.</summary>
    public static uint ToFourCc(PixelFormat format) => format switch
    {
        PixelFormat.Yuyv422 => V4l2.FourCc("YUYV"),
        PixelFormat.Uyvy422 => V4l2.FourCc("UYVY"),
        PixelFormat.Nv12 => V4l2.FourCc("NV12"),
        PixelFormat.Yuv420P => V4l2.FourCc("YU12"),
        PixelFormat.Yuv422P => V4l2.FourCc("422P"),
        PixelFormat.Yuv444P => V4l2.FourCc("444P"),
        PixelFormat.Mjpeg => V4l2.FourCc("MJPG"),
        PixelFormat.Rgb24 => V4l2.FourCc("RGB3"),
        PixelFormat.Bgr24 => V4l2.FourCc("BGR3"),
        PixelFormat.Rgba32 => V4l2.FourCc("AB24"),
        PixelFormat.Bgra32 => V4l2.FourCc("AR24"),
        _ => 0,
    };
}

/// <summary>Lists the <c>/dev/videoN</c> nodes that can capture video.</summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class V4l2DeviceEnumerator : IMediaDeviceEnumerator
{
    /// <inheritdoc />
    public IReadOnlyList<MediaDevice> Enumerate(MediaDeviceKind kind = MediaDeviceKind.All)
    {
        if ((kind & MediaDeviceKind.VideoCapture) == 0 || !Directory.Exists("/dev"))
        {
            return [];
        }

        var result = new List<MediaDevice>();
        foreach (var path in Directory.EnumerateFiles("/dev", "video*").OrderBy(p => p, StringComparer.Ordinal))
        {
            var fd = V4l2.Open(path, V4l2.ReadWrite);
            if (fd < 0)
            {
                continue;
            }

            try
            {
                V4l2Capability capability;
                if (V4l2.IoctlRetry(fd, V4l2.QueryCap, &capability) < 0)
                {
                    continue;
                }

                // device_caps describes this node; capabilities describes the
                // whole physical device, which may expose several nodes.
                var caps = capability.DeviceCaps != 0 ? capability.DeviceCaps : capability.Capabilities;
                if ((caps & V4l2.CapVideoCapture) == 0)
                {
                    continue;
                }

                var name = Utf8.FromFixedBuffer(capability.Card, 32);
                result.Add(new MediaDevice(
                    path,
                    string.IsNullOrWhiteSpace(name) ? path : name,
                    MediaDeviceKind.VideoCapture,
                    V4l2.BackendName));
            }
            finally
            {
                V4l2.Close(fd);
            }
        }

        return result;
    }

    /// <summary>V4L2 marks no default device, so this returns the first one found.</summary>
    public MediaDevice? GetDefault(MediaDeviceKind kind) => Enumerate(kind).FirstOrDefault();
}

/// <summary>
/// Captures frames from a V4L2 device using memory mapped buffers.
/// </summary>
/// <remarks>
/// Buffers are mapped once and cycled through the driver queue, so a frame
/// reaches <see cref="FrameCaptured"/> without ever being copied.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class V4l2VideoCapture : IVideoCapture
{
    private readonly Lock _gate = new();
    private readonly List<VideoFormat> _supported = [];

    private int _fd = -1;
    private MappedBuffer[] _buffers = [];
    private Thread? _worker;
    private CancellationTokenSource? _cancellation;
    private int _bytesPerLine;
    private bool _disposed;

    /// <summary>Opens the device described by <paramref name="settings"/>.</summary>
    public V4l2VideoCapture(VideoCaptureSettings settings)
    {
        var path = settings.Device?.Id ?? FirstDevice();
        _fd = V4l2.Open(path, V4l2.ReadWrite);
        if (_fd < 0)
        {
            throw new MediaToolkitNetException(
                V4l2.BackendName, $"could not open {path}", Marshal.GetLastPInvokeError());
        }

        try
        {
            ReadSupportedFormats();
            Format = Negotiate(settings.Format);
            MapBuffers(Math.Max(settings.BufferCount, 2));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public string Backend => V4l2.BackendName;

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

            for (var i = 0; i < _buffers.Length; i++)
            {
                QueueBuffer((uint)i);
            }

            var type = V4l2.BufferTypeVideoCapture;
            Check(V4l2.IoctlRetry(_fd, V4l2.StreamOn, &type), "VIDIOC_STREAMON");

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _worker = new Thread(() => CaptureLoop(token))
            {
                IsBackground = true,
                Name = "MediaToolkitNet.V4L2 capture",
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

        if (worker is null)
        {
            return;
        }

        worker.Join(TimeSpan.FromSeconds(2));

        var type = V4l2.BufferTypeVideoCapture;
        V4l2.IoctlRetry(_fd, V4l2.StreamOff, &type);

        lock (_gate)
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private static string FirstDevice()
    {
        var devices = new V4l2DeviceEnumerator().Enumerate(MediaDeviceKind.VideoCapture);
        return devices.Count > 0
            ? devices[0].Id
            : throw new MediaToolkitNetException(V4l2.BackendName, "no video capture devices found", 0);
    }

    private void ReadSupportedFormats()
    {
        for (var index = 0u; ; index++)
        {
            var descriptor = new V4l2FmtDesc { Index = index, Type = V4l2.BufferTypeVideoCapture };
            if (V4l2.IoctlRetry(_fd, V4l2.EnumFmt, &descriptor) < 0)
            {
                break;
            }

            var pixelFormat = V4l2FormatMap.ToPixelFormat(descriptor.PixelFormat);
            if (pixelFormat == PixelFormat.Unknown)
            {
                continue;
            }

            for (var sizeIndex = 0u; ; sizeIndex++)
            {
                var size = new V4l2FrameSizeEnum { Index = sizeIndex, PixelFormat = descriptor.PixelFormat };
                if (V4l2.IoctlRetry(_fd, V4l2.EnumFrameSizes, &size) < 0)
                {
                    break;
                }

                if (size.Type != V4l2.FrameSizeDiscrete)
                {
                    // Continuous and stepwise ranges cannot be expressed as a
                    // single VideoFormat; report the minimum of the range.
                    _supported.Add(new VideoFormat((int)size.Width, (int)size.Height, pixelFormat, Rational.Zero));
                    break;
                }

                _supported.Add(new VideoFormat((int)size.Width, (int)size.Height, pixelFormat, Rational.Zero));
            }
        }
    }

    private VideoFormat Negotiate(VideoFormat requested)
    {
        var fourCc = V4l2FormatMap.ToFourCc(requested.PixelFormat);
        if (fourCc == 0)
        {
            fourCc = V4l2.FourCc("YUYV");
        }

        var format = default(V4l2Format);
        format.Type = V4l2.BufferTypeVideoCapture;
        format.Pix.Width = (uint)requested.Width;
        format.Pix.Height = (uint)requested.Height;
        format.Pix.PixelFormat = fourCc;
        format.Pix.Field = V4l2.FieldNone;

        // The driver rewrites the structure with what it actually accepted.
        Check(V4l2.IoctlRetry(_fd, V4l2.SetFormat, &format), "VIDIOC_S_FMT");

        _bytesPerLine = (int)format.Pix.BytesPerLine;
        return new VideoFormat(
            (int)format.Pix.Width,
            (int)format.Pix.Height,
            V4l2FormatMap.ToPixelFormat(format.Pix.PixelFormat),
            requested.FrameRate);
    }

    private void MapBuffers(int count)
    {
        var request = new V4l2RequestBuffers
        {
            Count = (uint)count,
            Type = V4l2.BufferTypeVideoCapture,
            Memory = V4l2.MemoryMmap,
        };

        Check(V4l2.IoctlRetry(_fd, V4l2.RequestBuffers, &request), "VIDIOC_REQBUFS");

        var buffers = new MappedBuffer[request.Count];
        for (var i = 0u; i < request.Count; i++)
        {
            var descriptor = new V4l2Buffer
            {
                Index = i,
                Type = V4l2.BufferTypeVideoCapture,
                Memory = V4l2.MemoryMmap,
            };

            Check(V4l2.IoctlRetry(_fd, V4l2.QueryBuffer, &descriptor), "VIDIOC_QUERYBUF");

            var address = V4l2.Mmap(
                null, descriptor.Length, V4l2.ProtReadWrite, V4l2.MapShared, _fd, (long)descriptor.Offset);

            if ((nint)address == V4l2.MapFailed)
            {
                throw new MediaToolkitNetException(
                    V4l2.BackendName, "mmap could not map the device buffer", Marshal.GetLastPInvokeError());
            }

            buffers[i] = new MappedBuffer((nint)address, descriptor.Length);
        }

        _buffers = buffers;
    }

    private void QueueBuffer(uint index)
    {
        var descriptor = new V4l2Buffer
        {
            Index = index,
            Type = V4l2.BufferTypeVideoCapture,
            Memory = V4l2.MemoryMmap,
        };

        Check(V4l2.IoctlRetry(_fd, V4l2.QueueBuffer, &descriptor), "VIDIOC_QBUF");
    }

    private void CaptureLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var poll = new PollFd { Fd = _fd, Events = PollFd.PollIn };
                var ready = V4l2.Poll(&poll, 1, 200);
                if (ready <= 0 || (poll.Revents & PollFd.PollIn) == 0)
                {
                    continue;
                }

                var descriptor = new V4l2Buffer
                {
                    Type = V4l2.BufferTypeVideoCapture,
                    Memory = V4l2.MemoryMmap,
                };

                if (V4l2.IoctlRetry(_fd, V4l2.DequeueBuffer, &descriptor) < 0)
                {
                    if (Marshal.GetLastPInvokeError() == V4l2.Again)
                    {
                        continue;
                    }

                    Check(-1, "VIDIOC_DQBUF");
                }

                try
                {
                    Deliver(descriptor);
                }
                finally
                {
                    QueueBuffer(descriptor.Index);
                }
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex as MediaToolkitNetException ?? new MediaToolkitNetException("V4L2 capture error.", ex));
        }
    }

    private void Deliver(V4l2Buffer descriptor)
    {
        var handler = FrameCaptured;
        if (handler is null || descriptor.Index >= _buffers.Length)
        {
            return;
        }

        var buffer = _buffers[descriptor.Index];
        var timestamp = TimeSpan.FromSeconds(descriptor.TimestampSeconds)
                        + TimeSpan.FromMicroseconds(descriptor.TimestampMicroseconds);

        Span<nint> planes = stackalloc nint[MediaPlanes.MaxPlanes];
        Span<int> strides = stackalloc int[MediaPlanes.MaxPlanes];
        var stride = _bytesPerLine != 0 ? _bytesPerLine : Format.PixelFormat.MinimumStride(Format.Width);
        var planeCount = BuildPlanes(Format, buffer.Address, stride, planes, strides);

        var frame = new VideoFrame(Format, timestamp, planes, strides, planeCount);
        handler(in frame);
    }

    /// <summary>
    /// V4L2 hands over one contiguous mapping, so the planar formats need their
    /// plane offsets computed from the geometry.
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
            case PixelFormat.Yuv422P:
                var lumaSize = (long)stride * format.Height;
                var chromaStride = stride / 2;
                var chromaRows = format.PixelFormat == PixelFormat.Yuv420P ? (format.Height + 1) / 2 : format.Height;
                var chromaSize = (long)chromaStride * chromaRows;
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

    private static void Check(int result, string operation)
    {
        if (result < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new MediaToolkitNetException(
                V4l2.BackendName, $"{operation}: {Marshal.GetPInvokeErrorMessage(error)}", error);
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

        foreach (var buffer in _buffers)
        {
            V4l2.Munmap((void*)buffer.Address, buffer.Length);
        }

        _buffers = [];

        if (_fd >= 0)
        {
            V4l2.Close(_fd);
            _fd = -1;
        }
    }

    private readonly record struct MappedBuffer(nint Address, uint Length);
}
