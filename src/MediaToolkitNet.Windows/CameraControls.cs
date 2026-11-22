#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>A setting of a camera. Which of them a camera has depends on the camera and its driver.</summary>
public enum CameraProperty
{
    /// <summary>Horizontal rotation of the lens.</summary>
    Pan,

    /// <summary>Vertical rotation of the lens.</summary>
    Tilt,

    /// <summary>Rotation around the lens's axis.</summary>
    Roll,

    /// <summary>Zoom.</summary>
    Zoom,

    /// <summary>Exposure time, in log base 2 seconds on most cameras.</summary>
    Exposure,

    /// <summary>Iris opening.</summary>
    Iris,

    /// <summary>Focus distance.</summary>
    Focus,

    /// <summary>Brightness of the picture.</summary>
    Brightness,

    /// <summary>Contrast of the picture.</summary>
    Contrast,

    /// <summary>Hue of the picture.</summary>
    Hue,

    /// <summary>Colour saturation.</summary>
    Saturation,

    /// <summary>Sharpness.</summary>
    Sharpness,

    /// <summary>Gamma.</summary>
    Gamma,

    /// <summary>Colour on or off.</summary>
    ColorEnable,

    /// <summary>White balance, in kelvin on most cameras.</summary>
    WhiteBalance,

    /// <summary>Backlight compensation.</summary>
    BacklightCompensation,

    /// <summary>Gain.</summary>
    Gain,
}

/// <summary>What a camera setting can be set to.</summary>
/// <param name="Minimum">The lowest value.</param>
/// <param name="Maximum">The highest value.</param>
/// <param name="Step">The distance between values.</param>
/// <param name="Default">The camera's own choice.</param>
/// <param name="CanBeAutomatic">Whether the camera can set it by itself.</param>
/// <param name="CanBeManual">Whether it can be set by hand.</param>
public readonly record struct CameraPropertyRange(int Minimum, int Maximum, int Step, int Default, bool CanBeAutomatic, bool CanBeManual);

/// <summary>A camera setting as it is now.</summary>
/// <param name="Value">The value.</param>
/// <param name="IsAutomatic">Whether the camera is setting it by itself.</param>
public readonly record struct CameraPropertyValue(int Value, bool IsAutomatic);

/// <summary>
/// The controls of a camera that Windows exposes through DirectShow's <c>IAMCameraControl</c> (pan, tilt, zoom,
/// exposure, focus, iris) and <c>IAMVideoProcAmp</c> (brightness, contrast, white balance and the like), reached from
/// the Media Foundation capture source. A camera has some of them; <see cref="Supported"/> says which.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class CameraControls
{
    // KSPROPERTY_CAMERACONTROL_* and KSPROPERTY_VIDEOPROCAMP_* values, as the interfaces number them.
    private const int FlagsAuto = 1;
    private const int FlagsManual = 2;

    private readonly void* _source;

    internal CameraControls(void* source) => _source = source;

    /// <summary>The settings this camera has.</summary>
    public IReadOnlyList<CameraProperty> Supported
    {
        get
        {
            var result = new List<CameraProperty>();
            foreach (var property in Enum.GetValues<CameraProperty>())
            {
                if (GetRange(property) is not null)
                {
                    result.Add(property);
                }
            }

            return result;
        }
    }

    /// <summary>What a setting can be set to, or <see langword="null"/> when the camera does not have it.</summary>
    public CameraPropertyRange? GetRange(CameraProperty property)
    {
        using var control = Open(property, out var id);
        if (control.IsNull)
        {
            return null;
        }

        int min, max, step, def, caps;
        var hr = ((delegate* unmanaged[Stdcall]<void*, int, int*, int*, int*, int*, int*, int>)Com.Slot(control.Pointer, AudioSlots.CameraGetRange))(
            control.Pointer, id, &min, &max, &step, &def, &caps);
        return HResult.Failed(hr)
            ? null
            : new CameraPropertyRange(min, max, step, def, (caps & FlagsAuto) != 0, (caps & FlagsManual) != 0);
    }

    /// <summary>The setting as it is now, or <see langword="null"/> when the camera does not have it.</summary>
    public CameraPropertyValue? Get(CameraProperty property)
    {
        using var control = Open(property, out var id);
        if (control.IsNull)
        {
            return null;
        }

        int value, flags;
        var hr = ((delegate* unmanaged[Stdcall]<void*, int, int*, int*, int>)Com.Slot(control.Pointer, AudioSlots.CameraGet))(
            control.Pointer, id, &value, &flags);
        return HResult.Failed(hr) ? null : new CameraPropertyValue(value, (flags & FlagsAuto) != 0);
    }

    /// <summary>Sets a setting by hand.</summary>
    /// <exception cref="NotSupportedException">The camera does not have the setting.</exception>
    public void Set(CameraProperty property, int value) => Set(property, value, automatic: false);

    /// <summary>Hands a setting to the camera to adjust by itself, or takes it back by hand.</summary>
    /// <param name="property">The setting.</param>
    /// <param name="value">The value to set; ignored by the camera while it is automatic.</param>
    /// <param name="automatic">Whether the camera sets it by itself.</param>
    /// <exception cref="NotSupportedException">The camera does not have the setting.</exception>
    public void Set(CameraProperty property, int value, bool automatic)
    {
        // The interface can be there while the camera lacks this one setting (the call then fails with "not found").
        if (GetRange(property) is null)
        {
            throw new NotSupportedException($"this camera has no {property} setting");
        }

        using var control = Open(property, out var id);

        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, int, int, int, int>)Com.Slot(control.Pointer, AudioSlots.CameraSet))(
                control.Pointer, id, value, automatic ? FlagsAuto : FlagsManual),
            $"set {property}");
    }

    // The control interface that owns the property, queried from the source, or null when the source lacks it.
    private ComPtr Open(CameraProperty property, out int id)
    {
        var (iid, number) = property switch
        {
            CameraProperty.Pan => (AudioIids.CameraControl, 0),
            CameraProperty.Tilt => (AudioIids.CameraControl, 1),
            CameraProperty.Roll => (AudioIids.CameraControl, 2),
            CameraProperty.Zoom => (AudioIids.CameraControl, 3),
            CameraProperty.Exposure => (AudioIids.CameraControl, 4),
            CameraProperty.Iris => (AudioIids.CameraControl, 5),
            CameraProperty.Focus => (AudioIids.CameraControl, 6),
            CameraProperty.Brightness => (AudioIids.VideoProcAmp, 0),
            CameraProperty.Contrast => (AudioIids.VideoProcAmp, 1),
            CameraProperty.Hue => (AudioIids.VideoProcAmp, 2),
            CameraProperty.Saturation => (AudioIids.VideoProcAmp, 3),
            CameraProperty.Sharpness => (AudioIids.VideoProcAmp, 4),
            CameraProperty.Gamma => (AudioIids.VideoProcAmp, 5),
            CameraProperty.ColorEnable => (AudioIids.VideoProcAmp, 6),
            CameraProperty.WhiteBalance => (AudioIids.VideoProcAmp, 7),
            CameraProperty.BacklightCompensation => (AudioIids.VideoProcAmp, 8),
            CameraProperty.Gain => (AudioIids.VideoProcAmp, 9),
            _ => throw new ArgumentOutOfRangeException(nameof(property)),
        };

        id = number;
        return HResult.Succeeded(Com.QueryInterface(_source, iid, out var control)) ? new ComPtr(control) : default;
    }
}
