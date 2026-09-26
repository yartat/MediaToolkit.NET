#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer.Native;

/// <summary>
/// The parts of libgstpbutils and libgsttag the prober uses: GstDiscoverer and
/// the language-code table.
/// </summary>
/// <remarks>
/// Both libraries belong to gst-plugins-base, which a system with the core but
/// no base plugins does not have, so they are loaded on first use rather than
/// with the core. Everything a pipeline needs keeps working without them.
/// </remarks>
public static unsafe class GstPbutils
{
    private static readonly Lock Gate = new();
    private static bool _initialised;
    private static Exception? _failure;

    /// <summary>libgstpbutils-1.0, once loaded.</summary>
    public static NativeModule Pbutils { get; private set; } = null!;

    /// <summary>libgsttag-1.0, once loaded.</summary>
    public static NativeModule Tag { get; private set; } = null!;

    /// <summary>Loads both libraries, throwing when either is missing.</summary>
    /// <exception cref="MediaBackendUnavailableException">GStreamer or its base plugins libraries are missing.</exception>
    public static void EnsureLoaded()
    {
        Gst.EnsureLoaded();
        if (!_initialised)
        {
            lock (Gate)
            {
                if (!_initialised)
                {
                    try
                    {
                        Initialise();
                    }
                    catch (Exception ex)
                    {
                        _failure = ex;
                    }
                    finally
                    {
                        _initialised = true;
                    }
                }
            }
        }

        if (_failure is not null)
        {
            throw _failure as MediaBackendUnavailableException
                  ?? new MediaBackendUnavailableException(
                      GstConstants.BackendName, $"could not load libgstpbutils: {_failure.Message}", _failure);
        }
    }

    /// <summary><c>void gst_pb_utils_init(void)</c></summary>
    public static delegate* unmanaged[Cdecl]<void> gst_pb_utils_init;

    /// <summary><c>GstDiscoverer *gst_discoverer_new(GstClockTime timeout, GError **)</c></summary>
    public static delegate* unmanaged[Cdecl]<ulong, void**, void*> gst_discoverer_new;

    /// <summary><c>GstDiscovererInfo *gst_discoverer_discover_uri(GstDiscoverer *, const gchar *, GError **)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*, void**, void*> gst_discoverer_discover_uri;

    /// <summary><c>GstDiscovererResult gst_discoverer_info_get_result(const GstDiscovererInfo *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int> gst_discoverer_info_get_result;

    /// <summary><c>GstClockTime gst_discoverer_info_get_duration(const GstDiscovererInfo *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, ulong> gst_discoverer_info_get_duration;

    /// <summary><c>GstDiscovererStreamInfo *gst_discoverer_info_get_stream_info(GstDiscovererInfo *)</c>, a reference the caller owns.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_discoverer_info_get_stream_info;

    /// <summary><c>GList *gst_discoverer_info_get_stream_list(GstDiscovererInfo *)</c>, freed with the list function below.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_discoverer_info_get_stream_list;

    /// <summary><c>void gst_discoverer_stream_info_list_free(GList *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, void> gst_discoverer_stream_info_list_free;

    /// <summary><c>const GstToc *gst_discoverer_info_get_toc(const GstDiscovererInfo *)</c>, borrowed.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_discoverer_info_get_toc;

    /// <summary><c>const GstTagList *gst_discoverer_container_info_get_tags(const GstDiscovererContainerInfo *)</c>, borrowed; 1.20 and later.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_discoverer_container_info_get_tags;

    /// <summary><c>const gchar *gst_discoverer_stream_info_get_stream_type_nick(GstDiscovererStreamInfo *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*> gst_discoverer_stream_info_get_stream_type_nick;

    /// <summary><c>GstCaps *gst_discoverer_stream_info_get_caps(GstDiscovererStreamInfo *)</c>, a reference the caller owns.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_discoverer_stream_info_get_caps;

    /// <summary><c>const GstTagList *gst_discoverer_stream_info_get_tags(GstDiscovererStreamInfo *)</c>, borrowed.</summary>
    public static delegate* unmanaged[Cdecl]<void*, void*> gst_discoverer_stream_info_get_tags;

