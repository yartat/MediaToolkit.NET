using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MediaToolkitNet.Linux.Native;

/// <summary>Mirrors <c>struct v4l2_capability</c> (104 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct V4l2Capability
{
    /// <summary>Driver name.</summary>
    public fixed byte Driver[16];

    /// <summary>Device name.</summary>
    public fixed byte Card[32];

    /// <summary>Bus location.</summary>
    public fixed byte BusInfo[32];

    /// <summary>Kernel version.</summary>
    public uint Version;

    /// <summary>Capabilities of the physical device.</summary>
    public uint Capabilities;

    /// <summary>Capabilities of this particular node.</summary>
    public uint DeviceCaps;

    /// <summary>Reserved.</summary>
    public fixed uint Reserved[3];
}

/// <summary>Mirrors <c>struct v4l2_fmtdesc</c> (64 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct V4l2FmtDesc
{
    /// <summary>Index to enumerate.</summary>
    public uint Index;

    /// <summary>Buffer type.</summary>
    public uint Type;

    /// <summary>V4L2_FMT_FLAG_*.</summary>
    public uint Flags;

    /// <summary>Human-readable description.</summary>
    public fixed byte Description[32];

    /// <summary>FOURCC of the format.</summary>
    public uint PixelFormat;

    /// <summary>Media bus code and reserved words.</summary>
    public fixed uint Reserved[4];
}

/// <summary>Mirrors <c>struct v4l2_pix_format</c> (48 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct V4l2PixFormat
{
    /// <summary>Frame width in pixels.</summary>
    public uint Width;

    /// <summary>Frame height in pixels.</summary>
    public uint Height;

    /// <summary>FOURCC of the format.</summary>
    public uint PixelFormat;

    /// <summary>Field order; 1 is V4L2_FIELD_NONE.</summary>
    public uint Field;

    /// <summary>Bytes per row, including padding.</summary>
    public uint BytesPerLine;

    /// <summary>Size of one complete frame.</summary>
    public uint SizeImage;

    /// <summary>Colour space.</summary>
    public uint ColorSpace;

    /// <summary>Driver private data.</summary>
    public uint Priv;

    /// <summary>V4L2_PIX_FMT_FLAG_*.</summary>
    public uint Flags;

    /// <summary>YCbCr or HSV encoding.</summary>
    public uint Encoding;

    /// <summary>Quantisation range.</summary>
    public uint Quantization;

    /// <summary>Transfer function.</summary>
    public uint TransferFunction;
}

/// <summary>
/// Mirrors <c>struct v4l2_format</c> (208 bytes on 64-bit). Only the single
/// plane capture branch of the union is mapped.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct V4l2Format
{
    /// <summary>Buffer type.</summary>
    public uint Type;

    /// <summary>Padding inserted before the union, which is 8-byte aligned.</summary>
    public uint Padding;

    /// <summary>The <c>fmt.pix</c> branch of the union.</summary>
    public V4l2PixFormat Pix;

    /// <summary>Remainder of the 200-byte union.</summary>
    public fixed byte UnionTail[152];
}

/// <summary>Mirrors <c>struct v4l2_requestbuffers</c> (20 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct V4l2RequestBuffers
{
    /// <summary>Number of buffers requested, and granted on return.</summary>
    public uint Count;

    /// <summary>Buffer type.</summary>
    public uint Type;

    /// <summary>Memory model; 1 is V4L2_MEMORY_MMAP.</summary>
    public uint Memory;

    /// <summary>Reported buffer capabilities.</summary>
    public uint Capabilities;

    /// <summary>Flags and reserved bytes.</summary>
    public uint Flags;
}

