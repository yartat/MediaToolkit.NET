#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Capture;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Windows;
using MediaToolkitNet.Windows.Native;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>A fact that runs only where the default endpoint of one direction takes some format in exclusive mode.</summary>
public sealed class ExclusiveFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="ExclusiveFactAttribute"/> class.</summary>
    public ExclusiveFactAttribute(bool capture = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "WASAPI is Windows only.";
            return;
        }

        try
        {
            if (WasapiModeTests.ExclusiveFormat(capture ? DataFlow.Capture : DataFlow.Render) is null)
            {
                Skip = "the default endpoint takes none of the formats tried in exclusive mode";
            }
        }
        catch (Exception ex)
        {
            Skip = $"there is no audio endpoint: {ex.Message}";
        }
    }
}

/// <summary>A fact that runs only where the default render endpoint offers a shared period shorter than the default.</summary>
public sealed class LowLatencyFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="LowLatencyFactAttribute"/> class.</summary>
    public LowLatencyFactAttribute()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            Skip = "IAudioClient3 needs Windows 10.";
            return;
        }

        try
        {
            WasapiDeviceEnumerator.GetMixFormat(null, DataFlow.Render);
        }
        catch (Exception ex)
        {
            Skip = $"there is no audio output: {ex.Message}";
        }
    }
}

/// <summary>Tests that take the sound device for themselves run apart from every other test.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AudioEndpointCollection
{
    /// <summary>The name of the collection.</summary>
    public const string Name = "audio endpoint";
}

/// <summary>Exclusive mode and the short periods of IAudioClient3.</summary>
[Collection(AudioEndpointCollection.Name)]
[SupportedOSPlatform("windows")]
public class WasapiModeTests
{
    private const int DeviceInUse = unchecked((int)0x8889000A);

    internal static AudioFormat? ExclusiveFormat(DataFlow flow)
    {
        foreach (var rate in new[] { 48000, 44100, 96000 })
        {
            foreach (var channels in new[] { 2, 1 })
            {
                foreach (var sample in new[] { SampleFormat.S16, SampleFormat.S32, SampleFormat.F32 })
                {
                    var format = new AudioFormat(rate, channels, sample);
                    if (WasapiDeviceEnumerator.SupportsExclusive(null, flow, format))
                    {
                        return format;
                    }
                }
            }
        }

        return null;
    }

    private static AudioCaptureSettings Settings(AudioFormat format) => new(null, format, TimeSpan.Zero);

    [ExclusiveFact]
    public void AnExclusiveRendererHoldsTheDeviceAlone()
    {
        var format = ExclusiveFormat(DataFlow.Render)!.Value;

        using (var exclusive = new WasapiRenderer(Settings(format), WasapiMode.Exclusive))
        {
            exclusive.Mode.Should().Be(WasapiMode.Exclusive);
            exclusive.Format.Should().Be(format, "nothing is converted in exclusive mode");
            exclusive.PeriodFrames.Should().BeGreaterThan(0).And.Be(exclusive.BufferFrames, "the device takes the whole buffer at a time");

            var second = () => new WasapiRenderer(AudioCaptureSettings.Default);
            second.Should().Throw<MediaToolkitNet.Interop.Com.ComException>().Which.HResultCode.Should().Be(DeviceInUse);
        }

        using var afterwards = new WasapiRenderer(AudioCaptureSettings.Default);
    }

