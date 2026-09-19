namespace MediaToolkitNet.Windows.Native;

/// <summary>
/// CLSIDs and IIDs of the DirectShow filter graph and of the filters that ship
/// with Windows.
/// </summary>
/// <remarks>
/// These are the filters the operating system registers itself, so a graph built
/// from them needs nothing installed. The values come from the DirectShow SDK
/// headers (uuids.h, strmif.h, qedit.h); they are fixed for the lifetime of the
/// API and are safe to hard-code.
/// </remarks>
public static class DsGuids
{
    // ---------------------------------------------------------------- graph

    /// <summary>CLSID_FilterGraph, the standard graph with its own worker thread.</summary>
    public static readonly Guid FilterGraph = new("e436ebb3-524f-11ce-9f53-0020af0ba770");

    /// <summary>CLSID_FilterGraphNoThread, for a graph driven by the caller's thread.</summary>
    public static readonly Guid FilterGraphNoThread = new("e436ebb8-524f-11ce-9f53-0020af0ba770");

    /// <summary>CLSID_CaptureGraphBuilder2, the helper that wires capture graphs.</summary>
    public static readonly Guid CaptureGraphBuilder2 = new("BF87B6E1-8C27-11d0-B3F0-00AA003761C5");

    // ----------------------------------------------------------- interfaces

    /// <summary>IID_IFilterGraph.</summary>
    public static readonly Guid IFilterGraph = new("56a8689f-0ad4-11ce-b03a-0020af0ba770");

    /// <summary>IID_IGraphBuilder.</summary>
    public static readonly Guid IGraphBuilder = new("56a868a9-0ad4-11ce-b03a-0020af0ba770");

    /// <summary>IID_IFilterGraph2.</summary>
    public static readonly Guid IFilterGraph2 = new("36b73882-c2c8-11cf-8b46-00805f6cef60");

    /// <summary>IID_IMediaControl.</summary>
    public static readonly Guid IMediaControl = new("56a868b1-0ad4-11ce-b03a-0020af0ba770");

    /// <summary>IID_IMediaEvent.</summary>
    public static readonly Guid IMediaEvent = new("56a868b6-0ad4-11ce-b03a-0020af0ba770");

    /// <summary>IID_IMediaEventEx.</summary>
    public static readonly Guid IMediaEventEx = new("56a868c0-0ad4-11ce-b03a-0020af0ba770");

    /// <summary>IID_IMediaSeeking.</summary>
    public static readonly Guid IMediaSeeking = new("36b73880-c2c8-11cf-8b46-00805f6cef60");

    /// <summary>IID_IMediaFilter.</summary>
    public static readonly Guid IMediaFilter = new("56a86899-0ad4-11ce-b03a-0020af0ba770");

    /// <summary>IID_IBaseFilter.</summary>
    public static readonly Guid IBaseFilter = new("56a86895-0ad4-11ce-b03a-0020af0ba770");

    /// <summary>IID_IPin.</summary>
    public static readonly Guid IPin = new("56a86891-0ad4-11ce-b03a-0020af0ba770");

    /// <summary>IID_IEnumPins.</summary>
    public static readonly Guid IEnumPins = new("56a86892-0ad4-11ce-b03a-0020af0ba770");

    /// <summary>IID_IEnumFilters.</summary>
    public static readonly Guid IEnumFilters = new("56a86893-0ad4-11ce-b03a-0020af0ba770");

    /// <summary>IID_IFileSourceFilter.</summary>
    public static readonly Guid IFileSourceFilter = new("56a868a6-0ad4-11ce-b03a-0020af0ba770");

    /// <summary>IID_IFileSinkFilter.</summary>
    public static readonly Guid IFileSinkFilter = new("a2104830-7c70-11cf-8bce-00aa00a3f1a6");

    /// <summary>IID_ICaptureGraphBuilder2.</summary>
    public static readonly Guid ICaptureGraphBuilder2 = new("93E5A4E0-2D50-11d2-ABFA-00A0C9C6E38D");

