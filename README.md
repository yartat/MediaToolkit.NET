# MediaToolkit.NET

Low-level wrappers over the native media stacks for .NET 10, plus one
cross-platform API on top of them.

All interop goes through `[LibraryImport]` and unmanaged function pointers. COM
on Windows is called **directly through vtable slots**
(`delegate* unmanaged[Stdcall]`), without `[ComImport]` or the built-in COM
marshaller: the exact signature stays visible at the call site, and the code
remains AOT- and trim-safe.

## Layout

| Project | Wraps |
|---|---|
| `MediaToolkitNet.Abstractions` | Abstractions: devices, formats, frames, player, capture, recording. No dependencies. |
| `MediaToolkitNet.Interop` | Native library loading, UTF-8, `ComPtr` and vtable access. |
| `MediaToolkitNet.FFmpeg` | libavformat / libavcodec / libavutil / libswscale / libswresample / libavdevice |
| `MediaToolkitNet.Mpv` | libmpv (client API) |
| `MediaToolkitNet.Windows` | Media Foundation, WASAPI, DirectShow (devices, filter graph, Sample Grabber) |
| `MediaToolkitNet.Linux` | ALSA, PulseAudio (simple API), V4L2 |
| `MediaToolkitNet.GStreamer` | GStreamer 1.x: pipeline, element registry, appsink |
| `MediaToolkitNet.MacOS` | AVFoundation through the Objective-C runtime, CoreAudio / AudioQueue |
| `MediaToolkitNet.All` | Facade: backend registry and per-platform selection |
| `samples/MediaToolkitNet.Sample.Cli` | Demonstrates every scenario |

## What is implemented

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

## Getting started

```bash
dotnet build
```

```bash
dotnet run --project samples/MediaToolkitNet.Sample.Cli -- backends
```

```csharp
// Capture camera frames with whichever backend this machine has.
using var capture = MediaToolkitNetBackends.CreateVideoCapture(VideoCaptureSettings.Default);
capture.FrameCaptured += (in VideoFrame frame) =>
{
    // The frame is only alive inside this handler; copy the data if you need it later.
    var plane = frame.GetPlane(0);
};
capture.Start();
```

```csharp
// Write five point one Dolby Digital into a Matroska audio file. The layout is
// stated because six channels alone do not say where the speakers are, and the
// encoder is told which of the two five point one arrangements this is.
using var recorder = MediaToolkitNetBackends.CreateRecorder("surround.mka");
var format = new AudioFormat(48000, 6, SampleFormat.S16, ChannelLayout.FivePoint1Back);
var stream = recorder.AddAudioStream(new AudioEncodingSettings(format, MediaCodec.Ac3, 448_000));
recorder.Start();
// … recorder.WriteAudio(stream, frame) …
recorder.Stop();
```

An encoder can also be named outright and given its own settings, which is how a
stream asks for something the common abstraction does not name:

```csharp
recorder.AddAudioStream(new AudioEncodingSettings(format, MediaCodec.Aac)
{
    Quality = 2.0,                       // variable bitrate, as -q:a does it
    Options = new Dictionary<string, string> { ["aac_coder"] = "twoloop" },
});
```

## Filter graphs

Four stacks can assemble a processing graph, and each one is wrapped in its own
terms rather than behind a common abstraction: the shapes are genuinely
different, and hiding that would cost more than it saves.

**DirectShow** builds the graph out of filters and pins, and hands frames back
through the Sample Grabber. The callback runs on the streaming thread:

```csharp
using var graph = DirectShowGraph.Create();

var source = graph.AddSourceFilter(@"C:\clip.mp4");
var grabber = DirectShowSampleGrabber.AddTo(graph);
grabber.AcceptVideo(PixelFormat.Bgr24);          // forces a decoder and a converter in
var sink = graph.AddFilter(DsFilter.NullRenderer);

// Intelligent connect: the builder inserts whatever parser and decoder are needed.
graph.Connect(source.FirstFreeOutput!, grabber.Filter.InputPins[0]);
graph.Connect(grabber.Filter.FirstFreeOutput!, sink.InputPins[0]);

grabber.VideoGrabbed += (in VideoFrame frame) =>
{
    // Borrowed, and bottom-up: Stride(0) is negative for a DIB.
    ReadOnlySpan<byte> pixels = frame.GetPlane(0);
};

graph.Run();
graph.WaitForCompletion(TimeSpan.FromSeconds(30));
Console.Write(graph.Describe());                 // every filter, pin and connection
```

