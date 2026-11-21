# MediaToolkit.NET — bug, resource-leak and security audit

| | |
|---|---|
| Date | 2026-11-20 |
| Branch / commit | `feature/new-version` @ `8f79781` ("Update package versions"), version 0.3.1 |
| Scope | every project under `src/` (Interop, Abstractions, Windows, FFmpeg, GStreamer, Mpv, Linux, MacOS, All) |
| Environments used | Windows 11 (FFmpeg 7.1.5 LGPL, libmpv-2), WSL Debian (FFmpeg 7.1.5, GStreamer 1.26.2, libmpv 2.5) |
| Not run | Linux hardware backends (ALSA, PulseAudio, V4L2) and all of macOS — findings there come from reading the code |

This document is written to be executed by another model, one issue at a time.
Each issue says where the code is, what goes wrong, how to prove it with a
test **before** changing anything, how to fix it and how to know it is fixed.

---

## 0. Rules for whoever fixes these

1. **Read `CLAUDE.md` first.** Its hard rules apply to every fix: English only,
   64-bit layouts, no `[ComImport]`, no hard-coded `AVCodecID`, no `stackalloc`
   in a loop or sized from runtime data, every FFmpeg field access in
   `AbiLayout.cs`, every `.cs` file keeps its `#region Copyright` header.
2. **Do not commit or push.** Leave the changes in the working tree.
3. **One issue per change.** Do not "tidy" neighbouring code.
4. **Test first.** Write the test from the "Test first" paragraph, run it, see it
   fail (or crash) for the stated reason, then fix, then see it pass. If the test
   passes before the fix, stop and report: the finding may be wrong.
5. **The build must stay at 0 warnings, 0 errors:** `dotnet build`.
6. **Run the whole suite after each fix:**
   - Windows unit: `dotnet test tests/MediaToolkitNet.Tests`
   - Windows integration: set `MEDIATOOLKITNET_FFMPEG_DIR` to the FFmpeg `bin`
     folder and prepend it and the libmpv folder to `PATH` (see `CLAUDE.md`,
     "Environment notes"), then `dotnet test tests/MediaToolkitNet.IntegrationTests`.
   - WSL: copy the tree without `bin`/`obj` (script in Appendix A) and run
     `dotnet test` there; that is the only place GStreamer runs.
   Baseline before this audit: unit 206/206; integration Windows 79 passed,
   12 skipped (GStreamer); WSL 206 + 93 passed.
7. **Exercise the change**, not just the tests: the sample CLI commands in
   `CLAUDE.md` are how everything here was validated.

### Status legend

| Status | Meaning |
|---|---|
| **Confirmed** | Reproduced during this audit; the evidence is quoted. |
| **Certain** | Deterministic from the code; no timing or special input needed. |
| **Likely** | Follows from the code but needs a timing window or a device state to show. |
| **Hypothesis** | Plausible; the first step is the test that proves or refutes it. |

### Severity

**Critical** – data loss or memory corruption reachable through normal use.
**High** – security impact, or a crash/UB reachable from input data or common
API use. **Medium** – wrong output, leak, or crash in an uncommon path.
**Low** – hygiene that can turn into one of the above.

---

## 1. Summary

