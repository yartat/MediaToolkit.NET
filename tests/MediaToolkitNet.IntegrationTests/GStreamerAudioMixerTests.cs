#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.FFmpeg;
using MediaToolkitNet.GStreamer;
using Xunit;

namespace MediaToolkitNet.IntegrationTests;

/// <summary>
/// The audio mixer on pure tones: each input is one frequency at a known amplitude, so what is in the mix, and how loud, is
/// read from the output by looking for those frequencies in it.
/// </summary>
[SupportedOSPlatform("linux")]
public class GStreamerAudioMixerTests
{
    private const int Rate = 48000;
    private static readonly AudioFormat Stereo = new(Rate, 2, SampleFormat.S16);
    private static readonly AudioFormat Mono = new(Rate, 1, SampleFormat.F32);

    [GStreamerOnlyFact]
    public void TwoInputsAreBothInTheMixAtTheirOwnLevels()
    {
        using var mixer = new GStreamerAudioMixer(Stereo);
        var low = mixer.AddInput(Mono);
        var high = mixer.AddInput(Mono);
        using var mix = new Mix(mixer);
        mixer.Start();

        PushTone(mixer, low, Mono, 440, 0.25, seconds: 1);
        PushTone(mixer, high, Mono, 1000, 0.25, seconds: 1);
        mixer.EndOfStream();
        mix.WaitForSamples(Rate);

        mix.Samples.Should().BeInRange(Rate - 600, Rate + 600, "one second of each, played together and not one after the other");
        mix.Level(440, 0.2, 0.7).Should().BeApproximately(0.25, 0.01);
        mix.Level(1000, 0.2, 0.7).Should().BeApproximately(0.25, 0.01);
        mix.Level(700, 0.2, 0.7).Should().BeLessThan(0.005, "nothing was put at 700 Hz");
        mix.Level(440, 0.2, 0.7, channel: 1).Should().BeApproximately(0.25, 0.01, "a mono input is in both channels");
        mix.Format.Should().Be(Stereo);
    }

    [GStreamerOnlyFact]
    public void VolumeAndMuteApplyToTheirOwnInputOnly()
    {
        using var mixer = new GStreamerAudioMixer(Stereo);
        var full = mixer.AddInput(Mono);
        var half = mixer.AddInput(Mono, volume: 0.5);
        var gone = mixer.AddInput(Mono);
        using var mix = new Mix(mixer);
        mixer.Start();
        mixer.SetMute(gone, true);

        PushTone(mixer, full, Mono, 440, 0.25, seconds: 1);
        PushTone(mixer, half, Mono, 1000, 0.25, seconds: 1);
        PushTone(mixer, gone, Mono, 2000, 0.25, seconds: 1);
        mixer.EndOfStream();
        mix.WaitForSamples(Rate);

        mix.Level(440, 0.2, 0.7).Should().BeApproximately(0.25, 0.01);
        mix.Level(1000, 0.2, 0.7).Should().BeApproximately(0.125, 0.008, "half of its level");
        mix.Level(2000, 0.2, 0.7).Should().BeLessThan(0.005, "it is muted");
    }

    [GStreamerOnlyFact]
    public void AVolumeChangedWhileRunningAppliesToWhatIsPushedAfter()
    {
        using var mixer = new GStreamerAudioMixer(Stereo);
        var input = mixer.AddInput(Mono);
        using var mix = new Mix(mixer);
        mixer.Start();

        PushTone(mixer, input, Mono, 440, 0.4, seconds: 1);
        mix.WaitForSamples(Rate - 4800);
        mixer.SetVolume(input, 0.25);
        PushTone(mixer, input, Mono, 440, 0.4, seconds: 1, from: 1);
        mixer.EndOfStream();
        mix.WaitForSamples((2 * Rate) - 600);

        mix.Level(440, 0.2, 0.7).Should().BeApproximately(0.4, 0.01, "before the change");
        mix.Level(440, 1.4, 1.9).Should().BeApproximately(0.1, 0.008, "a quarter of it after");
    }

    [GStreamerOnlyFact]
    public void AnInputAtAnotherRateKeepsItsPitch()
    {
        var odd = new AudioFormat(22050, 1, SampleFormat.S16);
        using var mixer = new GStreamerAudioMixer(Stereo);
        var input = mixer.AddInput(odd);
        using var mix = new Mix(mixer);
        mixer.Start();

        PushTone(mixer, input, odd, 880, 0.5, seconds: 1);
        mixer.EndOfStream();
        mix.WaitForSamples(Rate - 600);

        mix.Samples.Should().BeInRange(Rate - 600, Rate + 600, "one second in, one second out at 48 kHz");
        mix.Level(880, 0.2, 0.7).Should().BeApproximately(0.5, 0.02);
    }

