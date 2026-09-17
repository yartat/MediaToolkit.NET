using System.Diagnostics;
using System.Runtime.Versioning;
using MediaToolkitNet.Core;
using MediaToolkitNet.Core.Formats;
using MediaToolkitNet.Core.Frames;
using MediaToolkitNet.Core.Recording;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>
/// Encodes and muxes through <c>IMFSinkWriter</c>, which picks up whatever
/// hardware encoders the machine has.
/// </summary>
/// <remarks>
/// <para>
/// The sink writer chooses the container from the file extension, so <c>.mp4</c>
/// gives an MP4 and <c>.wmv</c> an ASF. Media Foundation converts between the
/// input and output media types on its own, which is why the caller only has to
/// describe the frames it pushes in.
/// </para>
/// <para>
/// Video encoding needs a video encoder MFT to be registered on the machine.
/// Editions without one, such as Windows N without the Media Feature Pack,
/// report that as MF_E_INVALIDMEDIATYPE; see <see cref="HasVideoEncoder"/>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class MediaFoundationRecorder : IMediaRecorder
{
    private readonly List<StreamEntry> _streams = [];
    private readonly Stopwatch _elapsed = new();

    private ComPtr _writer;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates a recorder writing to <paramref name="outputPath"/>.</summary>
    public MediaFoundationRecorder(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        MfApi.Startup();

        void* attributes;
        HResult.ThrowIfFailed(MfApi.MFCreateAttributes(&attributes, 2), "MFCreateAttributes");
        try
        {
            Mf.SetUint32(attributes, WinGuids.EnableHardwareTransforms, 1);

            void* writer;
            HResult.ThrowIfFailed(
                MfApi.MFCreateSinkWriterFromURL(outputPath, null, attributes, &writer), "MFCreateSinkWriterFromURL");
            _writer = new ComPtr(writer);
        }
        finally
        {
            Com.Release(attributes);
        }
    }

    /// <inheritdoc />
    public string Backend => MediaFoundationDeviceEnumerator.BackendName;

    /// <inheritdoc />
    public bool IsRecording => _started;

    /// <inheritdoc />
    public TimeSpan Elapsed => _elapsed.Elapsed;

    /// <inheritdoc />
    public int AddVideoStream(VideoEncodingSettings settings)
    {
        EnsureNotStarted();

        var subtype = MediaFoundationFormatMap.ToEncoderSubtype(settings.Codec);
        if (subtype == Guid.Empty)
        {
            throw new NotSupportedException($"Media Foundation cannot encode {settings.Codec}.");
        }

        var frameRate = settings.Format.FrameRate.Denominator > 0 ? settings.Format.FrameRate : new Rational(30, 1);
        var bitrate = settings.BitrateBitsPerSecond > 0
            ? settings.BitrateBitsPerSecond
            : EstimateBitrate(settings.Format.Width, settings.Format.Height, frameRate);

        uint streamIndex;
        void* output;
        HResult.ThrowIfFailed(MfApi.MFCreateMediaType(&output), "MFCreateMediaType");
        try
        {
            HResult.ThrowIfFailed(Mf.SetGuid(output, WinGuids.MajorType, WinGuids.MediaTypeVideo), "SetGUID(MAJOR_TYPE)");
            HResult.ThrowIfFailed(Mf.SetGuid(output, WinGuids.Subtype, subtype), "SetGUID(SUBTYPE)");
            Mf.SetUint32(output, WinGuids.AverageBitrate, (uint)bitrate);
            Mf.SetUint32(output, WinGuids.InterlaceMode, MfApi.InterlaceProgressive);
            Mf.SetUint64(output, WinGuids.FrameSize, Mf.Pack((uint)settings.Format.Width, (uint)settings.Format.Height));
            Mf.SetUint64(output, WinGuids.FrameRate, Mf.Pack((uint)frameRate.Numerator, (uint)frameRate.Denominator));
            Mf.SetUint64(output, WinGuids.PixelAspectRatio, Mf.Pack(1, 1));

            HResult.ThrowIfFailed(Mf.AddStream(_writer.Pointer, output, out streamIndex), "IMFSinkWriter::AddStream");
        }
        finally
        {
            Com.Release(output);
        }

        var inputSubtype = MediaFoundationFormatMap.ToSubtype(settings.Format.PixelFormat);
        if (inputSubtype == Guid.Empty)
        {
            throw new NotSupportedException($"Pixel format {settings.Format.PixelFormat} is not supported.");
        }

        void* input;
        HResult.ThrowIfFailed(MfApi.MFCreateMediaType(&input), "MFCreateMediaType");
        try
        {
            HResult.ThrowIfFailed(Mf.SetGuid(input, WinGuids.MajorType, WinGuids.MediaTypeVideo), "SetGUID(MAJOR_TYPE)");
            HResult.ThrowIfFailed(Mf.SetGuid(input, WinGuids.Subtype, inputSubtype), "SetGUID(SUBTYPE)");
            Mf.SetUint32(input, WinGuids.InterlaceMode, MfApi.InterlaceProgressive);
            Mf.SetUint64(input, WinGuids.FrameSize, Mf.Pack((uint)settings.Format.Width, (uint)settings.Format.Height));
            Mf.SetUint64(input, WinGuids.FrameRate, Mf.Pack((uint)frameRate.Numerator, (uint)frameRate.Denominator));
            Mf.SetUint64(input, WinGuids.PixelAspectRatio, Mf.Pack(1, 1));

            var hr = Mf.SetInputMediaType(_writer.Pointer, streamIndex, input, null);
            if (hr == InvalidMediaType && !HasVideoEncoder())
            {
                throw new MediaBackendUnavailableException(
                    Backend,
                    "no Media Foundation video encoder is registered on this system " +
                    "(MFTEnumEx returned an empty list). This is usually Windows N without the Media Feature Pack; " +
                    "use the FFmpeg backend to record video.");
            }

            HResult.ThrowIfFailed(hr, "IMFSinkWriter::SetInputMediaType");
        }
        finally
        {
            Com.Release(input);
        }

        var frameDuration = TimeSpan.FromSeconds(frameRate.Inverse.Value);
        _streams.Add(new StreamEntry(streamIndex, isVideo: true)
        {
            VideoFormat = settings.Format with { FrameRate = frameRate },
            FrameDuration = frameDuration,
        });

        return _streams.Count - 1;
    }

    /// <inheritdoc />
    public int AddAudioStream(AudioEncodingSettings settings)
    {
        EnsureNotStarted();

        var subtype = MediaFoundationFormatMap.ToEncoderSubtype(settings.Codec);
        if (subtype == Guid.Empty)
        {
            throw new NotSupportedException($"Media Foundation cannot encode {settings.Codec}.");
        }

        // The AAC encoder only accepts 16-bit PCM input.
        var bitsPerSample = (uint)(settings.Format.SampleFormat.BytesPerSample() * 8);
        var bitrate = settings.BitrateBitsPerSecond > 0 ? settings.BitrateBitsPerSecond : 128_000;

        uint streamIndex;
        void* output;
        HResult.ThrowIfFailed(MfApi.MFCreateMediaType(&output), "MFCreateMediaType");
        try
        {
            HResult.ThrowIfFailed(Mf.SetGuid(output, WinGuids.MajorType, WinGuids.MediaTypeAudio), "SetGUID(MAJOR_TYPE)");
            HResult.ThrowIfFailed(Mf.SetGuid(output, WinGuids.Subtype, subtype), "SetGUID(SUBTYPE)");
            Mf.SetUint32(output, WinGuids.AudioBitsPerSample, 16);
            Mf.SetUint32(output, WinGuids.AudioSamplesPerSecond, (uint)settings.Format.SampleRate);
            Mf.SetUint32(output, WinGuids.AudioChannels, (uint)settings.Format.Channels);
            Mf.SetUint32(output, WinGuids.AudioAverageBytesPerSecond, (uint)(bitrate / 8));

            HResult.ThrowIfFailed(Mf.AddStream(_writer.Pointer, output, out streamIndex), "IMFSinkWriter::AddStream");
        }
        finally
        {
            Com.Release(output);
        }

        void* input;
        HResult.ThrowIfFailed(MfApi.MFCreateMediaType(&input), "MFCreateMediaType");
        try
        {
            HResult.ThrowIfFailed(Mf.SetGuid(input, WinGuids.MajorType, WinGuids.MediaTypeAudio), "SetGUID(MAJOR_TYPE)");
            HResult.ThrowIfFailed(
                Mf.SetGuid(input, WinGuids.Subtype, MediaFoundationFormatMap.ToAudioSubtype(settings.Format.SampleFormat)),
                "SetGUID(SUBTYPE)");
            Mf.SetUint32(input, WinGuids.AudioBitsPerSample, bitsPerSample);
            Mf.SetUint32(input, WinGuids.AudioSamplesPerSecond, (uint)settings.Format.SampleRate);
            Mf.SetUint32(input, WinGuids.AudioChannels, (uint)settings.Format.Channels);
            Mf.SetUint32(input, WinGuids.AudioBlockAlignment, (uint)settings.Format.BlockAlign);
            Mf.SetUint32(input, WinGuids.AudioAverageBytesPerSecond, (uint)settings.Format.AverageBytesPerSecond);

            HResult.ThrowIfFailed(
                Mf.SetInputMediaType(_writer.Pointer, streamIndex, input, null), "IMFSinkWriter::SetInputMediaType");
        }
        finally
        {
            Com.Release(input);
        }

        _streams.Add(new StreamEntry(streamIndex, isVideo: false) { AudioFormat = settings.Format });
        return _streams.Count - 1;
    }

    /// <inheritdoc />
    public void Start()
    {
        EnsureNotStarted();
        if (_streams.Count == 0)
        {
            throw new InvalidOperationException("Add at least one stream before starting.");
        }

        HResult.ThrowIfFailed(Mf.BeginWriting(_writer.Pointer), "IMFSinkWriter::BeginWriting");
        _started = true;
        _elapsed.Restart();
    }

    /// <inheritdoc />
    public void WriteVideo(int streamIndex, in VideoFrame frame)
    {
        var entry = Require(streamIndex, video: true);
        var format = entry.VideoFormat;

        var capacity = format.PixelFormat.FrameSize(format.Width, format.Height);
        if (capacity <= 0)
        {
            throw new NotSupportedException($"The frame size for {format.PixelFormat} cannot be derived.");
        }

        var buffer = CreateBuffer(capacity, out var data, out var maxLength);
        try
        {
            var destination = new Span<byte>(data, (int)maxLength);
            var written = 0;
            for (var plane = 0; plane < frame.PlaneCount; plane++)
            {
                written += frame.CopyPlaneTo(plane, destination[written..]);
            }

            FinishSample(entry, buffer, written, entry.FrameDuration);
        }
        finally
        {
            Com.Release(buffer);
        }
    }

    /// <inheritdoc />
    public void WriteAudio(int streamIndex, in AudioFrame frame)
    {
        var entry = Require(streamIndex, video: false);
        if (frame.Format.SampleFormat.IsPlanar())
        {
            throw new NotSupportedException("Media Foundation only accepts interleaved audio.");
        }

        var payload = frame.Interleaved;
        var buffer = CreateBuffer(payload.Length, out var data, out var maxLength);
        try
        {
            payload.CopyTo(new Span<byte>(data, (int)maxLength));
            FinishSample(entry, buffer, payload.Length, frame.Duration);
        }
        finally
        {
            Com.Release(buffer);
        }
    }

    /// <summary>
    /// Allocates a Media Foundation buffer and leaves it locked so the caller can
    /// fill it. The lock is released by <see cref="FinishSample"/>.
    /// </summary>
    private static void* CreateBuffer(int capacity, out byte* data, out uint maxLength)
    {
        void* buffer;
        HResult.ThrowIfFailed(MfApi.MFCreateMemoryBuffer((uint)capacity, &buffer), "MFCreateMemoryBuffer");

        var hr = Mf.LockBuffer(buffer, out data, out maxLength, out _);
        if (HResult.Failed(hr))
        {
            Com.Release(buffer);
            HResult.ThrowIfFailed(hr, "IMFMediaBuffer::Lock");
        }

        return buffer;
    }

    /// <summary>Unlocks the buffer, wraps it in a sample and hands it to the sink writer.</summary>
    private void FinishSample(StreamEntry entry, void* buffer, int written, TimeSpan duration)
    {
        Mf.UnlockBuffer(buffer);
        HResult.ThrowIfFailed(Mf.SetCurrentLength(buffer, (uint)written), "IMFMediaBuffer::SetCurrentLength");

        void* sample;
        HResult.ThrowIfFailed(MfApi.MFCreateSample(&sample), "MFCreateSample");
        try
        {
            HResult.ThrowIfFailed(Mf.AddBuffer(sample, buffer), "IMFSample::AddBuffer");
            HResult.ThrowIfFailed(Mf.SetSampleTime(sample, entry.Position.Ticks), "IMFSample::SetSampleTime");
            HResult.ThrowIfFailed(Mf.SetSampleDuration(sample, duration.Ticks), "IMFSample::SetSampleDuration");
            HResult.ThrowIfFailed(
                Mf.WriteSample(_writer.Pointer, entry.StreamIndex, sample), "IMFSinkWriter::WriteSample");
            entry.Position += duration;
        }
        finally
        {
            Com.Release(sample);
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _elapsed.Stop();
        HResult.ThrowIfFailed(Mf.FinalizeWriter(_writer.Pointer), "IMFSinkWriter::Finalize");
    }

    private StreamEntry Require(int streamIndex, bool video)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
        {
            throw new InvalidOperationException("Recording has not started: call Start.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(streamIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(streamIndex, _streams.Count);

        var entry = _streams[streamIndex];
        if (entry.IsVideo != video)
        {
            throw new ArgumentException($"Stream {streamIndex} has a different type.", nameof(streamIndex));
        }

        return entry;
    }

    private void EnsureNotStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("Streams cannot be added after Start.");
        }
    }

    /// <summary>
    /// True when the machine has at least one video encoder MFT. Media Foundation
    /// reports a missing encoder as MF_E_INVALIDMEDIATYPE on the input type,
    /// which is why this is checked before the error is surfaced.
    /// </summary>
    public static bool HasVideoEncoder()
    {
        void** activates;
        uint count;
        var hr = MfApi.MFTEnumEx(MfApi.CategoryVideoEncoder, MfApi.EnumFlagAll, null, null, &activates, &count);
        if (HResult.Failed(hr))
        {
            return false;
        }

        for (var i = 0u; i < count; i++)
        {
            Com.Release(activates[i]);
        }

        Ole32.CoTaskMemFree(activates);
        return count > 0;
    }

    /// <summary>MF_E_INVALIDMEDIATYPE.</summary>
    private const int InvalidMediaType = unchecked((int)0xC00D36B4);

    /// <summary>A rough constant-quality bitrate, used when the caller does not state one.</summary>
    private static int EstimateBitrate(int width, int height, Rational frameRate) =>
        (int)Math.Clamp(width * (long)height * (long)Math.Max(frameRate.Value, 1d) / 12, 200_000, 60_000_000);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            Stop();
        }
        catch (ComException)
        {
            // Disposal must not throw over a half-written file.
        }

        _writer.Dispose();
    }

    private sealed class StreamEntry(uint streamIndex, bool isVideo)
    {
        public uint StreamIndex { get; } = streamIndex;

        public bool IsVideo { get; } = isVideo;

        public VideoFormat VideoFormat { get; init; }

        public AudioFormat AudioFormat { get; init; }

        public TimeSpan FrameDuration { get; init; }

        public TimeSpan Position { get; set; }
    }
}
