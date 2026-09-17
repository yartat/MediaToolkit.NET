using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MediaToolkitNet.Interop;

/// <summary>
/// UTF-8 marshalling helpers for the C APIs wrapped by this repository
/// (FFmpeg, libmpv, ALSA, PulseAudio all take and return UTF-8 char pointers).
/// </summary>
public static unsafe class Utf8
{
    /// <summary>Reads a NUL-terminated UTF-8 string, returning <see langword="null"/> for a null pointer.</summary>
    public static string? ToManaged(byte* pointer) =>
        pointer is null ? null : Marshal.PtrToStringUTF8((nint)pointer);

    /// <summary>Reads a NUL-terminated UTF-8 string, returning an empty string for a null pointer.</summary>
    public static string ToManagedOrEmpty(byte* pointer) => ToManaged(pointer) ?? string.Empty;

    /// <summary>
    /// Reads a fixed-size, possibly unterminated byte field as UTF-8 and trims
    /// trailing NULs. V4L2 and ALSA both return names in such fields.
    /// </summary>
    public static string FromFixedBuffer(byte* pointer, int capacity)
    {
        if (pointer is null || capacity <= 0)
        {
            return string.Empty;
        }

        var span = new ReadOnlySpan<byte>(pointer, capacity);
        var end = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }

    /// <summary>
    /// Allocates a NUL-terminated UTF-8 copy on the unmanaged heap. Free it with
    /// <see cref="Free"/>.
    /// </summary>
    public static byte* Allocate(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        var buffer = (byte*)NativeMemory.Alloc((nuint)byteCount + 1);
        Encoding.UTF8.GetBytes(value, new Span<byte>(buffer, byteCount));
        buffer[byteCount] = 0;
        return buffer;
    }

    /// <summary>Frees a pointer returned by <see cref="Allocate"/>.</summary>
    public static void Free(byte* pointer)
    {
        if (pointer is not null)
        {
            NativeMemory.Free(pointer);
        }
    }
}

/// <summary>
/// A short-lived NUL-terminated UTF-8 copy of a string, kept on the stack when
/// it is small enough and on a pooled array otherwise.
/// </summary>
public unsafe ref struct Utf8Scoped
{
    /// <summary>Recommended size of the caller-provided stack buffer.</summary>
    public const int StackThreshold = 256;

    private byte* _owned;

    /// <summary>Copies <paramref name="value"/> into <paramref name="scratch"/> or onto the native heap.</summary>
    /// <param name="value">String to encode; may be <see langword="null"/>.</param>
    /// <param name="scratch">
    /// Caller-provided stack buffer, normally
    /// <c>stackalloc byte[Utf8Scoped.StackThreshold]</c>. Strings that do not fit
    /// fall back to <see cref="NativeMemory"/> so the pointer stays pinned.
    /// </param>
    public Utf8Scoped(string? value, Span<byte> scratch)
    {
        _owned = null;
        Pointer = null;

        if (value is null)
        {
            return;
        }

        var needed = Encoding.UTF8.GetByteCount(value) + 1;
        if (needed <= scratch.Length)
        {
            var written = Encoding.UTF8.GetBytes(value, scratch);
            scratch[written] = 0;

            // The scratch span is stack memory owned by the caller, so taking a
            // pointer to it needs no pinning.
            Pointer = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(scratch));
            return;
        }

        _owned = Utf8.Allocate(value);
        Pointer = _owned;
    }

    /// <summary>Pointer to the encoded string, or null when the source was <see langword="null"/>.</summary>
    public byte* Pointer { get; private set; }

    /// <summary>Frees the native buffer, if one was allocated.</summary>
    public void Dispose()
    {
        if (_owned is not null)
        {
            Utf8.Free(_owned);
            _owned = null;
            Pointer = null;
        }
    }
}
