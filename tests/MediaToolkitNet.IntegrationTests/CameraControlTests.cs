#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions.Capture;
using MediaToolkitNet.Windows;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>A fact that runs only on Windows with a camera that opens.</summary>
public sealed class CameraFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="CameraFactAttribute"/> class.</summary>
    public CameraFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "The camera controls are Windows only.";
            return;
        }

        try
        {
            using var camera = new MediaFoundationVideoCapture(VideoCaptureSettings.Default);
        }
        catch (Exception ex)
        {
            Skip = $"there is no camera that opens: {ex.Message}";
        }
    }
}

/// <summary>The controls of a camera, on the camera this machine has; every value is put back as it was.</summary>
[SupportedOSPlatform("windows")]
public class CameraControlTests
{
    [CameraFact]
    public void TheCameraSaysWhichSettingsItHasAndTheirRanges()
    {
        using var camera = new MediaFoundationVideoCapture(VideoCaptureSettings.Default);

        var supported = camera.Controls.Supported;

        foreach (var property in supported)
        {
            var range = camera.Controls.GetRange(property)!.Value;
            range.Minimum.Should().BeLessThanOrEqualTo(range.Maximum, property.ToString());
            range.Default.Should().BeInRange(range.Minimum, range.Maximum, property.ToString());
            camera.Controls.Get(property).Should().NotBeNull(property.ToString());
        }

        // A camera has none of the settings of a different kind of camera.
        foreach (var property in Enum.GetValues<CameraProperty>().Except(supported))
        {
            camera.Controls.Get(property).Should().BeNull();
            var set = () => camera.Controls.Set(property, 0);
            set.Should().Throw<NotSupportedException>();
        }
    }

    [CameraFact]
    public void ASettingCanBeChangedAndPutBack()
    {
        using var camera = new MediaFoundationVideoCapture(VideoCaptureSettings.Default);
        var controls = camera.Controls;
        var tried = 0;

        foreach (var property in controls.Supported)
        {
            var range = controls.GetRange(property)!.Value;
            var before = controls.Get(property)!.Value;
            if (!range.CanBeManual || range.Step <= 0 || range.Maximum - range.Minimum < range.Step)
            {
                continue;
            }

            // Another value of the setting, a step or more from where it is.
            var target = before.Value + range.Step <= range.Maximum ? before.Value + range.Step : before.Value - range.Step;
            try
            {
                controls.Set(property, target);
                var after = controls.Get(property)!.Value;
                after.Value.Should().Be(target, property.ToString());
                after.IsAutomatic.Should().BeFalse(property.ToString());
                tried++;
            }
            finally
            {
                controls.Set(property, before.Value, before.IsAutomatic);
            }

            var restored = controls.Get(property)!.Value;
            restored.IsAutomatic.Should().Be(before.IsAutomatic, property.ToString());
            if (!before.IsAutomatic)
            {
                restored.Value.Should().Be(before.Value, property.ToString());
            }
        }

        tried.Should().BeGreaterThan(0, "a camera has at least one setting that can be changed by hand: " + string.Join(", ", controls.Supported));
    }
}
