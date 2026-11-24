#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;

namespace MediaToolkitNet.GStreamer.Native;

/// <summary>
/// Keeps an error of the X server from ending the process while a screen capture is open.
/// </summary>
/// <remarks>
/// <para>
/// Xlib's default error handler prints the error and calls <c>exit</c>, and <c>ximagesrc</c> installs no handler of its own,
/// so capturing a window that does not exist, one that has just closed, or the root window of a session that does not allow it
/// (WSLg's) would end the application. While at least one capture exists this handler takes the errors of the requests a
/// capture makes (<c>GetWindowAttributes</c>, <c>GetGeometry</c>, <c>GetImage</c>, MIT-SHM's <c>ShmGetImage</c>, and the requests of DAMAGE and XFIXES) and returns,
/// so the failed call returns an error status and the element posts an ordinary GStreamer error. Every other error goes to
/// the handler that was installed before, which for an application that never set one is the one that exits.
/// </para>
/// <para>
/// <c>XErrorEvent</c> is read by offset, so its layout is confirmed before the handler is trusted: a request is made for a
/// window that cannot exist, and the error that comes back must name that request, that window and <c>BadWindow</c>.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal static unsafe partial class XErrors
{
    // XErrorEvent: int type 0, Display* 8, XID resourceid 16, unsigned long serial 24, error_code 32, request_code 33,
    // minor_code 34 (Xlib.h, LP64).
    private const int ResourceOffset = 16;
    private const int ErrorCodeOffset = 32;
    private const int RequestCodeOffset = 33;
    private const int MinorCodeOffset = 34;

    private const byte BadWindow = 3;
    private const byte GetWindowAttributes = 3;
    private const byte GetGeometry = 14;
    private const byte GetImage = 73;
    private const byte ShmGetImage = 4;
    private const ulong NoSuchWindow = 0x7FFFFFF0;

    private static readonly Lock Gate = new();
    private static int _users;
    private static nint _previous;
    private static int _shmMajor = -1;
    private static int _damageMajor = -1;
    private static int _fixesMajor = -1;
    private static bool? _layoutConfirmed;
    private static (byte Error, byte Request, ulong Resource)? _probe;
    private static string? _last;

    /// <summary>Raised with a description each time the handler takes an error, on the thread that made the request. Handlers must not throw.</summary>
    public static event Action<string>? Taken;

    /// <summary>The last error the handler took, for the message of a failure; null when there has been none.</summary>
    public static string? Last => Volatile.Read(ref _last);

    /// <summary>Installs the handler for one more capture.</summary>
    /// <exception cref="MediaToolkitNetException">The layout of the error event was not confirmed.</exception>
    public static void Acquire()
    {
        lock (Gate)
        {
            if (_users == 0)
            {
                _previous = XSetErrorHandler(&Handle);
                try
                {
                    _layoutConfirmed ??= ConfirmLayout();
                }
                catch
                {
                    XSetErrorHandler((delegate* unmanaged[Cdecl]<nint, byte*, int>)_previous);
                    throw;
                }

                if (_layoutConfirmed != true)
                {
                    XSetErrorHandler((delegate* unmanaged[Cdecl]<nint, byte*, int>)_previous);
                    throw new MediaToolkitNetException(
                        GstConstants.BackendName, "X errors cannot be kept from ending the process: the layout of XErrorEvent was not confirmed", 0);
                }
            }

            _users++;
        }
    }

    /// <summary>Takes the handler out when the last capture is gone, putting the earlier one back.</summary>
    public static void Release()
    {
        lock (Gate)
        {
            if (_users == 0 || --_users > 0)
            {
                return;
            }

            XSetErrorHandler((delegate* unmanaged[Cdecl]<nint, byte*, int>)_previous);
            _previous = 0;
        }
    }

    /// <summary>
    /// True when the X display has a window with this id. Needs a capture to be open (<see cref="Acquire"/>), or the error
    /// of a window that is not there ends the process. ximagesrc given such an id captures the whole screen instead, silently.
    /// </summary>
    public static bool WindowExists(nuint window)
    {
        var display = XOpenDisplay(0);
        if (display == 0)
        {
            return false;
        }

        try
        {
            var attributes = stackalloc byte[256];
            var found = XGetWindowAttributes(display, window, attributes) != 0;
            XSync(display, 0);
            return found;
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    // Makes the one error whose every field is known and checks that the event says so.
    private static bool ConfirmLayout()
    {
        var display = XOpenDisplay(0);
        if (display == 0)
        {
            // No display: nothing can be captured either, and the capture will say so.
            return true;
        }

        try
        {
            _probe = null;
            var attributes = stackalloc byte[256];
            XGetWindowAttributes(display, (nuint)NoSuchWindow, attributes);
            XSync(display, 0);

            _shmMajor = ExtensionMajor(display, "MIT-SHM"u8);
            _damageMajor = ExtensionMajor(display, "DAMAGE"u8);
            _fixesMajor = ExtensionMajor(display, "XFIXES"u8);

            return _probe is { } seen && seen == (BadWindow, GetWindowAttributes, NoSuchWindow);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    // The opcode an extension's requests carry, or -1 when the server has none by that name.
    private static int ExtensionMajor(nint display, ReadOnlySpan<byte> name)
    {
        var text = stackalloc byte[16];
        name.CopyTo(new Span<byte>(text, 15));
        text[name.Length] = 0;
        int major;
        int firstEvent;
        int firstError;
        return XQueryExtension(display, text, &major, &firstEvent, &firstError) != 0 ? major : -1;
    }

    // What a capture asks of the server: the window's attributes and geometry, its pixels (also through MIT-SHM), and, for the
    // watching of changes and of the pointer, every request of DAMAGE and XFIXES, which fail when the window is already gone.
    private static bool IsCapturesRequest(byte request, byte minor) =>
        request is GetWindowAttributes or GetGeometry or GetImage
        || (request == _shmMajor && minor == ShmGetImage)
        || request == _damageMajor
        || request == _fixesMajor;

    // int XErrorHandler(Display *, XErrorEvent *). Nothing may throw out of it.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Handle(nint display, byte* error)
    {
        try
        {
            var code = error[ErrorCodeOffset];
            var request = error[RequestCodeOffset];
            var minor = error[MinorCodeOffset];
            var resource = *(ulong*)(error + ResourceOffset);

            if (_layoutConfirmed is null)
            {
                _probe = (code, request, resource);
                return 0;
            }

            if (IsCapturesRequest(request, minor))
            {
                var text = $"X error {code} in request {request}.{minor} on window 0x{resource:x}";
                Volatile.Write(ref _last, text);
                Taken?.Invoke(text);
                return 0;
            }

            var previous = _previous;
            return previous == 0 ? 0 : ((delegate* unmanaged[Cdecl]<nint, byte*, int>)previous)(display, error);
        }
        catch
        {
            return 0;
        }
    }

    [LibraryImport("libX11.so.6")]
    private static partial nint XSetErrorHandler(delegate* unmanaged[Cdecl]<nint, byte*, int> handler);

    [LibraryImport("libX11.so.6")]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial int XSync(nint display, int discard);

    [LibraryImport("libX11.so.6")]
    private static partial int XGetWindowAttributes(nint display, nuint window, byte* attributes);

    [LibraryImport("libX11.so.6")]
    private static partial int XQueryExtension(nint display, byte* name, int* majorOpcode, int* firstEvent, int* firstError);
}
