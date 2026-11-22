#region Copyright
// Copyright (c) 2026 Yaroslav V Tatarenko.
// Licensed under the MIT License. See LICENSE in the repository root for details.
#endregion

using MediaToolkitNet.Abstractions.Frames;
using MediaToolkitNet.Windows;

/// <summary>The <c>proc-audio</c> command: captures the sound of one process (Windows 10 2004 or newer).</summary>
internal static class ProcessAudioCommand
{
    public const string Usage = """
          proc-audio <pid> [seconds] [exclude]
                                       capture what one process plays (or everything but it) and print the peak level (Windows only)

        """;

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Process loopback is Windows only.");
            return 2;
        }

        return RunWindows(args);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int RunWindows(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out var pid))
        {
            Console.Error.WriteLine("Missing argument: process id.");
            return 1;
        }

        var seconds = args.Length > 2 && int.TryParse(args[2], out var s) ? s : 3;
        var mode = args.Length > 3 && args[3].Equals("exclude", StringComparison.OrdinalIgnoreCase)
            ? ProcessLoopbackMode.ExcludeProcessTree
            : ProcessLoopbackMode.IncludeProcessTree;

        using var capture = WasapiCapture.ForProcess(pid, mode);
        var frames = 0L;
        var peak = 0f;
        capture.FrameCaptured += (in AudioFrame frame) =>
        {
            var bytes = frame.GetPlane(0);
            for (var i = 0; i + 4 <= bytes.Length; i += 4)
            {
                peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(bytes.Slice(i, 4))));
            }

            frames += frame.SampleCount;
        };

        capture.Start();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        capture.Stop();

        Console.WriteLine($"{mode} {pid}: {capture.Format}, {frames} frames in {seconds} s, peak {peak:F3}");
        return 0;
    }
}