    /// <summary>IID_IMediaSample.</summary>
    public static readonly Guid IMediaSample = new("56a8689a-0ad4-11ce-b03a-0020af0ba770");

    /// <summary>IID_ISampleGrabber, from qedit.h.</summary>
    public static readonly Guid ISampleGrabber = new("6B652FFF-11FE-4fce-92AD-0266B5D7C78F");

    /// <summary>IID_ISampleGrabberCB, the callback a host implements.</summary>
    public static readonly Guid ISampleGrabberCB = new("0579154A-2B53-4994-B0D0-E773148EFF85");

    // ------------------------------------------------------- source filters

    /// <summary>CLSID_AsyncReader, the "File Source (Async)" filter.</summary>
    public static readonly Guid AsyncReader = new("e436ebb5-524f-11ce-9f53-0020af0ba770");

    /// <summary>CLSID_URLReader, the "File Source (URL)" filter.</summary>
    public static readonly Guid UrlReader = new("e436ebb6-524f-11ce-9f53-0020af0ba770");

    /// <summary>CLSID_DVDNavigator, the DVD Navigator source filter.</summary>
    public static readonly Guid DvdNavigator = new("9B8C4620-2C1A-11d0-8493-00A02438AD48");

    // ----------------------------------------------------------- splitters

    /// <summary>CLSID_WavParser, the "WAVE Parser" filter.</summary>
    public static readonly Guid WaveParser = new("D51BD5A1-7548-11CF-A520-0080C77EF58A");

    /// <summary>CLSID_AviSplitter, the "AVI Splitter" filter.</summary>
    public static readonly Guid AviSplitter = new("1b544c20-fd0b-11ce-8c63-00aa0044b51e");

    /// <summary>CLSID_MPEG1Splitter, the "MPEG-1 Stream Splitter" filter.</summary>
    public static readonly Guid Mpeg1Splitter = new("336475d0-942a-11ce-a870-00aa002feab5");

    /// <summary>CLSID_MPEG2Demultiplexer, the "MPEG-2 Demultiplexer" filter.</summary>
    public static readonly Guid Mpeg2Demultiplexer = new("AFB6C280-2C41-11D3-8A60-0000F81E0E4A");

    /// <summary>CLSID_DVSplitter, the "DV Splitter" filter.</summary>
    public static readonly Guid DvSplitter = new("4EB31670-9FC6-11cf-AF6E-00AA00B67A42");

    // ------------------------------------------------------------- decoders

    /// <summary>CLSID_AviDec, the "AVI Decompressor" filter.</summary>
    public static readonly Guid AviDecompressor = new("cf49d4e0-1115-11ce-b03a-0020af0ba770");

    /// <summary>CLSID_ACMWrapper, the "ACM Wrapper" audio decoder.</summary>
    public static readonly Guid AcmWrapper = new("6a08cf80-0e18-11cf-a24d-0020afd79767");

    /// <summary>CLSID_CMpegVideoCodec, the "MPEG Video Decoder" filter.</summary>
    public static readonly Guid MpegVideoDecoder = new("feb50740-7bef-11ce-9bd9-0000e202599c");

    /// <summary>CLSID_CMpegAudioCodec, the "MPEG Audio Decoder" filter.</summary>
    public static readonly Guid MpegAudioDecoder = new("4a2286e0-7bef-11ce-9bd9-0000e202599c");

    /// <summary>CLSID_MJPGDec, the "MJPEG Decompressor" filter.</summary>
    public static readonly Guid MjpegDecompressor = new("301056D0-6DFF-11d2-9EEB-006008039E37");

    /// <summary>CLSID_DVVideoCodec, the "DV Video Decoder" filter.</summary>
    public static readonly Guid DvVideoDecoder = new("B1B77C00-C3E4-11cf-AF79-00AA00B67A42");

    /// <summary>CLSID_Line21Decoder, the closed-caption decoder.</summary>
    public static readonly Guid Line21Decoder = new("6E8D4A20-310C-11d0-B79A-00AA003767A7");

