# MediaToolkit.NET

[![Build](https://github.com/yartat/MediaToolkit.NET/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/yartat/MediaToolkit.NET/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/MediaToolkitNet.All.svg?label=MediaToolkitNet.All)](https://www.nuget.org/packages/MediaToolkitNet.All)
[![NuGet downloads](https://img.shields.io/nuget/dt/MediaToolkitNet.All.svg)](https://www.nuget.org/packages/MediaToolkitNet.All)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

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
| `MediaToolkitNet.Abstractions` | Abstractions: devices, formats, frames, player, capture, recording, probing, transcoding. No dependencies. |
| `MediaToolkitNet.Interop` | Native library loading, UTF-8, `ComPtr` and vtable access. |
| `MediaToolkitNet.FFmpeg` | libavformat / libavcodec / libavutil / libswscale / libswresample / libavdevice |
| `MediaToolkitNet.Mpv` | libmpv (client API) |
| `MediaToolkitNet.Windows` | Media Foundation, WASAPI, DirectShow (devices, filter graph, Sample Grabber) |
| `MediaToolkitNet.Linux` | ALSA, PulseAudio (simple API), V4L2 |
| `MediaToolkitNet.GStreamer` | GStreamer 1.x: pipeline, element registry, appsink, GstDiscoverer, transcoding |
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
| Probing a file | — | GstDiscoverer | — | libavformat | track list |
| Transcoding | — | GStreamer pipeline | — | full | encoding mode |
| Reading frames from a file | — | — | — | `FFmpegMediaReader` | — |

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

## Windows features

`MediaToolkitNet.Windows` has Windows-only classes next to the shared capture and recording ones.

**Desktop Duplication** (`DesktopCapture`) captures a monitor from the GPU through DXGI, as BGRA frames, without GDI:

```csharp
foreach (var output in DesktopCapture.GetOutputs())          // name, place on the desktop, size in physical pixels, rotation
{
    Console.WriteLine($"{output.Index}: {output.Name} {output.Width}x{output.Height}");
}

using var capture = new DesktopCapture(outputIndex: 0, frameRate: 30);
capture.FrameCaptured += (in VideoFrame frame) => Save(frame);   // it is an IVideoCapture
capture.Start();

capture.TryGrab(TimeSpan.FromSeconds(1), (in VideoFrame frame) => Screenshot(frame));   // or pull one frame (not while running)
```

**Window and monitor capture** (`GraphicsCapture`, Windows.Graphics.Capture, Windows 10 version 1903 or later) takes the picture of
one window straight from the compositor: it works while other windows cover it and for windows drawn with DirectX, which a
screen grab gets wrong. It is an `IVideoCapture` that delivers BGRA frames, can draw the mouse pointer into them, can switch
Windows' yellow border off (Windows 11), follows a resize, and raises `Closed` when the window is destroyed:

```csharp
foreach (var w in GraphicsCapture.GetWindows())                       // visible, titled, not minimised
{
    Console.WriteLine($"0x{w.Handle:X} pid {w.ProcessId} {w.Width}x{w.Height} {w.Title}");
}

using var capture = GraphicsCapture.ForWindow(handle, frameRate: 30, captureCursor: true, showBorder: false);   // or ForMonitor(0)
capture.FrameCaptured += (in VideoFrame frame) => Encode(frame);       // the size can change when the window is resized
capture.Closed += (_, _) => Console.WriteLine("the window is gone");
capture.Start();

capture.TryGrab(TimeSpan.FromSeconds(1), (in VideoFrame frame) => Screenshot(frame));   // or one frame (not while running)
```

A still window sends one frame at the start and then one when it draws; a minimised window sends none. `window list` and
`window <title part|0xHWND|monitor:N> 3 shot.bmp` in the sample try it.

**Sound of one process** (`WasapiCapture.ForProcess`, Windows 10 version 2004 or newer) listens to what a process and its
children play, or to everything but them, whichever device they play through:

```csharp
if (WasapiCapture.SupportsProcessLoopback)
{
    using var capture = WasapiCapture.ForProcess(processId, ProcessLoopbackMode.IncludeProcessTree);   // or ExcludeProcessTree
    capture.FrameCaptured += (in AudioFrame frame) => Write(frame);       // 48 kHz stereo float unless you pass a format
    capture.Start();
}
```

A process that does not exist is accepted and delivers silence. `proc-audio <pid>` in the sample tries it.

**Audio controls.** `AudioEndpointVolume` is the volume, mute switch and peak meter of a playback or recording endpoint, and
reports changes made anywhere; `AudioSession.GetAll()` lists the programs that have a session on the endpoint, each with its
own volume, mute and meter (what the Windows mixer shows); `AudioDeviceNotifications` tells you when a device is added,
removed, changes state or becomes the default:

```csharp
using var speakers = new AudioEndpointVolume();                       // the default output; (device, capture: true) for a microphone
speakers.Changed += (_, change) => Console.WriteLine($"{change.Volume:P0} muted={change.IsMuted}");
speakers.Volume = 0.4f;

foreach (var session in AudioSession.GetAll())
{
    Console.WriteLine($"{session.ProcessId} {session.State} {session.Volume:P0} peak {session.PeakLevel:F2}");
    session.Dispose();
}

using var devices = new AudioDeviceNotifications();
devices.DefaultDeviceChanged += (_, e) => Console.WriteLine($"default {e.Flow} is now {e.DeviceId}");
```

**Camera controls.** `MediaFoundationVideoCapture.Controls` reaches the camera's pan, tilt, zoom, exposure, focus, iris,
brightness, contrast, saturation, white balance and the rest (`IAMCameraControl`, `IAMVideoProcAmp`). `Supported` lists what
the camera has, `GetRange`, `Get` and `Set` read and change a setting, by hand or automatic:

```csharp
using var camera = new MediaFoundationVideoCapture(VideoCaptureSettings.Default);
if (camera.Controls.GetRange(CameraProperty.Exposure) is { CanBeManual: true } range)
{
    camera.Controls.Set(CameraProperty.Exposure, range.Default);
}
```

**Transcoding and probing through Media Foundation** (`MediaFoundationTranscoder`, `MediaFoundationProber`; both are the
Windows backend's `CreateTranscoder` and `CreateProber`) read with `IMFSourceReader` and write with `IMFSinkWriter`, which uses
the machine's hardware encoders (NVENC, Quick Sync, AMD) when there are any:

```csharp
var request = new TranscodeRequest("out.mp4")
{
    Inputs = ["in.mp4"],
    Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(40),
    Streams =
    [
        OutputStream.Video(StreamSource.First(MediaStreamKind.Video), new VideoOutputSettings(MediaCodec.Hevc) { Width = 1280, Height = -1, FrameRate = new Rational(30, 1), BitrateBitsPerSecond = 4_000_000, KeyFrameInterval = 60 }),
        OutputStream.Audio(StreamSource.First(MediaStreamKind.Audio), new AudioOutputSettings { Channels = 2, SampleRate = 48000, BitrateBitsPerSecond = 128_000 }),
    ],
};
var issues = new MediaFoundationTranscoder().Validate(request);       // errors and warnings, before anything is written
var result = await new MediaFoundationTranscoder(hardwareEncoders: true).RunAsync(request, progress, cancellation);
```

It does less than the FFmpeg backend and refuses the rest with a reason: one video stream (H.264 or HEVC) and one AAC audio
stream, from one input into `.mp4`, `.m4v` or `.m4a`; no stream copy, filters, quality-based rate control, encoder options, tags or
chapters (the chapters and tags of the input are not carried over, which the validation warns about). The AAC encoder takes 44.1
or 48 kHz, one or two channels and 96, 128, 160 or 192 kb/s; another bit rate is rounded to the nearest and reported.
The prober reads streams, codecs, sizes and duration; Media Foundation gives it no tags or chapters. The registry tries it
after FFmpeg, GStreamer and mpv. `transcode in.mp4 out.mp4 --backend windows --vcodec Hevc --size 1280x720` tries it.

A desktop frame arrives when the desktop changes, so a still desktop sends one at the start and then nothing; the mouse pointer
is not in the picture; the duplication is made again by itself after a mode change or the secure desktop. A monitor
set to a rotation other than 0 delivers its picture rotated. Needs a session with a desktop: a service has none.
`dotnet run --project samples/MediaToolkitNet.Sample.Cli -- desktop 0 3 shot.bmp` tries it.

## FFmpeg 8.x and 9.x features

`MediaToolkitNet.FFmpeg` exposes what FFmpeg added after 7.1 as FFmpeg-only API, with no counterpart on the other
backends. Each feature is optional: it exists only if the loaded libraries have it, and `FFmpegFeatures` says which
(`SynchronousDecoding`, `MimeCodecStrings`, `EncoderReconfiguration`, `BitstreamFilterGraphs`, `GainMaps`,
`DownmixMatrices`, `Exif`, `NativeDsd`, `HandleInput`). A call to a missing feature throws `NotSupportedException`; the typed filter builders
have `*Available` properties. Series 9 covers 9.0 and 9.1, so features are told apart by the functions the library
exports, not by its major version.

| Feature | API | Needs |
|---|---|---|
| Frame side data, and EXIF in it | `FFmpegFrameDetails` from the `TryReadNext` / `TryReadAt` overloads | any series; EXIF 8.1 |
| EXIF buffers | `FFmpegExif.Parse` | 8.1 |
| MIME codec string | `FFmpegDemuxer.MimeCodecString` | 8.1 |
| Synchronous decoding | `FFmpegDecoder.ReceiveFrame(frame, synchronous: true)` | 8.1 |
| Changing an encoder while it runs | `FFmpegRecorder.Reconfigure` | 9.1 |
| Bitstream filter graphs | `FFmpegBitstreamFilterGraph` | 9.1 |
| HDR gain maps (ISO 21496-1) | `FFmpegGainMap`, `FFmpegFrameDetails.GainMap()` | 9.1 |
| Downmix matrices | `FFmpegDownmixMatrix`, `FFmpegFrameDetails.DownmixMatrix()` | 9.1 |
| DSD (native bitstream) | `FFmpegDecoder.Open(..., nativeDsd: true)`, `AVSampleFormat.Dsd` | 9.1 |
| Reading a Windows `HANDLE` with seek | `FFmpegDemuxer.OpenHandle` | 9.1, Windows |
| Stream groups | `FFmpegDemuxer.StreamGroups` | any series (types 5-8 need 8.1-9.1) |
| Typed filters | `FFmpegNewFilters` (`ColorDetect`, `LatticePal`, `Whisper`, `GainMap`) | the filter's own release |

Things to know:

- `Reconfigure` only takes options FFmpeg marks as changeable at run time. Passing another (`b`, `maxrate`,
  `bufsize`, `sar`) crashes the process inside FFmpeg 9.1 (a NULL dereference in `avcodec_encode_reconfigure`), so the
  binding refuses them first.
- A bitstream filter graph holds graph filters only (`lcevc_merge` and the source and sink nodes); a classic
  filter such as `h264_mp4toannexb` is refused with a message that says so.
- DSD: FFmpeg 9.1 decodes DSD to float unless asked otherwise (and 9.0 and earlier always do). With `nativeDsd: true` the
  decoder returns the bitstream (`AVSampleFormat.Dsd`, one byte is eight one-bit samples, so the sample rate is an eighth
  of the DSD rate); `aresample` in a filter graph turns it into PCM. There is no PCM to DSD conversion, and the shared
  `SampleFormat` has no DSD value, so the readers, the recorder and the transcoders still work in PCM.
- `OpenHandle` takes a `SafeFileHandle` opened without `FILE_FLAG_OVERLAPPED` (`FileStream` with `useAsync: false`). The
  demuxer keeps the handle alive and does not close it, but it moves the file position. The string URL `handle:` stays
  refused by `Open`.
- Stream groups are read from the container as it opens; groups that appear later, while packets are read, are not
  listed. Tile grid and layered video parameters are checked for plausibility but were not tested against a real file.

## mpv node API

`MpvPlayer` reads and writes mpv's structured values as `MpvNode` trees (`mpv_node`), so `track-list`, `chapter-list`,
`metadata` and a command's result are data, not text:

```csharp
using var player = new MpvPlayer();
player.Open("movie.mkv");

foreach (var track in player.GetTracks())            // id, type, language, codec, size, rate, selected...
{
    Console.WriteLine($"{track.Type} #{track.Id} {track.Language} {track.Codec}");
}

var chapters = player.GetChapters();                  // title and start
var tags = player.GetMetadata();                      // the container's own key spelling, e.g. TITLE in Matroska

// Any property or command, with named arguments:
var raw = player.GetPropertyNode("command-list");
var text = player.CommandNode(MpvNode.Command("expand-text", ("text", "${filename}")));
```

`TakeScreenshot` wraps `screenshot-raw`: the frame as mpv shows it, after its filters, scaling, colour conversion and
subtitles, into memory (`MpvScreenshot`: size, stride, `Bgr0`, `Bgra`, `Rgba` or 16-bit `Rgba64`). It needs a frame, so
pause and wait for `video-params` first; it works with `vo=null`.

```csharp
player.Options["vo"] = "null";
player.Open("movie.mkv");
// ... wait until player.GetPropertyNode("video-params") is not null
var shot = player.TakeScreenshot(MpvScreenshotContent.Video, MpvScreenshotFormat.Bgra);
var row = shot.GetRow(100);        // Width * 4 bytes, without the padding after the row
```

`Open(Stream)` plays a .NET stream through mpv's `stream_cb` API, and `RegisterStreamProtocol` teaches mpv a scheme of
your own (`Open("myproto://thing")` calls your opener with the URI):

```csharp
using var file = File.OpenRead("movie.mkv");
player.Open(file, leaveOpen: true);                       // mpv reads it on its own thread; seekable streams seek

player.RegisterStreamProtocol("archive", uri => OpenEntry(uri));
player.Open("archive://movies/a.mkv");
```

mpv closes a stream it was given unless `leaveOpen` is set, and does so when something else is loaded or the player is
disposed. A .NET `Stream` cannot be interrupted, so a read that is waiting on a slow source ends only when the stream
returns; after mpv's cancel request (libmpv 1.106 and newer) further reads fail. A load that fails (opener returns
`null`, unreadable data) ends playback and raises `Failed`, as does any file mpv could not play.

`ObserveProperty` calls you when any property changes, with its value as a node (and once at the start); dispose the
returned `MpvObservation` to stop. `GetCommands`, `GetPropertyNames`, `HasCommand`, `HasOption`, `GetMpvVersion` and
`MpvPlayer.ClientApiVersion` say what the mpv behind libmpv can do, so a command or option a newer mpv added can be
checked for instead of tried:

```csharp
using var watch = player.ObserveProperty("track-list", (name, value) => Refresh(value));
if (player.HasCommand("update-clipboard")) { /* mpv 0.42 */ }
if (player.HasOption("dvda-device")) { player.Options["dvda-device"] = "E:"; }
```

`AddHook` makes mpv wait for you at `on_load`, `on_preloaded`, `on_loaded`, `on_unload` and the other hooks, and
`CommandNodeAsync` / `CommandAsync` run a command without waiting for it, with cancellation:

```csharp
// Redirect a URL before mpv opens anything (stream-open-filename); file-local-options/<name> sets options for this file.
player.AddHook("on_load", hook =>
{
    if (hook.Player.GetPropertyString("stream-open-filename") is { } url && url.StartsWith("video://"))
    {
        hook.Player.SetProperty("stream-open-filename", Resolve(url));
    }
});

// Work that takes time: keep mpv waiting and let it go from anywhere.
player.AddHook("on_load", hook =>
{
    hook.Defer();
    _ = Task.Run(async () => { await PrepareAsync(); hook.Continue(); });
});

var result = await player.CommandNodeAsync(MpvNode.Command("subprocess", ("args", MpvNode.Array("ffprobe", "x.mkv"))), token);
```

A hook handler runs on the player's event thread: mpv does nothing more with the file until it returns (or until
`Continue` after `Defer`), no other event of that player is delivered, and an `await` on its own asynchronous commands
inside it never finishes. A handler that throws is reported through `Failed` and mpv goes on. Cancelling a command asks
mpv to abort it, which only some commands honour (`subprocess` does); the task then ends as cancelled. Pending
commands fail with `ObjectDisposedException` when the player is disposed.

`MpvNode.Command` puts the command under `name`, which every libmpv accepts; a command with an argument called `name`
needs `_name` (mpv 0.42 and newer) in a node built with `MpvNode.Map`. Nodes are copies, so they stay valid after
libmpv frees its own. A libmpv without the node API throws `NotSupportedException` (`Mpv.HasNodeApi`).

## Transcoding

A transcode is described once, as a `TranscodeRequest`, and run by whichever
backend accepts it: FFmpeg first, then GStreamer, then mpv. The request names
inputs, an output, and what each output stream does with an input stream —
copy it, re-encode it, convert subtitles, burn them in — plus a trim, tags and
chapters. With no streams named it copies every video, audio and subtitle
stream, which is a container change:

```csharp
await MediaToolkitNetBackends.TranscodeAsync(TranscodeRequest.Remux("in.mkv", "out.mp4"));

await MediaToolkitNetBackends.TranscodeAsync(new TranscodeRequest("out.mkv")
{
    Inputs = ["film.mkv", "film.en.srt"],
    Start = TimeSpan.FromMinutes(1),
    End = TimeSpan.FromMinutes(2),
    Streams =
    [
        OutputStream.Video(StreamSource.First(MediaStreamKind.Video),
            new VideoOutputSettings(MediaCodec.H264) { Width = 1280, Quality = 23, Speed = EncoderSpeed.Fast }),
        OutputStream.Audio(StreamSource.All(MediaStreamKind.Audio), new AudioOutputSettings(MediaCodec.Opus)),
        OutputStream.Subtitles(StreamSource.First(MediaStreamKind.Subtitle, input: 1), MediaCodec.SubRip),
    ],
    Metadata = new Dictionary<string, string> { ["title"] = "Film" },
});
```

Every backend starts from the same `TranscodePlan`, which resolves the
selectors against what the inputs contain and finds the mistakes no backend can
fix. Each backend then says what it cannot do, through `Validate`, before
anything is written; when every backend refuses, the exception carries every
refusal. The three differ a good deal:

| | FFmpeg | GStreamer | mpv |
|---|:---:|:---:|:---:|
| Stream copy, container change | yes | video and audio | — |
| Re-encode video / audio | yes | yes | one track each |
| Several audio or video streams | yes | yes | — |
| Subtitle copy / conversion | yes | — | — |
| Subtitle burn-in | text (libass) | text (`textoverlay`) | yes |
| Several inputs | yes | yes | external audio and subtitles |
| Trim | yes | yes | yes |
| Tags / chapters | yes / yes | yes / Matroska only, untrimmed | yes / — |

GStreamer cannot write subtitles as a stream at all: its muxers take plain
UTF-8 while its subtitle parsers produce Pango markup. mpv re-encodes what it
plays, so it can copy nothing. Where one backend cannot do a job the next one is
asked, and `MpvTranscoder.CommandLine` and `GStreamerTranscoder.Describe` print
the equivalent `mpv` and `gst-launch-1.0` command for any plan.

`IMediaProber` reads what a file holds — streams, languages, flags, tags and
chapters — and numbers streams the same way on every backend, so an index from
one works with another. `FFmpegMediaReader` is the frame-level counterpart, in
the manner of FFMediaToolkit: open a file, then read decoded frames in order or
at a time, converted to the format asked for:

```csharp
using var reader = FFmpegMediaReader.Open("clip.mp4",
    new MediaReaderOptions { VideoPixelFormat = PixelFormat.Bgra32, VideoWidth = 640, VideoHeight = 360 });

reader.Video!.TryReadAt(TimeSpan.FromSeconds(12), (in VideoFrame frame) => Save(frame));
foreach (var cue in reader.SubtitleStreams[0].ReadAll())
{
    Console.WriteLine($"{cue.Start} {cue.Text}");
}
```

### Files, URLs and untrusted input

The FFmpeg backend opens **local files and pipes only**. A path that comes from
somebody else (an upload, a form field, a message) would otherwise let that
somebody make your process connect to a host of their choosing, or read other
files through a playlist. So, by default, these inputs are refused:

- **Network URLs**: `http://`, `https://`, `rtsp://`, `tcp://`, `udp://` and any
  other protocol but `file`, `pipe`, `crypto` and `data`. `FFmpegProber`,
  `FFmpegMediaReader` and `FFmpegTranscoder` answer
  `avformat_open_input(...): Invalid argument`, without connecting.
- **Playlists** (HLS `.m3u8`, DASH, `concat`): they name other files to read, so
  they are refused with "is a playlist, which names other files to read".

`FFmpegPlayer` is not restricted, since playing a URL is what it is for, and
neither are capture devices.

If your application opens URLs on purpose, say so. Either once, at start-up, for
the whole process:

```csharp
FFmpegDemuxer.AllowNetworkByDefault = true;

var info = new FFmpegProber().Probe("https://example.com/clip.mp4");
```

or for one reader, with the protocols that input needs and nothing else (`https`
needs `tls` and `tcp` besides itself):

```csharp
using var reader = FFmpegMediaReader.Open("https://example.com/clip.mp4",
    new MediaReaderOptions
    {
        DemuxerOptions = new Dictionary<string, string>
        {
            ["protocol_whitelist"] = "file,http,https,tcp,tls,crypto",
        },
    });
```

The per-reader form is the better one when only some inputs are URLs: the
process-wide switch applies to every input, so use it only in an application
that never opens a path it did not choose itself. Playlists have no such
switch in the high-level API; open one with
`FFmpegDemuxer.Open(url, allowPlaylists: true)` when you trust it.

The GStreamer and mpv backends take a URL as they always did. If a job may run on
either, check the input yourself before handing it over.

Sample commands: `backends`, `devices [audio|video|all|dshow]`, `probe`, `play`,
`decode`, `rec-audio`, `rec-video`, `rec-file`, `rec-audio-file`, `encoders`,
`ds-filters`, `ds-graph`, `ds-grab`, `av-filters`, `av-filter`, `gst-elements`,
`gst-run`, `transcode`, `read`, `mpv-args`, `gst-args`.

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

The transcoding tests run each backend's output back through the FFmpeg prober
and reader, so no backend checks its own work; the GStreamer ones need Linux as
well as FFmpeg, and the mpv ones libmpv.

The `Audit*` tests are the regression tests for the findings of the 1.0.0 audit
(`AUDIT.md` names the test for each). Some of them look for failures that end the
process: an access violation, an unhandled exception on a worker thread. Those
run their scenario in a child process, `tests/MediaToolkitNet.CrashProbe`, and
read its verdict, so a regression fails one test instead of killing the test host.
A few make their media with the `ffmpeg` program, which they look for in
`MEDIATOOLKITNET_FFMPEG_DIR` and then on `PATH`.

They skip themselves where FFmpeg is not loadable. Point them at a particular
build to test a series the machine does not have on its path:

```bash
MEDIATOOLKITNET_FFMPEG_DIR=/opt/ffmpeg-9/lib dotnet test
```

libmpv is found the way any native library is — beside the test assembly or on
the system search path — so on Windows put the folder holding `libmpv-2.dll` on
`PATH` to run the mpv tests.

## Packages

| Package | Contents |
|---|---|
| `MediaToolkitNet.All` | Facade: pulls in every backend and picks one at runtime |
| `MediaToolkitNet.Abstractions` | Abstractions only; no native code, no dependencies |
| `MediaToolkitNet.Interop` | Native loading, UTF-8 marshalling, COM vtable access |
| `MediaToolkitNet.FFmpeg` | FFmpeg 7.x, 8.x or 9.x: demux, decode, encode, mux, filter graphs, probing, transcoding, frame reader |
| `MediaToolkitNet.Mpv` | libmpv: playback, filter chains, probing, transcoding |
| `MediaToolkitNet.Windows` | Media Foundation, WASAPI, DirectShow |
| `MediaToolkitNet.Linux` | V4L2, ALSA, PulseAudio |
| `MediaToolkitNet.GStreamer` | GStreamer 1.x on Linux: pipelines, probing, transcoding |
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

No native binary ships inside any package. FFmpeg, libmpv and GStreamer have to
be installed separately; the platform backends only call libraries that are
already part of the operating system. Native libraries are looked for in the
directories given to `NativeSearchPaths.Prepend`, then beside the application,
in its `native` and `runtimes/<rid>/native` folders, and then where the
operating system looks.

The package page shows `docs/package/README.md`, which is written for someone
who has just installed a package, and the release notes of the version, from
`docs/release-notes/v<version>.nuget.txt`.

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

**Vtable slots and GUIDs are verified.** The Windows backend calls COM by slot
number, and the numbering counts every method of every base interface
(`IMFAttributes` occupies slots 3–32, so `IMFSample` starts at 33). A wrong
number raises no exception — it quietly calls the neighbouring method — so the
chains were validated by running them against real hardware. GUIDs fail just as
quietly: two Media Foundation GUIDs written from memory kept video recording
broken until 0.3.0, so every GUID in the source is now checked against the
Windows SDK headers.

**Input is untrusted until the caller says otherwise.** A path or URL handed to
the library often comes from a user. The FFmpeg backend therefore opens local
files and pipes only, refuses playlists, and reads the network only when asked
(see *Files, URLs and untrusted input*); the native library search does not walk
into folders other users may write to; and a custom filter is documented as what
it is, code that runs as written. Frames are checked against the buffer they came
from before they reach a handler (`FrameBounds.FitsIn`), because a producer that
trusts its caps reads past the end of a short buffer.

**An output is never opened for writing in place.** A transcoder writes a partial
file beside the output and renames it over the output when the job has finished
(`PartialOutput`). The rename replaces the directory entry, so a link to an input
is replaced rather than followed, and a failed, refused or cancelled job leaves
whatever was at the output path exactly as it was.

**One request, three transcoders.** FFmpeg, GStreamer and mpv share nothing in
how they transcode, so what is shared is the request and the plan resolved from
it. Each backend then refuses, before writing anything, whatever it cannot do,
and says why. Their quiet failures are handled where they arise: mpv writes a
file without the stream whose encoder it lacks and reports success, so the mpv
transcoder checks its output; a GStreamer muxer does not survive the flush of a
seek, so the GStreamer transcoder holds each branch back until the seek has
flushed it.

## Limitations

- **Media Foundation video recording needs a registered video encoder MFT.**
  Editions such as Windows N without the Media Feature Pack have none, and MF
  reports that as `MF_E_INVALIDMEDIATYPE` on the input type.
  `MediaFoundationRecorder` recognises the case and reports it plainly; check a
  machine with the `encoders` command.
- Subtitle burn-in is text only on FFmpeg and GStreamer, and PGS has no FFmpeg
  encoder, so it can be copied but not produced.
- mpv's encoding mode copies nothing, writes one video and one audio track and
  no chapters, and in MP4 writes the audio stream first. It pads uncompressed
  audio to a block of 16384 samples, so PCM output runs a little long (the
  transcoder warns), and its TrueHD and MLP run 20% long in Matroska, so those
  are refused there.
- GStreamer cannot write TrueHD: `avenc_truehd` encodes it but no muxer takes
  it. Its AAC encoder ignores a variable quality, since gst-libav does not
  expose libavcodec's `qscale` flag; the transcoder warns.
- `MacOSBackend` cannot write files (`AVAssetWriter` is not wrapped) — use FFmpeg.
  It has never been run on a Mac.
- WASAPI, Media Foundation, V4L2, ALSA and PulseAudio capture stop their worker
  with a two-second join and then free the native objects, so a frame handler
  that takes longer than that, or a device that stops answering, can still have
  them freed under it; and a capture whose loop failed reports `IsRunning` until
  `Stop` is called. `FFmpegPlayer` and `MpvPlayer` close their media from the
  worker instead. `AUDIT.md` lists every open finding.
- Only `FFmpegPlayer` can interrupt a read that is waiting on the network. A
  connect already in progress inside `FFmpegDemuxer.Open` cannot be interrupted;
  give it `rw_timeout` through `DemuxerOptions`.
- `FFmpegPlayer` only decodes: it has no renderer and no audio output.
- Only the mpv client API is wrapped; the render API (embedding into a host
  OpenGL context) is not covered.
- The GStreamer backend implements no capture or playback interface: what it
  offers is the pipeline, which covers those roles in GStreamer's own terms.
  Of the shared roles it takes probing and transcoding.
- GStreamer 1.26's `matroskamux` writes tag values that contain spaces
  serialised (`"a\ b"`), and it writes the input's chapters whatever the request
  says and with their original times. The transcoder warns about both.
- The mpv transcoder needs libmpv built with encoding support (`--o`); a player
  build without it is refused with that reason.
- `libavfilter` is optional. When it is missing beside the other FFmpeg
  libraries, `FFmpegFilterGraph.IsAvailable` is false and nothing else changes.
- The GStreamer backend binds the Linux library names, so it reports itself
  unavailable on Windows and macOS even where GStreamer is installed.
- 64-bit processes only.

