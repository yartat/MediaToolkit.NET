using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Core;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.MacOS.Native;

/// <summary>
/// Objective-C runtime bindings.
/// </summary>
/// <remarks>
/// <para>
/// AVFoundation has no C API, so this backend talks to it the way every other
/// non-Objective-C language does: by looking classes and selectors up in the
/// runtime and dispatching through <c>objc_msgSend</c>.
/// </para>
/// <para>
/// <c>objc_msgSend</c> is declared without a prototype on purpose. Its real
/// signature is whatever the selector expects, so the address is cast to the
/// matching function pointer at each call site; using one fixed managed
/// signature would corrupt the argument registers.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public static unsafe partial class ObjC
{
    private const string Runtime = "/usr/lib/libobjc.A.dylib";

    /// <summary>Address of <c>objc_msgSend</c>, to be cast at the call site.</summary>
    public static readonly void* MsgSend = LoadExport("objc_msgSend");

    /// <summary>
    /// Address of <c>objc_msgSend_stret</c>, which exists only on x86-64 and is
    /// needed there for selectors returning a struct larger than 16 bytes.
    /// </summary>
    public static readonly void* MsgSendStret = LoadExport("objc_msgSend_stret");

    /// <summary><c>Class objc_getClass(const char *name)</c></summary>
    [LibraryImport(Runtime, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint objc_getClass(string name);

    /// <summary><c>SEL sel_registerName(const char *name)</c></summary>
    [LibraryImport(Runtime, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint sel_registerName(string name);

    /// <summary><c>Protocol *objc_getProtocol(const char *name)</c></summary>
    [LibraryImport(Runtime, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint objc_getProtocol(string name);

    /// <summary><c>Class objc_allocateClassPair(Class superclass, const char *name, size_t extraBytes)</c></summary>
    [LibraryImport(Runtime, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);

    /// <summary><c>void objc_registerClassPair(Class)</c></summary>
    [LibraryImport(Runtime)]
    public static partial void objc_registerClassPair(nint cls);

    /// <summary><c>BOOL class_addMethod(Class, SEL, IMP, const char *types)</c></summary>
    [LibraryImport(Runtime, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool class_addMethod(nint cls, nint selector, void* implementation, string types);

    /// <summary><c>BOOL class_addProtocol(Class, Protocol *)</c></summary>
    [LibraryImport(Runtime)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool class_addProtocol(nint cls, nint protocol);

    /// <summary><c>Class object_getClass(id)</c></summary>
    [LibraryImport(Runtime)]
    public static partial nint object_getClass(nint obj);

    /// <summary>Sends a message with no arguments and an object or integer result.</summary>
    public static nint Send(nint receiver, nint selector) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint>)MsgSend)(receiver, selector);

    /// <summary>Sends a message with one pointer-sized argument.</summary>
    public static nint Send(nint receiver, nint selector, nint argument) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)MsgSend)(receiver, selector, argument);

    /// <summary>Sends a message with two pointer-sized arguments.</summary>
    public static nint Send(nint receiver, nint selector, nint first, nint second) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint>)MsgSend)(receiver, selector, first, second);

    /// <summary>Sends a message with three pointer-sized arguments.</summary>
    public static nint Send(nint receiver, nint selector, nint first, nint second, nint third) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint>)MsgSend)(receiver, selector, first, second, third);

    /// <summary>Sends a message returning a boolean.</summary>
    public static bool SendBool(nint receiver, nint selector) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, byte>)MsgSend)(receiver, selector) != 0;

    /// <summary>Sends a message taking a boolean.</summary>
    public static void SendSetBool(nint receiver, nint selector, bool value) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, byte, void>)MsgSend)(receiver, selector, value ? (byte)1 : (byte)0);

    /// <summary>Sends a message returning a double.</summary>
    public static double SendDouble(nint receiver, nint selector) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, double>)MsgSend)(receiver, selector);

    /// <summary>Sends a message taking a float.</summary>
    public static void SendSetFloat(nint receiver, nint selector, float value) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, float, void>)MsgSend)(receiver, selector, value);

    /// <summary>Allocates and initialises an instance of the named class.</summary>
    public static nint New(string className)
    {
        var cls = objc_getClass(className);
        if (cls == 0)
        {
            throw new MediaToolkitNetException($"The Objective-C class \"{className}\" was not found.");
        }

        return Send(Send(cls, Selectors.Alloc), Selectors.Init);
    }

    /// <summary>Wraps a managed string in an autoreleased <c>NSString</c>.</summary>
    public static nint NSString(string value)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(value, scratch);

        var cls = objc_getClass("NSString");
        // stringWithUTF8String: copies the bytes, so the scratch buffer can go.
        return ((delegate* unmanaged[Cdecl]<nint, nint, byte*, nint>)MsgSend)(
            cls, Selectors.StringWithUtf8String, utf8.Pointer);
    }

    /// <summary>Reads an <c>NSString</c> into a managed string.</summary>
    public static string? FromNSString(nint value)
    {
        if (value == 0)
        {
            return null;
        }

        var utf8 = (byte*)Send(value, Selectors.Utf8String);
        return Utf8.ToManaged(utf8);
    }

    /// <summary>Creates an <c>NSURL</c> from a file path or a URL string.</summary>
    public static nint NSUrl(string uri)
    {
        var cls = objc_getClass("NSURL");
        var text = NSString(uri);
        return uri.Contains("://", StringComparison.Ordinal)
            ? Send(cls, Selectors.UrlWithString, text)
            : Send(cls, Selectors.FileUrlWithPath, text);
    }

    /// <summary>
    /// Registers a selector, returning 0 when the process is not running on
    /// macOS so that type initialisation never fails on another platform.
    /// </summary>
    public static nint Sel(string name) => OperatingSystem.IsMacOS() ? sel_registerName(name) : 0;

    /// <summary>Number of elements in an <c>NSArray</c>.</summary>
    public static nint ArrayCount(nint array) => Send(array, Selectors.Count);

    /// <summary>Element of an <c>NSArray</c>.</summary>
    public static nint ArrayItem(nint array, nint index) => Send(array, Selectors.ObjectAtIndex, index);

    private static void* LoadExport(string name)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        if (!NativeModule.TryLoad([Runtime, "libobjc.A.dylib"], out var module))
        {
            return null;
        }

        return module.TryGetExport(name, out var address) ? (void*)address : null;
    }
}

