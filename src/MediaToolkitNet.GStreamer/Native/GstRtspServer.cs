#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Runtime.Versioning;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer.Native;

/// <summary>
/// <c>libgstrtspserver-1.0</c> and the parts of GLib its main loop needs. The library is optional: it is loaded when a
/// server is first asked for.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe class GstRtspServer
{
    private static readonly Lock Gate = new();
    private static bool _attempted;
    private static bool _loaded;

    public static delegate* unmanaged[Cdecl]<void*> ServerNew;
    public static delegate* unmanaged[Cdecl]<void*, byte*, void> SetAddress;
    public static delegate* unmanaged[Cdecl]<void*, byte*, void> SetService;
    public static delegate* unmanaged[Cdecl]<void*, int> GetBoundPort;
    public static delegate* unmanaged[Cdecl]<void*, void*> GetMountPoints;
    public static delegate* unmanaged[Cdecl]<void*, void*, uint> Attach;
    public static delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, void*, void*, int>, void*, void*> ClientFilter;
    public static delegate* unmanaged[Cdecl]<void*> FactoryNew;
    public static delegate* unmanaged[Cdecl]<void*, byte*, void> SetLaunch;
    public static delegate* unmanaged[Cdecl]<void*, int, void> SetShared;
    public static delegate* unmanaged[Cdecl]<void*, int, void> SetProtocols;
    public static delegate* unmanaged[Cdecl]<void*, void*> MediaGetElement;
    public static delegate* unmanaged[Cdecl]<void*, byte*, void*, void> AddFactory;
    public static delegate* unmanaged[Cdecl]<void*, byte*, void> RemoveFactory;

    public static delegate* unmanaged[Cdecl]<void*> ContextNew;
    public static delegate* unmanaged[Cdecl]<void*, void> ContextUnref;
    public static delegate* unmanaged[Cdecl]<void*, uint, void*> ContextFindSource;
    public static delegate* unmanaged[Cdecl]<void*, int, void*> LoopNew;
    public static delegate* unmanaged[Cdecl]<void*, void> LoopRun;
    public static delegate* unmanaged[Cdecl]<void*, void> LoopQuit;
    public static delegate* unmanaged[Cdecl]<void*, int> LoopIsRunning;
    public static delegate* unmanaged[Cdecl]<void*, void> LoopUnref;
    public static delegate* unmanaged[Cdecl]<void*, void> SourceDestroy;

    public static bool IsAvailable
    {
        get
        {
            Load();
            return _loaded;
        }
    }

    public static void Require()
    {
        Load();
        if (!_loaded)
        {
            throw new NotSupportedException("gst-rtsp-server (libgstrtspserver-1.0) is not installed.");
        }
    }

    private static void Load()
    {
        lock (Gate)
        {
            if (_attempted)
            {
                return;
            }

            _attempted = true;
            if (!Gst.IsAvailable || !NativeModule.TryLoad(["libgstrtspserver-1.0.so.0", "libgstrtspserver-1.0.so"], out var server))
            {
                return;
            }

            ServerNew = (delegate* unmanaged[Cdecl]<void*>)server.GetExport("gst_rtsp_server_new");
            SetAddress = (delegate* unmanaged[Cdecl]<void*, byte*, void>)server.GetExport("gst_rtsp_server_set_address");
            SetService = (delegate* unmanaged[Cdecl]<void*, byte*, void>)server.GetExport("gst_rtsp_server_set_service");
            GetBoundPort = (delegate* unmanaged[Cdecl]<void*, int>)server.GetExport("gst_rtsp_server_get_bound_port");
            GetMountPoints = (delegate* unmanaged[Cdecl]<void*, void*>)server.GetExport("gst_rtsp_server_get_mount_points");
            Attach = (delegate* unmanaged[Cdecl]<void*, void*, uint>)server.GetExport("gst_rtsp_server_attach");
            ClientFilter = (delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, void*, void*, int>, void*, void*>)server.GetExport("gst_rtsp_server_client_filter");
            FactoryNew = (delegate* unmanaged[Cdecl]<void*>)server.GetExport("gst_rtsp_media_factory_new");
            SetLaunch = (delegate* unmanaged[Cdecl]<void*, byte*, void>)server.GetExport("gst_rtsp_media_factory_set_launch");
            SetShared = (delegate* unmanaged[Cdecl]<void*, int, void>)server.GetExport("gst_rtsp_media_factory_set_shared");
            SetProtocols = (delegate* unmanaged[Cdecl]<void*, int, void>)server.GetExport("gst_rtsp_media_factory_set_protocols");
            MediaGetElement = (delegate* unmanaged[Cdecl]<void*, void*>)server.GetExport("gst_rtsp_media_get_element");
            AddFactory = (delegate* unmanaged[Cdecl]<void*, byte*, void*, void>)server.GetExport("gst_rtsp_mount_points_add_factory");
            RemoveFactory = (delegate* unmanaged[Cdecl]<void*, byte*, void>)server.GetExport("gst_rtsp_mount_points_remove_factory");

            ContextNew = (delegate* unmanaged[Cdecl]<void*>)Gst.GLib.GetExport("g_main_context_new");
            ContextUnref = (delegate* unmanaged[Cdecl]<void*, void>)Gst.GLib.GetExport("g_main_context_unref");
            ContextFindSource = (delegate* unmanaged[Cdecl]<void*, uint, void*>)Gst.GLib.GetExport("g_main_context_find_source_by_id");
            LoopNew = (delegate* unmanaged[Cdecl]<void*, int, void*>)Gst.GLib.GetExport("g_main_loop_new");
            LoopRun = (delegate* unmanaged[Cdecl]<void*, void>)Gst.GLib.GetExport("g_main_loop_run");
            LoopQuit = (delegate* unmanaged[Cdecl]<void*, void>)Gst.GLib.GetExport("g_main_loop_quit");
            LoopIsRunning = (delegate* unmanaged[Cdecl]<void*, int>)Gst.GLib.GetExport("g_main_loop_is_running");
            LoopUnref = (delegate* unmanaged[Cdecl]<void*, void>)Gst.GLib.GetExport("g_main_loop_unref");
            SourceDestroy = (delegate* unmanaged[Cdecl]<void*, void>)Gst.GLib.GetExport("g_source_destroy");
            _loaded = true;
        }
    }
}
