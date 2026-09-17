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
| `MediaToolkitNet.Core` | Abstractions: devices, formats, frames, player, capture, recording. No dependencies. |
| `MediaToolkitNet.Interop` | Native library loading, UTF-8, `ComPtr` and vtable access. |
| `MediaToolkitNet.FFmpeg` | libavformat / libavcodec / libavutil / libswscale / libswresample / libavdevice |
| `MediaToolkitNet.Mpv` | libmpv (client API) |
| `MediaToolkitNet.Windows` | Media Foundation, WASAPI, DirectShow device enumeration |
| `MediaToolkitNet.Linux` | ALSA, PulseAudio (simple API), V4L2 |
| `MediaToolkitNet.MacOS` | AVFoundation through the Objective-C runtime, CoreAudio / AudioQueue |
| `MediaToolkitNet` | Facade: backend registry and per-platform selection |
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

Sample commands: `backends`, `devices [audio|video|all|dshow]`, `probe`, `play`,
`decode`, `rec-audio`, `rec-video`, `rec-file`, `rec-audio-file`, `encoders`.

## Packages

| Package | Contents |
|---|---|
| `MediaToolkitNet` | Facade: pulls in every backend and picks one at runtime |
| `MediaToolkitNet.Core` | Abstractions only; no native code, no dependencies |
| `MediaToolkitNet.Interop` | Native loading, UTF-8 marshalling, COM vtable access |
| `MediaToolkitNet.FFmpeg` | FFmpeg 7.x backend |
| `MediaToolkitNet.Mpv` | libmpv backend |
| `MediaToolkitNet.Windows` | Media Foundation, WASAPI, DirectShow |
| `MediaToolkitNet.Linux` | V4L2, ALSA, PulseAudio |
| `MediaToolkitNet.MacOS` | AVFoundation, CoreAudio |

```bash
dotnet add package MediaToolkitNet
```

Reference a single backend instead when you do not want the rest:

```bash
dotnet add package MediaToolkitNet.Windows
```

Package ids, assembly names and namespaces are all `MediaToolkitNet.*`. The name
is not plain `MediaToolkit` because both `MediaToolkit` and `MediaToolkit.NET` are
held by an unrelated project on nuget.org, and an id there stays reserved even
when all of its versions are unlisted. `MediaToolkit.NET` remains the name of the
product and of this repository.

No native binary ships inside any package. FFmpeg and libmpv have to be installed
separately; the platform backends only call libraries that are already part of
the operating system.

## Key decisions

**Frames do not own their memory.** `VideoFrame` and `AudioFrame` are
`ref struct`s over somebody else's buffers, alive only inside the handler that
received them. The compiler refuses to let you store one. Zero copies, zero
allocations per frame.

**FFmpeg is pinned to 7.x.** FFmpeg keeps no stable ABI for its public structs
across major releases. Every direct field access lives in
`MediaToolkitNet.FFmpeg/Native/AbiLayout.cs`; the loader checks the major versions
and then self-checks the layout against documented post-allocation defaults
(`av_frame_alloc` leaves `format == -1` and `pts == AV_NOPTS_VALUE`,
`av_packet_alloc` leaves `pos == -1`). The `AVCodecParameters` offsets and the
audio fields of `AVFrame` are **discovered at runtime by probing** rather than
hard-coded. A layout that does not check out produces a clear error instead of
memory corruption. Codecs are resolved by name, never by the unstable
`AVCodecID` values.

**Whatever can go through an API does not go through a field.** Bitrate, GOP
size, sample rate and channel count are set on the encoder with `av_opt_set_*`,
and codec parameters travel through `avcodec_parameters_to_context` — none of
which depends on struct layout.

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
- DirectShow is wrapped for device enumeration only; building a filter graph is
  out of scope.
- 64-bit processes only.

## Releasing

`.github/workflows/ci.yml` runs on every push and pull request: it builds on
Ubuntu, Windows and macOS, runs the backend registry on each as a smoke test, and
uploads the packages as a build artifact.

`.github/workflows/release.yml` publishes to nuget.org when a `v*` tag is pushed.
Add both notes files for the version first — `docs/release-notes/v0.1.0.nuget.txt`
goes into the packages, `docs/release-notes/v0.1.0.md` is the GitHub release body
— then tag:

```bash
git tag v0.1.0
git push origin v0.1.0
gh release create v0.1.0 --notes-file docs/release-notes/v0.1.0.md
```

The version comes from the tag, so nothing has to be bumped in the repository;
`VersionPrefix` in `Directory.Build.props` is only the fallback for local builds.
A `workflow_dispatch` run takes the version as an input instead. The workflow
rejects a tag that is not SemVer 2.0 before it builds anything, because a version
published to nuget.org can never be replaced.

Publishing uses NuGet trusted publishing, so no long-lived API key is stored.
One-time setup:

1. On nuget.org, add a trusted publishing policy for this repository
   (`yartat/MediaToolkit.NET`) and the `Release` workflow.
2. In the repository settings, add a **secret** named `NUGET_USER` holding the
   nuget.org account name, which is what `release.yml` reads.
3. Optionally add required reviewers to the `nuget.org` environment to gate every
   publish behind an approval.

## Verified by running

On Windows 11 the following were actually exercised: device enumeration through
Media Foundation, WASAPI and DirectShow; video capture from a USB camera
(640x480 NV12 @ 30, ~23 fps); audio capture through WASAPI; recording live audio
to AAC/MP4 through `IMFSinkWriter`. The Linux, macOS, FFmpeg and mpv backends
compile but were not run on this machine: the FFmpeg and mpv native libraries
are not present here.