/// <summary>Mirrors <c>struct v4l2_buffer</c> (88 bytes on 64-bit).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct V4l2Buffer
{
    /// <summary>Buffer index.</summary>
    public uint Index;

    /// <summary>Buffer type.</summary>
    public uint Type;

    /// <summary>Bytes actually filled by the driver.</summary>
    public uint BytesUsed;

    /// <summary>V4L2_BUF_FLAG_*.</summary>
    public uint Flags;

    /// <summary>Field order of this buffer.</summary>
    public uint Field;

    /// <summary>Padding before the 8-byte aligned timestamp.</summary>
    public uint Padding;

    /// <summary>Capture time, seconds part.</summary>
    public long TimestampSeconds;

    /// <summary>Capture time, microseconds part.</summary>
    public long TimestampMicroseconds;

    /// <summary>Timecode; not interpreted here.</summary>
    public fixed byte Timecode[16];

    /// <summary>Frame sequence number.</summary>
    public uint Sequence;

    /// <summary>Memory model.</summary>
    public uint Memory;

    /// <summary>The <c>m.offset</c> branch of the union, widened to the pointer size.</summary>
    public ulong Offset;

    /// <summary>Size of the buffer in bytes.</summary>
    public uint Length;

    /// <summary>Reserved.</summary>
    public uint Reserved2;

    /// <summary>Request file descriptor or reserved.</summary>
    public int RequestFd;

    /// <summary>Tail padding to the full 88-byte size.</summary>
    public int TailPadding;
}

/// <summary>Mirrors <c>struct v4l2_frmsizeenum</c> (44 bytes) for the discrete case.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct V4l2FrameSizeEnum
{
    /// <summary>Index to enumerate.</summary>
    public uint Index;

    /// <summary>FOURCC being queried.</summary>
    public uint PixelFormat;

    /// <summary>1 for discrete sizes, 2 for continuous, 3 for stepwise.</summary>
    public uint Type;

    /// <summary>Discrete width.</summary>
    public uint Width;

    /// <summary>Discrete height.</summary>
    public uint Height;

    /// <summary>Remainder of the stepwise branch of the union.</summary>
    public fixed uint UnionTail[4];

    /// <summary>Reserved.</summary>
    public fixed uint Reserved[2];
}

/// <summary>
/// V4L2 bindings.
/// </summary>
/// <remarks>
/// V4L2 has no shared library of its own: it is a set of ioctls on a
/// <c>/dev/videoN</c> file descriptor, so everything here goes through libc.
/// </remarks>
[SupportedOSPlatform("linux")]
public static unsafe partial class V4l2
{
    /// <summary>Backend name used in error messages.</summary>
    public const string BackendName = "v4l2";

    /// <summary>O_RDWR.</summary>
    public const int ReadWrite = 0x0002;

    /// <summary>O_NONBLOCK.</summary>
    public const int NonBlock = 0x0800;

    /// <summary>PROT_READ | PROT_WRITE.</summary>
    public const int ProtReadWrite = 0x1 | 0x2;

    /// <summary>MAP_SHARED.</summary>
    public const int MapShared = 0x01;

    /// <summary>MAP_FAILED.</summary>
    public static readonly nint MapFailed = -1;

    /// <summary>V4L2_BUF_TYPE_VIDEO_CAPTURE.</summary>
    public const uint BufferTypeVideoCapture = 1;

    /// <summary>V4L2_MEMORY_MMAP.</summary>
    public const uint MemoryMmap = 1;

    /// <summary>V4L2_CAP_VIDEO_CAPTURE.</summary>
    public const uint CapVideoCapture = 0x00000001;

    /// <summary>V4L2_CAP_STREAMING.</summary>
    public const uint CapStreaming = 0x04000000;

    /// <summary>V4L2_FIELD_NONE.</summary>
    public const uint FieldNone = 1;

    /// <summary>V4L2_FRMSIZE_TYPE_DISCRETE.</summary>
    public const uint FrameSizeDiscrete = 1;

    /// <summary>EAGAIN, returned by a non-blocking dequeue with no buffer ready.</summary>
    public const int Again = 11;

    private const uint IocNone = 0;
    private const uint IocWrite = 1;
    private const uint IocRead = 2;
    private const byte IocTypeV = (byte)'V';

    /// <summary>
    /// Builds an ioctl request code the way the kernel <c>_IOC</c> macro does:
    /// direction in the top two bits, then the payload size, the type letter and
    /// the command number.
    /// </summary>
    public static uint Ioc(uint direction, byte type, byte number, int size) =>
        (direction << 30) | ((uint)size << 16) | ((uint)type << 8) | number;

    /// <summary>VIDIOC_QUERYCAP.</summary>
    public static readonly uint QueryCap = Ioc(IocRead, IocTypeV, 0, sizeof(V4l2Capability));

    /// <summary>VIDIOC_ENUM_FMT.</summary>
    public static readonly uint EnumFmt = Ioc(IocRead | IocWrite, IocTypeV, 2, sizeof(V4l2FmtDesc));

