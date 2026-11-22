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
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>A fact that runs only on a Windows that can capture one process and has somewhere to play to.</summary>
public sealed class ProcessLoopbackFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="ProcessLoopbackFactAttribute"/> class.</summary>
    public ProcessLoopbackFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Process loopback is Windows only.";
            return;
        }

        if (!WasapiCapture.SupportsProcessLoopback)
        {
            Skip = "Process loopback needs Windows 10 version 2004 or newer.";
            return;
        }

        try
        {
            using var renderer = new WasapiRenderer(AudioCaptureSettings.Default);
        }
        catch (Exception ex)
        {
            Skip = $"there is no audio output to play a tone to: {ex.Message}";
        }
    }
}

/// <summary>Capturing the sound of one process (or of everything but it), checked with a tone this process plays.</summary>
[SupportedOSPlatform("windows")]
public class ProcessLoopbackTests
{
    private const double ToneHz = 18000;

    [ProcessLoopbackFact]
    public void TheSoundOfAProcessIsCapturedAndOthersAreNot()
    {
        var own = Environment.ProcessId;

        var included = CaptureWhileToneIsPlayed(own, ProcessLoopbackMode.IncludeProcessTree);
        var excluded = CaptureWhileToneIsPlayed(own, ProcessLoopbackMode.ExcludeProcessTree);

        included.Samples.Should().BeGreaterThan(48000, "a second and a half of audio was captured");
        included.ToneToReference.Should().BeGreaterThan(20, "the tone this process played is in what was captured of it");
        excluded.ToneToReference.Should().BeLessThan(3, "the same tone is not in what was captured of everything else");
    }

    [ProcessLoopbackFact]
    public void TheFormatAskedForIsTheFormatDelivered()
    {
        using var capture = WasapiCapture.ForProcess(Environment.ProcessId, format: new AudioFormat(44100, 1, SampleFormat.S16));

        capture.Format.Should().Be(new AudioFormat(44100, 1, SampleFormat.S16));
        capture.Backend.Should().NotBeNullOrEmpty();
        capture.Loopback.Should().BeFalse("it is not a device loopback");
    }

    [ProcessLoopbackFact]
    public void AProcessThatDoesNotExistIsAcceptedAndHearsNothing()
    {
        // Windows makes the client whatever the process id is; there is simply nothing playing from it.
        var heard = CaptureWhileToneIsPlayed(0x7FFFFF00, ProcessLoopbackMode.IncludeProcessTree);

        heard.ToneToReference.Should().BeLessThan(3);
    }

    private static (int Samples, double ToneToReference) CaptureWhileToneIsPlayed(int processId, ProcessLoopbackMode mode)
    {
        using var capture = WasapiCapture.ForProcess(processId, mode, new AudioFormat(48000, 2, SampleFormat.F32));
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

        using var renderer = new WasapiRenderer(AudioCaptureSettings.Default with { Format = new AudioFormat(48000, 2, SampleFormat.F32) });
        capture.Start();
        renderer.Start();
        PlayTone(renderer, seconds: 1.5);
        renderer.Drain();
        Thread.Sleep(200);
        capture.Stop();
        renderer.Stop();

        float[] samples;
        lock (left)
        {
            samples = [.. left];
        }

        // Skip the first 200 ms, while the streams start up.
        var settled = samples.Skip(9600).ToArray();
        var rate = capture.Format.SampleRate;
        var tone = Goertzel(settled, rate, ToneHz);
        var reference = Math.Max(Goertzel(settled, rate, 9000), 1e-6);
        return (samples.Length, tone / reference);
    }

    private static void PlayTone(WasapiRenderer renderer, double seconds)
    {
        var format = renderer.Format;
        var frames = (int)(format.SampleRate * seconds);
        var bytesPerSample = format.SampleFormat.BytesPerSample();
        var buffer = new byte[frames * format.Channels * bytesPerSample];
        for (var n = 0; n < frames; n++)
        {
            var value = 0.2 * Math.Sin(2 * Math.PI * ToneHz * n / format.SampleRate);
            for (var c = 0; c < format.Channels; c++)
            {
                var at = ((n * format.Channels) + c) * bytesPerSample;
                if (format.SampleFormat == SampleFormat.F32)
                {
                    BitConverter.TryWriteBytes(buffer.AsSpan(at, 4), (float)value);
                }
                else if (format.SampleFormat == SampleFormat.S16)
                {
                    BitConverter.TryWriteBytes(buffer.AsSpan(at, 2), (short)(value * short.MaxValue));
                }
                else
                {
                    throw new NotSupportedException($"the test plays {format.SampleFormat} nowhere");
                }
            }
        }

        var offset = 0;
        var clock = Stopwatch.StartNew();
        while (offset < buffer.Length && clock.Elapsed < TimeSpan.FromSeconds(seconds + 5))
        {
            offset += renderer.Write(buffer.AsSpan(offset));
        }
    }

    // The strength of one frequency in a block of samples.
    private static double Goertzel(float[] samples, int rate, double frequency)
    {
        var k = 2 * Math.Cos(2 * Math.PI * frequency / rate);
        double s1 = 0, s2 = 0;
        foreach (var x in samples)
        {
            var s0 = x + (k * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        return Math.Sqrt((s1 * s1) + (s2 * s2) - (k * s1 * s2)) / Math.Max(samples.Length, 1);
    }
}
