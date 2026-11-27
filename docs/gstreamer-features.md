# GStreamer-only features

Candidates for `MediaToolkitNet.GStreamer` that need nothing from the shared interfaces. Status as of commit `60e9fb4`, judged from the public API and the bound native functions; nothing was run.

Done = public API exists. Partial = some of it exists, or it only works internally. No = absent.

Out of scope here, because they implement shared interfaces: `GStreamerPlayer`, `GStreamerRecorder` (including its HLS and SRT output), `GStreamerProber`, `GStreamerTranscoder`, `GStreamerScreenCapture`. Camera and microphone capture would belong to the same group and does not exist.

## Pipeline construction and control

| # | Feature | Status | Notes |
|---|---|---|---|
| 1 | Pipeline from a `gst-launch` string | Done | `GStreamerPipeline.Parse` |
| 2 | State changes, duration, position, wait for completion | Done | |
| 3 | Building by hand: make elements, add to bins, link elements and pads | Done | `Create`, `Add`, `AddBin` (from a description), `Remove`, `Link`, `Unlink`; no bin built element by element |
| 4 | Request pads and linking in `pad-added` | Done | `Link` requests pads and takes pad names |
| 5 | Add, remove or replace elements while playing (pad blocking) | Done | `AddProbe`, `SyncStateWithPipeline`, `Remove`, `ReleasePad`; only a `tee` branch was tried |
| 6 | Send EOS to a whole pipeline | Done | `GStreamerPipeline.EndOfStream`, added after that commit and tested in WSL |
| 7 | Seek with a rate and flags: trick play, reverse, segments, loops | Partial | `Seek(position)` only; the full seek event is used by the transcoder |
| 8 | Frame stepping | No | |
| 9 | Clock, base time, latency, synchronised pipelines | No | |
| 10 | Queries beyond duration and position (latency, seekable range, buffering) | No | |
| 11 | Custom events, force a key frame | No | |

## Elements, properties, introspection

| # | Feature | Status | Notes |
|---|---|---|---|
| 12 | Element registry | Partial | `GStreamerElements.All` / `Exists`; no pad templates, property list or caps |
| 13 | Read and write properties | Partial | Scalars, strings, enums, flags, caps, structures; no array or fraction property, no object read |
| 14 | Pad properties | Partial | `SetPadValue` only, no getter |
| 15 | Handle signals | Done | Caps and structure arguments arrive typed |
| 16 | Emit action signals (`split-now`, `get-stats`, ...) | Done | `GStreamerElement.Emit`; signals answering with a reference are refused |
| 17 | Typed `GstCaps` / `GstStructure` | Done | `GStreamerCaps`, `GStreamerStructure`; made and changed through their text |
| 18 | Plugin loading, element rank (force or ban a decoder) | No | |
| 19 | Pipeline graph dump | Done | `ToDot` |

## Bus, diagnostics, data access

| # | Feature | Status | Notes |
|---|---|---|---|
| 20 | Bus messages, application messages | Done | `GStreamerBus` |
| 21 | Parsed QoS, progress, TOC, stream and latency messages | No | QoS, progress, TOC and stream-start arrive by type only |
| 22 | Typed element messages (`level`, `spectrum`, motion) | Partial | Delivered as a structure string |
| 23 | GStreamer debug log into .NET | No | |
| 24 | `appsink` raw video and audio | Done | `GStreamerSink` |
| 25 | `appsink` encoded data (H.264 packets, buffer flags) | No | Throws for anything that is not raw |
| 26 | `appsrc` raw frames and bytes | Done | `GStreamerSource` |
| 27 | Pad probes: inspect, drop or block buffers and events | Partial | `AddProbe`; a buffer's time and size, not its data |

## Processing and streaming

| # | Feature | Status | Notes |
|---|---|---|---|
| 28 | Video compositor | Done | `GStreamerCompositor` |
| 29 | Audio mixer | Done | `GStreamerAudioMixer` |
| 30 | Add or remove mixer inputs and layers while running | No | Fixed before `Start` |
| 31 | Property animation: fades, transitions (`GstController`) | No | |
| 32 | Branches at run time: record while previewing (`tee`, `valve`) | Done | By hand with the calls of row 5; no wrapper class |
| 33 | Pre-event recording buffer | No | |
| 34 | Source switching (`input-selector`, fallback source) | No | |
| 35 | RTSP server (`gst-rtsp-server`) | No | Separate native library |
| 36 | WebRTC (`webrtcbin`, WHIP, WHEP) | No | Also needs promises and SDP objects |
| 37 | Network statistics (SRT, RTP, RTSP) | Partial | Readable with `GetValue("stats")` and `Emit`; only `multiudpsink` was tried, no typed wrapper |
| 38 | Device hot-plug monitoring (`GstDeviceMonitor`) | No | Listing devices alone would fit the shared enumerator |
| 39 | Zero-copy memory (DMABuf, GL, CUDA) | No | |
| 40 | Editing timeline (GStreamer Editing Services) | No | Separate native library |
| 41 | Elements written in .NET | No | |

## Suggested order

1. 25 and 7.
2. 35 and 36, if network delivery is the goal.
