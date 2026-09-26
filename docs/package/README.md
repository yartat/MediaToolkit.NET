# MediaToolkit.NET

Low-level wrappers over the native media stacks for .NET 10, plus one
cross-platform API on top of them: device enumeration, camera and microphone
capture, audio playback, decoding, recording, probing and transcoding.

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
| `MediaToolkitNet.FFmpeg` | FFmpeg 7.x, 8.x or 9.x: demux, decode, encode, mux, filter graphs, probing, transcoding, frame reader |
| `MediaToolkitNet.Mpv` | libmpv client API: playback, filter chains, probing, and its encoding mode as a transcoder |
| `MediaToolkitNet.Windows` | Media Foundation, WASAPI, DirectShow (devices, graph, Sample Grabber) |
| `MediaToolkitNet.Linux` | V4L2, ALSA, PulseAudio |
| `MediaToolkitNet.GStreamer` | GStreamer 1.x on Linux: pipelines, appsink, GstDiscoverer probing, transcoding |
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

## Convert, probe and read files

A conversion is described once, as a `TranscodeRequest`: which streams of which
inputs, copied or re-encoded, subtitles converted or burned in, a trim, tags and
chapters. The first backend that can do all of it runs the job — FFmpeg, then
GStreamer, then mpv — and one that cannot says why before anything is written.

```csharp
using MediaToolkitNet.Abstractions.Recording;
using MediaToolkitNet.Abstractions.Transcoding;

// Every stream copied into another container.
await MediaToolkitNetBackends.TranscodeAsync(TranscodeRequest.Remux("in.mkv", "out.mp4"));

// A minute of it, re-encoded, with subtitles from another file burned in.
await MediaToolkitNetBackends.TranscodeAsync(new TranscodeRequest("clip.mp4")
{
    Inputs = ["in.mkv", "in.en.srt"],
    Start = TimeSpan.FromMinutes(1),
    End = TimeSpan.FromMinutes(2),
    Streams =
    [
        OutputStream.Video(StreamSource.First(MediaStreamKind.Video),
            new VideoOutputSettings(MediaCodec.H264) { Width = 1280, Quality = 23 }),
        OutputStream.Audio(StreamSource.First(MediaStreamKind.Audio, language: "eng"),
            new AudioOutputSettings(MediaCodec.Aac) { BitrateBitsPerSecond = 160_000 }),
        OutputStream.BurnIn(StreamSource.First(MediaStreamKind.Subtitle, input: 1),
            StreamSource.First(MediaStreamKind.Video)),
    ],
});
```

A specific backend can be asked directly, and asked first whether it can:

```csharp
var transcoder = new FFmpegTranscoder();
foreach (var issue in transcoder.Validate(request))
{
    Console.WriteLine(issue);                    // "error: ..." or "warning: ..."
}

await transcoder.RunAsync(request, new Progress<TranscodeProgress>(p => Console.Write($"\r{p.Fraction:P0}")));
```

The backends differ a good deal:

| | FFmpeg | GStreamer | mpv |
|---|:---:|:---:|:---:|
| Stream copy, container change | yes | video and audio | — |
| Re-encode video / audio | yes | yes | one track each |
| Several audio or video streams | yes | yes | — |
| Subtitle copy / conversion | yes | — | — |
| Subtitle burn-in | text (libass) | text | yes |
| Several inputs | yes | yes | external audio and subtitles |
| Trim, tags | yes | yes | yes |
| Chapters | yes | Matroska only, untrimmed | — |

Probing reads the container, streams (codec, language, title, default and forced
flags), tags and chapters, and numbers streams the same way on every backend.
`FFmpegMediaReader` reads decoded frames, in order or at a time, converted to the
format asked for:

```csharp
MediaInfo info = MediaToolkitNetBackends.Probe("in.mkv");

using var reader = FFmpegMediaReader.Open("in.mkv",
    new MediaReaderOptions { VideoPixelFormat = PixelFormat.Bgra32, VideoWidth = 640, VideoHeight = 360 });
reader.Video!.TryReadAt(TimeSpan.FromSeconds(12), (in VideoFrame frame) => Save(frame));

foreach (var cue in reader.SubtitleStreams[0].ReadAll())
{
    Console.WriteLine($"{cue.Start} {cue.Text}");
}
```

## Processing graphs

Each stack is wrapped in its own terms, because the shapes are genuinely
different:

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
| Probing a file | — | GstDiscoverer | — | libavformat | track list |
| Transcoding | — | GStreamer pipeline | — | full | encoding mode |
| Reading frames from a file | — | — | — | `FFmpegMediaReader` | — |

A dash means the backend does not take that role: FFmpeg owns no audio output,
mpv never hands frames back, and on Linux and macOS writing files is the FFmpeg
backend's job.

## Requirements

- .NET 10, and a **64-bit** process: every mapped struct layout assumes a 64-bit
  pointer.
- **No native binary ships in any package.** The platform backends only call
  libraries that are already part of the operating system, but:
  - `MediaToolkitNet.FFmpeg` needs FFmpeg **7.x, 8.x or 9.x** as shared
    libraries. The loader reads the versions it actually got and refuses a
    mixture belonging to no single series. `libavfilter` is optional: without it
    filter graphs and re-encoding transcodes are unavailable;
  - `MediaToolkitNet.Mpv` needs libmpv (`libmpv-2.dll`, `libmpv.so.2`), built
    with encoding support for transcoding;
  - `MediaToolkitNet.GStreamer` needs GStreamer 1.x and its plugins, and binds
    the Linux library names, so it reports itself unavailable elsewhere. Probing
    needs gst-plugins-base (`libgstpbutils`).
- Native libraries are looked for in any directory given to
  `NativeSearchPaths.Prepend`, then beside the application, in its `native` and
  `runtimes/<rid>/native` folders, and finally where the operating system looks
  (on Windows, the `PATH`).

A backend whose libraries are missing reports `IsAvailable == false` instead of
throwing, so probing is always safe.

## Status

This is an early release and the public API may still change.

Exercised against real hardware:

- on Windows 11: device enumeration, camera capture, WASAPI audio capture,
  recording to H.264, HEVC and AAC through Media Foundation, the DirectShow
  graph with the Sample Grabber, and FFmpeg and libmpv transcoding;
- the FFmpeg backend against FFmpeg 7.1.5 on Debian 13 and Windows 11, and
  against FFmpeg 9.0.1 on Windows 11, with every struct offset checked against
  the library actually installed;
- libavfilter, FFmpeg transcoding and the frame reader, and probing and
  transcoding through GStreamer 1.26.2 and libmpv 2.5, on Debian 13.

**Not run yet**: mpv playback, FFmpeg 8 and 9 transcoding, the macOS backend,
and the V4L2, ALSA and PulseAudio paths on Linux. They compile and are guarded
at runtime — a layout that cannot be confirmed disables the feature rather than
proceeding — but that is not the same as having been run. See the release notes
for the full picture before depending on it.

## Links

- [Source and documentation](https://github.com/yartat/MediaToolkit.NET)
- [Release notes](https://github.com/yartat/MediaToolkit.NET/releases)
- [Report an issue](https://github.com/yartat/MediaToolkit.NET/issues)

MIT licensed.