    [GStreamerOnlyFact]
    public void AnInputThatEndsLeavesTheOthersGoing()
    {
        using var mixer = new GStreamerAudioMixer(Stereo);
        var briefly = mixer.AddInput(Mono);
        var longer = mixer.AddInput(Mono);
        using var mix = new Mix(mixer);
        mixer.Start();

        PushTone(mixer, briefly, Mono, 1000, 0.25, seconds: 0.5);
        mixer.EndOfStream(briefly);
        PushTone(mixer, longer, Mono, 440, 0.25, seconds: 2);
        mixer.EndOfStream(longer);
        mix.WaitForSamples((2 * Rate) - 600);

        mix.Level(1000, 0.1, 0.4).Should().BeApproximately(0.25, 0.01);
        mix.Level(1000, 1.2, 1.7).Should().BeLessThan(0.005, "it has ended");
        mix.Level(440, 1.2, 1.7).Should().BeApproximately(0.25, 0.01, "the other one goes on");
    }

    [GStreamerOnlyFact]
    public void ASumAboveFullScaleClipsAndDoesNotWrapAround()
    {
        var loud = new AudioFormat(Rate, 1, SampleFormat.S16);
        using var mixer = new GStreamerAudioMixer(new AudioFormat(Rate, 1, SampleFormat.S16));
        var first = mixer.AddInput(loud);
        var second = mixer.AddInput(loud);
        using var mix = new Mix(mixer);
        mixer.Start();

        // A constant 20000 on each: the sum, 40000, is past 32767.
        PushConstant(mixer, first, loud, 20000, seconds: 0.5);
        PushConstant(mixer, second, loud, 20000, seconds: 0.5);
        mixer.EndOfStream();
        mix.WaitForSamples(Rate / 2 - 600);

        var values = mix.Channel(0).Skip(1000).Take(Rate / 4).ToList();
        values.Should().NotBeEmpty();
        values.Min().Should().BeGreaterThan(0.99f, "the sum stays at the top instead of coming out negative");
    }

    [GStreamerFact]
    public void TheMixGoesStraightToTheRecorder()
    {
        using var media = new TestMedia();
        var path = media.PathTo("mix.flac");

        using (var recorder = new GStreamerRecorder(path))
        using (var mixer = new GStreamerAudioMixer(Stereo))
        {
            var audio = recorder.AddAudioStream(new AudioEncodingSettings(Stereo, MediaCodec.Flac));
            var one = mixer.AddInput(Mono);
            var two = mixer.AddInput(Mono);
            var written = 0L;
            mixer.FrameMixed += (in AudioFrame frame) =>
            {
                recorder.WriteAudio(audio, in frame);
                Interlocked.Add(ref written, frame.SampleCount);
            };
            recorder.Start();
            mixer.Start();

            PushTone(mixer, one, Mono, 440, 0.25, seconds: 1);
            PushTone(mixer, two, Mono, 1000, 0.25, seconds: 1);
            mixer.EndOfStream();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && Interlocked.Read(ref written) < Rate - 600)
            {
                Thread.Sleep(20);
            }

            recorder.Stop();
        }

