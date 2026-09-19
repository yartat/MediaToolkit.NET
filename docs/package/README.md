# MediaToolkit.NET

Low-level wrappers over the native media stacks for .NET 10, plus one
cross-platform API on top of them: device enumeration, camera and microphone
capture, audio playback, decoding and recording.

Interop goes through `[LibraryImport]` and unmanaged function pointers. COM on
Windows is called directly through vtable slots, without `[ComImport]` or the
built-in COM marshaller, so the assemblies stay AOT- and trim-safe.

## Install

```bash
dotnet add package MediaToolkitNet.All
```

`MediaToolkitNet.All` pulls in every backend and selects one at runtime. Take a
single backend instead when you do not want the rest:

| Package | Contents |
|---|---|
| `MediaToolkitNet.All` | Facade: backend registry and per-platform selection |
| `MediaToolkitNet.Abstractions` | Abstractions only; no native code, no dependencies |
| `MediaToolkitNet.Interop` | Native loading, UTF-8 marshalling, COM vtable access |
| `MediaToolkitNet.FFmpeg` | FFmpeg 7.x, 8.x or 9.x: demux, decode, encode, mux |
| `MediaToolkitNet.Mpv` | libmpv client API |
| `MediaToolkitNet.Windows` | Media Foundation, WASAPI, DirectShow (devices, graph, Sample Grabber) |
| `MediaToolkitNet.Linux` | V4L2, ALSA, PulseAudio |
| `MediaToolkitNet.GStreamer` | GStreamer 1.x pipelines on Linux |
| `MediaToolkitNet.MacOS` | AVFoundation, CoreAudio |

## Quick start

```csharp
using MediaToolkitNet;
using MediaToolkitNet.Abstractions.Capture;
using MediaToolkitNet.Abstractions.Frames;

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

Assemble a processing graph. Each stack is wrapped in its own terms, because
the shapes are genuinely different:

```csharp
// libavfilter: the same chain the ffmpeg command line takes after -vf.
var input = new VideoFormat(1920, 1080, PixelFormat.Bgr24, 30);
using var chain = FFmpegFilterGraph.ForVideo(input, "scale=640:-1,hflip");

chain.Push(sourceFrame);
while (chain.TryReceive((in VideoFrame frame) => Save(frame)))
{
}
```

```csharp
// DirectShow: filters and pins, with frames handed back by the Sample Grabber.
using var graph = DirectShowGraph.Create();
var grabber = DirectShowSampleGrabber.AddTo(graph);
grabber.AcceptVideo(PixelFormat.Bgr24);
grabber.VideoGrabbed += (in VideoFrame frame) => Save(frame);
```

```csharp
// GStreamer: a whole pipeline from one gst-launch string.
using var pipeline = GStreamerPipeline.Parse(
    "filesrc location=clip.mp4 ! decodebin ! videoconvert ! appsink name=sink");
var sink = pipeline.GetSink("sink");
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
| Filter graph | DirectShow + Sample Grabber | GStreamer pipeline | — | libavfilter | `vf` / `af` chains |

A dash means the backend does not take that role: FFmpeg owns no audio output,
mpv never hands frames back, and on Linux and macOS writing files is the FFmpeg
backend's job.

## Requirements

- .NET 10, and a **64-bit** process: every mapped struct layout assumes a 64-bit
  pointer.
- **No native binary ships in any package.** The platform backends only call
  libraries that are already part of the operating system, but:
  - `MediaToolkitNet.FFmpeg` needs FFmpeg **7.x, 8.x or 9.x** at runtime. The
    loader reads the versions it actually got and refuses a mixture belonging to
    no single series. `libavfilter` is optional: without it only filter graphs
    are unavailable;
  - `MediaToolkitNet.Mpv` needs libmpv at runtime;
  - `MediaToolkitNet.GStreamer` needs GStreamer 1.x and its plugins, and binds
    the Linux library names, so it reports itself unavailable elsewhere.

A backend whose libraries are missing reports `IsAvailable == false` instead of
throwing, so probing is always safe.

## Status

This is an early release and the public API may still change.

Exercised against real hardware: on Windows, device enumeration, camera capture,
WASAPI audio capture, audio recording to AAC/MP4, and the DirectShow graph with
the Sample Grabber; the FFmpeg backend on Debian 13 against FFmpeg 7.1.5 and on
Windows 11 against FFmpeg 9.0.1, with every struct offset checked against the
library actually installed.

**Not run yet**: libavfilter, the mpv backend, the whole of the GStreamer
backend, the macOS backend, and the V4L2, ALSA and PulseAudio paths on Linux.
They compile and are guarded at runtime — a layout that cannot be confirmed
disables the feature rather than proceeding — but that is not the same as having
been run. See the release notes for the full picture before depending on it.

## Links

- [Source and documentation](https://github.com/yartat/MediaToolkit.NET)
- [Release notes](https://github.com/yartat/MediaToolkit.NET/releases)
- [Report an issue](https://github.com/yartat/MediaToolkit.NET/issues)

MIT licensed.
