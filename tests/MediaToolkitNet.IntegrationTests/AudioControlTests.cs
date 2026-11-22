#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Diagnostics;
using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions.Capture;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Windows;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>A fact that runs only on Windows with an audio output to control.</summary>
public sealed class AudioOutputFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="AudioOutputFactAttribute"/> class.</summary>
    public AudioOutputFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "The audio controls are Windows only.";
            return;
        }

        try
        {
            using var volume = new AudioEndpointVolume();
        }
        catch (Exception ex)
        {
            Skip = $"there is no audio output: {ex.Message}";
        }
    }
}

/// <summary>Volume, mute, meters and sessions of the audio endpoints, and the notifications about devices.</summary>
[SupportedOSPlatform("windows")]
public class AudioControlTests
{
    [AudioOutputFact]
    public void VolumeAndMuteCanBeChangedAndPutBack()
    {
        using var endpoint = new AudioEndpointVolume();
        var volume = endpoint.Volume;
        var muted = endpoint.IsMuted;
        try
        {
            endpoint.ChannelCount.Should().BeGreaterThan(0);
            volume.Should().BeInRange(0f, 1f);

            endpoint.Volume = 0.37f;
            endpoint.Volume.Should().BeApproximately(0.37f, 0.03f, "the endpoint rounds to its own steps");

            endpoint.IsMuted = !muted;
            endpoint.IsMuted.Should().Be(!muted);
        }
        finally
        {
            endpoint.IsMuted = muted;
            endpoint.Volume = volume;
        }

        endpoint.IsMuted.Should().Be(muted);
        endpoint.Volume.Should().BeApproximately(volume, 0.03f);
    }

    [AudioOutputFact]
    public void AVolumeChangeIsReportedWhoeverMakesIt()
    {
        using var endpoint = new AudioEndpointVolume();
        var volume = endpoint.Volume;
        var changes = new List<AudioVolumeChange>();
        endpoint.Changed += (_, change) =>
        {
            lock (changes)
            {
                changes.Add(change);
            }
        };

        var target = volume > 0.5f ? volume - 0.07f : volume + 0.07f;
        try
        {
            endpoint.Volume = target;
            WaitUntil(() => { lock (changes) { return changes.Count > 0; } }).Should().BeTrue("Windows said the volume changed");
        }
        finally
        {
            endpoint.Volume = volume;
        }

        lock (changes)
        {
            changes[0].Volume.Should().BeApproximately(target, 0.03f);
        }
    }

    [AudioOutputFact]
    public void TheMeterAndTheSessionOfThisProcessFollowATone()
    {
        using var endpoint = new AudioEndpointVolume();
        var volume = endpoint.Volume;
        var sessionVolume = -1f;
        var sessionMuted = false;
        try
        {
            using var renderer = new WasapiRenderer(AudioCaptureSettings.Default with { Format = new AudioFormat(48000, 2, SampleFormat.F32) });
            renderer.Start();
            var peak = 0f;
            var sessionPeak = 0f;
            AudioSession? own = null;
            var clock = Stopwatch.StartNew();
            var phase = 0.0;
            var format = renderer.Format;
            var chunk = new byte[format.SampleRate / 20 * format.Channels * 4];
            while (clock.Elapsed < TimeSpan.FromSeconds(1.2))
            {
                for (var n = 0; n < chunk.Length / (format.Channels * 4); n++)
                {
                    var value = (float)(0.3 * Math.Sin(phase));
                    phase += 2 * Math.PI * 18000 / format.SampleRate;
                    for (var c = 0; c < format.Channels; c++)
                    {
                        BitConverter.TryWriteBytes(chunk.AsSpan(((n * format.Channels) + c) * 4, 4), value);
                    }
                }

                renderer.Write(chunk);
                peak = Math.Max(peak, endpoint.PeakLevel);
                if (clock.ElapsedMilliseconds > 300 && own is null)
                {
                    foreach (var session in AudioSession.GetAll())
                    {
                        if (session.ProcessId == Environment.ProcessId)
                        {
                            own = session;
                        }
                        else
                        {
                            session.Dispose();
                        }
                    }
                }

                if (own is not null)
                {
                    sessionPeak = Math.Max(sessionPeak, own.PeakLevel);
                }
            }

            try
            {
                own.Should().NotBeNull("a program that plays has a session");
                own!.State.Should().Be(AudioSessionState.Active);
                own.IsSystemSounds.Should().BeFalse();
                sessionVolume = own.Volume;
                sessionMuted = own.IsMuted;
                own.Volume = 0.5f;
                own.Volume.Should().BeApproximately(0.5f, 0.02f);
                own.IsMuted = true;
                own.IsMuted.Should().BeTrue();
                sessionPeak.Should().BeGreaterThan(0f, "the session's own meter saw the tone");
            }
            finally
            {
                if (own is not null)
                {
                    if (sessionVolume >= 0)
                    {
                        own.Volume = sessionVolume;
                        own.IsMuted = sessionMuted;
                    }

                    own.Dispose();
                }
            }

            if (!endpoint.IsMuted && volume > 0.01f)
            {
                peak.Should().BeGreaterThan(0f, "the endpoint's meter saw the tone");
            }
        }
        finally
        {
            endpoint.Volume = volume;
        }
    }

    [AudioOutputFact]
    public void ListeningForDevicesStartsAndStopsCleanly()
    {
        var notifications = new AudioDeviceNotifications();
        var failures = 0;
        notifications.Failed += (_, _) => failures++;

        notifications.Dispose();
        notifications.Dispose();

        failures.Should().Be(0);
    }

    private static bool WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return condition();
    }
}