| ID | Sev. | Status | Title |
|---|---|---|---|
| [DL-1](#dl-1) | Critical | Confirmed | An output path that is a link to an input overwrites the input (all three transcoders) |
| [DL-2](#dl-2) | High | Confirmed | A failed or refused job deletes a file that already existed at the output path (all three transcoders) |
| [SEC-1](#sec-1) | High | Confirmed | No input protocol/format policy: a playlist reads any local media file into the output |
| [SEC-2](#sec-2) | High | Certain | Native library search walks up to 8 parent folders for `src/FFMpeg/<platform>` (DLL planting) |
| [SEC-3](#sec-3) | Medium | Certain | Bare-name `NativeLibrary.TryLoad` fallback uses the default Windows search order (CWD) |
| [SEC-4](#sec-4) | Medium | Hypothesis | `MpvPlayer` leaves mpv's `ytdl` hook and script loading on |
| [SEC-5](#sec-5) | Info | Certain | Custom filter strings are code, not data |
| [MEM-1](#mem-1) | High | Confirmed | `GStreamerSink` builds planes from caps and never checks the buffer size (AccessViolation) |
| [MEM-2](#mem-2) | High | Likely | Same unchecked plane geometry in DirectShow, Media Foundation and V4L2 producers |
| [MEM-3](#mem-3) | High | Confirmed | FFmpeg audio frames are described with the format known at open, not the frame's |
| [MEM-4](#mem-4) | Low | Refined | `FFmpegFilterGraph.Push(AudioFrame)` with more than 8 planar channels reads wild pointers |
| [MEM-5](#mem-5) | High | Likely | Stop/Dispose free native state after a 2-second join timeout (use-after-free) — 7 classes |
| [MEM-6](#mem-6) | High | Certain | Exceptions from `Failed` handlers escape native callbacks / worker threads and end the process |
| [MEM-7](#mem-7) | High | Likely | `FFmpegPlayer.Seek` while paused runs on the caller's thread concurrently with the worker |
| [MEM-8](#mem-8) | High | Hypothesis | `AVFoundationVideoCapture.Dispose` frees the GCHandle while a callback may be in flight |
| [MEM-9](#mem-9) | Medium | Likely | `MediaFoundationVideoCapture.Dispose` releases the reader before shutting the source down |
| [MEM-10](#mem-10) | Medium | Certain | `DirectShowSampleGrabber`: leaks on constructor failure, races on standalone dispose |
| [MEM-11](#mem-11) | Medium | Likely | `MpvPlayer` reads `_handle` without the lock while `Dispose` destroys it |
| [BUG-1](#bug-1) | Medium | Confirmed | `TryReadAt` on MPEG-TS returns the next key frame after the requested time |
| [BUG-2](#bug-2) | Medium | Not reproduced | FFmpeg transcoder aborts on a packet of a stream that appeared after open |
| [BUG-3](#bug-3) | Medium | Certain | Unrecognised encoder options are silently dropped |
| [BUG-4](#bug-4) | Medium | Certain | Mid-stream format change aborts the whole FFmpeg transcode; audio checks only the sample format |
| [BUG-5](#bug-5) | Medium | Certain | Audio retiming does not drop non-increasing timestamps |
| [BUG-6](#bug-6) | Low | Certain | One subtitle event that fails to encode aborts the whole job |
| [BUG-7](#bug-7) | Medium | Certain | A capture whose loop failed stays "running" forever (WASAPI, V4L2, ALSA, Pulse) |
| [BUG-8](#bug-8) | Medium | Certain | `WasapiRenderer.Write` loops forever when the client is not started |
| [BUG-9](#bug-9) | Medium | Certain | `WasapiRenderer` declares planar formats as interleaved |
| [BUG-10](#bug-10) | Medium | Hypothesis | `AlsaCapture.Stop` raises a spurious `Failed` |
| [BUG-11](#bug-11) | Medium | Hypothesis | `AudioQueueCapture` delivers nothing after Stop → Start |
| [BUG-12](#bug-12) | Low | Certain | mpv `lavfi=[...]` quoting breaks on a `]` inside the chain |
| [BUG-13](#bug-13) | Medium | Likely | Media Foundation capture: negative default stride, ignored lock length, silent format fallback |
| [BUG-14](#bug-14) | Low | Certain | Media Foundation recorder does not check frame format against the stream format |
| [LEAK-1](#leak-1) | Medium | Certain | `FFmpegDemuxer.Open` leaks the context when `ValidateOpenInput` throws |
| [LEAK-2](#leak-2) | Medium | Certain | `FFmpegRecorder` leaks contexts on partial construction |
| [LEAK-3](#leak-3) | Medium | Certain | `FFmpegMediaReader.Open` leaks readers when a later one fails |
| [LEAK-4](#leak-4) | Medium | Certain | `DirectShowGraph` keeps every wrapper it ever handed out; constructor leaks on QI failure |
| [LEAK-5](#leak-5) | Low | Certain | `V4l2VideoCapture` never unmaps buffers mapped before a failure |
| [LEAK-6](#leak-6) | Low | Certain | `FFmpegPlayer.Play` after `Ended` replaces the `CancellationTokenSource` without disposing it |
| [LEAK-7](#leak-7) | Low | Certain | `DirectShowDeviceEnumerator.ReadStringProperty` does not clear non-BSTR variants |
| [LEAK-8](#leak-8) | Low | Certain | COM initialisation is never balanced |
| [HYG-1](#hyg-1) … [HYG-9](#hyg-9) | Low | — | Hygiene items (section 8) |

Verified **not** to be problems are listed in section 9 so nobody re-opens them.

### Suggested order

1. DL-1 and DL-2 together (one design: write to a temporary file, rename on success).
2. MEM-1, MEM-2, MEM-3 (crashes reachable from media data).
3. MEM-6 (one-line wraps, many places), then MEM-5 (the teardown pattern).
4. SEC-1, SEC-2, SEC-3, SEC-4.
5. BUG-1 … BUG-5, LEAK-1 … LEAK-4.
6. Everything else.

---

## 1a. Fix log (TDD)

Every fix below followed red → fix → green; the test is named. Windows run =
FFmpeg 7.1.5 + libmpv; WSL run = all three backends.

| Issue | Test | State |
|---|---|---|
| DL-1, DL-2 | `AuditDataLossTests` (6) | **fixed** — `PartialOutput` (Abstractions) used by all three transcoders |
| SEC-1 | `AuditFFmpegTests.AnHlsPlaylist…` | **fixed** — playlist demuxers refused unless `allowPlaylists`; protocol whitelist and interrupt callback still open |
| SEC-2 | `NativeSearchPathsTests` | **fixed** — ancestor must contain `MediaToolkit.NET.slnx` |
| SEC-3 | `NativeSearchPathsTests` | **fixed** — relative/empty `PATH` entries dropped; Windows fallback no longer uses the default search order |
| SEC-4 | `AuditMpvQuotingTests` | **fixed** |
| BUG-12 | `AuditMpvQuotingTests` | **fixed** |
| MEM-1 | `AuditGStreamerSinkTests` (child process) | **fixed** for video; audio cannot overrun (samples derive from length) |
| MEM-3 | `AuditFFmpegTests.AudioFrames…` | **fixed** — `AbiLayout.AudioFormatOf`; the reader's conversion graph is still built once per seek |
| BUG-1 | `AuditFFmpegTests.ReadingAtATime…` | **fixed** — `SeekToFrameAtOrBefore` |
| BUG-3 | `AuditFFmpegTranscodeTests.AnEncoderOption…` | **fixed** for the FFmpeg transcoder (warning); recorder and mpv still open |
| BUG-4 | `AuditFFmpegTranscodeTests.AVideoThatChangesSize…` | **fixed for video**; the audio rate/channel check is still open |
| MEM-4 | `AuditFFmpegTranscodeTests.PlanarAudio…` | **fixed**; it was *not* a wild read: libavfilter refused the frame with an FFmpeg error. Now an `ArgumentException` up front |
| MEM-6 | `AuditPlayerTests.AThrowingFailedHandler…` (child process, `CrashProbe`) | **fixed** — `SafeEvents.RaiseFailed` used by every backend's worker/callback; macOS code changed without a run |
| MEM-5 (FFmpeg player, mpv not yet) | `AuditPlayerTests.DisposingFromInside…` | **fixed for `FFmpegPlayer`** — a dispose from the worker, or one that cannot join, leaves the media for the worker to close. Other six classes still open |
| MEM-7, LEAK-6 | `AuditPlayerTests.SeekingWhilePaused…` | **fixed** — only the worker touches the contexts while it exists; 15 runs in a row green |
| BUG-5 | `AuditFFmpegTranscodeTests.AudioWhoseTimestampsJumpBack…` | **fixed**; the real first failure was different: the audio check compared the frame with the *container's* sample format, so any MP2 input failed. `Check` now follows the frame and rebuilds the graph (this also completes the audio half of BUG-4) |
| MEM-2 | `FrameBoundsTests` (4 unit) + `AuditGStreamerSinkTests` | **fixed** — `FrameBounds.FitsIn` used by GStreamer, DirectShow, Media Foundation (also BUG-13 negative stride), V4L2 (also the error flag) and AVFoundation (null planes, lock result). `ds-grab` on WAV and a bottom-up BGR24 H.264 file still delivers every frame. Only the helper and GStreamer have a red test; the other producers need a device |
| BUG-8 | `AuditPlayerTests.WritingBeforeStart…` (child process, default render endpoint) | **fixed** — red on this machine, green after |
| LEAK-1, LEAK-2, LEAK-4, LEAK-5, LEAK-7, LEAK-8, HYG-1 | — (no test possible, fixed by review at the user's request) | **fixed**. LEAK-1: `ValidateOpenInput` inside the `try`. LEAK-2: constructor and stream-adding paths free their contexts. LEAK-4: wrappers untrack themselves on dispose, `Describe` disposes what it enumerates, constructor releases on a failed QueryInterface. LEAK-5: V4L2 mappings kept as made (+ `O_CLOEXEC`). LEAK-7: `VariantClear` for non-BSTR values. LEAK-8: `Ole32.Initialize` counts `S_FALSE`; the WASAPI and Media Foundation worker threads balance it. HYG-1: `NativeMemory` for the mpv argument vector (zeroed, which the old `stackalloc` was not). Checked on Windows: `ds-filters`, `ds-grab` (bottom-up H.264), `devices`, `rec-audio-file`, `rec-video` |
| MEM-11 | `AuditPlayerTests.StoppingWhileAnotherThread…` (child process, 150 rounds) | **fixed** — red was a real `0xC0000005` in `Volume`→`SetProperty`; native calls now run under a read lock, `mpv_terminate_destroy` under the write lock. 3 green runs in a row |
| SEC-1 (protocols) | `AuditFFmpegTests.ProbingDoesNotOpenAConnection…` | **fixed** — `protocol_whitelist=file,pipe,crypto,data` unless the caller allows network (`allowNetwork`, or `FFmpegDemuxer.AllowNetworkByDefault`); the player allows it. Red: the probe connected and then hung on a silent server. **Still open:** an interrupt callback to cancel a network read that is already running (needs an offset probe in `AbiLayout`) |
| MEM-3 / BUG-4 (reader) | `AuditFFmpegTests.AudioThatChanges…` (2 red) + `AVideoThatChangesSize…` (2, green both before and after) | **fixed** — `FrameStreamReader` keys every decoded frame by its format; on a change the old graph is flushed and delivered first, then a new graph is built for the new format. Red: with the key held constant the two audio tests fail (the converting one with `av_buffersrc_add_frame_flags: Invalid argument`). The video tests never failed, so FFmpeg 7.1 copes there; they stay as guards. An MP2 decoder may emit one frame with the new channel count and the old rate, which the test tolerates |
| SEC-1 (interrupt) / MEM-5 for network reads | `AuditPlayerTests.DisposingAPlayerThatIsWaiting…` (child process; HTTP server that sends the start of a file and goes silent) + `AuditNetworkTests` (the normal path) | **fixed** — red: `Dispose` took 2013 ms, the whole join timeout. A network input is now opened with `avio_open2`, which takes the interrupt callback as a parameter, so no `AVFormatContext` offset is needed; `FFmpegDemuxer.Interrupt()` / `ClearInterrupt()`; `FFmpegPlayer.Stop` interrupts before it joins and does not report the interrupted read as `Failed`. Green three runs in a row. Limits: only inputs with `://` are pre-opened (files cannot block), and a connect that is already in progress inside `Open` cannot be interrupted, because the demuxer does not exist yet |
| BUG-3 (mpv) | `AuditMpvTranscodeTests.AnEncoderOptionNobodyUnderstood…` | **fixed** — red: the job succeeded and `Warnings` was empty. mpv logs `AVOption 'x' not found.` at *error* level and carries on, so the transcoder now reads that line, reports the option once as a warning, and keeps it out of the list of errors that explains a failure |
| BUG-9 | — | **not an issue**: `Format` already reports the interleaved layout the endpoint was opened with |
| LEAK-3 | — | **not an issue**: the stream readers open the file lazily, their constructors allocate nothing native (CA2000 is a false positive) |
| BUG-3 (recorder) | — | **left as is**: the recorder's `EncoderOptions` apply to every stream, so an option one encoder does not know is normal |
| BUG-2 | `AuditFFmpegTranscodeTests.AStreamThatAppears…` | **not reproduced**: an audio PID added by a later PMT (12 Mbit/s video, 6 MB before it) did not stop the job. Test kept as a guard; the code path may still fail for FLV |

## 2. Data loss

<a id="dl-1"></a>
### DL-1 — An output path that is a link to an input overwrites the input

- **Severity:** Critical · **Status:** Confirmed (WSL, all three backends)
- **Where:**
  - `src/MediaToolkitNet.Abstractions/Transcoding/TranscodePlan.cs:245-249` — the
    only "output is an input" check compares `Path.GetFullPath` strings.
  - Every backend then opens the output for writing with truncation:
    FFmpeg (`FFmpegMuxer.WriteHeader` → `avio_open`), GStreamer (`filesink`),
    mpv (`o=` option).
- **What happens:** a symbolic link, a hard link, a second mount, an 8.3 short
  name, a UNC or mapped-drive path, or different case on a case-insensitive
  volume all name the same file with a different string. The check passes, the
  backend truncates the file it is still reading.
- **Evidence** (`transcode src.mkv alias.mkv` where `alias.mkv -> src.mkv`):

  | backend | input before | input after | job result |
  |---|---|---|---|
  | ffmpeg | 192038 bytes | 31094 bytes (now the output) | reported **success** |
  | gstreamer | 192038 bytes | **0 bytes** | failed: "Can't typefind empty stream" |
  | mpv | 192038 bytes | 170606 bytes (now the output) | reported success |

- **Test first** (integration, `TranscodingTests`, `GStreamerTranscodingTests`,
  `MpvTranscodingTests`; skip on Windows when symlink creation needs privilege):
  ```csharp
  var input = media.WriteMovie("movie.mkv");
  var before = File.ReadAllBytes(input);
  var alias = media.PathTo("alias.mkv");
  File.CreateSymbolicLink(alias, input);
  var request = new TranscodeRequest(alias) { Inputs = [input], Streams = [/* re-encode video to Mjpeg */] };
  try { await transcoder.RunAsync(request); } catch (Exception) { /* refusal is acceptable */ }
  File.ReadAllBytes(input).Should().Equal(before);
  ```
  Add a hard-link variant on Linux (`ln` through `Process.Start`, or skip).
- **Fix** (shared with DL-2):
  1. Add one helper in Abstractions, e.g. `TranscodeOutput` / `PartialFile`, that
     for a plain local output path returns a temporary path **in the same
     directory** that keeps the extension (the muxer is chosen from it):
     `Path.Combine(dir, $".{name}.partial-{Guid.NewGuid():N}{ext}")`.
  2. Each backend writes to that temporary path. On success:
     `File.Move(temp, request.Output, overwrite: true)`. A rename replaces the
     directory entry, so a symlink or hard link at the output path is replaced
     and the input it pointed to is untouched.
  3. On failure or cancellation: delete only the temporary file.
  4. mpv's `VerifyOutput` and any probe of the result must probe the temporary
     path before the rename.
  5. Keep the existing string check: it still gives a clear refusal for the
     plain case.
  6. If the output is not a plain local path (FFmpeg accepts URLs such as
     `pipe:`), keep today's behaviour and do not rename.
- **Done when:** the three new tests pass; existing transcoding tests pass on
  Windows and WSL; `transcode` in the sample still writes and probes its output.

<a id="dl-2"></a>
### DL-2 — A failed or refused job deletes a file that already existed at the output path

- **Severity:** High · **Status:** Confirmed (WSL, all three backends)
- **Where:**
  - `src/MediaToolkitNet.FFmpeg/Transcoding/FFmpegTranscoder.cs:158-167` (`finally`
    → `TryDelete(request.Output)`, `TryDelete` at :527) — runs whenever the muxer
    object exists, although `avio_open` only happens later in `WriteHeader`.
  - `src/MediaToolkitNet.GStreamer/Transcoding/GStreamerTranscoder.cs:304-314` —
    the `finally` also covers `Parse`, so even a **refusal**
    (`TranscodeRejectedException` thrown inside the `try`) deletes the file.
  - `src/MediaToolkitNet.Mpv/Transcoding/MpvTranscoder.cs:506-516`.
- **Evidence:** `keep.mkv` containing "precious bytes", then a job with an
  invalid custom filter `--vf no_such_filter_xyz`: all three backends fail and
  `keep.mkv` is **deleted** in every case, although none of them had opened it.
- **Second defect in the same lines:** the cleanup catches only `IOException`. A
  read-only output or a locked directory throws `UnauthorizedAccessException`
  from inside `finally`, which replaces the job's real exception.
- **Test first:** write a file at the output path, run a job that is refused
  (GStreamer: custom filter naming a missing element) or fails (FFmpeg: custom
  filter `no_such_filter_xyz`), assert the file still has its bytes.
- **Fix:** the temporary-file scheme of DL-1 makes the `finally` delete only its
  own temporary file. Catch `IOException` **and** `UnauthorizedAccessException`
  there.
- **Done when:** the tests pass for all three backends and the cancelled-job
  tests (`ACancelledJobLeavesNoFileBehind`) still pass.

---

## 3. Security

<a id="sec-1"></a>
### SEC-1 — No input protocol/format policy; a playlist reads any local media file

- **Severity:** High · **Status:** Confirmed (local file read); network part refuted for nested URLs
- **Where:** every `avformat_open_input` goes through
  `src/MediaToolkitNet.FFmpeg/FFmpegDemuxer.cs:94` with no `protocol_whitelist`,
  `format_whitelist` or `AVIOInterruptCB`. Callers: `FFmpegProber`, `FFmpegPlayer`,
  `FFmpegMediaReader` (prober + per-stream demuxers), `FFmpegTranscoder` (main and
  unseeked subtitle demuxer). GStreamer treats any input containing `://` as a URI
  (`GStreamerGraph.cs:136`, `GStreamerProber.cs:101`); mpv's `loadfile` takes
  URLs and its own pseudo-protocols (`edl://`, `av://`, `memory://`).
- **What happens:** a service that transcodes or probes files supplied by users
  is given `x.m3u8` (an HLS playlist is "a media file" to FFmpeg). The playlist
  names any local file by absolute path; FFmpeg reads it and the transcoder copies
  its streams into the output that is returned to the user.
- **Evidence (WSL, FFmpeg 7.1.5):**
  - `local.m3u8` naming `/tmp/secret/private.ts` (outside the playlist's folder):
    `probe` opens it (`[hls] Opening '/tmp/secret/private.ts' for reading`) and
    `transcode local.m3u8 out.mkv` writes its video and audio into `out.mkv`.
  - `net.m3u8` naming `http://127.0.0.1:8765/seg.ts`: **blocked by FFmpeg itself**
    (`Protocol 'http' not on whitelist 'file,crypto,data'`). Nested network
    requests from a local playlist are not the problem; local file reads are.
  - A top-level input string is still a URL: `http://169.254.169.254/...`,
    `tcp://`, `udp://`, `concat:`, `subfile,,start,...:` are all opened as given.
  - With no interrupt callback a network read cannot be cancelled, which feeds
    MEM-5 (the player's worker cannot be stopped within the join timeout).
- **Test first** (integration, FFmpeg): write a TS segment to a second temporary
  folder, write a playlist in the first folder that names it by absolute path,
  probe the playlist with the new "untrusted input" policy, and assert it throws.
  A second test: input `"http://127.0.0.1:1/x.ts"` with the policy throws without
  connecting.
- **Fix:**
  1. Add an input policy, e.g. `FFmpegInputPolicy { AllowedProtocols, AllowedFormats }`,
     exposed on `MediaReaderOptions`, the prober and `TranscodeRequest` (or a
     static default on the FFmpeg backend — choose one place and document it).
  2. In `FFmpegDemuxer.Open`, put `protocol_whitelist` (default `file,pipe`) and,
     when set, `format_whitelist` into the options dictionary passed to
     `avformat_open_input`. A whitelist of formats is what stops the HLS case:
     `protocol_whitelist=file` still lets HLS read local files.
  3. Prefix plain local paths with `file:` before opening, so a file named
     `concat:a|b` or `c:x` is not parsed as a protocol.
  4. Install an `AVIOInterruptCB` (callback checking a volatile flag the owner
     sets on Stop/Dispose/cancel) — needed for MEM-5 anyway.
  5. Keep the player permissive by default if you must (it plays URLs), but say so.
  6. GStreamer/mpv: refuse inputs that are not existing local files unless the
     caller opts in; mpv keeps `load-unsafe-playlists=no` (its default).
  7. Document the policy in `README.md` and `docs/package/README.md`.
- **Done when:** both tests pass, existing tests pass, and the README says what is
  allowed by default.

<a id="sec-2"></a>
### SEC-2 — Native library search walks up the directory tree (DLL planting)

- **Severity:** High · **Status:** Certain
- **Where:** `src/MediaToolkitNet.Interop/NativeSearchPaths.cs:70-72` — from
  `AppContext.BaseDirectory` up to 8 parents, any `<ancestor>/src/FFMpeg/<platform>`
  is added to the search list ("development convenience").
- **What happens:** for an application installed in
  `C:\Program Files\Vendor\App\`, the walk reaches `C:\`. On a default Windows
  install any authenticated user may create `C:\src\FFMpeg\Windows\` and drop an
  `avcodec-61.dll` there. The next time the application runs — possibly elevated
  or as a service — it loads that DLL: local privilege escalation. On Linux the
  same applies to any writable ancestor (for example a shared `/opt` tree).
- **Test first** (unit, `tests/MediaToolkitNet.Tests`; add a project reference to
  `MediaToolkitNet.Interop` if missing): build a temporary tree
  `<root>/src/FFMpeg/<Platform>/` and `<root>/a/b/c/`, call the resolver with
  `<root>/a/b/c` as the base directory (make the base directory a parameter of an
  internal overload), assert the FFMpeg folder is **not** returned. Then put a
  `MediaToolkit.NET.slnx` file in `<root>` and assert it **is** returned.
- **Fix:** only accept an ancestor that also contains the repository marker
  `MediaToolkit.NET.slnx` (or `global.json` **and** the `src/FFMpeg` folder), or
  only walk when an explicit opt-in such as `MEDIATOOLKITNET_DEV_SEARCH=1` is set.
  Prefer the marker: the sample and the tests keep working unchanged.
- **Done when:** the unit test passes; the sample's `backends` command still finds
  FFmpeg from the repository on Windows.

<a id="sec-3"></a>
### SEC-3 — Bare-name load fallback uses the default Windows search order

- **Severity:** Medium · **Status:** Certain
- **Where:** `src/MediaToolkitNet.Interop/NativeModule.cs:74`
  (`NativeLibrary.TryLoad(candidate, out var systemHandle)` with a bare file name).
- **What happens:** the single-argument overload ends in `LoadLibraryEx` with
  default flags, so after the application and system folders Windows searches the
  **current directory** and then `PATH`. FFmpeg, libmpv and GStreamer DLLs are not
  in System32, so a matching DLL in the working directory (a downloads or media
  folder the user opened) is loaded. Linux `dlopen` does not search the current
  directory unless `LD_LIBRARY_PATH` has an empty entry, so this is Windows-only.
- **Constraint:** the documented workflow relies on `PATH` (CLAUDE.md: prepend the
  FFmpeg `bin` to `PATH` for the sample), so simply switching to
  `DllImportSearchPath.SafeDirectories` would break it.
- **Test first:** unit-test a new helper that turns `PATH` into candidate full
  paths: relative and empty entries (which mean the current directory) are
  dropped, absolute entries are kept in order.
- **Fix (Windows only):** instead of the bare-name call, walk the absolute `PATH`
  entries yourself and load by full path; for system libraries use
  `NativeLibrary.TryLoad(name, assembly, DllImportSearchPath.System32, out h)`.
  Keep the bare-name call on Linux/macOS.
- **Done when:** the helper test passes and `backends` still finds FFmpeg and mpv
  through `PATH` on Windows.

<a id="sec-4"></a>
### SEC-4 — `MpvPlayer` leaves the ytdl hook and script loading on

- **Severity:** Medium · **Status:** Hypothesis (code certain, impact unproven)
- **Where:** `src/MediaToolkitNet.Mpv/MpvPlayer.cs:51-58` (default `Options`).
  Compare `MpvTranscoder.cs:217-220` and `MpvProber.cs:64-70`, which set
  `load-scripts=no` and `ytdl=no`. `MpvTranscoder.cs:606` (encoder-list client)
  sets neither.
- **What happens:** mpv's built-in `ytdl_hook` runs for `http(s)` URLs and starts
  a `yt-dlp`/`youtube-dl` process. On Windows `CreateProcess` without an
  application name searches the host executable's folder and the **current
  directory** before `PATH`, so a planted `yt-dlp.exe` runs inside the host's
  security context when the user plays a URL.
- **Test first (manual, Windows):** put a `yt-dlp.exe` stub that writes a marker
  file into a temporary folder, make it the current directory, call
  `new MpvPlayer().Open("https://example.invalid/x")`, wait 2 s, check whether the
  marker exists. If it does not, downgrade this to Low and only do the fix below.
- **Fix:** add `["ytdl"] = "no"`, `["load-scripts"] = "no"` and `["config"] = "no"`
  to the player's default options (callers can still override through `Options`);
  add the same two to the encoder-list client at `MpvTranscoder.cs:606`.
  Mention it in the XML doc of `Options`.
- **Done when:** the player still plays a local file (sample `play` command, if
  present, or the mpv integration tests) and the test above, if it showed the
  marker, no longer does.

<a id="sec-5"></a>
### SEC-5 — Custom filter strings are code

- **Severity:** Info · **Status:** Certain
- **Where:** `VideoFilter.Custom` / `AudioFilter.Custom`
  (`Abstractions/Transcoding/Filters.cs`), `FFmpegFilterGraph.ForVideo/ForAudio(…, description)`,
  `GStreamerGraph.cs:451-452` (custom description spliced verbatim),
  `MpvFilterChain`.
- **What happens:** FFmpeg `movie=`/`amovie=` read any file or URL; a GStreamer
  description can add `filesrc`/`filesink`/`souphttpsrc`. That is by design, but it
  is not documented.
- **Fix:** add a `<remarks>` to each of these APIs and a sentence to the README:
  "a custom filter is executed as written; never build one from untrusted text".
  No code change.

---

## 4. Memory safety

<a id="mem-1"></a>
### MEM-1 — `GStreamerSink` builds planes from caps without checking the buffer

- **Severity:** High · **Status:** Confirmed (WSL, process abort)
- **Where:** `src/MediaToolkitNet.GStreamer/GStreamerSink.cs:188-199` (video path;
  `length` is read at :189 and used only for audio) and `BuildPlanes` at :280-300.
  It also ignores `GstVideoMeta` (non-default strides and offsets).
- **Evidence:**
  ```
  gst-run "fakesrc num-buffers=3 sizetype=fixed sizemax=100 filltype=zero !
           capsfilter caps=video/x-raw,format=I420,width=1920,height=1080,framerate=25/1 !
           appsink name=sink" 3
  Fatal error. System.AccessViolationException ... at GStreamerSink.TryPull   (exit 134)
  ```
  The same pipeline with `videotestsrc` works, so the crash is the size mismatch.
- **Test first** (integration, `GStreamerFact`): pull from the pipeline above with
  `TryPullVideo` and assert it throws `MediaToolkitNetException` (or returns false
  and skips) instead of calling the handler. Before the fix the test host dies;
  that is the failing state.
- **Fix:**
  1. Compute the bytes the layout needs: for each plane
     `offset[plane] + |stride| * PlaneHeight(height, plane)`; take the maximum.
  2. If `gst_buffer_get_video_meta` returns a meta (bind it; it is a function, so
     no new `GstLayout` offset is needed — read `n_planes`, `offset[]`, `stride[]`
     only through a verified layout, or simply refuse buffers that carry a meta
     until it is supported), use its offsets/strides.
  3. If the needed size exceeds `length`, throw `MediaToolkitNetException`
     ("the buffer holds N bytes, the caps describe M") without calling the handler.
  4. Do the same for audio: `samples * bytesPerSample * channels` must not exceed
     `length` (it cannot today, because `samples` is derived from `length`, but
     keep the check next to the planar split).
- **Done when:** the test passes and `gst-run` with `videotestsrc` still reports
  the same checksum as before (757307 for the 3-frame 1080p I420 run above).

<a id="mem-2"></a>
### MEM-2 — Same unchecked geometry in the other frame producers

- **Severity:** High · **Status:** Likely (same pattern as MEM-1, not reproduced)
- **Where:**
  - `src/MediaToolkitNet.Windows/DirectShow/DirectShowSampleGrabber.cs:205-230` —
    `length` is checked only for `<= 0` and passed to audio; video planes (and the
    NV12 chroma offset at :230) are computed from the media type alone.
  - `src/MediaToolkitNet.Windows/MediaFoundationVideoCapture.cs` — the buffer
    `Lock` call returns `currentLength`, which is ignored (see also BUG-13).
  - `src/MediaToolkitNet.Linux/V4l2Video.cs:401-420` — `descriptor.BytesUsed` and
    the mapping length `buffer.Length` are ignored; frames flagged
    `V4L2_BUF_FLAG_ERROR` are delivered as good ones.
  - `src/MediaToolkitNet.MacOS/AVFoundation.cs:320` — the result of
    `CVPixelBufferLockBaseAddress` is ignored; on failure the base address is null
    and a null plane with a non-zero stride reaches the handler.
- **Root cause:** `VideoFrame.GetPlane` (`Abstractions/Frames/VideoFrame.cs:80-90`)
  trusts the producer; nothing between the native buffer and the span checks size.
- **Test first:** a unit test of a new shared helper (below) with a frame layout
  that needs more bytes than given → returns false. For DirectShow, a sample with
  a deliberately short `IMediaSample` is hard to build; rely on the helper test
  plus a review that every producer calls it.
- **Fix:** add one helper to Abstractions, e.g.
  `static long VideoFrame.RequiredBytes(VideoFormat format, ReadOnlySpan<int> strides, ReadOnlySpan<long> planeOffsets)`,
  and call it in every producer before constructing the frame; on mismatch skip
  the frame and raise `Failed` once (not per frame). V4L2: also skip buffers with
  the error flag and use `BytesUsed` for compressed formats. AVFoundation: check
  the lock result and skip the frame.
- **Done when:** the helper tests pass; `ds-grab` (Windows), `rec-video 2`
  (Windows camera) and the GStreamer tests still pass.

<a id="mem-3"></a>
### MEM-3 — FFmpeg audio frames carry the format known at open

- **Severity:** High · **Status:** Confirmed (wrong format); out-of-bounds read follows from the code
- **Where:**
  - `src/MediaToolkitNet.FFmpeg/FFmpegPlayer.cs:405-417` → `FFmpegDecoder.DescribeAudio`
    (`FFmpegDecoder.cs:181-182`) takes rate and channels from the decoder's
    open-time `AudioFormat`, only the sample format from the frame.
  - `src/MediaToolkitNet.FFmpeg/Reading/FFmpegMediaReader.cs:657-662` (`OpenCore`
    stores `_decoded`) and `:710-727` (`Deliver` uses it when there is no graph);
    `BuildGraph` at :692-701 also builds the graph's input from `_decoded`.
- **Evidence:** a transport stream whose audio parameters are not known when the
  file is opened (WSL, `late.ts` in Appendix B): `read late.ts` reports
  `audio #1: 115 blocks, 132480 samples of 0 Hz, 0 ch` — the decoder produced
  44.1 kHz mono, the frames said 0 Hz / 0 channels (so `PlaneCount` 0).
- **Out-of-bounds path:** when the real channel count is lower than the open-time
  one (a broadcast switching 5.1 → 2.0, HE-AAC with parametric stereo), an
  interleaved frame's `GetPlane(0)` spans `samples × bytes × reportedChannels`,
  beyond the real buffer; a planar frame gets null plane pointers.
- **Test first** (integration): generate a file whose audio changes channel count
  mid-stream (two `ffmpeg` segments, 2 ch then 1 ch, concatenated as MPEG-TS) and
  assert every delivered `AudioFrame.Format.Channels` equals what `ffprobe -show_frames`
  reports for that frame; also the `late.ts` case: no frame may report 0 Hz.
- **Fix:** add `AbiLayout.AudioFormatOf(AVFrameHead*)` reading `sample_rate` and
  `ch_layout` at the offsets `ProbeAudioFrameFields` already verified
  (`FrameSampleRate`, `FrameChannelLayout`; refuse when `AudioFrameLayoutVerified`
  is false). Use it in `DescribeAudio` and `AudioStreamReader.Deliver`. Rebuild the
  reader's conversion graph when the frame format differs from the graph's input
  (today the graph is built once from the first frame).
- **Done when:** both tests pass; `read` on `av.mkv` reports the same numbers as before.

<a id="mem-4"></a>
### MEM-4 — `FFmpegFilterGraph.Push(AudioFrame)` beyond 8 planar channels

- **Severity:** Medium · **Status:** Certain
- **Where:** `src/MediaToolkitNet.FFmpeg/Filtering/FFmpegFilterGraph.cs:421-446`;
  check `FFmpegRecorder` around `:486` / `:788` for the same pattern.
- **What happens:** `PrepareAudioFrame` declares `Channels` channels, only
  `frame.PlaneCount` (≤ `MediaPlanes.MaxPlanes` = 8) data pointers are set, and
  `extended_data` points at the 8-entry `data` array after `av_frame_unref`.
  `av_buffersrc_add_frame_flags(KEEP_REF)` copies the non-refcounted frame and
  reads `extended_data[8..]`, i.e. the `linesize` integers, as pointers.
  `AudioFrame` cannot describe more than 8 planes at all.
- **Test first** (unit/integration): push a planar `AudioFrame` with
  `Channels = 10` and 8 planes; assert `ArgumentException`. Today it reads wild
  memory (may crash or produce noise).
- **Fix:** in both places, reject a planar frame whose `Channels != PlaneCount`
  with `ArgumentException` ("planar audio with more than 8 channels must be pushed
  interleaved"). Document the limit on `AudioFrame` and `MediaPlanes.MaxPlanes`.
  Optionally make every producer that clamps (`FFmpegPlayer.cs:407`,
  `FFmpegMediaReader.cs:716`, `FFmpegFilterGraph.cs:498`, `GStreamerSink.cs:238`)
  convert such audio to interleaved instead of silently dropping channels 9+.
- **Done when:** the test passes.

<a id="mem-5"></a>
### MEM-5 — Teardown after a 2-second join timeout frees what the worker still uses

- **Severity:** High · **Status:** Likely
- **Where** (each joins with a timeout and then frees native state unconditionally):

  | class | join | then frees |
  |---|---|---|
  | `Windows/WasapiCapture.cs` | :178 | COM clients, event handle |
  | `Windows/MediaFoundationVideoCapture.cs` | :127 | source reader, source (see MEM-9) |
  | `FFmpeg/FFmpegPlayer.cs` | :203 | demuxer, decoders |
  | `Mpv/MpvPlayer.cs` | :501 | `mpv_terminate_destroy` |
  | `Linux/V4l2Video.cs` | :223 | `munmap`, `close` (in `Dispose`) |
  | `Linux/AlsaAudio.cs` | :403 | `_buffer`, `snd_pcm_close` |
  | `Linux/PulseAudio.cs` | :232 | `_buffer`, `pa_simple_free` |

- **What happens:** the worker is still inside a native call or inside the user's
  frame handler when the timeout expires: a handler that takes longer than 2 s,
  `IMFSourceReader::ReadSample` on an unplugged camera, `pa_simple_read` on a
  suspended source (it blocks until data arrives and nothing interrupts it), a
  network read in the FFmpeg player (no interrupt callback, see SEC-1), or
  `Dispose()` called **from inside a handler** (the worker joins itself and always
  times out). The native objects are freed under the worker → heap corruption or
  a crash later, often far from the cause.
- **Test first** (integration, FFmpeg player — the only one runnable everywhere):
  subscribe `VideoFrameDecoded` with a handler that sleeps 3 s on the first call,
  start playback, call `Dispose()` from the test thread after the handler has
  entered; run under the test host with `DOTNET_gcServer=0`. Expected today: an
  `AccessViolationException` or garbage. A second test calls `Dispose()` from
  inside the handler.
- **Fix pattern** (apply per class; do not invent a shared base class):
  1. **Unblock first:** WASAPI — signal the event; MF — `IMFMediaSource::Shutdown`
     before joining (MEM-9); FFmpeg — set the interrupt flag (SEC-1 step 4); mpv —
     `mpv_wakeup` (already there); ALSA — `snd_pcm_drop` (already there); Pulse —
     nothing interrupts `pa_simple_read`, so read in smaller blocks (≤ 20 ms).
  2. **If `Thread.CurrentThread == worker`**, do not join: mark disposal pending
     and let the worker free everything when its loop returns.
  3. **Join without a timeout**, or, if a timeout is kept, when the join fails
     **do not free** the native objects — leak them and report it (a leak is
     recoverable, a use-after-free is not).
- **Done when:** both tests pass for the FFmpeg player; each class has been
  reviewed against the three steps; nothing else in the suite changes.

<a id="mem-6"></a>
### MEM-6 — Exceptions escape native callbacks and worker threads

- **Severity:** High · **Status:** Certain
- **Where:**
  - `[UnmanagedCallersOnly]` callbacks (an exception there terminates the process):
    `MacOS/AVFoundation.cs:299-303` and `MacOS/AudioQueueAudio.cs:303-307` invoke
    `Failed` inside `catch` with no protection; `MacOS/AudioQueueAudio.cs:167`
    calls `_free.Release()`, which throws `ObjectDisposedException` once
    `Dispose` (:188) has disposed the semaphore.
  - Worker threads (an unhandled exception on any thread also ends the process):
    the `catch` blocks that raise `Failed` in `WasapiCapture.cs:242`,
    `V4l2Video.cs:395-397`, `AlsaAudio.cs:447-449`, `PulseAudio.cs:263-266`,
    `MpvPlayer.cs:401-405`, `FFmpegPlayer` and `MediaFoundationVideoCapture`
    worker loops.
- **What happens:** a subscriber's `Failed` handler that throws (or a frame
  handler that throws, which leads to `Failed`) brings the whole process down.
  `SampleGrabberCallback` already follows the right rule (CLAUDE.md, "Nothing may
  throw across the boundary").
- **Test first** (integration, FFmpeg player): a `VideoFrameDecoded` handler that
  throws and a `Failed` handler that throws. Today the test host crashes; after the
  fix `State` becomes `Faulted` and the process lives. Run it in a child process
  (the sample CLI, or `dotnet test --blame-crash`) so the failing state is visible.
- **Fix:** wrap every `Failed?.Invoke(...)` raised from a worker or callback in
  `try { … } catch (Exception) { /* a subscriber's exception must not end the process */ }`.
  In `OnBufferPlayed`, return early when the renderer is disposed and catch
  `ObjectDisposedException` around `Release`.
- **Done when:** the test passes; a grep for `Failed?.Invoke` shows every call from
  a non-caller thread wrapped.

<a id="mem-7"></a>
### MEM-7 — `FFmpegPlayer.Seek` while paused races the worker

- **Severity:** High · **Status:** Likely
- **Where:** `src/MediaToolkitNet.FFmpeg/FFmpegPlayer.cs:222-238` (`Seek` calls
  `ApplyPendingSeek()` on the caller's thread when paused), `:180` (`Pause` only
  sets state; it does not wait for the worker to park), `:432` (`ApplyPendingSeek`
  touches the demuxer and decoders).
- **What happens:** right after `Pause()`, the worker can still be inside
  `av_read_frame` / `avcodec_send_packet`; the caller's seek flushes the same
  `AVFormatContext` / `AVCodecContext` concurrently. FFmpeg contexts are not
  thread-safe → corruption or crash.
- **Test first:** loop 200 times: `Play()`, `Pause()`, `Seek(random)` on a short
  file; run with the FFmpeg debug check `AVCodecContext` locking (or just observe
  a crash/`Failed`). Expect intermittent failure today.
- **Fix:** never touch the contexts on the caller's thread. `Seek` stores the
  target and signals the worker; a paused worker waits on a `ManualResetEventSlim`
  / `Monitor.Wait` and wakes up to apply the seek, decode the first frame (for the
  paused preview), and park again. `Pause` should return only once the worker is
  parked, or `Seek` must wait for that.
- **Done when:** the loop test runs clean 10 times in a row; existing player
  behaviour (the sample's play/seek, if any) unchanged.

<a id="mem-8"></a>
### MEM-8 — AVFoundation dispose races an in-flight sample callback

- **Severity:** High · **Status:** Hypothesis (macOS never run)
- **Where:** `src/MediaToolkitNet.MacOS/AVFoundation.cs:363-414` (`Dispose`) and
  `:281-304` (`OnSampleBuffer`).
- **What happens:** `setSampleBufferDelegate:nil queue:nil` does not wait for a
  callback already running on the dispatch queue. That callback may have read
  `*slot` before it was zeroed and then call `GCHandle.FromIntPtr` on a handle
  `Dispose` has just freed.
- **Fix:** after `stopRunning` and removing the delegate, drain the queue with
  `dispatch_sync_f(_queue, null, &Noop)` (bind `dispatch_sync_f` from
  libdispatch; `Noop` is an `[UnmanagedCallersOnly]` static that does nothing),
  then zero the slot, release, and free the handle.
- **Test:** on macOS, start a capture whose handler sleeps 100 ms, `Dispose` from
  another thread 1000 times; no crash. Until macOS is available, review only.

<a id="mem-9"></a>
### MEM-9 — Media Foundation capture releases the reader before shutting down the source

- **Severity:** Medium · **Status:** Likely
- **Where:** `src/MediaToolkitNet.Windows/MediaFoundationVideoCapture.cs:369-385`
  (`Dispose`; `ShutdownSource` at :382 runs after the reader is released) and the
  2-s join at :127.
- **What happens:** a `ReadSample` blocked on a stalled or unplugged camera is not
  released by anything before the join times out; the reader is then released
  under the worker (MEM-5).
- **Fix:** order: set the stop flag → `IMFMediaSource::Shutdown` (makes the
  pending `ReadSample` return `MF_E_SHUTDOWN`) → join → release reader → release
  source.
- **Test:** manual — start `rec-video 30`, unplug the camera, stop; no crash.

<a id="mem-10"></a>
### MEM-10 — `DirectShowSampleGrabber` constructor leak and standalone dispose race

- **Severity:** Medium · **Status:** Certain
- **Where:** `src/MediaToolkitNet.Windows/DirectShow/DirectShowSampleGrabber.cs:47-49`
  (constructor: the callback object holds a **strong** `GCHandle`; if
  `SetCallback` fails, neither the callback nor `_grabber` is released) and :327
  (`Dispose` when the grabber is not owned by a graph that has been stopped:
  `SampleCb` may still be running on the streaming thread and resolve the
  grabber through a pointer `Dispose` is releasing).
- **Fix:** wrap the constructor body after the callback is created in
  `try { … } catch { callback.Release(); _grabber.Dispose(); throw; }`. In
  `Dispose`, when the owning graph is running, stop it first (or document that the
  graph must be stopped and throw `InvalidOperationException` otherwise).
- **Test:** unit-level is not practical; review plus `ds-grab` on a WAV and an
  H.264 file.

<a id="mem-11"></a>
### MEM-11 — `MpvPlayer` uses `_handle` outside the lock

- **Severity:** Medium · **Status:** Likely
- **Where:** `src/MediaToolkitNet.Mpv/MpvPlayer.cs` — `Volume` setter (:97),
  `Pause` (:158), `Stop` (:170), `EnsureHandle` fast path (:292) read `_handle`
  without `_gate`; `Dispose` (:486-506) nulls it under the lock and destroys it.
- **What happens:** a `Stop()` racing `Dispose()` calls `mpv_command` on a handle
  that `mpv_terminate_destroy` is freeing.
- **Fix:** take a reference count or hold `_gate` around every native call (they
  are short, except `Command("loadfile")`, which returns quickly in mpv), and
  re-check `_disposed` under the lock.

---

## 5. Correctness bugs

<a id="bug-1"></a>
### BUG-1 — `TryReadAt` on MPEG-TS returns the next key frame after the time

- **Severity:** Medium · **Status:** Confirmed (WSL)
- **Where:** `src/MediaToolkitNet.FFmpeg/Reading/FFmpegMediaReader.cs:339`
  (`FrameStreamReader.ReadAt`) and its seek (`SeekTo`).
- **Evidence:** `a.ts` (MPEG-2 video, 25 fps, GOP 12, Appendix B):

  | asked | got | expected |
  |---|---|---|
  | 3.000 | frame 3.360 | the frame starting at or before 3.000 |
  | 1.000 | frame 1.440 | 0.960 – 1.000 |

  `av.mkv` returns 2.983 for 3.000, as documented. MPEG-TS has no index and
  FFmpeg's seek lands on the first key frame **after** the target, despite
  `AVSEEK_FLAG_BACKWARD`.
- **Test first** (integration): write a TS with `ffmpeg -f lavfi -i testsrc=d=6:r=25
  -c:v mpeg2video -g 12 -f mpegts`; for t in {1.0, 2.5, 3.0, 4.2}, assert the
  delivered frame's timestamp is `<= t` and `> t - 1/25`.
- **Fix:** in `ReadAt`, after seeking, decode the first frame; if its start is
  after `time`, seek again to `time - step` (start with 1 s, double each retry, stop
  at the start of the file) and decode forward to the last frame starting at or
  before `time`. Keep the number of retries bounded (e.g. 6).
- **Done when:** the test passes; the existing reader tests and the
  burn-in tests that use `TryReadAt` still pass.

<a id="bug-2"></a>
### BUG-2 — A stream that appears after open aborts the FFmpeg transcode

- **Severity:** Medium · **Status:** Certain (not reproduced: a small file is
  fully probed; the second stream must start beyond the probe window, ~5 MB)
- **Where:** `src/MediaToolkitNet.FFmpeg/Transcoding/FFmpegTranscoder.cs:573-585`
  (`InputReader.Advance` → `_demuxer.StreamHandle(_packet->StreamIndex)`) and
  `FFmpegDemuxer.StreamHandle` (`FFmpegDemuxer.cs:181`), which throws
  `ArgumentOutOfRangeException` for an index past the streams known at open.
- **What happens:** MPEG-TS and FLV add streams while reading
  (`AVFMTCTX_NOHEADER`). The first packet of such a stream aborts the whole job.
- **Test first:** concatenate a 10 MB video-only TS and a TS that adds an audio PID
  (same service id), transcode video only; today it throws.
- **Fix:** in `Advance`, skip packets whose index is not one the plan maps (check
  the index against the stream count captured at open **before** calling
  `StreamHandle`).

<a id="bug-3"></a>
### BUG-3 — Unrecognised encoder options are silently dropped

- **Severity:** Medium · **Status:** Certain
- **Where:** `src/MediaToolkitNet.FFmpeg/EncoderFactory.cs:169` and the recorder's
  `EncoderOptions` path in `FFmpegRecorder.cs` free the `AVDictionary` after
  `avcodec_open2` without looking at what is left in it. mpv:
  `MpvTranscoder.cs:272/329` pass `ovcopts`/`oacopts`; mpv logs an unknown option
  and carries on (not verified).
- **Why it matters:** CLAUDE.md: "Every setting of a request reaches the output,
  or is reported." `avcodec_open2` leaves every option it did not consume in the
  dictionary.
- **Test first** (integration): `VideoOutputSettings(MediaCodec.Mjpeg) { Options =
  new Dictionary<string,string> { ["no_such_option"] = "1" } }`; assert `Validate`
  or the result's warnings mention `no_such_option` (or the run fails naming it).
- **Fix:** after `avcodec_open2`, if `av_dict_count(options) > 0`, list the keys
  (`av_dict_get(options, "", prev, AV_DICT_IGNORE_SUFFIX)` loop) and return them to
  the caller as a `TranscodeIssue` warning (transcoder) or throw (recorder). For
  mpv, check the log for `Could not set AVOption` and turn it into a warning.

<a id="bug-4"></a>
### BUG-4 — Mid-stream format change aborts the FFmpeg transcode

- **Severity:** Medium · **Status:** Certain
- **Where:** `src/MediaToolkitNet.FFmpeg/Transcoding/EncodePipelines.cs:380`
  (video `Check`) and `:586` (audio `Check`).
- **What happens:** the video check throws on any change of size/pixel format;
  broadcast captures change resolution at programme boundaries. The audio check
  compares only the sample format, so a rate or channel change passes the check
  and reaches a buffer source configured for the old format, where libavfilter
  either refuses it (job aborts with a less clear message) or, if it accepts it,
  converts with the wrong assumptions. Which one happens with FFmpeg 7–9 is the
  first thing the test below shows.
- **Test first:** a TS made of a 320x240 segment followed by a 640x480 segment,
  re-encoded to H.264/MJPEG at a fixed size: today it throws; afterwards the output
  has the requested size throughout. For audio: 48 kHz stereo then 44.1 kHz mono.
- **Fix:** on a change, rebuild the filter graph from the new frame (flush the old
  one into the encoder first) instead of throwing, as `ffmpeg` does by default;
  compare rate and channel layout too (read them with `AbiLayout.AudioFormatOf`,
  MEM-3).

<a id="bug-5"></a>
### BUG-5 — Audio retiming does not drop non-increasing timestamps

- **Severity:** Medium · **Status:** Certain
- **Where:** `src/MediaToolkitNet.FFmpeg/Transcoding/EncodePipelines.cs:597`
  (audio `Retime`); compare the video version at :392.
- **What happens:** a frame whose rescaled pts is ≤ the previous one reaches the
  encoder; the muxer then fails with "non monotonically increasing dts" and the
  job aborts.
- **Test first:** feed the pipeline two frames with equal pts (unit test with a
  hand-built `AVFrame` is hard; use an input with a timestamp discontinuity, e.g.
  two concatenated TS segments with `-output_ts_offset` going backwards).
- **Fix:** drop (return false) a frame whose pts is not greater than the last
  emitted one, as the video path does.

<a id="bug-6"></a>
### BUG-6 — One subtitle event that fails to encode aborts the job

- **Severity:** Low · **Status:** Certain
- **Where:** `src/MediaToolkitNet.FFmpeg/Transcoding/SubtitlePipeline.cs:170`.
- **What happens:** decode failures are tolerated, encode failures (an event
  larger than the 1 MB buffer, or a malformed ASS event) throw.
- **Fix:** skip the event and add a warning issue, consistent with decode.

<a id="bug-7"></a>
### BUG-7 — A capture whose loop failed stays "running"

- **Severity:** Medium · **Status:** Certain
- **Where:** `WasapiCapture.cs` (the worker field is cleared only in `Stop`, :168),
  `V4l2Video.cs:168/395`, `AlsaAudio.cs:353/447`, `PulseAudio.cs:190/263`,
  `MediaFoundationVideoCapture.cs` worker loop.
- **What happens:** after a failure (device unplugged) `IsRunning` stays true and
  `Start()` returns immediately without starting anything; only `Stop()` + `Start()`
  recovers, which callers do not know.
- **Fix:** in the loop's `catch`/`finally`, under `_gate`, clear the worker field
  if it still refers to the current thread (`Interlocked.CompareExchange`).
- **Test:** WASAPI loopback capture on Windows, force a failure by disabling the
  render endpoint; `IsRunning` must turn false. (Manual.)

<a id="bug-8"></a>
### BUG-8 — `WasapiRenderer.Write` loops forever when not started

- **Severity:** Medium · **Status:** Certain
- **Where:** `src/MediaToolkitNet.Windows/WasapiRenderer.cs:170-217` — waits on
  `_bufferReady.WaitOne(100)` in a loop until there is room.
- **What happens:** pre-filling before `Start()` (a common pattern), or writing
  after `Stop()`, with more data than the free buffer: the event is never signalled,
  the loop never ends.
- **Test first** (Windows integration, default render device; skip when none):
  `Write` 2 s of silence before `Start()` on a separate task; assert it returns
  (partial count) within 1 s.
- **Fix:** when the client is not started, write only what fits and return the
  number of bytes written (the interface already returns a count).

<a id="bug-9"></a>
### BUG-9 — `WasapiRenderer` declares planar formats as interleaved

- **Severity:** Medium · **Status:** Certain
- **Where:** `src/MediaToolkitNet.Windows/Native/WasapiNative.cs:84` (`WaveFormatEx.From`).
- **What happens:** a planar `SampleFormat` produces a `WAVEFORMATEX` for the
  interleaved layout; the renderer then plays planar bytes as interleaved (noise)
  and `BlockAlign`-based counts are wrong.
- **Fix:** refuse planar formats in the renderer constructor with
  `NotSupportedException` (or convert with `ToInterleaved()` and document that
  `Write` takes interleaved data, which the parameter name already says).

<a id="bug-10"></a>
### BUG-10 — `AlsaCapture.Stop` raises a spurious `Failed`

- **Severity:** Medium · **Status:** Hypothesis
- **Where:** `src/MediaToolkitNet.Linux/AlsaAudio.cs:402` (`snd_pcm_drop` from
  `Stop`) and the loop at :421-430.
- **What happens:** `snd_pcm_drop` makes the parked `snd_pcm_readi` return
  `-EBADFD`; `Recover` cannot recover that state; `Check` throws; `Failed` is raised
  although the caller asked to stop.
- **Test first:** on Linux with the ALSA `null` device
  (`new AudioCaptureSettings { Device = <id "null"> }`), start, stop, assert
  `Failed` was not raised.
- **Fix:** after a negative `readi`, if the token is cancelled, `break` before
  calling `Recover`/`Check`.

<a id="bug-11"></a>
### BUG-11 — `AudioQueueCapture` delivers nothing after Stop → Start

- **Severity:** Medium · **Status:** Hypothesis (macOS never run)
- **Where:** `src/MediaToolkitNet.MacOS/AudioQueueAudio.cs:266-275` and :308-313.
- **What happens:** `Stop` clears `_running` first, so buffers returned during
  `AudioQueueStop` are not re-enqueued; the next `Start` runs a queue with no
  buffers. The renderer's `Write` after Stop/Start may block the same way if the
  output queue does not return its buffers on an immediate stop.
- **Fix:** in `Start`, re-enqueue every buffer in `_buffers` (after
  `AudioQueueReset`), and check the result of `AudioQueueEnqueueBuffer` at :312.

<a id="bug-12"></a>
### BUG-12 — mpv `lavfi=[...]` quoting breaks on `]`

- **Severity:** Low · **Status:** Certain
- **Where:** `src/MediaToolkitNet.Mpv/MpvFilterChain.cs:80-84`.
- **What happens:** mpv's `[...]` quoting ends at the first `]`; a chain with
  labels (`[in]…`) or a `drawtext` text containing `]` is cut and the rest is parsed
  as further filters/options. `MpvTranscoder.KeyValueList` already uses mpv's
  length-prefixed quoting correctly.
- **Test first** (unit): `MpvFilterChain.Lavfi("drawtext=text='a]b'")` produces a
  string that mpv parses back to the same graph — check the produced string equals
  `lavfi=graph=%<bytes>%drawtext=text='a]b'`.
- **Fix:** `$"lavfi=graph=%{Encoding.UTF8.GetByteCount(chain)}%{chain}"`.
  Run the mpv transcoding tests on WSL.

<a id="bug-13"></a>
### BUG-13 — Media Foundation capture geometry

- **Severity:** Medium · **Status:** Likely (hypothesis for the negative stride)
- **Where:** `src/MediaToolkitNet.Windows/MediaFoundationVideoCapture.cs:239`
  (`MF_MT_DEFAULT_STRIDE` read as `uint`, cast to `int`) and the frame delivery
  that locks the buffer.
- **What happens:** (a) a bottom-up RGB format has a negative default stride;
  plane 0 is then the buffer start with a negative stride, so `GetPlane` starts
  `stride × (rows-1)` bytes **before** the buffer. (b) `Lock`'s `currentLength`
  is ignored (MEM-2). (c) if `SetCurrentMediaType` with the requested format fails,
  the native type (often MJPG) is kept silently and `Format` still claims the
  requested one.
- **Fix:** prefer `IMF2DBuffer::Lock2D` (gives scanline-0 pointer and signed
  pitch); otherwise, for a negative stride point plane 0 at
  `buffer + |stride| × (height-1)`. Check `currentLength` with the MEM-2 helper.
  After `SetCurrentMediaType`, read the current type back and set `Format` from it;
  raise an error when it is a compressed type the caller did not ask for.

<a id="bug-14"></a>
### BUG-14 — MF recorder does not check frame format

- **Severity:** Low · **Status:** Certain
- **Where:** `src/MediaToolkitNet.Windows/MediaFoundationRecorder.cs` (write path).
- **Fix:** compare the incoming frame's `Format` with the stream's input type and
  throw `ArgumentException` on mismatch, before copying.

---

## 6. Resource leaks

<a id="leak-1"></a>
### LEAK-1 — `FFmpegDemuxer.Open` leaks the context

- **Severity:** Medium · **Status:** Certain
- **Where:** `src/MediaToolkitNet.FFmpeg/FFmpegDemuxer.cs:114` —
  `AbiLayout.ValidateOpenInput(context, url)` runs after the `try` that frees the
  context on failure.
- **Fix:** move the call inside the `try`, or `avformat_close_input(&context)` in a
  `catch` around it.
- **Test:** none practical (the probe does not fail on a supported FFmpeg); review.

<a id="leak-2"></a>
### LEAK-2 — `FFmpegRecorder` leaks on partial construction

- **Severity:** Medium · **Status:** Certain
- **Where:** `src/MediaToolkitNet.FFmpeg/FFmpegRecorder.cs:41-64` (output context
  leaks if `av_packet_alloc` at :63 fails) and the stream-adding methods before
  `_streams.Add` at :148, :277, :311 (an encoder context leaks if `CreateStream` /
  frame allocation throws).
- **Fix:** constructor: `try { … } catch { Dispose(); throw; }`. Stream methods:
  wrap from `avcodec_alloc_context3` to `_streams.Add` and free the context in
  `catch`. Also: `cue.Ass` is written raw while `EscapeAss` only handles newlines —
  escape `{`/`}` too, or document that `Ass` is trusted ASS text.

<a id="leak-3"></a>
### LEAK-3 — `FFmpegMediaReader.Open` leaks readers

- **Severity:** Medium · **Status:** Certain (analyzer CA2000 at `FFmpegMediaReader.cs:86-88`)
- **Where:** `src/MediaToolkitNet.FFmpeg/Reading/FFmpegMediaReader.cs:70-122`.
- **Fix:** collect created readers in a list; on exception dispose them all and
  rethrow.

<a id="leak-4"></a>
### LEAK-4 — `DirectShowGraph` keeps every wrapper

- **Severity:** Medium · **Status:** Certain (analyzer CA2000 at `DirectShowGraph.cs:443`)
- **Where:** `src/MediaToolkitNet.Windows/DirectShow/DirectShowGraph.cs:37`, `:96`,
  `:481-486`; `DirectShowFilter`/`DirectShowPin` constructors call `owner?.Track(this)`.
- **What happens:** every `Filters`, `Pins`, `FirstFreeOutput`, `InputPins`,
  `Describe` and `ConnectedTo` call creates wrappers holding COM references that
  live until the graph is disposed; a long-lived graph that is inspected
  periodically grows without bound. The constructor leaks `_control` when a later
  `QueryInterface` fails.
- **Fix:** let wrappers created for a transient query (`Describe`, `ConnectedTo`
  used internally) be disposed by the method that created them (`using`), and make
  `Track` a weak list or remove disposed items. Constructor: dispose acquired
  interfaces in a `catch`.

<a id="leak-5"></a>
### LEAK-5 — V4L2 mappings leak on failure

- **Severity:** Low · **Status:** Certain
- **Where:** `src/MediaToolkitNet.Linux/V4l2Video.cs:317-341` — mappings made before
  a failing `mmap`/`QUERYBUF` are held in a local array; `_buffers` is assigned only
  at the end, so `Dispose` unmaps nothing.
- **Fix:** assign each mapping into `_buffers` as it is made (allocate the array
  first), or unmap the local ones in a `catch`. Also open the device with
  `O_CLOEXEC` so the descriptor is not inherited by child processes.

<a id="leak-6"></a>
### LEAK-6 — `FFmpegPlayer.Play` after `Ended`

- **Severity:** Low · **Status:** Certain
- **Where:** `src/MediaToolkitNet.FFmpeg/FFmpegPlayer.cs:148-165` — a new
  `CancellationTokenSource` is assigned over the old one without disposing it.
- **Fix:** dispose the previous one (after its worker has exited).

<a id="leak-7"></a>
### LEAK-7 — Variant not cleared

- **Severity:** Low · **Status:** Certain
- **Where:** `src/MediaToolkitNet.Windows/DirectShowDeviceEnumerator.cs:123`
  (`ReadStringProperty`) — `VariantClear` runs only for `VT_BSTR`.
- **Fix:** call `VariantClear` for every successfully read variant.

<a id="leak-8"></a>
### LEAK-8 — COM initialisation never balanced

- **Severity:** Low · **Status:** Certain
- **Where:** `src/MediaToolkitNet.Interop/Com/Ole32.cs:65-68` returns `false` for
  `S_FALSE` (already initialised — which still needs a matching `CoUninitialize`)
  as well as for `RPC_E_CHANGED_MODE` (which must not be balanced); `Uninitialize`
  has no callers; threads the library creates never uninitialise. `MFStartup`
  without `MFShutdown` is process-lifetime and acceptable.
- **Fix:** return a tri-state (`Initialized`, `AlreadyInitialized`, `ChangedMode`);
  on threads the library owns, call `CoUninitialize` at thread exit for the first
  two.

---

## 7. Analyzer sweep

`dotnet build -p:AnalysisMode=All` reports 237 warnings. The relevant ones are
folded into the issues above (CA2000 → LEAK-3, LEAK-4; CA1806 → HYG-4, MEM-2;
CA2214, CA2211, CA5392 → HYG). The rest (CA1508 on pointers passed by address,
CA1063/CA1816 dispose-pattern style) are noise for this codebase. Do not turn
`AnalysisMode=All` on in the build; it is a review aid.

---

## 8. Hygiene

<a id="hyg-1"></a>
- **HYG-1** `stackalloc byte*[arguments.Length + 1]` — size from runtime data,
  against the hard rule: `src/MediaToolkitNet.Mpv/MpvPlayer.cs:267`,
  `src/MediaToolkitNet.Mpv/Transcoding/MpvClient.cs:77`. Use
  `NativeMemory.Alloc` + `Free` in `finally`.
- **HYG-2** `ComPtr` is a mutable `IDisposable` struct (`Interop/Com/Com.cs`): a
  copy disposes the same pointer twice. Search for `ComPtr` passed by value or
  assigned from another `ComPtr`; pass by `ref`, or make the type a `ref struct`.
- **HYG-3** `NativeSearchPaths.Prepend` (`:32`) says non-existent folders are
  ignored but does not check; check with `Directory.Exists` or fix the comment.
- **HYG-4** Ignored results (CA1806): `MfNative.cs:137` (`MFShutdown`),
  `WasapiNative.cs:331` (`PropVariantClear`), `AudioQueueAudio.cs:312`
  (`AudioQueueEnqueueBuffer`), `AVFoundation.cs:320,358` (lock/unlock — the lock
  one matters, MEM-2).
- **HYG-5** `src/MediaToolkitNet.Linux/Native/Alsa.cs` — public static mutable
  function-pointer fields (CA2211): any code can redirect them. Make them `internal`.
- **HYG-6** 23 `LibraryImport`s without `DefaultDllImportSearchPaths` (CA5392); for
  Windows system DLLs add `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]`.
- **HYG-7** `WasapiNative.cs:460` says the sub-format GUID is 8 bytes past the
  base structure; the code (`:468`, `+ 6` past the 18-byte struct = offset 24) is
  right, the comment is wrong.
- **HYG-8** Buffer sizes computed in `int`: `PulseAudio.cs:185-186`,
  `AlsaAudio.cs:349`, `AudioQueueAudio.cs:226` overflow for very long
  `BufferDuration`; `PulseAudio.cs:42` truncates `Channels` with `(byte)`. Validate
  the settings (channels 1..32, duration ≤ 10 s) and compute in `long`.
- **HYG-9** `AlsaPcm` calls the virtual `Dispose` from its constructor (CA2214,
  `AlsaAudio.cs:144`). It works today because derived field initialisers run first;
  make the cleanup a private non-virtual method.

---

## 9. Checked and not a problem

- `dotnet list package --vulnerable --include-transitive`: no vulnerable package.
  Only FluentAssertions is outdated (8.x), and it must stay on 7.x (8.x is under a
  commercial licence).
- CA2215 at `SubtitlePipeline.cs:63`, `EncodePipelines.cs:75`, `StreamPipeline.cs:248`:
  the base `Dispose` is empty.
- CA2000 at `GStreamerTranscoder.cs:379`: the `catch` at :413-417 disposes the pipeline.
- `GStreamerGraph.Quoted` (:593) escapes backslash and quote correctly for the two
  levels (`gst_parse` and `GstStructure`); paths and tags cannot break out.
- mpv codec options are length-quoted (`MpvTranscoder.KeyValueList`, :675); paths
  go through `mpv_set_option_string`/`change-list` as single values.
- The mpv transcoder and prober already set `ytdl=no`, `load-scripts=no`.
- FFmpeg blocks network URLs nested in a local playlist by itself (SEC-1 evidence).
- `GStreamerPipeline.FindElement` wrappers are released with the pipeline.
- `stackalloc` sizes elsewhere are constants (`GstLayout.MapInfoSize` = 104,
  `Utf8Scoped.StackThreshold`, fixed small buffers).
- `FFmpegFilterGraph.Push(VideoFrame)` with non-refcounted data is copied by
  `av_frame_ref`, as its comment says.

---

## Appendix A — running in WSL

```bash
#!/bin/bash
# usage: wsl.sh sync|build|test [dotnet test args]
set -e
SRC=/mnt/d/Work/My/Projects/Multimedia/MediaToolkit.NET
DST=~/mtn
sync() { mkdir -p "$DST"; tar -C "$SRC" --exclude=./.git --exclude=bin --exclude=obj \
  --exclude=artifacts --exclude=.vs --exclude=graphify-out --exclude=.codegraph -cf - . | tar -C "$DST" -xf -; }
cmd=$1; shift || true
case "$cmd" in
  sync)  sync ;;
  build) sync; cd $DST && dotnet build -v q --nologo ;;
  test)  sync; cd $DST && dotnet test "$@" --nologo ;;
esac
```

Invoke from Git Bash on Windows as
`MSYS_NO_PATHCONV=1 wsl.exe -d Debian -- bash /mnt/c/path/to/wsl.sh build`.
The sample CLI is then
`dotnet ~/mtn/samples/MediaToolkitNet.Sample.Cli/bin/Debug/net10.0/MediaToolkitNet.Sample.Cli.dll`.

## Appendix B — reproduction scripts used

**SEC-1** (local file read through a playlist):
```bash
W=~/audit; mkdir -p $W/srv /tmp/secret; cd $W
ffmpeg -v error -f lavfi -i testsrc=d=1:s=64x48 -f lavfi -i sine=d=1 -c:v mpeg2video -c:a mp2 -t 1 srv/seg.ts
cp srv/seg.ts /tmp/secret/private.ts
printf '#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\n/tmp/secret/private.ts\n#EXT-X-ENDLIST\n' > local.m3u8
$CLI probe $W/local.m3u8 ffmpeg            # opens /tmp/secret/private.ts
$CLI transcode $W/local.m3u8 $W/out.mkv --backend ffmpeg   # out.mkv now holds it
```

**DL-1 / DL-2**:
```bash
ffmpeg -v error -f lavfi -i testsrc=d=2:s=160x120 -f lavfi -i sine=d=2 -c:v mjpeg -c:a flac in.mkv
for b in ffmpeg gstreamer mpv; do
  cp in.mkv src-$b.mkv; ln -sf src-$b.mkv alias-$b.mkv
  $CLI transcode $PWD/src-$b.mkv $PWD/alias-$b.mkv --backend $b --vcodec Mjpeg --acodec Flac --scodec none
  stat -c %s src-$b.mkv                     # was 192038
  echo "precious bytes" > keep-$b.mkv
  $CLI transcode $PWD/in.mkv $PWD/keep-$b.mkv --backend $b --vcodec Mjpeg --acodec Flac --scodec none --vf no_such_filter_xyz
  ls keep-$b.mkv                            # gone
done
```

**MEM-3 / BUG-1** (`a.ts`, `b.ts`, `late.ts`):
```bash
ffmpeg -v error -f lavfi -i testsrc=d=6:s=160x120:r=25 -c:v mpeg2video -mpegts_service_id 1 -streamid 0:256 -f mpegts a.ts
ffmpeg -v error -f lavfi -i testsrc=d=3:s=160x120:r=25 -f lavfi -i sine=d=3 -c:v mpeg2video -c:a mp2 \
  -mpegts_service_id 1 -streamid 0:256 -streamid 1:257 -output_ts_offset 6 -f mpegts b.ts
cat a.ts b.ts > late.ts
$CLI read late.ts 7 1     # audio "... of 0 Hz, 0 ch"            (MEM-3)
$CLI read a.ts 3 1        # "at 03.000: frame 03.360"             (BUG-1)
```

**MEM-1**:
```bash
$CLI gst-run "fakesrc num-buffers=3 sizetype=fixed sizemax=100 filltype=zero ! capsfilter caps=video/x-raw,format=I420,width=1920,height=1080,framerate=25/1 ! appsink name=sink" 3
# Fatal error. System.AccessViolationException ... exit 134
```