    // ------------------------------------------------------- transformers

    /// <summary>CLSID_Colour, the "Color Space Converter" filter.</summary>
    public static readonly Guid ColorSpaceConverter = new("1643E180-90F5-11CE-97D5-00AA0055595A");

    /// <summary>CLSID_InfTee, the "Infinite Pin Tee" filter.</summary>
    public static readonly Guid InfinitePinTee = new("F8388A40-D5BB-11d0-BE5A-0080C706568E");

    /// <summary>CLSID_SmartTee, which splits a capture stream into capture and preview.</summary>
    public static readonly Guid SmartTee = new("CC58E280-8AA1-11d1-B3F1-00AA003761C5");

    /// <summary>CLSID_SampleGrabber, from qedit.h. Deprecated by Microsoft but still registered.</summary>
    public static readonly Guid SampleGrabber = new("C1F400A0-3F08-11d3-9F0B-006008039E37");

    // --------------------------------------------------------- renderers

    /// <summary>CLSID_NullRenderer, which swallows everything it is given.</summary>
    public static readonly Guid NullRenderer = new("C1F400A4-3F08-11d3-9F0B-006008039E37");

    /// <summary>CLSID_VideoRendererDefault, which picks the best renderer available.</summary>
    public static readonly Guid VideoRendererDefault = new("6BC1CFFA-8FC1-4261-AC22-CFB4CC38DB50");

    /// <summary>CLSID_VideoRenderer, the legacy renderer.</summary>
    public static readonly Guid VideoRenderer = new("70e102b0-5556-11ce-97c0-00aa0055595a");

    /// <summary>CLSID_VideoMixingRenderer9.</summary>
    public static readonly Guid VideoMixingRenderer9 = new("51b4abf3-748f-4e3b-a276-c828330e926a");

    /// <summary>CLSID_EnhancedVideoRenderer.</summary>
    public static readonly Guid EnhancedVideoRenderer = new("FA10746C-9B63-4B6C-BC49-FC300EA5F256");

    /// <summary>CLSID_DSoundRender, the "Default DirectSound Device" renderer.</summary>
    public static readonly Guid DirectSoundRenderer = new("79376820-07D0-11CF-A24D-0020AFD79767");

    /// <summary>CLSID_AudioRender, the "Default WaveOut Device" renderer.</summary>
    public static readonly Guid WaveOutRenderer = new("e30629d2-27e5-11ce-875d-00608cb78066");

    // ------------------------------------------------------------- writers

    /// <summary>CLSID_FileWriter, the "File Writer" sink.</summary>
    public static readonly Guid FileWriter = new("8596E5F0-0DA5-11d0-BD21-00A0C911CE86");

    /// <summary>CLSID_AviDest, the "AVI Mux" filter.</summary>
    public static readonly Guid AviMux = new("e2510970-f137-11ce-8b67-00aa00a3f1a6");

    /// <summary>CLSID_WavDest, the "WAV Dest" filter.</summary>
    public static readonly Guid WavDest = new("3C78B8E2-6C4D-11D1-ADE2-0000F8754B99");

    /// <summary>CLSID_DVMux, the "DV Muxer" filter.</summary>
    public static readonly Guid DvMux = new("129D7E40-C10D-11d0-AFB9-00AA00B67A42");

    // --------------------------------------------------------- media types

    /// <summary>MEDIATYPE_Video. Shares its value with MFMediaType_Video.</summary>
    public static readonly Guid MediaTypeVideo = WinGuids.MediaTypeVideo;

    /// <summary>MEDIATYPE_Audio. Shares its value with MFMediaType_Audio.</summary>
    public static readonly Guid MediaTypeAudio = WinGuids.MediaTypeAudio;

    /// <summary>MEDIATYPE_Stream, the type an unparsed file source produces.</summary>
    public static readonly Guid MediaTypeStream = new("e436eb83-524f-11ce-9f53-0020af0ba770");

