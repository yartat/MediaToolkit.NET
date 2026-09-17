using System.Runtime.Versioning;
using MediaToolkitNet.Core.Devices;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>
/// Lists capture devices through the DirectShow system device enumerator.
/// </summary>
/// <remarks>
/// DirectShow predates Media Foundation and still sees devices that ship only a
/// legacy WDM or VFW driver, so it is worth consulting when
/// <see cref="MediaFoundationDeviceEnumerator"/> comes back short. Only
/// enumeration is wrapped; building a filter graph is out of scope, and the
/// device names returned here are exactly what FFmpeg's <c>dshow</c> demuxer
/// expects.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class DirectShowDeviceEnumerator : IMediaDeviceEnumerator
{
    /// <summary>Backend name reported on the devices this enumerator returns.</summary>
    public const string BackendName = "directshow";

    // IEnumMoniker : IUnknown.
    private const int EnumMonikerNext = 3;

    // IMoniker : IPersistStream : IPersist : IUnknown, so BindToStorage is the
    // ninth slot: three from IUnknown, one from IPersist, four from
    // IPersistStream, then BindToObject.
    private const int MonikerBindToStorage = 9;
    private const int MonikerGetDisplayName = 20;

    // IPropertyBag : IUnknown.
    private const int PropertyBagRead = 3;

    // ICreateDevEnum : IUnknown.
    private const int CreateDevEnumCreateClassEnumerator = 3;

    /// <inheritdoc />
    public IReadOnlyList<MediaDevice> Enumerate(MediaDeviceKind kind = MediaDeviceKind.All)
    {
        Ole32.Initialize();

        var result = new List<MediaDevice>();
        if ((kind & MediaDeviceKind.VideoCapture) != 0)
        {
            Collect(WinGuids.VideoInputDeviceCategory, MediaDeviceKind.VideoCapture, result);
        }

        if ((kind & MediaDeviceKind.AudioCapture) != 0)
        {
            Collect(WinGuids.AudioInputDeviceCategory, MediaDeviceKind.AudioCapture, result);
        }

        return result;
    }

    /// <summary>DirectShow marks no default device, so this returns the first one found.</summary>
    public MediaDevice? GetDefault(MediaDeviceKind kind) => Enumerate(kind).FirstOrDefault();

    private static void Collect(Guid category, MediaDeviceKind kind, List<MediaDevice> result)
    {
        using var devEnum = Ole32.CreateInstance(WinGuids.SystemDeviceEnum, WinGuids.ICreateDevEnum);

        void* enumMoniker;
        int hr;
        {
            var localCategory = category;
            hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, uint, int>)
                Com.Slot(devEnum.Pointer, CreateDevEnumCreateClassEnumerator))(
                devEnum.Pointer, &localCategory, &enumMoniker, 0);
        }

        // S_FALSE means the category exists but holds no devices.
        if (HResult.Failed(hr) || hr == HResult.False || enumMoniker is null)
        {
            return;
        }

        try
        {
            while (true)
            {
                void* moniker;
                uint fetched;
                var next = ((delegate* unmanaged[Stdcall]<void*, uint, void**, uint*, int>)
                    Com.Slot(enumMoniker, EnumMonikerNext))(enumMoniker, 1, &moniker, &fetched);

                if (next != HResult.Ok || fetched == 0)
                {
                    break;
                }

                try
                {
                    var name = ReadStringProperty(moniker, "FriendlyName");
                    var path = ReadStringProperty(moniker, "DevicePath") ?? ReadDisplayName(moniker);
                    if (name is null)
                    {
                        continue;
                    }

                    result.Add(new MediaDevice(path ?? name, name, kind, BackendName));
                }
                finally
                {
                    Com.Release(moniker);
                }
            }
        }
        finally
        {
            Com.Release(enumMoniker);
        }
    }

    private static string? ReadStringProperty(void* moniker, string property)
    {
        void* bag;
        var iid = WinGuids.IPropertyBag;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void*, void*, Guid*, void**, int>)
            Com.Slot(moniker, MonikerBindToStorage))(moniker, null, null, &iid, &bag);

        if (HResult.Failed(hr))
        {
            return null;
        }

        try
        {
            // VARIANT is 24 bytes on 64-bit; VT_BSTR keeps its pointer at offset 8.
            var variant = stackalloc byte[24];
            new Span<byte>(variant, 24).Clear();

            fixed (char* name = property)
            {
                hr = ((delegate* unmanaged[Stdcall]<void*, char*, void*, void*, int>)
                    Com.Slot(bag, PropertyBagRead))(bag, name, variant, null);
            }

            if (HResult.Failed(hr))
            {
                return null;
            }

            const ushort vtBstr = 8;
            if (*(ushort*)variant != vtBstr)
            {
                return null;
            }

            var text = *(char**)(variant + 8);
            try
            {
                return text is null ? null : new string(text);
            }
            finally
            {
                if (text is not null)
                {
                    SysFreeString(text);
                }
            }
        }
        finally
        {
            Com.Release(bag);
        }
    }

    private static string? ReadDisplayName(void* moniker)
    {
        char* name;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void*, void*, char**, int>)
            Com.Slot(moniker, MonikerGetDisplayName))(moniker, null, null, &name);

        if (HResult.Failed(hr) || name is null)
        {
            return null;
        }

        try
        {
            return new string(name);
        }
        finally
        {
            Ole32.CoTaskMemFree(name);
        }
    }

    [System.Runtime.InteropServices.LibraryImport("oleaut32.dll")]
    private static partial void SysFreeString(char* bstr);
}
