using MediaToolkitNet.Abstractions.Formats;

namespace MediaToolkitNet.Abstractions.Frames;

/// <summary>
/// A borrowed view over one decoded or captured block of PCM audio.
/// </summary>
/// <remarks>
/// Like <see cref="VideoFrame"/>, the frame does not own its memory; the
/// pointers are valid only inside the callback that delivered it.
/// </remarks>
public readonly ref struct AudioFrame
{
    private readonly PlanePointers _planes;

    /// <summary>Creates a frame view from raw plane pointers.</summary>
    /// <param name="format">Sample rate, channel count and sample layout.</param>
    /// <param name="timestamp">Presentation timestamp relative to the start of the stream.</param>
    /// <param name="sampleCount">Number of samples per channel in this frame.</param>
    /// <param name="planes">Pointer to the first byte of each plane.</param>
    /// <param name="planeCount">Number of valid entries in <paramref name="planes"/>.</param>
    public AudioFrame(
        AudioFormat format,
        TimeSpan timestamp,
        int sampleCount,
        ReadOnlySpan<nint> planes,
        int planeCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(planeCount, MediaPlanes.MaxPlanes);

        Format = format;
        Timestamp = timestamp;
        SampleCount = sampleCount;
        PlaneCount = planeCount;
        for (var i = 0; i < planeCount; i++)
        {
            _planes[i] = planes[i];
        }
    }

    /// <summary>Creates an interleaved frame view over a contiguous buffer.</summary>
    public unsafe AudioFrame(AudioFormat format, TimeSpan timestamp, int sampleCount, byte* data)
    {
        Format = format;
        Timestamp = timestamp;
        SampleCount = sampleCount;
        PlaneCount = 1;
        _planes[0] = (nint)data;
    }

    /// <summary>Sample rate, channel count and sample layout.</summary>
    public AudioFormat Format { get; }

    /// <summary>Presentation timestamp relative to the start of the stream.</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>Number of samples per channel.</summary>
    public int SampleCount { get; }

    /// <summary>Number of valid planes: one when interleaved, one per channel when planar.</summary>
    public int PlaneCount { get; }

    /// <summary>Duration of the audio held by this frame.</summary>
    public TimeSpan Duration => Format.DurationOf(SampleCount);

    /// <summary>Raw pointer to the first byte of the given plane.</summary>
    public nint PlanePointer(int plane) => _planes[Check(plane)];

    /// <summary>The given plane as a span of bytes.</summary>
    public unsafe ReadOnlySpan<byte> GetPlane(int plane)
    {
        var index = Check(plane);
        var bytesPerSample = Format.SampleFormat.BytesPerSample();
        var channelsInPlane = Format.SampleFormat.IsPlanar() ? 1 : Format.Channels;
        return new ReadOnlySpan<byte>((void*)_planes[index], SampleCount * bytesPerSample * channelsInPlane);
    }

    /// <summary>
    /// The interleaved payload as a span. Only valid when the format is
    /// interleaved; planar frames must be read plane by plane.
    /// </summary>
    public ReadOnlySpan<byte> Interleaved =>
        Format.SampleFormat.IsPlanar()
            ? throw new InvalidOperationException("The frame is stored as planes; use GetPlane.")
            : GetPlane(0);

    private int Check(int plane)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plane);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(plane, PlaneCount);
        return plane;
    }
}

/// <summary>Receives a borrowed <see cref="AudioFrame"/>.</summary>
public delegate void AudioFrameHandler(in AudioFrame frame);