    /// <summary>MEDIATYPE_Interleaved, used by DV.</summary>
    public static readonly Guid MediaTypeInterleaved = new("73766169-0000-0010-8000-00AA00389B71");

    /// <summary>MEDIASUBTYPE_RGB24. Bottom-up BGR in memory.</summary>
    public static readonly Guid SubtypeRgb24 = new("e436eb7d-524f-11ce-9f53-0020af0ba770");

    /// <summary>MEDIASUBTYPE_RGB32.</summary>
    public static readonly Guid SubtypeRgb32 = new("e436eb7e-524f-11ce-9f53-0020af0ba770");

    /// <summary>MEDIASUBTYPE_ARGB32.</summary>
    public static readonly Guid SubtypeArgb32 = new("773c9ac0-3274-11d0-b724-00aa006c1a01");

    /// <summary>MEDIASUBTYPE_YUY2.</summary>
    public static readonly Guid SubtypeYuy2 = WinGuids.FromFourCc("YUY2");

    /// <summary>MEDIASUBTYPE_UYVY.</summary>
    public static readonly Guid SubtypeUyvy = WinGuids.FromFourCc("UYVY");

    /// <summary>MEDIASUBTYPE_NV12.</summary>
    public static readonly Guid SubtypeNv12 = WinGuids.FromFourCc("NV12");

    /// <summary>MEDIASUBTYPE_MJPG.</summary>
    public static readonly Guid SubtypeMjpg = WinGuids.FromFourCc("MJPG");

    /// <summary>MEDIASUBTYPE_PCM.</summary>
    public static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");

    /// <summary>MEDIASUBTYPE_WAVE.</summary>
    public static readonly Guid SubtypeWave = new("e436eb8b-524f-11ce-9f53-0020af0ba770");

    // ------------------------------------------------------ format blocks

    /// <summary>FORMAT_VideoInfo: the format block is a VIDEOINFOHEADER.</summary>
    public static readonly Guid FormatVideoInfo = new("05589f80-c356-11ce-bf01-00aa0055595a");

    /// <summary>FORMAT_VideoInfo2: the format block is a VIDEOINFOHEADER2.</summary>
    public static readonly Guid FormatVideoInfo2 = new("f72a76A0-eb0a-11d0-ace4-0000c0cc16ba");

    /// <summary>FORMAT_WaveFormatEx: the format block is a WAVEFORMATEX.</summary>
    public static readonly Guid FormatWaveFormatEx = new("05589f81-c356-11ce-bf01-00aa0055595a");

    /// <summary>FORMAT_None.</summary>
    public static readonly Guid FormatNone = new("0F6417D6-c318-11d0-a43f-00a0c9223196");

    // ------------------------------------------------------ pin categories

    /// <summary>PIN_CATEGORY_CAPTURE.</summary>
    public static readonly Guid PinCategoryCapture = new("fb6c4281-0353-11d1-905f-0000c0cc16ba");

    /// <summary>PIN_CATEGORY_PREVIEW.</summary>
    public static readonly Guid PinCategoryPreview = new("fb6c4282-0353-11d1-905f-0000c0cc16ba");

    /// <summary>PIN_CATEGORY_STILL.</summary>
    public static readonly Guid PinCategoryStill = new("fb6c428a-0353-11d1-905f-0000c0cc16ba");

    /// <summary>TIME_FORMAT_MEDIA_TIME, 100-nanosecond units.</summary>
    public static readonly Guid TimeFormatMediaTime = new("7b785574-8c82-11cf-bc0c-00aa00ac74f6");

    /// <summary>TIME_FORMAT_FRAME.</summary>
    public static readonly Guid TimeFormatFrame = new("7b785572-8c82-11cf-bc0c-00aa00ac74f6");
}

/// <summary>
/// The Windows filters this library exposes by name, so callers do not have to
/// carry CLSIDs around.
/// </summary>
public enum DsFilter
{
    /// <summary>File Source (Async): reads a local file and emits an unparsed stream.</summary>
    FileSourceAsync,