**libavfilter** takes the same chain the `ffmpeg` command line takes after `-vf`
or `-af`. The buffer source and sink are added for you, so the string holds only
the filters in between:

```csharp
var input = new VideoFormat(1920, 1080, PixelFormat.Bgr24, 30);
using var chain = FFmpegFilterGraph.ForVideo(input, "scale=640:-1,hflip");

chain.Push(decodedFrame);                        // an AVFrame, or a borrowed VideoFrame
while (chain.TryReceive((in VideoFrame frame) => Save(frame)))
{
}

chain.Flush();                                   // let buffering filters empty out
Console.WriteLine(chain.OutputVideoFormat);      // 640x360 Bgr24, as negotiated
```

**mpv** builds the graph itself, so what is wrapped is the `vf` and `af`
properties it is set through:

```csharp
using var player = new MpvPlayer();
player.Open("clip.mkv");

player.VideoFilters.Add(MpvFilterChain.Lavfi("hqdn3d,eq=contrast=1.1"));
player.AudioFilters.Set("lavfi=[volume=0.5]");
player.Play();
```

**GStreamer** parses a whole pipeline from one `gst-launch` string, and frames
leave it through an `appsink`:

```csharp
using var pipeline = GStreamerPipeline.Parse(
    "filesrc location=clip.mp4 ! decodebin ! videoconvert ! " +
    "video/x-raw,format=BGR ! appsink name=sink");

var sink = pipeline.GetSink("sink");
sink.DropWhenFull(true);                         // a live preview would rather skip than block

pipeline.Pause();                                // preroll, so the duration is known
Console.WriteLine($"{pipeline.Duration}, {sink.NegotiatedFormat}");

pipeline.Play();
while (!sink.IsEndOfStream)
{
    sink.TryPullVideo(TimeSpan.FromMilliseconds(500), (in VideoFrame frame) => Save(frame));
}
```

Sample commands: `backends`, `devices [audio|video|all|dshow]`, `probe`, `play`,
`decode`, `rec-audio`, `rec-video`, `rec-file`, `rec-audio-file`, `encoders`,
`ds-filters`, `ds-graph`, `ds-grab`, `av-filters`, `av-filter`, `gst-elements`,
`gst-run`.

## Tests

```bash
dotnet test
```

`tests/MediaToolkitNet.Tests` needs nothing but the SDK: layouts, format maps,
encoder names and the series table.

`tests/MediaToolkitNet.IntegrationTests` encodes and reads real files, and
checks **every struct field this binding reads by offset** against the library
that is installed. Each check establishes the expected value by a route that
does not use the offset it is checking — an AVOption that FFmpeg writes through
its own offset, a struct FFmpeg filled itself, or a file the test wrote — so a
mismatch fails by name rather than by corrupting memory:

```
AbiLayoutTests.CodecContextPixFmtIsWhereParametersToContextWritesIt [FAIL]
  Expected value to be 4 because pix_fmt was read at 140, but found -1.
```

They skip themselves where FFmpeg is not loadable. Point them at a particular
build to test a series the machine does not have on its path:

```bash
MEDIATOOLKITNET_FFMPEG_DIR=/opt/ffmpeg-9/lib dotnet test
```

## Packages

| Package | Contents |
|---|---|
| `MediaToolkitNet.All` | Facade: pulls in every backend and picks one at runtime |
| `MediaToolkitNet.Abstractions` | Abstractions only; no native code, no dependencies |
| `MediaToolkitNet.Interop` | Native loading, UTF-8 marshalling, COM vtable access |
| `MediaToolkitNet.FFmpeg` | FFmpeg 7.x, 8.x or 9.x backend |
| `MediaToolkitNet.Mpv` | libmpv backend |
| `MediaToolkitNet.Windows` | Media Foundation, WASAPI, DirectShow |
| `MediaToolkitNet.Linux` | V4L2, ALSA, PulseAudio |
| `MediaToolkitNet.GStreamer` | GStreamer 1.x pipelines on Linux |
| `MediaToolkitNet.MacOS` | AVFoundation, CoreAudio |

