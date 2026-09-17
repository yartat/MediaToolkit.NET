namespace MediaToolkitNet.Windows.Native;

/// <summary>
/// CLSIDs, IIDs and attribute GUIDs used by the Windows backends.
/// </summary>
/// <remarks>
/// Media Foundation derives most media subtype GUIDs from a FOURCC or a
/// WAVE_FORMAT tag through the well-known
/// <c>XXXXXXXX-0000-0010-8000-00AA00389B71</c> pattern; see
/// <see cref="FromFourCc"/> and <see cref="FromWaveFormatTag"/>.
/// </remarks>
public static class WinGuids
{
    // ------------------------------------------------------------- interfaces

    /// <summary>IID_IMFAttributes.</summary>
    public static readonly Guid IMFAttributes = new("2cd2d921-c447-44a7-a13c-4adabfc247e3");

    /// <summary>IID_IMFActivate.</summary>
    public static readonly Guid IMFActivate = new("7fee9e9a-4a89-47a6-899c-b6a53a70fb67");

    /// <summary>IID_IMFMediaSource.</summary>
    public static readonly Guid IMFMediaSource = new("279a808d-aec7-40c8-9c6b-a6b492c78a66");

    /// <summary>IID_IMFSourceReader.</summary>
    public static readonly Guid IMFSourceReader = new("70ae66f2-c809-4e4f-8915-bdcb406b7993");

    /// <summary>IID_IMFSinkWriter.</summary>
    public static readonly Guid IMFSinkWriter = new("3137f1cd-fe5e-4805-a5d8-fb477448cb3d");

    /// <summary>IID_IMMDeviceEnumerator.</summary>
    public static readonly Guid IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    /// <summary>CLSID_MMDeviceEnumerator.</summary>
    public static readonly Guid MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary>IID_IAudioClient.</summary>
    public static readonly Guid IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    /// <summary>IID_IAudioRenderClient.</summary>
    public static readonly Guid IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    /// <summary>IID_IAudioCaptureClient.</summary>
    public static readonly Guid IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    /// <summary>CLSID_SystemDeviceEnum.</summary>
    public static readonly Guid SystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");

    /// <summary>IID_ICreateDevEnum.</summary>
    public static readonly Guid ICreateDevEnum = new("29840822-5B84-11D0-BD3B-00A0C911CE86");

    /// <summary>CLSID_VideoInputDeviceCategory.</summary>
    public static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");

    /// <summary>CLSID_AudioInputDeviceCategory.</summary>
    public static readonly Guid AudioInputDeviceCategory = new("33D9A762-90C8-11d0-BD43-00A0C911CE86");

    /// <summary>IID_IPropertyBag.</summary>
    public static readonly Guid IPropertyBag = new("55272A00-42CB-11CE-8135-00AA004BB851");

    // ------------------------------------------- Media Foundation device source

    /// <summary>MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE.</summary>
    public static readonly Guid DevSourceAttributeSourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");

    /// <summary>MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID.</summary>
    public static readonly Guid DevSourceVideoCapture = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");

    /// <summary>MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_AUDCAP_GUID.</summary>
    public static readonly Guid DevSourceAudioCapture = new("14dd9a1c-7cff-41be-b1b9-ba1ac6ecb571");

    /// <summary>MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME.</summary>
    public static readonly Guid DevSourceFriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");

    /// <summary>MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK.</summary>
    public static readonly Guid DevSourceVideoSymbolicLink = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");

    /// <summary>MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_AUDCAP_ENDPOINT_ID.</summary>
    public static readonly Guid DevSourceAudioEndpointId = new("30da9258-feb9-47a7-a453-763a7a8e1c5f");

    // ------------------------------------------------ Media Foundation options

    /// <summary>MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS.</summary>
    public static readonly Guid EnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");

    /// <summary>MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING.</summary>
    public static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");

    /// <summary>MF_SINK_WRITER_DISABLE_THROTTLING.</summary>
    public static readonly Guid DisableThrottling = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");

    // ------------------------------------------------- Media type attributes

    /// <summary>MF_MT_MAJOR_TYPE.</summary>
    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");

    /// <summary>MF_MT_SUBTYPE.</summary>
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");

    /// <summary>MF_MT_FRAME_SIZE, a packed 64-bit width and height.</summary>
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");

    /// <summary>MF_MT_FRAME_RATE, a packed 64-bit numerator and denominator.</summary>
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");

    /// <summary>MF_MT_PIXEL_ASPECT_RATIO.</summary>
    public static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");

    /// <summary>MF_MT_DEFAULT_STRIDE.</summary>
    public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    /// <summary>MF_MT_INTERLACE_MODE.</summary>
    public static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b6-a48c7ed27025");