    /// <summary>VIDIOC_G_FMT.</summary>
    public static readonly uint GetFormat = Ioc(IocRead | IocWrite, IocTypeV, 4, sizeof(V4l2Format));

    /// <summary>VIDIOC_S_FMT.</summary>
    public static readonly uint SetFormat = Ioc(IocRead | IocWrite, IocTypeV, 5, sizeof(V4l2Format));

    /// <summary>VIDIOC_REQBUFS.</summary>
    public static readonly uint RequestBuffers = Ioc(IocRead | IocWrite, IocTypeV, 8, sizeof(V4l2RequestBuffers));

    /// <summary>VIDIOC_QUERYBUF.</summary>
    public static readonly uint QueryBuffer = Ioc(IocRead | IocWrite, IocTypeV, 9, sizeof(V4l2Buffer));

    /// <summary>VIDIOC_QBUF.</summary>
    public static readonly uint QueueBuffer = Ioc(IocRead | IocWrite, IocTypeV, 15, sizeof(V4l2Buffer));

    /// <summary>VIDIOC_DQBUF.</summary>
    public static readonly uint DequeueBuffer = Ioc(IocRead | IocWrite, IocTypeV, 17, sizeof(V4l2Buffer));

    /// <summary>VIDIOC_STREAMON.</summary>
    public static readonly uint StreamOn = Ioc(IocWrite, IocTypeV, 18, sizeof(int));

    /// <summary>VIDIOC_STREAMOFF.</summary>
    public static readonly uint StreamOff = Ioc(IocWrite, IocTypeV, 19, sizeof(int));

    /// <summary>VIDIOC_ENUM_FRAMESIZES.</summary>
    public static readonly uint EnumFrameSizes = Ioc(IocRead | IocWrite, IocTypeV, 74, sizeof(V4l2FrameSizeEnum));

    /// <summary>Unused direction constant, kept so the <c>_IOC</c> mapping stays readable.</summary>
    public const uint DirectionNone = IocNone;

    /// <summary><c>int open(const char *, int)</c></summary>
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int Open(string path, int flags);

    /// <summary><c>int close(int)</c></summary>
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);

    /// <summary><c>int ioctl(int, unsigned long, void *)</c></summary>
    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static partial int Ioctl(int fd, nuint request, void* argument);

    /// <summary><c>void *mmap(void *, size_t, int, int, int, off_t)</c></summary>
    [LibraryImport("libc", EntryPoint = "mmap", SetLastError = true)]
    public static partial void* Mmap(void* address, nuint length, int protection, int flags, int fd, long offset);

    /// <summary><c>int munmap(void *, size_t)</c></summary>
    [LibraryImport("libc", EntryPoint = "munmap", SetLastError = true)]
    public static partial int Munmap(void* address, nuint length);

    /// <summary><c>int poll(struct pollfd *, nfds_t, int)</c></summary>
    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    public static partial int Poll(PollFd* fds, nuint count, int timeoutMilliseconds);

    /// <summary>
    /// Retries an ioctl that the kernel interrupted with EINTR, which is the
    /// standard V4L2 idiom.
    /// </summary>
    public static int IoctlRetry(int fd, uint request, void* argument)
    {
        int result;
        do
        {
            result = Ioctl(fd, request, argument);
        }
        while (result == -1 && Marshal.GetLastPInvokeError() == 4);

        return result;
    }

    /// <summary>Builds a FOURCC the way <c>v4l2_fourcc</c> does.</summary>
    public static uint FourCc(string code) =>
        (uint)(code[0] | (code[1] << 8) | (code[2] << 16) | (code[3] << 24));

    /// <summary>Formats a FOURCC back into its four characters.</summary>
    public static string FourCcToString(uint value) =>
        new([(char)(value & 0xFF), (char)((value >> 8) & 0xFF), (char)((value >> 16) & 0xFF), (char)((value >> 24) & 0xFF)]);
}

/// <summary>Mirrors <c>struct pollfd</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PollFd
{
    /// <summary>File descriptor to watch.</summary>
    public int Fd;

    /// <summary>Requested events; POLLIN is 0x001.</summary>
    public short Events;

    /// <summary>Events that occurred.</summary>
    public short Revents;

    /// <summary>POLLIN.</summary>
    public const short PollIn = 0x001;
}
