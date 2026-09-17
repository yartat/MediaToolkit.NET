# MediaToolkit.NET

Low-level wrappers over the native media stacks for .NET 10, plus one
cross-platform API on top of them: device enumeration, camera and microphone
capture, audio playback, decoding and recording.

Interop goes through `[LibraryImport]` and unmanaged function pointers. COM on
Windows is called directly through vtable slots, without `[ComImport]` or the
built-in COM marshaller, so the assemblies stay AOT- and trim-safe.

## Install

```bash
dotnet add package MediaToolkitNet
```

`MediaToolkitNet` pulls in every backend and selects one at runtime. Take a
single backend instead when you do not want the rest:

| Package | Contents |
|---|---|
| `MediaToolkitNet` | Facade: backend registry and per-platform selection |
| `MediaToolkitNet.Core` | Abstractions only; no native code, no dependencies |
| `MediaToolkitNet.Interop` | Native loading, UTF-8 marshalling, COM vtable access |
| `MediaToolkitNet.FFmpeg` | FFmpeg 7.x: demux, decode, encode, mux |
| `MediaToolkitNet.Mpv` | libmpv client API |
| `MediaToolkitNet.Windows` | Media Foundation, WASAPI, DirectShow |
| `MediaToolkitNet.Linux` | V4L2, ALSA, PulseAudio |
| `MediaToolkitNet.MacOS` | AVFoundation, CoreAudio |

## Quick start

```csharp
using MediaToolkitNet;
using MediaToolkitNet.Core.Capture;
using MediaToolkitNet.Core.Frames;

// Whichever backend this machine actually has.
using var capture = MediaToolkitNetBackends.CreateVideoCapture(VideoCaptureSettings.Default);

capture.FrameCaptured += (in VideoFrame frame) =>
{
    // The frame is borrowed: it is valid only inside this handler.
    // Copy the data out if you need to keep it.
    ReadOnlySpan<byte> luma = frame.GetPlane(0);
};

capture.Start();
```

List what a machine can do:

```csharp
foreach (var backend in MediaToolkitNetBackends.Registered)
{
    Console.WriteLine($"{backend.Name}: {backend.IsAvailable}, {backend.Capabilities}");
}

foreach (var device in MediaToolkitNetBackends.EnumerateDevices())
{
    Console.WriteLine($"{device.Kind} {device.Name}");
}
```

## What each backend provides

| Capability | Windows | Linux | macOS | FFmpeg | mpv |
|---|:---:|:---:|:---:|:---:|:---:|
| Device enumeration | MF + WASAPI + DShow | ALSA + V4L2 | CoreAudio + AVF | avdevice | — |
| Video capture | `IMFSourceReader` | V4L2 mmap | `AVCaptureSession` | — | — |
| Audio capture | WASAPI (+ loopback) | ALSA / PulseAudio | AudioQueue | — | — |
| Audio render | WASAPI | ALSA / PulseAudio | AudioQueue | — | — |
| Playback | — | — | `AVPlayer` | decode to frames | full |
| Recording to file | `IMFSinkWriter` | — | — | encode + mux | — |

A dash means the backend does not take that role: FFmpeg owns no audio output,
mpv never hands frames back, and on Linux and macOS writing files is the FFmpeg
backend's job.

## Requirements

- .NET 10, and a **64-bit** process: every mapped struct layout assumes a 64-bit
  pointer.
- **No native binary ships in any package.** The platform backends only call
  libraries that are already part of the operating system, but:
  - `MediaToolkitNet.FFmpeg` needs FFmpeg **7.x** at runtime
    (avutil 59 / avcodec 61 / avformat 61);
  - `MediaToolkitNet.Mpv` needs libmpv at runtime.

A backend whose libraries are missing reports `IsAvailable == false` instead of
throwing, so probing is always safe.

## Status

This is an early release. On Windows, device enumeration, camera capture, WASAPI
audio capture and audio recording to AAC/MP4 have been exercised against real
hardware. **The Linux, macOS, FFmpeg and mpv backends compile but have not been
run yet**, and the public API may still change. See the release notes for the
full picture before depending on it.

## Links

- [Source and documentation](https://github.com/yartat/MediaToolkit.NET)
- [Release notes](https://github.com/yartat/MediaToolkit.NET/releases)
- [Report an issue](https://github.com/yartat/MediaToolkit.NET/issues)

MIT licensed.