        var info = new FFmpegProber().Probe(path);
        var sound = info.OfKind(MediaStreamKind.Audio).Single().Audio!.Value;
        (sound.SampleRate, sound.Channels).Should().Be((Rate, 2));
        info.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(60));
    }

    [GStreamerOnlyFact]
    public void MisuseIsRefusedWithWhatWasWrong()
    {
        using var mixer = new GStreamerAudioMixer(Stereo);
        var input = mixer.AddInput(Mono);

        var early = () => PushConstant(mixer, input, Mono, 0, seconds: 0.1);
        early.Should().Throw<InvalidOperationException>("nothing is pushed before Start");
        mixer.Start();
        var late = () => mixer.AddInput(Mono);
        late.Should().Throw<InvalidOperationException>("the inputs are fixed once it runs");
        var unknown = () => mixer.SetVolume(5, 1);
        unknown.Should().Throw<ArgumentOutOfRangeException>();
        var tooLoud = () => mixer.SetVolume(input, 11);
        tooLoud.Should().Throw<ArgumentOutOfRangeException>();
        var mismatch = () => PushConstant(mixer, input, new AudioFormat(44100, 1, SampleFormat.F32), 0, seconds: 0.1);
        mismatch.Should().Throw<MediaToolkitNetException>().Which.Message.Should().Contain("44100").And.Contain("48000");

        var planar = () => new GStreamerAudioMixer(new AudioFormat(Rate, 2, SampleFormat.F32Planar));
        planar.Should().Throw<ArgumentException>();
        using var empty = new GStreamerAudioMixer(Stereo);
        var start = () => empty.Start();
        start.Should().Throw<InvalidOperationException>("a mixer with no input has nothing to mix");
    }

    [GStreamerOnlyFact]
    public void DisposingWhileSamplesArePushedLetsGo()
    {
        for (var i = 0; i < 10; i++)
        {
            var mixer = new GStreamerAudioMixer(Stereo);
            var input = mixer.AddInput(Mono);
            mixer.FrameMixed += (in AudioFrame _) => { };
            mixer.Start();
            PushTone(mixer, input, Mono, 440, 0.2, seconds: 0.1);
            mixer.Dispose();
        }
    }

    private static void PushTone(GStreamerAudioMixer mixer, int input, AudioFormat format, double frequency, double amplitude, double seconds, double from = 0) =>
        Push(mixer, input, format, seconds, from, n => amplitude * Math.Sin(2 * Math.PI * frequency * n / format.SampleRate));

    private static void PushConstant(GStreamerAudioMixer mixer, int input, AudioFormat format, int value, double seconds) =>
        Push(mixer, input, format, seconds, 0, _ => value / 32768.0);

    // 10 ms blocks of one channel, stamped from `from` seconds on, in the input's sample format.
    private static unsafe void Push(GStreamerAudioMixer mixer, int input, AudioFormat format, double seconds, double from, Func<long, double> wave)
    {
        var block = format.SampleRate / 100;
        var bytes = format.SampleFormat.BytesPerSample();
        var buffer = new byte[block * format.Channels * bytes];
        var first = (long)(from * format.SampleRate);
        var total = (long)(seconds * format.SampleRate);
        fixed (byte* p = buffer)
        {
            for (var done = 0L; done < total; done += block)
            {
                for (var i = 0; i < block; i++)
                {
                    var value = wave(first + done + i);
                    for (var c = 0; c < format.Channels; c++)
                    {
                        var at = ((i * format.Channels) + c) * bytes;
                        if (format.SampleFormat == SampleFormat.F32)
                        {
                            BitConverter.TryWriteBytes(new Span<byte>(buffer, at, 4), (float)value);
                        }
                        else
                        {
                            BitConverter.TryWriteBytes(new Span<byte>(buffer, at, 2), (short)Math.Clamp(value * 32768, short.MinValue, short.MaxValue));
                        }
                    }
                }

                mixer.Push(input, new AudioFrame(format, TimeSpan.FromSeconds((double)(first + done) / format.SampleRate), block, p));
            }
        }
    }

    /// <summary>Keeps what the mixer puts out, so that the level of a frequency in any stretch of it can be read.</summary>
    private sealed class Mix : IDisposable
    {
        private readonly GStreamerAudioMixer _mixer;
        private readonly List<float>[] _channels = [[], []];
        private readonly Lock _gate = new();

        public Mix(GStreamerAudioMixer mixer)
        {
            _mixer = mixer;
            mixer.FrameMixed += OnFrame;
        }

        public AudioFormat Format { get; private set; }

        public int Samples
        {
            get
            {
                lock (_gate)
                {
                    return _channels[0].Count;
                }
            }
        }

        public List<float> Channel(int channel)
        {
            lock (_gate)
            {
                return [.. _channels[channel]];
            }
        }

        public void WaitForSamples(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && Samples < count)
            {
                Thread.Sleep(20);
            }
        }

        /// <summary>The amplitude of a sine at this frequency between two times, 1 being full scale (one bin of a Fourier transform).</summary>
        public double Level(double frequency, double from, double to, int channel = 0)
        {
            var data = Channel(channel);
            var start = (int)(from * Rate);
            var end = (int)(to * Rate);
            data.Count.Should().BeGreaterThanOrEqualTo(end, "the stretch asked about has come out");
            double real = 0, imaginary = 0;
            for (var n = start; n < end; n++)
            {
                var angle = 2 * Math.PI * frequency * n / Rate;
                real += data[n] * Math.Cos(angle);
                imaginary += data[n] * Math.Sin(angle);
            }

            return 2 * Math.Sqrt((real * real) + (imaginary * imaginary)) / (end - start);
        }

        public void Dispose() => _mixer.FrameMixed -= OnFrame;

        private void OnFrame(in AudioFrame frame)
        {
            lock (_gate)
            {
                Format = frame.Format;
                var data = frame.Interleaved;
                if (frame.Format.SampleFormat == SampleFormat.S16)
                {
                    var samples = MemoryMarshal.Cast<byte, short>(data);
                    for (var i = 0; i < frame.SampleCount; i++)
                    {
                        for (var c = 0; c < _channels.Length; c++)
                        {
                            _channels[c].Add(samples[(i * frame.Format.Channels) + Math.Min(c, frame.Format.Channels - 1)] / 32768f);
                        }
                    }
                }
            }
        }
    }
}