```bash
dotnet add package MediaToolkitNet.All
```

Reference a single backend instead when you do not want the rest:

```bash
dotnet add package MediaToolkitNet.Windows
```

The name is not plain `MediaToolkit` because nuget.org rejects an id that, once
`.` and `-` are stripped, matches an existing one — and `MediaToolkit`,
`MediaToolkit.NET` and `MediaToolkit.NetCore` are all taken by unrelated projects.
That is also why the facade package is `MediaToolkitNet.All` and the abstractions
package is `MediaToolkitNet.Abstractions`: plain `MediaToolkitNet` collides with
`MediaToolkit.NET`, and `MediaToolkitNet.Core` collides with
`MediaToolkit.NetCore`. The facade keeps the namespace `MediaToolkitNet`, so
consumer code is unaffected. `MediaToolkit.NET` remains the name of the product
and of this repository.

No native binary ships inside any package. FFmpeg and libmpv have to be installed
separately; the platform backends only call libraries that are already part of
the operating system.

## Key decisions

**Frames do not own their memory.** `VideoFrame` and `AudioFrame` are
`ref struct`s over somebody else's buffers, alive only inside the handler that
received them. The compiler refuses to let you store one. Zero copies, zero
allocations per frame.

**FFmpeg 7.x, 8.x and 9.x are supported, one series at a time.** FFmpeg keeps no
stable ABI for its public structs across major releases, so the loader reads the
versions it actually got, matches them against the series listed in
`FFmpegGeneration`, and refuses a mixture that belongs to none of them. Every
direct field access lives in `MediaToolkitNet.FFmpeg/Native/AbiLayout.cs`, and
the two offsets that differ between series come from the matched series; the
rest have held since FFmpeg 6. The layout is then self-checked against
documented post-allocation defaults (`av_frame_alloc` leaves `format == -1` and
`pts == AV_NOPTS_VALUE`, `av_packet_alloc` leaves `pos == -1`), a new output
stream is checked before anything is written through its `codecpar`, and an
opened input is checked before anything is read. The `AVCodecParameters` offsets
and the audio fields of `AVFrame` are **discovered at runtime by probing**
rather than hard-coded. A layout that does not check out produces a clear error
instead of memory corruption. Codecs are resolved by name, never by the unstable
`AVCodecID` values.

**Whatever can go through an API does not go through a field.** Bitrate, GOP
size, sample rate and channel layout are set on the encoder with `av_opt_set_*`,
and codec parameters travel through `avcodec_parameters_to_context` — none of
which depends on struct layout. The layout of a bare channel count comes from
`av_channel_layout_default` rather than from a table of our own, because four
channels are quad and not 3.1.

**Vtable slots are verified.** The Windows backend calls COM by slot number, and
the numbering counts every method of every base interface (`IMFAttributes`
occupies slots 3–32, so `IMFSample` starts at 33). A wrong number raises no
exception — it quietly calls the neighbouring method — so the chains were
validated by running them against real hardware.

## Limitations

- **Media Foundation video recording needs a registered video encoder MFT.**
  Editions such as Windows N without the Media Feature Pack have none, and MF
  reports that as `MF_E_INVALIDMEDIATYPE` on the input type.
  `MediaFoundationRecorder` recognises the case and reports it plainly; check a
  machine with the `encoders` command.
- `MacOSBackend` cannot write files (`AVAssetWriter` is not wrapped) — use FFmpeg.
- `FFmpegPlayer` only decodes: it has no renderer and no audio output.
- Only the mpv client API is wrapped; the render API (embedding into a host
  OpenGL context) is not covered.
- The GStreamer backend implements no capture or playback interface: what it
  offers is the pipeline, which covers those roles in GStreamer's own terms.
- `libavfilter` is optional. When it is missing beside the other FFmpeg
  libraries, `FFmpegFilterGraph.IsAvailable` is false and nothing else changes.
- The GStreamer backend binds the Linux library names, so it reports itself
  unavailable on Windows and macOS even where GStreamer is installed.
- 64-bit processes only.

