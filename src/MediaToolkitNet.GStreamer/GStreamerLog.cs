#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaToolkitNet.GStreamer.Native;
using MediaToolkitNet.Interop;

namespace MediaToolkitNet.GStreamer;

/// <summary>One line of GStreamer's debug log.</summary>
/// <param name="Level">How important it is.</param>
/// <param name="Category">The debug category, often the element's name such as <c>videotestsrc</c>.</param>
/// <param name="Message">What was logged.</param>
/// <param name="File">The native source file that logged it.</param>
/// <param name="Function">The native function.</param>
/// <param name="Line">The line in that file.</param>
public readonly record struct GStreamerLogMessage(
    GstDebugLevel Level, string Category, string Message, string File, string Function, int Line);

/// <summary>A subscription to GStreamer's debug log. Disposing it ends it.</summary>
/// <remarks>
/// The log belongs to the process: thresholds set for one subscription are what every subscriber and GStreamer's own
/// printing see. The handler runs on whatever thread logged, often a streaming thread, so it should be quick; what it
/// throws is ignored, and what GStreamer logs because of the handler itself is not delivered to it.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe class GStreamerLog : IDisposable
{
    // GStreamer can still be inside a log function while it is being removed, so the native side is given a number to
    // look up rather than a handle that could be freed under it.
    private static readonly ConcurrentDictionary<nint, Action<GStreamerLogMessage>> Handlers = new();
    private static readonly Lock Gate = new();
    private static nint _next;
    private static int _silencing;
    private static bool _removedPrinter;

    [ThreadStatic]
    private static bool _delivering;

    private readonly nint _id;
    private readonly string[] _categories;
    private readonly int _previousDefault;
    private readonly bool _silence;
    private bool _disposed;

    private GStreamerLog(nint id, string[] categories, int previousDefault, bool silence)
    {
        _id = id;
        _categories = categories;
        _previousDefault = previousDefault;
        _silence = silence;
    }

    /// <summary>Delivers the log to <paramref name="handler"/>.</summary>
    /// <param name="thresholds">
    /// What to log, in the form of the <c>GST_DEBUG</c> variable: <c>videotestsrc:6,*:2</c>. A category logs messages up
    /// to its level, 1 for errors only and 6 for nearly everything.
    /// </param>
    /// <param name="handler">Receives each message.</param>
    /// <param name="silenceStandardError">
    /// Whether to stop GStreamer printing the log to standard error while subscribed, which is the default.
    /// </param>
    public static GStreamerLog Subscribe(string thresholds, Action<GStreamerLogMessage> handler, bool silenceStandardError = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thresholds);
        ArgumentNullException.ThrowIfNull(handler);
        Gst.EnsureLoaded();

        lock (Gate)
        {
            if (silenceStandardError && _silencing++ == 0)
            {
                _removedPrinter = Gst.gst_debug_remove_log_function((void*)Gst.DefaultLogFunction) > 0;
            }

            var id = ++_next;
            Handlers[id] = handler;
            var previous = Gst.gst_debug_get_default_threshold();
            Gst.gst_debug_set_active(1);
            Gst.gst_debug_add_log_function(&OnLog, (void*)id, null);

            Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
            using var utf8 = new Utf8Scoped(thresholds, scratch);
            Gst.gst_debug_set_threshold_from_string(utf8.Pointer, 0);

            var categories = thresholds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(entry => entry.Contains(':'))
                .Select(entry => entry[..entry.IndexOf(':')])
                .Where(name => name != "*")
                .ToArray();
            return new GStreamerLog(id, categories, previous, silenceStandardError);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Gst.gst_debug_remove_log_function_by_data((void*)_id);
            Handlers.TryRemove(_id, out _);

            foreach (var category in _categories)
            {
                Unset(category);
            }

            Gst.gst_debug_set_default_threshold(_previousDefault);

            if (_silence && --_silencing == 0 && _removedPrinter)
            {
                Gst.gst_debug_add_log_function(
                    (delegate* unmanaged[Cdecl]<void*, int, byte*, byte*, int, void*, void*, void*, void>)Gst.DefaultLogFunction, null, null);
            }
        }
    }

    private static void Unset(string category)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(category, scratch);
        Gst.gst_debug_unset_threshold_for_name(utf8.Pointer);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(void* category, int level, byte* file, byte* function, int line, void* source, void* message, void* data)
    {
        try
        {
            if (_delivering || !Handlers.TryGetValue((nint)data, out var handler))
            {
                return;
            }

            _delivering = true;
            try
            {
                handler(new GStreamerLogMessage(
                    (GstDebugLevel)level,
                    Utf8.ToManagedOrEmpty(Gst.gst_debug_category_get_name(category)),
                    Utf8.ToManagedOrEmpty(Gst.gst_debug_message_get(message)),
                    Utf8.ToManagedOrEmpty(file),
                    Utf8.ToManagedOrEmpty(function),
                    line));
            }
            finally
            {
                _delivering = false;
            }
        }
        catch
        {
            // Nothing may throw into GStreamer, and logging the failure would log again.
        }
    }
}
