#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.FFmpeg.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.FFmpeg.Reading;

/// <summary>
/// The kinds of side data a decoded frame can carry, numbered as
/// <c>enum AVFrameSideDataType</c> numbers them. FFmpeg only appends to the enum,
/// and checks the names of the ones from 8.x and 9.x against the libraries at run time.
/// </summary>
public enum FFmpegSideDataType
{
    /// <summary>Pan and scan rectangles.</summary>
    Panscan = 0,

    /// <summary>ATSC A53 closed captions.</summary>
    A53ClosedCaptions = 1,

    /// <summary>Stereoscopic 3D layout.</summary>
    Stereo3D = 2,

    /// <summary>Matrixed stereo encoding.</summary>
    MatrixEncoding = 3,

    /// <summary>Downmix coefficients from the stream's metadata.</summary>
    DownmixInfo = 4,

    /// <summary>ReplayGain.</summary>
    ReplayGain = 5,

    /// <summary>A 3x3 transformation matrix: nine 16.16 fixed-point values, as in a QuickTime <c>tkhd</c>.</summary>
    DisplayMatrix = 6,

    /// <summary>Active format description.</summary>
    ActiveFormatDescription = 7,

    /// <summary>Motion vectors the decoder exported.</summary>
    MotionVectors = 8,

    /// <summary>Samples to skip at the start and end of an audio frame.</summary>
    SkipSamples = 9,

    /// <summary>Audio service type.</summary>
    AudioServiceType = 10,

    /// <summary>SMPTE 2086 mastering display colour volume.</summary>
    MasteringDisplayMetadata = 11,

    /// <summary>GOP timecode.</summary>
    GopTimecode = 12,

    /// <summary>Spherical video mapping.</summary>
    Spherical = 13,

    /// <summary>Content light level.</summary>
    ContentLightLevel = 14,

    /// <summary>An ICC profile.</summary>
    IccProfile = 15,

    /// <summary>SMPTE 12-1 timecode.</summary>
    S12MTimecode = 16,

    /// <summary>HDR10+ dynamic metadata.</summary>
    DynamicHdrPlus = 17,

    /// <summary>Regions of interest.</summary>
    RegionsOfInterest = 18,

    /// <summary>Video encoding parameters.</summary>
    VideoEncParams = 19,

    /// <summary>SEI unregistered user data.</summary>
    SeiUnregistered = 20,

    /// <summary>Film grain parameters.</summary>
    FilmGrainParams = 21,

    /// <summary>Detection bounding boxes.</summary>
    DetectionBoundingBoxes = 22,

    /// <summary>A raw Dolby Vision RPU.</summary>
    DoviRpuBuffer = 23,

    /// <summary>Parsed Dolby Vision metadata.</summary>
    DoviMetadata = 24,

    /// <summary>Vivid HDR dynamic metadata.</summary>
    DynamicHdrVivid = 25,

    /// <summary>Ambient viewing environment.</summary>
    AmbientViewingEnvironment = 26,

    /// <summary>Video encoding hints.</summary>
    VideoHint = 27,

    /// <summary>LCEVC enhancement data.</summary>
    Lcevc = 28,

    /// <summary>The view a frame belongs to (multiview).</summary>
    ViewId = 29,

    /// <summary>3D reference displays (FFmpeg 8.0).</summary>
    ThreeDReferenceDisplays = 30,

    /// <summary>EXIF metadata, a TIFF structure with its header (FFmpeg 8.1).</summary>
    Exif = 31,

    /// <summary>SMPTE 2094-50 dynamic HDR metadata (FFmpeg 9.0).</summary>
    DynamicHdrSmpte2094App5 = 32,

    /// <summary>IAMF mix gain parameters.</summary>
    IamfMixGainParam = 33,

    /// <summary>IAMF demixing information.</summary>
    IamfDemixingInfoParam = 34,

    /// <summary>IAMF recon gain information.</summary>
    IamfReconGainInfoParam = 35,

    /// <summary>Raw sensor colour parameters (FFmpeg 9.1).</summary>
    RawColorParams = 36,

    /// <summary>A downmix matrix (FFmpeg 9.1).</summary>
    DownmixMatrix = 37,

    /// <summary>HDR gain map parameters, ISO 21496-1 (FFmpeg 9.1).</summary>
    GainMapParams = 38,
}

