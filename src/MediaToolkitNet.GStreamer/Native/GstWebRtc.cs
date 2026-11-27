#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer.Native;

/// <summary>
/// <c>libgstwebrtc-1.0</c>, <c>libgstsdp-1.0</c> and the promises <c>webrtcbin</c> answers with. The libraries are
/// optional: they are loaded when a peer is first asked for.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe class GstWebRtc
{
    /// <summary>Offset of <c>GstWebRTCSessionDescription::sdp</c>; the type comes first.</summary>
    public const int DescriptionSdp = 8;

    private static readonly Lock Gate = new();
    private static bool _attempted;
    private static bool _loaded;

    public static delegate* unmanaged[Cdecl]<int, void*, void*> DescriptionNew;
    public static delegate* unmanaged[Cdecl]<void*, void> DescriptionFree;
    public static delegate* unmanaged[Cdecl]<void**, int> SdpNew;
    public static delegate* unmanaged[Cdecl]<byte*, uint, void*, int> SdpParse;
    public static delegate* unmanaged[Cdecl]<void*, byte*> SdpAsText;
    public static delegate* unmanaged[Cdecl]<void*, int> SdpFree;
    public static delegate* unmanaged[Cdecl]<void*> PromiseNew;
    public static delegate* unmanaged[Cdecl]<void*, int> PromiseWait;
    public static delegate* unmanaged[Cdecl]<void*, void*> PromiseGetReply;
    public static delegate* unmanaged[Cdecl]<void*, void> PromiseInterrupt;

    public static bool IsAvailable
    {
        get
        {
            lock (Gate)
            {
                if (_attempted)
                {
                    return _loaded;
                }

                _attempted = true;
                if (!Gst.IsAvailable ||
                    !NativeModule.TryLoad(["libgstwebrtc-1.0.so.0", "libgstwebrtc-1.0.so"], out var webrtc) ||
                    !NativeModule.TryLoad(["libgstsdp-1.0.so.0", "libgstsdp-1.0.so"], out var sdp))
                {
                    return false;
                }

                DescriptionNew = (delegate* unmanaged[Cdecl]<int, void*, void*>)webrtc.GetExport("gst_webrtc_session_description_new");
                DescriptionFree = (delegate* unmanaged[Cdecl]<void*, void>)webrtc.GetExport("gst_webrtc_session_description_free");
                SdpNew = (delegate* unmanaged[Cdecl]<void**, int>)sdp.GetExport("gst_sdp_message_new");
                SdpParse = (delegate* unmanaged[Cdecl]<byte*, uint, void*, int>)sdp.GetExport("gst_sdp_message_parse_buffer");
                SdpAsText = (delegate* unmanaged[Cdecl]<void*, byte*>)sdp.GetExport("gst_sdp_message_as_text");
                SdpFree = (delegate* unmanaged[Cdecl]<void*, int>)sdp.GetExport("gst_sdp_message_free");
                PromiseNew = (delegate* unmanaged[Cdecl]<void*>)Gst.Core.GetExport("gst_promise_new");
                PromiseWait = (delegate* unmanaged[Cdecl]<void*, int>)Gst.Core.GetExport("gst_promise_wait");
                PromiseGetReply = (delegate* unmanaged[Cdecl]<void*, void*>)Gst.Core.GetExport("gst_promise_get_reply");
                PromiseInterrupt = (delegate* unmanaged[Cdecl]<void*, void>)Gst.Core.GetExport("gst_promise_interrupt");
                _loaded = true;
                return true;
            }
        }
    }
}
