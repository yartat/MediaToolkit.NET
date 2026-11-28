#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>Opening a client for exclusive and for low-latency shared mode, for capture and render alike.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class WasapiStreams
{
    // IID_IAudioClient3 and its slots, counted from audioclient.h: IAudioClient is 3 to 14, IAudioClient2 adds 15 to 17,
    // then GetSharedModeEnginePeriod 18, GetCurrentSharedModeEnginePeriod 19, InitializeSharedAudioStream 20.
    private static readonly Guid AudioClient3 = new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
    private const int Client3GetSharedModeEnginePeriod = 18;
    private const int Client3InitializeSharedAudioStream = 20;

    // AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED.
    private const int BufferSizeNotAligned = unchecked((int)0x88890019);

    // IID_IAudioClock; GetFrequency 3, GetPosition 4.
    private static readonly Guid AudioClock = new("CD63314F-3FBA-4a1b-812C-EF96358728E7");
    private const int ClockGetFrequency = 3;
    private const int ClockGetPosition = 4;

    /// <summary>WAVEFORMATEXTENSIBLE; most drivers take only this form in exclusive mode.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Extensible
    {
        public WaveFormatEx Format;
        public ushort ValidBitsPerSample;
        public uint ChannelMask;
        public Guid SubFormat;
    }

    /// <summary>
    /// Opens the device alone, event driven: the buffer and the period are the same, and each event hands over one whole
    /// buffer. A <paramref name="duration"/> shorter than the device's minimum becomes the minimum. Returns the buffer's frames.
    /// </summary>
    public static uint InitializeExclusive(void* device, ref ComPtr client, AudioFormat format, TimeSpan duration, nint ready)
    {
        MediaToolkitNetException Refused() => new(
            WasapiDeviceEnumerator.BackendName,
            $"the device does not take {format.SampleRate} Hz, {format.Channels} channels, {format.SampleFormat} in exclusive mode",
            Wasapi.UnsupportedFormat);

        if (!TryExclusiveFormat(client.Pointer, format, out var wave))
        {
            throw Refused();
        }

        HResult.ThrowIfFailed(Wasapi.GetDevicePeriod(client.Pointer, out _, out var minimum), "IAudioClient::GetDevicePeriod");
        var period = Math.Max(duration.Ticks, minimum);
        var hr = Wasapi.Initialize(client.Pointer, AudioShareMode.Exclusive, Wasapi.StreamFlagsEventCallback, period, period, &wave.Format);

        if (hr == BufferSizeNotAligned)
        {
            // The documented way: ask what size the driver rounds to, and start over with a new client and that duration.
            HResult.ThrowIfFailed(Wasapi.GetBufferSize(client.Pointer, out var aligned), "IAudioClient::GetBufferSize");
            period = (long)Math.Round(10_000_000d * aligned / format.SampleRate);
            client.Dispose();
            HResult.ThrowIfFailed(Wasapi.Activate(device, WinGuids.IAudioClient, out var again), "IMMDevice::Activate(IAudioClient)");
            client = new ComPtr(again);
            hr = Wasapi.Initialize(client.Pointer, AudioShareMode.Exclusive, Wasapi.StreamFlagsEventCallback, period, period, &wave.Format);
        }

        // A driver may answer yes to IsFormatSupported and still refuse here.
        if (hr == Wasapi.UnsupportedFormat)
        {
            throw Refused();
        }

        HResult.ThrowIfFailed(hr, "IAudioClient::Initialize in exclusive mode (another stream may hold the device, or its settings forbid it)");
        HResult.ThrowIfFailed(Wasapi.SetEventHandle(client.Pointer, ready), "IAudioClient::SetEventHandle");
        HResult.ThrowIfFailed(Wasapi.GetBufferSize(client.Pointer, out var frames), "IAudioClient::GetBufferSize");
        return frames;
    }

    /// <summary>
    /// Opens a shared stream with a period of IAudioClient3's, in the mix format. The period is the one nearest to
    /// <paramref name="duration"/> that the engine offers, and the shortest for zero. Returns the period's frames.
    /// </summary>
    public static uint InitializeLowLatency(void* client, WaveFormatEx* mix, TimeSpan duration, nint ready)
    {
        using var client3 = AsClient3(client);
        var periods = Periods(client3.Pointer, mix);

        var wanted = duration <= TimeSpan.Zero ? periods.Minimum : (uint)Math.Round(duration.TotalSeconds * mix->SamplesPerSec);
        var step = periods.Fundamental;
        var period = Math.Clamp((wanted + (step / 2)) / step * step, periods.Minimum, periods.Maximum);

        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, uint, uint, WaveFormatEx*, Guid*, int>)
                Com.Slot(client3.Pointer, Client3InitializeSharedAudioStream))(
                client3.Pointer, Wasapi.StreamFlagsEventCallback, period, mix, null),
            "IAudioClient3::InitializeSharedAudioStream");
        HResult.ThrowIfFailed(Wasapi.SetEventHandle(client, ready), "IAudioClient::SetEventHandle");
        return period;
    }

    /// <summary>The periods IAudioClient3 offers for <paramref name="mix"/>, in frames.</summary>
    public static AudioEnginePeriods EnginePeriods(void* client, WaveFormatEx* mix)
    {
        using var client3 = AsClient3(client);
        return Periods(client3.Pointer, mix);
    }

    private static ComPtr AsClient3(void* client)
    {
        void* raw;
        var iid = AudioClient3;
        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Com.Slot(client, 0))(client, &iid, &raw),
            "QueryInterface(IAudioClient3), which needs Windows 10");
        return new ComPtr(raw);
    }

    private static AudioEnginePeriods Periods(void* client3, WaveFormatEx* mix)
    {
        uint usual, step, shortest, longest;
        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, WaveFormatEx*, uint*, uint*, uint*, uint*, int>)
                Com.Slot(client3, Client3GetSharedModeEnginePeriod))(client3, mix, &usual, &step, &shortest, &longest),
            "IAudioClient3::GetSharedModeEnginePeriod");
        return new AudioEnginePeriods(usual, step, shortest, longest);
    }

    /// <summary>The IAudioClock of an initialised client.</summary>
    public static ComPtr OpenClock(void* client)
    {
        HResult.ThrowIfFailed(Wasapi.GetService(client, AudioClock, out var clock), "IAudioClient::GetService(IAudioClock)");
        return new ComPtr(clock);
    }

    /// <summary>How many sample frames the device has played since the stream was started or reset.</summary>
    public static ulong PlayedFrames(void* clock, int sampleRate)
    {
        ulong frequency, position;
        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, ulong*, int>)Com.Slot(clock, ClockGetFrequency))(clock, &frequency),
            "IAudioClock::GetFrequency");
        HResult.ThrowIfFailed(
            ((delegate* unmanaged[Stdcall]<void*, ulong*, ulong*, int>)Com.Slot(clock, ClockGetPosition))(clock, &position, null),
            "IAudioClock::GetPosition");
        return (ulong)((double)position * sampleRate / frequency);
    }

    /// <summary>The frames of the engine's usual period, which is what a plain shared stream runs at.</summary>
    public static uint SharedPeriod(void* client, int sampleRate)
    {
        HResult.ThrowIfFailed(Wasapi.GetDevicePeriod(client, out var usual, out _), "IAudioClient::GetDevicePeriod");
        return (uint)Math.Round(usual * (double)sampleRate / 10_000_000d);
    }

    /// <summary>The description of <paramref name="format"/> the device takes in exclusive mode, if it takes it at all.</summary>
    public static bool TryExclusiveFormat(void* client, AudioFormat format, out Extensible wave)
    {
        var plain = WaveFormatEx.From(format);
        wave = new Extensible
        {
            Format = plain,
            ValidBitsPerSample = plain.BitsPerSample,
            ChannelMask = format.Channels switch
            {
                1 => 0x4,
                2 => 0x3,
                _ => (1u << format.Channels) - 1,
            },
            SubFormat = format.SampleFormat.IsFloat() ? WinGuids.AudioFloat : WinGuids.AudioPcm,
        };
        wave.Format.FormatTag = WaveFormatEx.Extensible;
        wave.Format.Size = 22;

        fixed (Extensible* extensible = &wave)
        {
            if (Wasapi.IsFormatSupported(client, AudioShareMode.Exclusive, &extensible->Format, null) == 0)
            {
                return true;
            }
        }

        // The plain form: the extension is then not read, since Size says there is none.
        wave.Format = plain;
        return Wasapi.IsFormatSupported(client, AudioShareMode.Exclusive, &plain, null) == 0;
    }
}