/// <summary>Selectors resolved once and reused, since <c>sel_registerName</c> is not free.</summary>
[SupportedOSPlatform("macos")]
public static class Selectors
{
    /// <summary><c>alloc</c></summary>
    public static readonly nint Alloc = ObjC.Sel("alloc");

    /// <summary><c>init</c></summary>
    public static readonly nint Init = ObjC.Sel("init");

    /// <summary><c>release</c></summary>
    public static readonly nint Release = ObjC.Sel("release");

    /// <summary><c>retain</c></summary>
    public static readonly nint Retain = ObjC.Sel("retain");

    /// <summary><c>count</c></summary>
    public static readonly nint Count = ObjC.Sel("count");

    /// <summary><c>objectAtIndex:</c></summary>
    public static readonly nint ObjectAtIndex = ObjC.Sel("objectAtIndex:");

    /// <summary><c>UTF8String</c></summary>
    public static readonly nint Utf8String = ObjC.Sel("UTF8String");

    /// <summary><c>stringWithUTF8String:</c></summary>
    public static readonly nint StringWithUtf8String = ObjC.Sel("stringWithUTF8String:");

    /// <summary><c>URLWithString:</c></summary>
    public static readonly nint UrlWithString = ObjC.Sel("URLWithString:");

    /// <summary><c>fileURLWithPath:</c></summary>
    public static readonly nint FileUrlWithPath = ObjC.Sel("fileURLWithPath:");
}
