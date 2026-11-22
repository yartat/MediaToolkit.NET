#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.Abstractions.Formats;
using MediaToolkitNet.Abstractions.Transcoding;
using MediaToolkitNet.Interop.Com;
using MediaToolkitNet.Windows.Native;

namespace MediaToolkitNet.Windows;

/// <summary>
/// Reads what a file contains through <c>IMFSourceReader</c>, the way Windows itself would open it: whatever the
/// installed Media Foundation sources and codecs understand, MP4 and its relatives, WAV, MP3, and on current
/// Windows Matroska too.
/// </summary>
/// <remarks>
/// Media Foundation does not expose file tags, chapters or the container's own name, so
/// <see cref="MediaInfo.Metadata"/> and <see cref="MediaInfo.Chapters"/> are empty and the container is taken from the
/// file's extension. Stream indexes are the source reader's, which need not match the container's order: an MP4
/// written video first can come back audio first.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class MediaFoundationProber : IMediaProber
{
    /// <inheritdoc />
    public string Backend => MediaFoundationDeviceEnumerator.BackendName;

    /// <inheritdoc />
    public MediaInfo Probe(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        MfApi.Startup();

        void* readerPointer;
        HResult.ThrowIfFailed(MfApi.MFCreateSourceReaderFromURL(uri, null, &readerPointer), $"MFCreateSourceReaderFromURL({uri})");
        using var reader = new ComPtr(readerPointer);

        var streams = new List<MediaStreamInfo>();
        for (uint index = 0; ; index++)
        {
            var hr = Mf.GetNativeMediaType(reader.Pointer, index, 0, out var type);
            if (hr == MfApi.InvalidStreamNumber)
            {
                break;
            }

            HResult.ThrowIfFailed(hr, "IMFSourceReader::GetNativeMediaType");
            try
            {
                streams.Add(Describe((int)index, type));
            }
            finally
            {
                Com.Release(type);
            }
        }

        var duration = TimeSpan.Zero;
        if (HResult.Succeeded(Mf.GetPresentationUint64(reader.Pointer, MfApi.MediaSource, WinGuids.PresentationDuration, out var ticks)))
        {
            duration = TimeSpan.FromTicks((long)ticks);
        }

        var extension = Path.GetExtension(uri).TrimStart('.').ToLowerInvariant();
        return new MediaInfo(uri, extension, duration, streams)
        {
            Backend = Backend,
            Container = ContainerOf(extension),
        };
    }

    private static MediaStreamInfo Describe(int index, void* type)
    {
        Mf.GetGuid(type, WinGuids.MajorType, out var major);
        Mf.GetGuid(type, WinGuids.Subtype, out var subtype);
        Mf.GetUint32(type, WinGuids.AudioBitsPerSample, out var bits);
        var (codec, name) = MediaFoundationFormatMap.Describe(subtype, bits);
        Mf.GetUint32(type, WinGuids.AverageBitrate, out var bitrate);

        var kind = major == WinGuids.MediaTypeVideo ? MediaStreamKind.Video
            : major == WinGuids.MediaTypeAudio ? MediaStreamKind.Audio
            : MediaStreamKind.Data;
        var info = new MediaStreamInfo(index, kind, name) { Codec = codec, BitRate = bitrate };

        if (kind == MediaStreamKind.Video)
        {
            var (width, height) = HResult.Succeeded(Mf.GetUint64(type, WinGuids.FrameSize, out var size)) ? Mf.Unpack(size) : (0u, 0u);
            var (numerator, denominator) = HResult.Succeeded(Mf.GetUint64(type, WinGuids.FrameRate, out var rate)) ? Mf.Unpack(rate) : (0u, 0u);
            return info with
            {
                Video = new VideoFormat((int)width, (int)height, MediaFoundationFormatMap.ToPixelFormat(subtype), new Rational((int)numerator, (int)denominator)),
            };
        }

        if (kind == MediaStreamKind.Audio)
        {
            Mf.GetUint32(type, WinGuids.AudioChannels, out var channels);
            Mf.GetUint32(type, WinGuids.AudioSamplesPerSecond, out var rate);
            return info with
            {
                Audio = new AudioFormat((int)rate, (int)channels, MediaFoundationFormatMap.ToSampleFormat(subtype, bits)),
            };
        }

        return info;
    }

    private static MediaContainer? ContainerOf(string extension) => extension switch
    {
        "mp4" or "m4v" or "m4a" => MediaContainer.Mp4,
        "mov" => MediaContainer.Mov,
        "mkv" or "mka" => MediaContainer.Matroska,
        "webm" => MediaContainer.WebM,
        "ts" or "m2ts" => MediaContainer.MpegTs,
        "avi" => MediaContainer.Avi,
        "wav" => MediaContainer.Wav,
        "flac" => MediaContainer.Flac,
        "mp3" => MediaContainer.Mp3,
        _ => null,
    };
}
