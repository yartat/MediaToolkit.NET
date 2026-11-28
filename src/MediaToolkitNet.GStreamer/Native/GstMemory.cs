#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer.Native;

/// <summary>Buffers around memory GStreamer did not allocate, and <c>libgstallocators-1.0</c> for DMABuf.</summary>
[SupportedOSPlatform("linux")]
internal static unsafe class GstMemory
{
    private static readonly ConcurrentDictionary<nint, Action> Pending = new();
    private static readonly Lock Gate = new();
    private static long _next;
    private static bool _attempted;
    private static void* _allocator;
    private static delegate* unmanaged[Cdecl]<void*, int, nuint, void*> _allocate;
    private static delegate* unmanaged[Cdecl]<void*, int> _isDmaBuf;
    private static delegate* unmanaged[Cdecl]<void*, int> _fdOf;

    // GST_MEMORY_FLAG_READONLY: nothing downstream may write into memory that is not GStreamer's.
    private const int ReadOnly = 1 << 1;

    /// <summary>A buffer that is the caller's memory; <paramref name="released"/> runs once when nothing uses it.</summary>
    public static void* Wrap(void* data, int size, Action? released)
    {
        var id = released is null ? 0 : Register(released);
        return Gst.gst_buffer_new_wrapped_full(
            ReadOnly, data, (nuint)size, 0, (nuint)size, (void*)id,
            released is null ? null : (void*)(delegate* unmanaged[Cdecl]<void*, void>)&OnReleased);
    }

    /// <summary>A buffer of one DMABuf memory over the descriptor, which GStreamer takes over and closes.</summary>
    public static void* DmaBuf(int fd, int size, Action? released)
    {
        if (!LoadAllocators())
        {
            throw new NotSupportedException("libgstallocators-1.0 is not installed, so descriptors cannot be passed on.");
        }

        var memory = _allocate(_allocator, fd, (nuint)size);
        if (memory is null)
        {
            return null;
        }

        if (released is not null)
        {
            Gst.gst_mini_object_weak_ref(memory, &OnFreed, (void*)Register(released));
        }

        var buffer = Gst.gst_buffer_new();
        Gst.gst_buffer_append_memory(buffer, memory);
        return buffer;
    }

    /// <summary>The descriptor behind a buffer that is one DMABuf memory, or null.</summary>
    public static int? DescriptorOf(void* buffer)
    {
        if (!LoadAllocators() || Gst.gst_buffer_n_memory(buffer) != 1)
        {
            return null;
        }

        var memory = Gst.gst_buffer_peek_memory(buffer, 0);
        return _isDmaBuf(memory) != 0 ? _fdOf(memory) : null;
    }

    private static bool LoadAllocators()
    {
        lock (Gate)
        {
            if (!_attempted)
            {
                _attempted = true;
                if (NativeModule.TryLoad(["libgstallocators-1.0.so.0", "libgstallocators-1.0.so"], out var module))
                {
                    _allocate = (delegate* unmanaged[Cdecl]<void*, int, nuint, void*>)module.GetExport("gst_dmabuf_allocator_alloc");
                    _isDmaBuf = (delegate* unmanaged[Cdecl]<void*, int>)module.GetExport("gst_is_dmabuf_memory");
                    _fdOf = (delegate* unmanaged[Cdecl]<void*, int>)module.GetExport("gst_dmabuf_memory_get_fd");
                    _allocator = ((delegate* unmanaged[Cdecl]<void*>)module.GetExport("gst_dmabuf_allocator_new"))();
                }
            }

            return _allocator is not null;
        }
    }

    // The native side is given a number, which is spent when the callback runs, so nothing can run it twice.
    private static nint Register(Action released)
    {
        var id = (nint)Interlocked.Increment(ref _next);
        Pending[id] = released;
        return id;
    }

    private static void Release(nint id)
    {
        if (Pending.TryRemove(id, out var released))
        {
            released();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnReleased(void* data)
    {
        try
        {
            Release((nint)data);
        }
        catch
        {
            // Nothing may throw into GStreamer; the caller's action is wrapped to report what it throws.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFreed(void* data, void* memory)
    {
        try
        {
            Release((nint)data);
        }
        catch
        {
            // As above.
        }
    }
}
