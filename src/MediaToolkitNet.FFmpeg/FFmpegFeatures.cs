#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.FFmpeg.Native;

namespace MediaToolkitNet.FFmpeg;

/// <summary>
/// What the FFmpeg libraries that were loaded can do beyond the common core: the
/// features FFmpeg 8.x, 9.0 and 9.1 added to its API. Each answers
/// <see langword="false"/> when the libraries are older, or are not loaded, so a
/// caller can ask before it uses one; the call itself then throws
/// <see cref="NotSupportedException"/> if it was asked anyway.
/// </summary>
/// <remarks>
/// These are properties of the FFmpeg backend alone. No other backend has an
/// equivalent, and nothing here is part of the shared interfaces.
/// </remarks>
public static unsafe class FFmpegFeatures
{
    /// <summary>
    /// <c>avcodec_receive_frame_flags</c>: a decoder can be asked for a frame
    /// without waiting for its frame-threading pipeline to fill (FFmpeg 8.1).
    /// </summary>
    public static bool SynchronousDecoding => Ready && AVModern.avcodec_receive_frame_flags is not null;

    /// <summary>
    /// <c>av_mime_codec_str</c>: the codec string of a stream as a browser or a
    /// DASH manifest writes it, for example <c>avc1.640028</c> (FFmpeg 8.1).
    /// </summary>
    public static bool MimeCodecStrings => Ready && AVModern.av_mime_codec_str is not null && AbiLayout.BprintLayoutVerified;

    /// <summary>
    /// <c>avcodec_encode_reconfigure</c>: change the settings of an open encoder,
    /// such as its bit rate, without closing it (FFmpeg 9.1). Only some encoders
    /// allow it (libx264 and libaom among them).
    /// </summary>
    public static bool EncoderReconfiguration => Ready && AVModern.avcodec_encode_reconfigure is not null;

    /// <summary>
    /// <c>av_bsf_graph_*</c>: graphs of bitstream filters over packets (FFmpeg 9.1).
    /// See <see cref="FFmpegBitstreamFilterGraph"/>.
    /// </summary>
    public static bool BitstreamFilterGraphs => Ready && AVModern.HasBitstreamFilterGraphs;

    /// <summary>
    /// <c>av_gain_map_params_*</c>: HDR gain map parameters (ISO 21496-1) and their
    /// frame side data (FFmpeg 9.1). See <see cref="FFmpegGainMap"/>.
    /// </summary>
    public static bool GainMaps => Ready && AVModern.av_gain_map_params_validate is not null && AbiLayout.GainMapLayoutVerified;

    /// <summary>
    /// <c>av_downmix_matrix_*</c>: a downmix matrix as frame side data (FFmpeg 9.1).
    /// See <see cref="FFmpegDownmixMatrix"/>.
    /// </summary>
    public static bool DownmixMatrices => Ready && AVModern.av_downmix_matrix_alloc is not null && AbiLayout.DownmixLayoutVerified;

    /// <summary>
    /// The DSD sample format (<see cref="AVSampleFormat.Dsd"/>): a DSD decoder asked for it with
    /// <c>FFmpegDecoder.Open(..., nativeDsd: true)</c> hands over the bitstream, and libswresample turns it into PCM
    /// (FFmpeg 9.1). Older libraries have no such format and always decode DSD to float.
    /// </summary>
    public static bool NativeDsd => Ready && AV.av_get_sample_fmt_name((int)AVSampleFormat.Dsd) is not null;

    /// <summary>
    /// The <c>handle:</c> protocol (Windows, FFmpeg 9.1), which reads a <c>HANDLE</c> with random access, not
    /// just from start to end like <c>pipe:</c>. See <see cref="FFmpegDemuxer.OpenHandle"/>.
    /// </summary>
    public static unsafe bool HandleInput
    {
        get
        {
            if (!Ready || !OperatingSystem.IsWindows() || AVModern.avio_find_protocol_name is null)
            {
                return false;
            }

            fixed (byte* url = "handle:\0"u8)
            {
                return AVModern.avio_find_protocol_name(url) is not null;
            }
        }
    }

    /// <summary>
    /// <c>av_exif_*</c>: parsing, writing and querying EXIF metadata, as frame side
    /// data and as a structure of its own (FFmpeg 8.1).
    /// </summary>
    public static bool Exif => Ready && AVModern.av_exif_parse_buffer is not null && AbiLayout.ExifLayoutVerified;

    private static bool Ready => FFmpegLibraries.IsAvailable;
}
