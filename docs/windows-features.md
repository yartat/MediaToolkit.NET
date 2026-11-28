# Windows media features and what the library covers

Native Windows APIs only: what `MediaToolkitNet.Windows` does with them. Playback, recording and transcoding through FFmpeg or
mpv on Windows do not count here. Taken from the code on 2026-11-28; "no" means the API is not used anywhere in the backend.

Status: **yes** implemented, **partial** some of it, **no** not at all.

## Audio (Core Audio / WASAPI)

| Windows feature | Status | What there is, what is missing |
|---|---|---|
| Capture and render device list | yes | `WasapiDeviceEnumerator` |
| Microphone capture, shared mode | yes | `WasapiCapture` |
| PCM playback, shared mode | yes | `WasapiRenderer` |
| System loopback | yes | `WasapiCapture(loopback: true)` |
| Sound of one process | yes | `WasapiCapture.ForProcess` (Windows 10 2004+) |
| Endpoint volume, mute, peak meter | yes | `AudioEndpointVolume` |
| Application sessions (the Windows mixer) | yes | `AudioSession` |
| Device notifications | partial | `AudioDeviceNotifications`; no test makes the events fire |
| Exclusive mode | yes | `WasapiMode.Exclusive`, capture and render, event driven |
| Low latency (`IAudioClient3`) | yes | `WasapiMode.LowLatencyShared`, `GetEnginePeriods`; mix format only |
| Stream position and clock (`IAudioClock`) | partial | used to drain an exclusive stream; not offered to callers |
| Stream categories, raw mode, ducking | no | |
| Capture effects (echo and noise suppression) | no | |
| Spatial sound (`ISpatialAudioClient`) | no | |
| XAudio2, MIDI | no | |

## Cameras

| Windows feature | Status | What there is, what is missing |
|---|---|---|
| Camera list | yes | Media Foundation and DirectShow |
| Frame capture | yes | `MediaFoundationVideoCapture` |
| Pan, zoom, focus, exposure, brightness and the like | yes | `CameraControls` |
| Still photo at full resolution | no | |
| Infrared and depth cameras, several streams | no | |
| Virtual camera (Windows 11) | no | |

## Screen capture

| Windows feature | Status | What there is, what is missing |
|---|---|---|
| Monitor through Desktop Duplication | yes | `DesktopCapture` |
| Window or monitor through Windows.Graphics.Capture | yes | `GraphicsCapture`, with cursor and border; hiding the border is untested |
| Cursor and dirty rectangles in Desktop Duplication | no | |
| HDR and 10 bit | no | BGRA8 only |

## Files and codecs (Media Foundation)

| Windows feature | Status | What there is, what is missing |
|---|---|---|
| Probing | yes | `MediaFoundationProber` |
| Transcoding | partial | H.264/HEVC and AAC, with trim, scaling and frame rate; no subtitles, no other codecs |
| Recording from your own frames | partial | `MediaFoundationRecorder`: H.264/HEVC, AAC (MJPEG and PCM are mapped, not checked separately) |
| Hardware encoders | partial | enabled, but which encoder ran is not visible and cannot be chosen |
| Listing MFTs (the system's codecs) | no | used internally to check that an encoder exists |
| Hardware decoding with D3D11 (DXVA), zero copy | no | |
| GPU video processor (scaling, colour, deinterlacing) | no | |
| Fragmented MP4 | no | |
| Network sources (HTTP, RTSP) | no | neither implemented nor tried |

## Playback

| Windows feature | Status | What there is, what is missing |
|---|---|---|
| Player (`IMediaPlayer`) | partial | `MediaFoundationPlayer`: Windows plays the sound, frames come back as BGRA; no audio frames, no window output |
| MF Media Engine | yes | what the player is built on; Media Session is not used |
| Playing through a DirectShow graph | partial | `RenderFile` and `Run`, without the player interface |
| Protected content (PlayReady) | no | |

## DirectShow

| Windows feature | Status |
|---|---|
| Graph building, filters, pins, events | yes |
| Frames and audio out of a graph (Sample Grabber) | yes |
| Device list | yes |
| Filters of your own in C# | no |

## Other

| Windows feature | Status |
|---|---|
| Images and thumbnails (WIC, Shell) | no |
| File tags and metadata (Property System) | no |
| System media transport controls (SMTC) | no |
| Casting to devices (Miracast) | no |
| Editing (`Windows.Media.Editing`) | no |

Speech synthesis, speech recognition and OCR are left out: they are not media input or output.

## Order of work

1. ~~A player on the MF Media Engine~~ done (see Playback).
2. ~~Exclusive mode and `IAudioClient3` for low latency~~ done (see Audio).
3. The D3D11 hardware path: decoding and video processor without copies. Large, buys speed.
4. Cursor and dirty rectangles in `DesktopCapture`. Small.
