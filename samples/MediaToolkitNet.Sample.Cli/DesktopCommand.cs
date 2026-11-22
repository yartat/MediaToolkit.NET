#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using System.Buffers.Binary;
using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Windows;

/// <summary>The <c>desktop</c> command: lists the monitors and captures one with Desktop Duplication (Windows).</summary>
internal static class DesktopCommand
{
    public const string Usage = """
          desktop [output] [seconds] [file.bmp]
                                       list the monitors, capture one and count the frames; save the first as a BMP (Windows only)

        """;

    public const string WindowUsage = """
          window [list | <title part | 0xHWND | monitor:N>] [seconds] [file.bmp] [--no-cursor] [--no-border]
                                       list the windows, or capture one (or a monitor) with Windows.Graphics.Capture (Windows only)

        """;

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Desktop Duplication is Windows only.");
            return 2;
        }

        return RunWindows(args);
    }

    public static int RunWindow(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Windows.Graphics.Capture is Windows only.");
            return 2;
        }

        return CaptureWindow(args);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int CaptureWindow(string[] args)
    {
        if (!GraphicsCapture.IsSupported)
        {
            Console.Error.WriteLine("This Windows cannot capture windows this way (it needs Windows 10 version 1903).");
            return 2;
        }

        var flags = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var rest = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var windows = GraphicsCapture.GetWindows();
        if (rest.Count == 0 || rest[0] == "list")
        {
            foreach (var w in windows)
            {
                Console.WriteLine($"  0x{w.Handle:X} pid {w.ProcessId} {w.Width}x{w.Height} at {w.X},{w.Y}  {w.Title}");
            }

            return 0;
        }

        var cursor = !flags.Contains("--no-cursor");
        var border = !flags.Contains("--no-border");
        using var capture = rest[0].StartsWith("monitor:", StringComparison.Ordinal)
            ? GraphicsCapture.ForMonitor(int.Parse(rest[0]["monitor:".Length..]), 30, cursor, border)
            : GraphicsCapture.ForWindow(Find(windows, rest[0]), 30, cursor, border);
        var seconds = rest.Count > 1 && int.TryParse(rest[1], out var s) ? s : 3;
        var file = rest.Count > 2 ? rest[2] : null;

        var frames = 0;
        var saved = false;
        var sizes = new HashSet<string>();
        capture.Closed += (_, _) => Console.WriteLine("the window was closed");
        capture.FrameCaptured += (in VideoFrame frame) =>
        {
            frames++;
            sizes.Add($"{frame.Format.Width}x{frame.Format.Height}");
            if (file is not null && !saved)
            {
                SaveBmp(file, frame);
                saved = true;
            }
        };

        capture.Start();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        capture.Stop();

        Console.WriteLine($"{capture.Title}: {frames} frames in {seconds} s, sizes {string.Join(", ", sizes)} (a still window sends one at the start, then only changes)");
        return frames > 0 ? 0 : 3;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static nint Find(IReadOnlyList<CaptureWindow> windows, string what)
    {
        if (what.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(what[2..], System.Globalization.NumberStyles.HexNumber, null, out var handle))
        {
            return (nint)handle;
        }

        return windows.FirstOrDefault(w => w.Title.Contains(what, StringComparison.OrdinalIgnoreCase))?.Handle
            ?? throw new ArgumentException($"No window has \"{what}\" in its title; `window list` shows them.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int RunWindows(string[] args)
    {
        foreach (var output in DesktopCapture.GetOutputs())
        {
            Console.WriteLine($"  #{output.Index} {output.Name} {output.Width}x{output.Height} at {output.X},{output.Y} rotation {output.Rotation}");
        }

        var index = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 0;
        var seconds = args.Length > 2 && int.TryParse(args[2], out var s) ? s : 3;
        var file = args.Length > 3 ? args[3] : null;

        using var capture = new DesktopCapture(index);
        var frames = 0;
        var saved = false;
        capture.FrameCaptured += (in VideoFrame frame) =>
        {
            frames++;
            if (file is not null && !saved)
            {
                SaveBmp(file, frame);
                saved = true;
            }
        };

        capture.Start();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        capture.Stop();

        Console.WriteLine($"{capture.Format.Width}x{capture.Format.Height}: {frames} frames in {seconds} s (a still desktop sends one at the start, then only changes)");
        return frames > 0 ? 0 : 3;
    }

    private static void SaveBmp(string path, in VideoFrame frame)
    {
        var width = frame.Format.Width;
        var height = frame.Format.Height;
        var stride = frame.Stride(0);
        var pixels = frame.GetPlane(0);
        using var stream = File.Create(path);
        var header = new byte[54];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2), 54 + (width * height * 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(22), -height); // top-down
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(28), 32);
        stream.Write(header);
        for (var y = 0; y < height; y++)
        {
            stream.Write(pixels.Slice(y * stride, width * 4));
        }
    }
}
