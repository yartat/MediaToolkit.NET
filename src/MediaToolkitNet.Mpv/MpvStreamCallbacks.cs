#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.Mpv;

/// <summary>
/// Opens the stream a custom protocol's URI names. Return <see langword="null"/> when there is no such stream; mpv
/// then fails to load it. mpv reads the stream on one of its own threads and disposes it when it is done with it.
/// </summary>
/// <param name="uri">The whole URI, protocol prefix included.</param>
public delegate Stream? MpvStreamOpener(string uri);

/// <summary>One protocol registered with <c>mpv_stream_cb_add_ro</c>: what its URIs open.</summary>
internal sealed class MpvStreamProtocol(Func<string, (Stream? Stream, bool Own)> open)
{
    public Func<string, (Stream? Stream, bool Own)> Open { get; } = open;

    public GCHandle Handle { get; set; }
}

/// <summary>
/// The callbacks libmpv's <c>stream_cb</c> API calls into, which present a .NET <see cref="Stream"/> as an mpv stream.
/// Nothing here may throw into libmpv, and none may call libmpv, which would deadlock.
/// </summary>
internal static unsafe class MpvStreamCallbacks
{
    // mpv_stream_cb_info: cookie 0, read_fn 8, seek_fn 16, size_fn 24, close_fn 32, cancel_fn 40 (since API 1.106).
    // The struct is libmpv's, so on an older library it is 40 bytes and cancel_fn must not be written.
    private const int CancelOffset = 40;
    private const uint CancelApiVersion = (1 << 16) | 106;

    private const int ErrorLoadingFailed = -13;
    private const int ErrorUnsupported = -18;
    private const int ErrorGeneric = -20;

    private sealed class Cookie(Stream stream, bool own)
    {
        public Stream Stream { get; } = stream;

        public bool Own { get; } = own;

        public volatile bool Cancelled;
    }

    public static delegate* unmanaged[Cdecl]<void*, byte*, nint*, int> OpenFunction => &Open;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Open(void* userData, byte* uri, nint* info)
    {
        try
        {
            var protocol = (MpvStreamProtocol)GCHandle.FromIntPtr((nint)userData).Target!;
            var (stream, own) = protocol.Open(Utf8.ToManagedOrEmpty(uri));
            if (stream is null)
            {
                return ErrorLoadingFailed;
            }

            if (!stream.CanRead)
            {
                if (own)
                {
                    stream.Dispose();
                }

                return ErrorLoadingFailed;
            }

            info[0] = GCHandle.ToIntPtr(GCHandle.Alloc(new Cookie(stream, own)));
            info[1] = (nint)(delegate* unmanaged[Cdecl]<void*, byte*, ulong, long>)&Read;
            info[2] = (nint)(delegate* unmanaged[Cdecl]<void*, long, long>)&Seek;
            info[3] = (nint)(delegate* unmanaged[Cdecl]<void*, long>)&Size;
            info[4] = (nint)(delegate* unmanaged[Cdecl]<void*, void>)&Close;
            if (Native.Mpv.ApiVersion >= CancelApiVersion)
            {
                *(nint*)((byte*)info + CancelOffset) = (nint)(delegate* unmanaged[Cdecl]<void*, void>)&Cancel;
            }

            return 0;
        }
        catch (Exception)
        {
            return ErrorLoadingFailed;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long Read(void* cookie, byte* buffer, ulong size)
    {
        try
        {
            var state = Of(cookie);
            if (state.Cancelled)
            {
                return -1;
            }

            return state.Stream.Read(new Span<byte>(buffer, (int)Math.Min(size, int.MaxValue)));
        }
        catch (Exception)
        {
            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long Seek(void* cookie, long offset)
    {
        try
        {
            var state = Of(cookie);
            return state.Cancelled ? ErrorGeneric
                : state.Stream.CanSeek ? state.Stream.Seek(offset, SeekOrigin.Begin)
                : ErrorUnsupported;
        }
        catch (Exception)
        {
            return ErrorGeneric;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long Size(void* cookie)
    {
        try
        {
            var stream = Of(cookie).Stream;
            return stream.CanSeek ? stream.Length : ErrorUnsupported;
        }
        catch (Exception)
        {
            return ErrorUnsupported;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Close(void* cookie)
    {
        var handle = GCHandle.FromIntPtr((nint)cookie);
        try
        {
            var state = (Cookie)handle.Target!;
            if (state.Own)
            {
                state.Stream.Dispose();
            }
        }
        catch (Exception)
        {
            // Nothing may be thrown into libmpv.
        }
        finally
        {
            handle.Free();
        }
    }

    // Called from another thread to stop reads that are waiting. A .NET Stream cannot be interrupted, so this
    // fails what follows; a read already in progress ends when the stream returns.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Cancel(void* cookie)
    {
        try
        {
            Of(cookie).Cancelled = true;
        }
        catch (Exception)
        {
            // Nothing may be thrown into libmpv.
        }
    }

    private static Cookie Of(void* cookie) => (Cookie)GCHandle.FromIntPtr((nint)cookie).Target!;
}