/// <summary>
/// What FFmpeg attached to the frame being delivered, beyond its pixels or
/// samples: side data such as a display matrix, EXIF, a gain map or HDR metadata.
/// Like the frame it accompanies it is borrowed, and valid only inside the handler.
/// </summary>
/// <remarks>
/// This is the FFmpeg backend's own view of a frame. The shared
/// <see cref="VideoFrame"/> and <see cref="AudioFrame"/> carry none of it.
/// </remarks>
public readonly unsafe ref struct FFmpegFrameDetails
{
    private readonly AVFrameHead* _frame;

    internal FFmpegFrameDetails(AVFrameHead* frame) => _frame = frame;

    /// <summary>The bytes of one kind of side data, or an empty span when the frame has none.</summary>
    /// <param name="type">Which kind.</param>
    public ReadOnlySpan<byte> SideData(FFmpegSideDataType type)
    {
        if (_frame is null || AVModern.av_frame_get_side_data is null || !AbiLayout.SideDataLayoutVerified)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        // The types FFmpeg 8 and 9 added are numbered by the libraries that carry
        // them; one whose name is not the expected one is not the type asked for.
        if (type >= FFmpegSideDataType.ThreeDReferenceDisplays && !LibrariesNameIt(type))
        {
            return ReadOnlySpan<byte>.Empty;
        }

        var block = AVModern.av_frame_get_side_data(_frame, (int)type);
        return block is null ? ReadOnlySpan<byte>.Empty : AbiLayout.SideDataBytes(block);
    }

    private static bool LibrariesNameIt(FFmpegSideDataType type)
    {
        var expected = type switch
        {
            FFmpegSideDataType.ThreeDReferenceDisplays => "3D Reference Displays Information",
            FFmpegSideDataType.Exif => "EXIF metadata",
            FFmpegSideDataType.DynamicHdrSmpte2094App5 => "HDR Dynamic Metadata SMPTE2094-50",
            FFmpegSideDataType.IamfMixGainParam => "IAMF Mix Gain Parameter Data",
            FFmpegSideDataType.IamfDemixingInfoParam => "IAMF Demixing Info Parameter Data",
            FFmpegSideDataType.IamfReconGainInfoParam => "IAMF Recon Gain Info Parameter Data",
            FFmpegSideDataType.RawColorParams => "RAW camera color parameters",
            FFmpegSideDataType.DownmixMatrix => "Downmix Matrix",
            FFmpegSideDataType.GainMapParams => "Gain map parameters",
            _ => null,
        };

        if (expected is null || AVModern.av_frame_side_data_name is null)
        {
            return false;
        }

        var name = AVModern.av_frame_side_data_name((int)type);
        return name is not null && Utf8.ToManagedOrEmpty(name) == expected;
    }

    /// <summary>
    /// The EXIF metadata of the frame, which a decoder attaches to a picture that
    /// carries it (FFmpeg 8.1). Empty when it has none.
    /// </summary>
    /// <remarks>
    /// The decoder moves the <c>Orientation</c> entry out of the EXIF and into a
    /// <see cref="FFmpegSideDataType.DisplayMatrix"/> of its own, so a photo that is
    /// rotated by its EXIF shows the rotation there and not in this list.
    /// </remarks>
    /// <exception cref="NotSupportedException">The libraries are older than FFmpeg 8.1.</exception>
    public IReadOnlyList<ExifEntry> Exif()
    {
        var bytes = SideData(FFmpegSideDataType.Exif);
        return bytes.IsEmpty ? [] : FFmpegExif.Parse(bytes, ExifHeaderMode.TiffHeader);
    }

    /// <summary>The HDR gain map parameters of the frame (FFmpeg 9.1), or <see langword="null"/>.</summary>
    public FFmpegGainMap? GainMap() =>
        FFmpegFeatures.GainMaps ? FFmpegGainMapNative.FromNative(SideData(FFmpegSideDataType.GainMapParams)) : null;

    /// <summary>The downmix matrix of the frame (FFmpeg 9.1), or <see langword="null"/>.</summary>
    public FFmpegDownmixMatrix? DownmixMatrix() =>
        SideData(FFmpegSideDataType.DownmixMatrix) is { IsEmpty: false } bytes ? FFmpegDownmixMatrix.Parse(bytes) : null;

    /// <summary>True when the frame carries that kind of side data.</summary>
    /// <param name="type">Which kind.</param>
    public bool HasSideData(FFmpegSideDataType type) => !SideData(type).IsEmpty;
}

/// <summary>Receives a borrowed video frame together with what FFmpeg attached to it.</summary>
/// <param name="frame">The frame.</param>
/// <param name="details">Its side data.</param>
public delegate void FFmpegVideoFrameHandler(in VideoFrame frame, in FFmpegFrameDetails details);

/// <summary>Receives a borrowed block of audio together with what FFmpeg attached to it.</summary>
/// <param name="frame">The audio.</param>
/// <param name="details">Its side data.</param>
public delegate void FFmpegAudioFrameHandler(in AudioFrame frame, in FFmpegFrameDetails details);