    /// <summary>File Source (URL): reads over http and emits an unparsed stream.</summary>
    FileSourceUrl,

    /// <summary>DVD Navigator: plays a DVD volume.</summary>
    DvdNavigator,

    /// <summary>WAVE Parser: splits a RIFF WAVE stream into PCM.</summary>
    WaveParser,

    /// <summary>AVI Splitter.</summary>
    AviSplitter,

    /// <summary>MPEG-1 Stream Splitter.</summary>
    Mpeg1Splitter,

    /// <summary>MPEG-2 Demultiplexer.</summary>
    Mpeg2Demultiplexer,

    /// <summary>DV Splitter.</summary>
    DvSplitter,

    /// <summary>AVI Decompressor: decodes the video stream of an AVI.</summary>
    AviDecompressor,

    /// <summary>ACM Wrapper: decodes audio through an installed ACM codec.</summary>
    AcmWrapper,

    /// <summary>MPEG Video Decoder.</summary>
    MpegVideoDecoder,

    /// <summary>MPEG Audio Decoder.</summary>
    MpegAudioDecoder,

    /// <summary>MJPEG Decompressor.</summary>
    MjpegDecompressor,

    /// <summary>DV Video Decoder.</summary>
    DvVideoDecoder,

    /// <summary>Line 21 Decoder, for closed captions.</summary>
    Line21Decoder,

    /// <summary>Color Space Converter.</summary>
    ColorSpaceConverter,

    /// <summary>Infinite Pin Tee: duplicates a stream to any number of outputs.</summary>
    InfinitePinTee,

    /// <summary>Smart Tee: splits a capture stream into a capture and a preview branch.</summary>
    SmartTee,

    /// <summary>Sample Grabber: hands every sample to a callback.</summary>
    SampleGrabber,

    /// <summary>Null Renderer: terminates a branch and discards its samples.</summary>
    NullRenderer,

    /// <summary>Video Renderer (Default): the renderer Windows considers best.</summary>
    VideoRendererDefault,

    /// <summary>Video Renderer, the legacy one.</summary>
    VideoRenderer,

    /// <summary>Video Mixing Renderer 9.</summary>
    VideoMixingRenderer9,

    /// <summary>Enhanced Video Renderer.</summary>
    EnhancedVideoRenderer,

    /// <summary>Default DirectSound Device.</summary>
    DirectSoundRenderer,

    /// <summary>Default WaveOut Device.</summary>
    WaveOutRenderer,

    /// <summary>File Writer.</summary>
    FileWriter,

    /// <summary>AVI Mux.</summary>
    AviMux,

    /// <summary>WAV Dest: wraps PCM in a RIFF WAVE header for the File Writer.</summary>
    WavDest,

    /// <summary>DV Muxer.</summary>
    DvMux,
}

