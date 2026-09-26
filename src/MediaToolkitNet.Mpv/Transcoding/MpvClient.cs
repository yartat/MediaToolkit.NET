#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Globalization;
using MediaToolkitNet.Abstractions;
using MediaToolkitNet.Interop;
using MediaToolkitNet.Mpv.Native;

namespace MediaToolkitNet.Mpv.Transcoding;

/// <summary>
/// One short-lived mpv instance: options, one file, events until it is done.
/// The prober and the transcoder each run one per call.
/// </summary>
internal sealed unsafe class MpvClient : IDisposable
{
    private void* _handle;

    private MpvClient(void* handle) => _handle = handle;

    /// <summary>Creates an instance with the given options and initialises it.</summary>
    /// <remarks>
    /// An option named with the <c>-append</c> suffix, such as
    /// <c>sub-files-append</c>, is what the command line takes, but
    /// <c>mpv_set_option_string</c> knows no suffixes and answers "option not
    /// found". Those are applied after initialising with
    /// <c>change-list &lt;list&gt; append &lt;value&gt;</c>, which takes the
    /// value as one argument, so a path needs no escaping.
    /// </remarks>
    public static MpvClient Create(IEnumerable<KeyValuePair<string, string>> options)
    {
        const string append = "-append";

        Native.Mpv.EnsureLoaded();
        var handle = Native.Mpv.mpv_create();
        if (handle is null)
        {
            throw new MediaToolkitNetException(Native.Mpv.BackendName, "mpv_create returned NULL", 0);
        }

        var client = new MpvClient(handle);
        try
        {
            var appended = new List<KeyValuePair<string, string>>();
            foreach (var (name, value) in options)
            {
                if (name.EndsWith(append, StringComparison.Ordinal))
                {
                    appended.Add(new(name[..^append.Length], value));
                }
                else
                {
                    client.SetOption(name, value);
                }
            }

            Native.Mpv.Check(Native.Mpv.mpv_initialize(handle), "mpv_initialize");
            foreach (var (list, value) in appended)
            {
                client.Command("change-list", list, "append", value);
            }

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Runs a command, for example <c>loadfile</c>.</summary>
    public void Command(params string[] arguments)
    {
        var native = stackalloc byte*[arguments.Length + 1];
        try
        {
            for (var i = 0; i < arguments.Length; i++)
            {
                native[i] = Utf8.Allocate(arguments[i]);
            }

            native[arguments.Length] = null;
            Native.Mpv.Check(Native.Mpv.mpv_command(_handle, native), $"mpv_command({arguments[0]})");
        }
        finally
        {
            for (var i = 0; i < arguments.Length; i++)
            {
                Utf8.Free(native[i]);
            }
        }
    }

    /// <summary>Asks for log messages at this level and above to arrive as events.</summary>
    public void RequestLogMessages(string minimumLevel)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var level = new Utf8Scoped(minimumLevel, scratch);
        Native.Mpv.Check(Native.Mpv.mpv_request_log_messages(_handle, level.Pointer), "mpv_request_log_messages");
    }

    /// <summary>The text of a log-message event, as <c>prefix: text</c>.</summary>
    public static string TextOfLogMessage(MpvEvent* evt)
    {
        if (evt->Data is null)
        {
            return string.Empty;
        }

        // mpv_event_log_message: { const char *prefix; const char *level; const char *text; int log_level; }
        var raw = (byte**)evt->Data;
        return $"{Utf8.ToManagedOrEmpty(raw[0])}: {Utf8.ToManagedOrEmpty(raw[2]).TrimEnd('\n')}";
    }

    /// <summary>Waits for the next event, up to a timeout.</summary>
    public MpvEvent* WaitEvent(TimeSpan timeout) => Native.Mpv.mpv_wait_event(_handle, timeout.TotalSeconds);

    /// <summary>Reads a property as mpv formats it, or <see langword="null"/> when it has none.</summary>
    public string? Get(string name)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var utf8 = new Utf8Scoped(name, scratch);
        var value = Native.Mpv.mpv_get_property_string(_handle, utf8.Pointer);
        if (value is null)
        {
            return null;
        }

        try
        {
            return Utf8.ToManagedOrEmpty(value);
        }
        finally
        {
            Native.Mpv.mpv_free(value);
        }
    }

    /// <summary>Reads a numeric property, or <see langword="null"/> when it has none.</summary>
    public double? GetNumber(string name) =>
        double.TryParse(Get(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Reads a yes/no property.</summary>
    public bool GetFlag(string name) => Get(name) == "yes";

    /// <inheritdoc />
    /// <remarks>
    /// In encoding mode this is also what finishes the output: mpv writes the
    /// trailer while it shuts down, so the file is complete once this returns.
    /// </remarks>
    public void Dispose()
    {
        if (_handle is not null)
        {
            Native.Mpv.mpv_terminate_destroy(_handle);
            _handle = null;
        }
    }

    private void SetOption(string name, string value)
    {
        Span<byte> nameScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        Span<byte> valueScratch = stackalloc byte[Utf8Scoped.StackThreshold];
        using var nameUtf8 = new Utf8Scoped(name, nameScratch);
        using var valueUtf8 = new Utf8Scoped(value, valueScratch);
        var result = Native.Mpv.mpv_set_option_string(_handle, nameUtf8.Pointer, valueUtf8.Pointer);
        if (result < 0)
        {
            throw new MediaToolkitNetException(
                Native.Mpv.BackendName,
                name is "o" or "ovc" or "oac" or "of"
                    ? $"mpv refused the option {name}={value}: {Native.Mpv.Describe(result)}; this libmpv may have been built without encoding support"
                    : $"mpv refused the option {name}={value}: {Native.Mpv.Describe(result)}",
                result);
        }
    }
}
