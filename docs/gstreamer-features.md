# GStreamer-only features

Candidates for `MediaToolkitNet.GStreamer` that need nothing from the shared interfaces. Status as of commit `60e9fb4`, judged from the public API and the bound native functions; nothing was run.

Done = public API exists. Partial = some of it exists, or it only works internally. No = absent.

Out of scope here, because they implement shared interfaces: `GStreamerPlayer`, `GStreamerRecorder` (including its HLS and SRT output), `GStreamerProber`, `GStreamerTranscoder`, `GStreamerScreenCapture`. Camera and microphone capture would belong to the same group and does not exist.

## Pipeline construction and control

| # | Feature | Status | Notes |
|---|---|---|---|
| 1 | Pipeline from a `gst-launch` string | Done | `GStreamerPipeline.Parse` |
| 2 | State changes, duration, position, wait for completion | Done | |
| 3 | Building by hand: make elements, add to bins, link elements and pads | No | `gst_element_factory_make` is bound for internal use only |
| 4 | Request pads and linking in `pad-added` | No | The signal can be handled, but there is nothing to link with |
| 5 | Add, remove or replace elements while playing (pad blocking) | No | |
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
| 13 | Read and write properties | Partial | Scalars, strings, enums, flags; no caps, structure, array or fraction |
| 14 | Pad properties | Partial | `SetPadValue` only, no getter |
| 15 | Handle signals | Done | Caps and structure arguments are not covered |
| 16 | Emit action signals (`split-now`, `get-stats`, ...) | No | Prerequisite for WebRTC |
| 17 | Typed `GstCaps` / `GstStructure` | No | Strings only |
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
| 27 | Pad probes: inspect, drop or block buffers and events | Partial | Internal only (`PipelineGate`) |

## Processing and streaming

| # | Feature | Status | Notes |
|---|---|---|---|
| 28 | Video compositor | Done | `GStreamerCompositor` |
| 29 | Audio mixer | Done | `GStreamerAudioMixer` |
| 30 | Add or remove mixer inputs and layers while running | No | Fixed before `Start` |
| 31 | Property animation: fades, transitions (`GstController`) | No | |
| 32 | Branches at run time: record while previewing (`tee`, `valve`) | No | Possible by hand from a parsed string |
| 33 | Pre-event recording buffer | No | |
| 34 | Source switching (`input-selector`, fallback source) | No | |
| 35 | RTSP server (`gst-rtsp-server`) | No | Separate native library |
| 36 | WebRTC (`webrtcbin`, WHIP, WHEP) | No | Needs 16 and 17 |
| 37 | Network statistics (SRT, RTP, RTSP) | No | Needs 17 |
| 38 | Device hot-plug monitoring (`GstDeviceMonitor`) | No | Listing devices alone would fit the shared enumerator |
| 39 | Zero-copy memory (DMABuf, GL, CUDA) | No | |
| 40 | Editing timeline (GStreamer Editing Services) | No | Separate native library |
| 41 | Elements written in .NET | No | |

## Suggested order

1. 3 and 4: small, and they remove the limits of parsed pipelines.
2. 17 and 16: they unlock 36 and 37 and finish 13 and 15.
3. 27, 5 and 32: dynamic pipelines.
4. 25 and 7.
5. 35 and 36, if network delivery is the goal.