/// <summary>Maps <see cref="DsFilter"/> onto the CLSID and the name Windows registers.</summary>
public static class DsFilters
{
    /// <summary>The CLSID Windows registers the filter under.</summary>
    public static Guid ClassId(DsFilter filter) => filter switch
    {
        DsFilter.FileSourceAsync => DsGuids.AsyncReader,
        DsFilter.FileSourceUrl => DsGuids.UrlReader,
        DsFilter.DvdNavigator => DsGuids.DvdNavigator,
        DsFilter.WaveParser => DsGuids.WaveParser,
        DsFilter.AviSplitter => DsGuids.AviSplitter,
        DsFilter.Mpeg1Splitter => DsGuids.Mpeg1Splitter,
        DsFilter.Mpeg2Demultiplexer => DsGuids.Mpeg2Demultiplexer,
        DsFilter.DvSplitter => DsGuids.DvSplitter,
        DsFilter.AviDecompressor => DsGuids.AviDecompressor,
        DsFilter.AcmWrapper => DsGuids.AcmWrapper,
        DsFilter.MpegVideoDecoder => DsGuids.MpegVideoDecoder,
        DsFilter.MpegAudioDecoder => DsGuids.MpegAudioDecoder,
        DsFilter.MjpegDecompressor => DsGuids.MjpegDecompressor,
        DsFilter.DvVideoDecoder => DsGuids.DvVideoDecoder,
        DsFilter.Line21Decoder => DsGuids.Line21Decoder,
        DsFilter.ColorSpaceConverter => DsGuids.ColorSpaceConverter,
        DsFilter.InfinitePinTee => DsGuids.InfinitePinTee,
        DsFilter.SmartTee => DsGuids.SmartTee,
        DsFilter.SampleGrabber => DsGuids.SampleGrabber,
        DsFilter.NullRenderer => DsGuids.NullRenderer,
        DsFilter.VideoRendererDefault => DsGuids.VideoRendererDefault,
        DsFilter.VideoRenderer => DsGuids.VideoRenderer,
        DsFilter.VideoMixingRenderer9 => DsGuids.VideoMixingRenderer9,
        DsFilter.EnhancedVideoRenderer => DsGuids.EnhancedVideoRenderer,
        DsFilter.DirectSoundRenderer => DsGuids.DirectSoundRenderer,
        DsFilter.WaveOutRenderer => DsGuids.WaveOutRenderer,
        DsFilter.FileWriter => DsGuids.FileWriter,
        DsFilter.AviMux => DsGuids.AviMux,
        DsFilter.WavDest => DsGuids.WavDest,
        DsFilter.DvMux => DsGuids.DvMux,
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "unknown built-in filter"),
    };

    /// <summary>The name the filter is registered under, useful for logs and graph dumps.</summary>
    public static string DisplayName(DsFilter filter) => filter switch
    {
        DsFilter.FileSourceAsync => "File Source (Async.)",
        DsFilter.FileSourceUrl => "File Source (URL)",
        DsFilter.DvdNavigator => "DVD Navigator",
        DsFilter.WaveParser => "WAVE Parser",
        DsFilter.AviSplitter => "AVI Splitter",
        DsFilter.Mpeg1Splitter => "MPEG-I Stream Splitter",
        DsFilter.Mpeg2Demultiplexer => "MPEG-2 Demultiplexer",
        DsFilter.DvSplitter => "DV Splitter",
        DsFilter.AviDecompressor => "AVI Decompressor",
        DsFilter.AcmWrapper => "ACM Wrapper",
        DsFilter.MpegVideoDecoder => "MPEG Video Decoder",
        DsFilter.MpegAudioDecoder => "MPEG Audio Decoder",
        DsFilter.MjpegDecompressor => "MJPEG Decompressor",
        DsFilter.DvVideoDecoder => "DV Video Decoder",
        DsFilter.Line21Decoder => "Line 21 Decoder",
        DsFilter.ColorSpaceConverter => "Color Space Converter",
        DsFilter.InfinitePinTee => "Infinite Pin Tee",
        DsFilter.SmartTee => "Smart Tee",
        DsFilter.SampleGrabber => "Sample Grabber",
        DsFilter.NullRenderer => "Null Renderer",
        DsFilter.VideoRendererDefault => "Video Renderer (Default)",
        DsFilter.VideoRenderer => "Video Renderer",
        DsFilter.VideoMixingRenderer9 => "Video Mixing Renderer 9",
        DsFilter.EnhancedVideoRenderer => "Enhanced Video Renderer",
        DsFilter.DirectSoundRenderer => "Default DirectSound Device",
        DsFilter.WaveOutRenderer => "Default WaveOut Device",
        DsFilter.FileWriter => "File Writer",
        DsFilter.AviMux => "AVI Mux",
        DsFilter.WavDest => "WAV Dest",
        DsFilter.DvMux => "DV Muxer",
        _ => filter.ToString(),
    };

    /// <summary>Every filter this library knows by name.</summary>
    public static IReadOnlyList<DsFilter> All { get; } = Enum.GetValues<DsFilter>();
}