    /// <summary>MF_MT_ALL_SAMPLES_INDEPENDENT.</summary>
    public static readonly Guid AllSamplesIndependent = new("c9173739-5e56-461c-b713-46fb995cb95f");

    /// <summary>MF_MT_MPEG2_PROFILE, which H.264 reuses to carry its profile.</summary>
    public static readonly Guid Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");

    /// <summary>MF_MT_AVG_BITRATE.</summary>
    public static readonly Guid AverageBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");

    /// <summary>MF_MT_AUDIO_NUM_CHANNELS.</summary>
    public static readonly Guid AudioChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");

    /// <summary>MF_MT_AUDIO_SAMPLES_PER_SECOND.</summary>
    public static readonly Guid AudioSamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");

    /// <summary>MF_MT_AUDIO_BITS_PER_SAMPLE.</summary>
    public static readonly Guid AudioBitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");

    /// <summary>MF_MT_AUDIO_BLOCK_ALIGNMENT.</summary>
    public static readonly Guid AudioBlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");

    /// <summary>MF_MT_AUDIO_AVG_BYTES_PER_SECOND.</summary>
    public static readonly Guid AudioAverageBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");

    // --------------------------------------------------------- major types

    /// <summary>MFMediaType_Video.</summary>
    public static readonly Guid MediaTypeVideo = FromFourCc("vids");

    /// <summary>MFMediaType_Audio.</summary>
    public static readonly Guid MediaTypeAudio = FromFourCc("auds");

    // ------------------------------------------------------- video subtypes

    /// <summary>MFVideoFormat_NV12.</summary>
    public static readonly Guid VideoNv12 = FromFourCc("NV12");

    /// <summary>MFVideoFormat_YUY2.</summary>
    public static readonly Guid VideoYuy2 = FromFourCc("YUY2");

    /// <summary>MFVideoFormat_UYVY.</summary>
    public static readonly Guid VideoUyvy = FromFourCc("UYVY");

    /// <summary>MFVideoFormat_I420, the planar YUV 4:2:0 layout.</summary>
    public static readonly Guid VideoI420 = FromFourCc("I420");

    /// <summary>MFVideoFormat_IYUV, an alias of I420.</summary>
    public static readonly Guid VideoIyuv = FromFourCc("IYUV");

    /// <summary>MFVideoFormat_MJPG.</summary>
    public static readonly Guid VideoMjpg = FromFourCc("MJPG");

    /// <summary>MFVideoFormat_H264.</summary>
    public static readonly Guid VideoH264 = FromFourCc("H264");

    /// <summary>MFVideoFormat_HEVC.</summary>
    public static readonly Guid VideoHevc = FromFourCc("HEVC");

    /// <summary>MFVideoFormat_RGB24, which is BGR in memory.</summary>
    public static readonly Guid VideoRgb24 = FromWaveFormatTag(20);

    /// <summary>MFVideoFormat_RGB32, which is BGRX in memory.</summary>
    public static readonly Guid VideoRgb32 = FromWaveFormatTag(22);

    /// <summary>MFVideoFormat_ARGB32, which is BGRA in memory.</summary>
    public static readonly Guid VideoArgb32 = FromWaveFormatTag(21);

    // ------------------------------------------------------- audio subtypes

    /// <summary>MFAudioFormat_PCM (WAVE_FORMAT_PCM).</summary>
    public static readonly Guid AudioPcm = FromWaveFormatTag(0x0001);

    /// <summary>MFAudioFormat_Float (WAVE_FORMAT_IEEE_FLOAT).</summary>
    public static readonly Guid AudioFloat = FromWaveFormatTag(0x0003);

    /// <summary>MFAudioFormat_AAC.</summary>
    public static readonly Guid AudioAac = FromWaveFormatTag(0x1610);

    /// <summary>MFAudioFormat_MP3.</summary>
    public static readonly Guid AudioMp3 = FromWaveFormatTag(0x0055);

    /// <summary>Builds a Media Foundation subtype GUID from a four character code.</summary>
    public static Guid FromFourCc(string fourCc)
    {
        ArgumentException.ThrowIfNullOrEmpty(fourCc);
        ArgumentOutOfRangeException.ThrowIfNotEqual(fourCc.Length, 4);

        var value = (uint)(fourCc[0] | (fourCc[1] << 8) | (fourCc[2] << 16) | (fourCc[3] << 24));
        return FromWaveFormatTag(value);
    }

    /// <summary>Builds a Media Foundation subtype GUID from a WAVE_FORMAT tag or FOURCC value.</summary>
    public static Guid FromWaveFormatTag(uint tag) =>
        new(tag, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
}