    [ExclusiveFact]
    public void AnExclusiveRendererIsPacedByTheDevice()
    {
        var format = ExclusiveFormat(DataFlow.Render)!.Value;
        using var renderer = new WasapiRenderer(Settings(format), WasapiMode.Exclusive);
        var silence = new byte[format.SampleRate * 2 * format.Channels * format.SampleFormat.BytesPerSample()];

        renderer.Start();
        var clock = Stopwatch.StartNew();
        var offset = 0;
        while (offset < silence.Length && clock.Elapsed < TimeSpan.FromSeconds(8))
        {
            // Pieces that do not divide into periods, as a decoder's are.
            offset += renderer.Write(silence.AsSpan(offset, Math.Min(silence.Length - offset, 4 * 1013)));
        }

        renderer.Drain();
        var elapsed = clock.Elapsed;
        renderer.Stop();

        offset.Should().Be(silence.Length);
        elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(2), "Drain returns when the last of it has been played, not before")
            .And.BeLessThan(TimeSpan.FromSeconds(2.5), "two seconds of audio take two seconds to play");
    }

    [ExclusiveFact]
    public void ARendererStopsAndStartsAgainInExclusiveMode()
    {
        var format = ExclusiveFormat(DataFlow.Render)!.Value;
        using var renderer = new WasapiRenderer(Settings(format), WasapiMode.Exclusive);
        var period = new byte[renderer.PeriodFrames * (uint)(format.Channels * format.SampleFormat.BytesPerSample())];

        renderer.Write(period.AsSpan()).Should().Be(period.Length, "the buffer is filled before the stream starts");
        renderer.Write(period.AsSpan()).Should().Be(period.Length, "a second period waits to be submitted");
        renderer.Write(period.AsSpan()).Should().Be(0, "a stopped endpoint takes nothing more");

        for (var round = 0; round < 2; round++)
        {
            renderer.Start();
            for (var n = 0; n < 20; n++)
            {
                renderer.Write(period.AsSpan()).Should().Be(period.Length);
            }

            renderer.Stop();
        }
    }

    [ExclusiveFact]
    public void AFormatTheDeviceDoesNotTakeIsRefusedNotConverted()
    {
        var odd = new AudioFormat(12345, 2, SampleFormat.S16);

        var open = () => new WasapiRenderer(Settings(odd), WasapiMode.Exclusive);

        open.Should().Throw<MediaToolkitNetException>().WithMessage("*12345*exclusive*");
    }

    [ExclusiveFact(capture: true)]
    public void AnExclusiveCaptureDeliversWholePeriods()
    {
        var format = ExclusiveFormat(DataFlow.Capture)!.Value;
        using var capture = new WasapiCapture(Settings(format), mode: WasapiMode.Exclusive);
        var sizes = new List<int>();
        var failures = new List<Exception>();
        capture.Failed += (_, e) => failures.Add(e);
        capture.FrameCaptured += (in AudioFrame frame) =>
        {
            lock (sizes)
            {
                sizes.Add(frame.SampleCount);
            }
        };

        capture.Format.Should().Be(format);
        var second = () => new WasapiCapture(AudioCaptureSettings.Default);
        second.Should().Throw<MediaToolkitNet.Interop.Com.ComException>().Which.HResultCode.Should().Be(DeviceInUse);

        capture.Start();
        Thread.Sleep(1000);
        capture.Stop();

        failures.Should().BeEmpty();
        lock (sizes)
        {
            sizes.Should().OnlyContain(n => n == capture.PeriodFrames);
            sizes.Sum().Should().BeInRange((int)(format.SampleRate * 0.8), (int)(format.SampleRate * 1.2));
        }
    }

    [LowLatencyFact]
    public void ALowLatencyRendererRunsTheShortestPeriodAndIsHeard()
    {
        var mix = WasapiDeviceEnumerator.GetMixFormat(null, DataFlow.Render);
        mix.SampleFormat.Should().Be(SampleFormat.F32, "the tone below is written as floats");
        using var capture = WasapiCapture.ForProcess(Environment.ProcessId, format: new AudioFormat(48000, 2, SampleFormat.F32));
        var left = new List<float>();
        capture.FrameCaptured += (in AudioFrame frame) =>
        {
            var bytes = frame.GetPlane(0);
            lock (left)
            {
                for (var i = 0; i + 8 <= bytes.Length; i += 8)
                {
                    left.Add(BitConverter.ToSingle(bytes.Slice(i, 4)));
                }
            }
        };

        using var renderer = new WasapiRenderer(Settings(mix), WasapiMode.LowLatencyShared);
        renderer.Format.Should().Be(mix);
        // How short that is depends on the driver: this machine's output offers nothing under 10 ms, its input does.
        renderer.PeriodFrames.Should().Be(WasapiDeviceEnumerator.GetEnginePeriods(null, DataFlow.Render).Minimum);

        var tone = new float[mix.SampleRate * mix.Channels];
        for (var n = 0; n < mix.SampleRate; n++)
        {
            for (var c = 0; c < mix.Channels; c++)
            {
                tone[(n * mix.Channels) + c] = (float)(0.05 * Math.Sin(2 * Math.PI * 1000 * n / mix.SampleRate));
            }
        }

        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(tone.AsSpan()).ToArray();
        capture.Start();
        renderer.Start();
        var offset = 0;
        while (offset < bytes.Length)
        {
            offset += renderer.Write(bytes.AsSpan(offset));
        }

        renderer.Drain();
        Thread.Sleep(200);
        capture.Stop();
        renderer.Stop();

        lock (left)
        {
            // A one-bin Fourier transform of half a second out of the middle.
            var samples = left.Skip(14400).Take(24000).ToArray();
            samples.Length.Should().Be(24000);
            double re = 0, im = 0;
            for (var n = 0; n < samples.Length; n++)
            {
                re += samples[n] * Math.Cos(2 * Math.PI * 1000 * n / 48000);
                im += samples[n] * Math.Sin(2 * Math.PI * 1000 * n / 48000);
            }

            var amplitude = 2 * Math.Sqrt((re * re) + (im * im)) / samples.Length;
            amplitude.Should().BeApproximately(0.05, 0.015, "the whole tone arrived, without gaps");
        }
    }

    [LowLatencyFact]
    public void ALowLatencyRendererRefusesAnotherFormat()
    {
        var mix = WasapiDeviceEnumerator.GetMixFormat(null, DataFlow.Render);
        var other = mix with { SampleRate = mix.SampleRate == 48000 ? 44100 : 48000 };

        var open = () => new WasapiRenderer(Settings(other), WasapiMode.LowLatencyShared);

        open.Should().Throw<MediaToolkitNetException>().WithMessage($"*{mix.SampleRate}*");
    }

    [LowLatencyFact]
    public void ALowLatencyCaptureDeliversShortPeriods()
    {
        using var capture = new WasapiCapture(AudioCaptureSettings.Default with { BufferDuration = TimeSpan.Zero }, mode: WasapiMode.LowLatencyShared);
        var sizes = new List<int>();
        var failures = new List<Exception>();
        capture.Failed += (_, e) => failures.Add(e);
        capture.FrameCaptured += (in AudioFrame frame) =>
        {
            lock (sizes)
            {
                sizes.Add(frame.SampleCount);
            }
        };

        var periods = WasapiDeviceEnumerator.GetEnginePeriods(null, DataFlow.Capture);
        capture.PeriodFrames.Should().Be(periods.Minimum);
        capture.Start();
        Thread.Sleep(1000);
        capture.Stop();

        failures.Should().BeEmpty();
        lock (sizes)
        {
            sizes.Sum().Should().BeInRange((int)(capture.Format.SampleRate * 0.8), (int)(capture.Format.SampleRate * 1.2));
            sizes.Order().ElementAt(sizes.Count / 2).Should().Be((int)capture.PeriodFrames);
        }
    }

    [LowLatencyFact]
    public void TheBufferDurationPicksTheNearestPeriodTheEngineOffers()
    {
        var periods = WasapiDeviceEnumerator.GetEnginePeriods(null, DataFlow.Capture);
        var rate = WasapiDeviceEnumerator.GetMixFormat(null, DataFlow.Capture).SampleRate;
        var usual = AudioCaptureSettings.Default with { BufferDuration = TimeSpan.FromSeconds((double)periods.Default / rate) };
        var endless = AudioCaptureSettings.Default with { BufferDuration = TimeSpan.FromSeconds(1) };

        using (var capture = new WasapiCapture(usual, mode: WasapiMode.LowLatencyShared))
        {
            capture.PeriodFrames.Should().Be(periods.Default);
        }

        using (var capture = new WasapiCapture(endless, mode: WasapiMode.LowLatencyShared))
        {
            capture.PeriodFrames.Should().Be(periods.Maximum);
        }
    }

    [ExclusiveFact]
    public void ALongerBufferDurationIsALongerExclusivePeriod()
    {
        var format = ExclusiveFormat(DataFlow.Render)!.Value;
        var settings = new AudioCaptureSettings(null, format, TimeSpan.FromMilliseconds(40));

        using var renderer = new WasapiRenderer(settings, WasapiMode.Exclusive);

        // A driver may round the buffer to its own alignment.
        renderer.PeriodFrames.Should().BeCloseTo((uint)(format.SampleRate / 25), 256);
    }

    [LowLatencyFact]
    public void LoopbackHasNoOtherModes()
    {
        var exclusive = () => new WasapiCapture(AudioCaptureSettings.Default, loopback: true, WasapiMode.Exclusive);
        var lowLatency = () => new WasapiCapture(AudioCaptureSettings.Default, loopback: true, WasapiMode.LowLatencyShared);

        exclusive.Should().Throw<ArgumentException>();
        lowLatency.Should().Throw<ArgumentException>();
    }
}