    /// <summary><c>gint gst_discoverer_stream_info_get_stream_number(GstDiscovererStreamInfo *)</c>, 1.20 and later.</summary>
    public static delegate* unmanaged[Cdecl]<void*, int> gst_discoverer_stream_info_get_stream_number;

    /// <summary><c>guint gst_discoverer_video_info_get_width(const GstDiscovererVideoInfo *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, uint> gst_discoverer_video_info_get_width;

    /// <summary><c>guint gst_discoverer_video_info_get_height(const GstDiscovererVideoInfo *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, uint> gst_discoverer_video_info_get_height;

    /// <summary><c>guint gst_discoverer_video_info_get_framerate_num(const GstDiscovererVideoInfo *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, uint> gst_discoverer_video_info_get_framerate_num;

    /// <summary><c>guint gst_discoverer_video_info_get_framerate_denom(const GstDiscovererVideoInfo *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, uint> gst_discoverer_video_info_get_framerate_denom;

    /// <summary><c>guint gst_discoverer_video_info_get_bitrate(const GstDiscovererVideoInfo *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, uint> gst_discoverer_video_info_get_bitrate;

    /// <summary><c>gboolean gst_discoverer_video_info_is_image(const GstDiscovererVideoInfo *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, int> gst_discoverer_video_info_is_image;

    /// <summary><c>guint gst_discoverer_audio_info_get_sample_rate(const GstDiscovererAudioInfo *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, uint> gst_discoverer_audio_info_get_sample_rate;

    /// <summary><c>guint gst_discoverer_audio_info_get_channels(const GstDiscovererAudioInfo *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, uint> gst_discoverer_audio_info_get_channels;

    /// <summary><c>guint gst_discoverer_audio_info_get_bitrate(const GstDiscovererAudioInfo *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, uint> gst_discoverer_audio_info_get_bitrate;

    /// <summary><c>const gchar *gst_discoverer_audio_info_get_language(const GstDiscovererAudioInfo *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*> gst_discoverer_audio_info_get_language;

    /// <summary><c>const gchar *gst_discoverer_subtitle_info_get_language(const GstDiscovererSubtitleInfo *)</c></summary>
    public static delegate* unmanaged[Cdecl]<void*, byte*> gst_discoverer_subtitle_info_get_language;

    /// <summary><c>const gchar *gst_tag_get_language_code_iso_639_2B(const gchar *)</c>, from libgsttag.</summary>
    public static delegate* unmanaged[Cdecl]<byte*, byte*> gst_tag_get_language_code_iso_639_2B;

