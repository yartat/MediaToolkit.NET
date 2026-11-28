#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

namespace MediaToolkitNet.Windows;

/// <summary>How a WASAPI stream shares its endpoint.</summary>
public enum WasapiMode
{
    /// <summary>Through the Windows audio engine, at the engine's own period (usually 10 ms).</summary>
    Shared,

    /// <summary>
    /// Through the audio engine with the short period of <c>IAudioClient3</c> (Windows 10 and newer). Only the device's mix
    /// format can be used.
    /// </summary>
    LowLatencyShared,

    /// <summary>
    /// The device alone: no other application can play to it or record from it while the stream exists, nothing is mixed
    /// or converted, and the endpoint volume of the Windows mixer sessions does not apply.
    /// </summary>
    Exclusive,
}

/// <summary>The shared-mode periods an endpoint offers, in sample frames of its mix format.</summary>
/// <param name="Default">The period of a plain shared stream.</param>
/// <param name="Fundamental">Every period is a multiple of this.</param>
/// <param name="Minimum">The shortest period; equal to <paramref name="Default"/> when the driver offers nothing shorter.</param>
/// <param name="Maximum">The longest period.</param>
public readonly record struct AudioEnginePeriods(uint Default, uint Fundamental, uint Minimum, uint Maximum);