    private static void Initialise()
    {
        Pbutils = NativeModule.Load(GstConstants.BackendName, ["libgstpbutils-1.0.so.0", "libgstpbutils-1.0.so"]);
        Tag = NativeModule.Load(GstConstants.BackendName, ["libgsttag-1.0.so.0", "libgsttag-1.0.so"]);

        gst_pb_utils_init = (delegate* unmanaged[Cdecl]<void>)Pbutils.GetExport(nameof(gst_pb_utils_init));
        gst_discoverer_new = (delegate* unmanaged[Cdecl]<ulong, void**, void*>)Pbutils.GetExport(nameof(gst_discoverer_new));
        gst_discoverer_discover_uri = (delegate* unmanaged[Cdecl]<void*, byte*, void**, void*>)Pbutils.GetExport(nameof(gst_discoverer_discover_uri));
        gst_discoverer_info_get_result = (delegate* unmanaged[Cdecl]<void*, int>)Pbutils.GetExport(nameof(gst_discoverer_info_get_result));
        gst_discoverer_info_get_duration = (delegate* unmanaged[Cdecl]<void*, ulong>)Pbutils.GetExport(nameof(gst_discoverer_info_get_duration));
        gst_discoverer_info_get_stream_info = (delegate* unmanaged[Cdecl]<void*, void*>)Pbutils.GetExport(nameof(gst_discoverer_info_get_stream_info));
        gst_discoverer_info_get_stream_list = (delegate* unmanaged[Cdecl]<void*, void*>)Pbutils.GetExport(nameof(gst_discoverer_info_get_stream_list));
        gst_discoverer_stream_info_list_free = (delegate* unmanaged[Cdecl]<void*, void>)Pbutils.GetExport(nameof(gst_discoverer_stream_info_list_free));
        gst_discoverer_info_get_toc = (delegate* unmanaged[Cdecl]<void*, void*>)Pbutils.GetExport(nameof(gst_discoverer_info_get_toc));
        gst_discoverer_container_info_get_tags = (delegate* unmanaged[Cdecl]<void*, void*>)Pbutils.GetExport(nameof(gst_discoverer_container_info_get_tags));
        gst_discoverer_stream_info_get_stream_type_nick = (delegate* unmanaged[Cdecl]<void*, byte*>)Pbutils.GetExport(nameof(gst_discoverer_stream_info_get_stream_type_nick));
        gst_discoverer_stream_info_get_caps = (delegate* unmanaged[Cdecl]<void*, void*>)Pbutils.GetExport(nameof(gst_discoverer_stream_info_get_caps));
        gst_discoverer_stream_info_get_tags = (delegate* unmanaged[Cdecl]<void*, void*>)Pbutils.GetExport(nameof(gst_discoverer_stream_info_get_tags));
        gst_discoverer_stream_info_get_stream_number = (delegate* unmanaged[Cdecl]<void*, int>)Pbutils.GetExport(nameof(gst_discoverer_stream_info_get_stream_number));
        gst_discoverer_video_info_get_width = (delegate* unmanaged[Cdecl]<void*, uint>)Pbutils.GetExport(nameof(gst_discoverer_video_info_get_width));
        gst_discoverer_video_info_get_height = (delegate* unmanaged[Cdecl]<void*, uint>)Pbutils.GetExport(nameof(gst_discoverer_video_info_get_height));
        gst_discoverer_video_info_get_framerate_num = (delegate* unmanaged[Cdecl]<void*, uint>)Pbutils.GetExport(nameof(gst_discoverer_video_info_get_framerate_num));
        gst_discoverer_video_info_get_framerate_denom = (delegate* unmanaged[Cdecl]<void*, uint>)Pbutils.GetExport(nameof(gst_discoverer_video_info_get_framerate_denom));
        gst_discoverer_video_info_get_bitrate = (delegate* unmanaged[Cdecl]<void*, uint>)Pbutils.GetExport(nameof(gst_discoverer_video_info_get_bitrate));
        gst_discoverer_video_info_is_image = (delegate* unmanaged[Cdecl]<void*, int>)Pbutils.GetExport(nameof(gst_discoverer_video_info_is_image));
        gst_discoverer_audio_info_get_sample_rate = (delegate* unmanaged[Cdecl]<void*, uint>)Pbutils.GetExport(nameof(gst_discoverer_audio_info_get_sample_rate));
        gst_discoverer_audio_info_get_channels = (delegate* unmanaged[Cdecl]<void*, uint>)Pbutils.GetExport(nameof(gst_discoverer_audio_info_get_channels));
        gst_discoverer_audio_info_get_bitrate = (delegate* unmanaged[Cdecl]<void*, uint>)Pbutils.GetExport(nameof(gst_discoverer_audio_info_get_bitrate));
        gst_discoverer_audio_info_get_language = (delegate* unmanaged[Cdecl]<void*, byte*>)Pbutils.GetExport(nameof(gst_discoverer_audio_info_get_language));
        gst_discoverer_subtitle_info_get_language = (delegate* unmanaged[Cdecl]<void*, byte*>)Pbutils.GetExport(nameof(gst_discoverer_subtitle_info_get_language));
        gst_tag_get_language_code_iso_639_2B = (delegate* unmanaged[Cdecl]<byte*, byte*>)Tag.GetExport(nameof(gst_tag_get_language_code_iso_639_2B));

        gst_pb_utils_init();
    }
}
