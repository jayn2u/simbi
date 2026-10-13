---
title: Windows native Simbi
created: 2026-10-13
status: done
route: direct
baseline_commit: 62ece40e2a33059bed475dfb79d2e867e971cd16
context: []
---

## Intent

Make Simbi usable as a native Windows desktop application, with actual Windows builds and execution verification. The user authorized an isolated worktree preserving the original checkout and explicitly requested direct implementation by the primary agent. That instruction takes precedence over skill dispatch and further implementation approval gates.

## Design

Keep the macOS application intact and add a separate .NET 10 WPF app under `windows/`. WPF provides native Windows controls, keyboard navigation, file pickers, DPI scaling and accessibility. WASAPI through NAudio supplies microphone and default-output loopback capture. Managed Concentus encodes Opus without requiring FFmpeg, Python, WSL or a native codec installation. A minimal streaming WebM muxer writes the same audio container as the existing app. Plain folders hold `note.md`, `summary.md`, `transcript.vtt`, `audio.webm`, `files/` and `context/`.

The core user flow is selecting a notes folder, creating or opening a note, editing with autosave, recording microphone and/or system audio, receiving live transcript chunks, retrying pending uploads after offline operation, playback, importing attachments, generating AI notes and asking questions. Codex is discovered from an explicit executable setting, PATH or the desktop installation; credentials are read from CODEX_HOME or the user's .codex directory. Secrets are never copied into settings or logged. Recording and editing remain available without credentials. AI actions are explicit and use the user's installed Codex app-server over redirected stdin/stdout; no new account, API key or paid API integration is introduced.

macOS uses FluidAudio/CoreML for VAD and speaker diarization, AppKit/SwiftUI, Ghostty and Sparkle. They cannot run on Windows. The initial Windows implementation uses audio-energy segmentation and clearly identifies microphone versus system-audio sources rather than claiming individual speaker recognition. Both sources are mixed for the continuous recording, while source-specific speech segments produce transcript cues. There is no automatic updater or embedded terminal in this implementation. These platform differences are documented in the app and README.

## Boundaries

- Existing macOS code, user changes and real user notes remain intact.
- Windows recording bookkeeping lives in `.simbi/windows-state.json`; unknown macOS JSON fields are never discarded.
- An existing macOS audio file must not be overwritten by starting a Windows recording.
- Offline or rejected uploads retain segments durably. A retry commits each cue idempotently before deleting its queue entry.
- Do not contact transcription/AI services during synthetic smoke tests. Real account integration can be checked through the protocol handshake without starting an AI turn.
- Use isolated synthetic content for Windows smoke tests and loopback audio capture.
- Deliver a self-contained Windows x64 publish, plus repeatable build scripts and a Windows CI workflow.

## Code Map

- `Packages/SimbiKit/Sources/SimbiKit/NoteLayout.swift`: canonical file names.
- `Packages/SimbiKit/Sources/SimbiKit/VTT.swift`: cue voice tags and timestamps.
- `Packages/SimbiKit/Sources/SimbiKit/NoteRecordingState.swift`: macOS bookkeeping to preserve.
- `Packages/SimbiKit/Sources/CodexKit/TranscriptionClient.swift`: authenticated multipart WebM uploads and offline behavior.
- `Packages/SimbiKit/Sources/CodexKit/CodexWorker.swift`: JSON-RPC thread and turn shapes.
- `windows/Simbi.Windows/`: new WPF application and platform services.
- `windows/Simbi.Windows.Tests/`: executable contract and integration tests.

## Tasks & Acceptance

- [x] Create a WPF shell with folder selection, note list/search, autosaved editor, transcript, AI notes, attachment list, settings and chat.
- [x] Implement safe note creation, atomic writes, WebVTT parsing/appending and durable transcription queue.
- [x] Implement WASAPI capture on a common elapsed-time timeline, recoverable recordings, managed Opus/WebM, source-labelled segmentation and seekable playback. Long dual-device clock drift remains a hardware validation limit.
- [x] Port credentials and transcription transport, plus Codex discovery and app-server AI notes/chat.
- [x] Add repeatable Windows build, tests, publish and CI documentation.
- [x] Verify on this Windows host: release build, storage/queue/codec contracts, synthetic loopback capture and actual GUI interactions.

Acceptance: Given a Windows machine, publishing produces an executable that launches without WSL. Given a new note, edits persist and reopening restores content. Given generated system audio, capture yields a decodable recording with nonzero samples and a valid WebM stream. Given no account or network, recording persists and pending segments survive restart. Given a failed upload, retry does not duplicate already committed cues. Given an existing macOS note, its contents can be opened and its audio/state cannot be overwritten. Given a missing Codex executable, AI actions explain the setup requirement and leave notes untouched.

## Verification

Windows .NET CLI Release build and self-contained publish; run the console test executable on Windows; launch the actual published GUI and inspect/interact with it using Computer Use. Validate capture using generated audio and Opus decode rather than merely checking file existence. Compare the original checkout against its pre-work diff.

## Implementation Notes

Original checkout has 455 tracked CRLF-only changes. A managed worktree at origin/main isolates the implementation. Windows SDK 10.0.400 and desktop runtime 10.0.11 are installed. Source is stored in WSL but compiled by the actual Windows dotnet.exe through a UNC path; distributable output goes to a local Windows directory.

After the user moved this chat to PowerShell, source was copied (excluding build outputs) into a native Windows Git worktree on `codex/windows-native`. All subsequent coding/build/testing uses local Windows paths. External Computer Use is unavailable here, so actual GUI behavior is verified by a visible WPF integration test using real routed control events. The single-file published executable was also launched separately and its live native window/response state checked.

## Review Triage Log

Three read-only review agents used the user-requested gpt-6-luna / max configuration. The primary agent verified findings and applied code fixes directly, as requested by the user. No review agent edited source.

| Finding | Verdict | Evidence and disposition |
|---|---|---|
| B1 capture endpoint wording | low | Corrected README: input device for microphone, output loopback for system audio. |
| B2 no pending-audio discard | medium | Added a confirmed context-menu action, guarded against active uploads; tests preserve recording/transcript. |
| B3 orphan segment files at crash boundaries | high | Manifest-first staging, partial/PCM recovery and payload-first post-commit cleanup; recovery tests passed. |
| B4 summary snapshot before saving edits | medium | Save edits before snapshot; WPF test generates with a pending user summary edit and detects false conflicts. |
| B5 mouse-only slider seeking | medium | Added navigation-key seek and tested the audible output position. |
| B6 elapsed playback omits hours | low | Both playback timestamps now use HH:MM:SS. |
| B7 playback lacks independent/macos fixtures | medium | Added independent 16-kHz-input mono/stereo Opus fixtures; decoding/seeking passed. Actual macOS capture provenance remains explicitly unverified. |
| B8 in-progress checklist unchecked | false | Checklist was intentionally pending completion during review; it is marked from final verification now. |
| B9 remove session facts from spec | low | Rejected: standalone implementation evidence has no runtime consequence; retaining authorization/worktree facts helps explain the isolated implementation. |
| B10 LICENSE omitted from CI paths | low | LICENSE now triggers the Windows build. |
| E1 failed startup leaves active marker | medium | Failed capture startup closes files and saves an inactive stamp; invalid-device test passed. |
| E2 silent capture buffer overflow | high | Overflow cancels recording and reports the failure; cleanup no longer replaces the failure with a success message. |
| E3 queue trigger lost while gate held | medium | Drain requests await serialization rather than dropping a trigger. |
| E4 external edit compare/replace race | high | Windows File.Replace backs up the exact replaced version; a race test proves that version survives, and GUI conflict recovery preserves both versions. |
| E5 pending summary edit false conflict | medium | Same verified defect as B4, fixed and tested independently at the WPF boundary. |
| E6 newly created note hidden by search | medium | Creation clears the filter; actual New note dialog test passed with an excluding search. |
| E7 crash before segment JSON commit | high | Same failure family as B3; staged source/times survive, and checkpointed mixed PCM recovers speech before the first Opus packet. |
| E8 independent source clocks drift on long captures | maybe-false | Nominal-rate sources share the elapsed-time pump but no long dual-device hardware benchmark was run. Needs measured device clock offsets/long simultaneous capture; documented as unverified medium risk. |
| E9 stale Windows marker permits external audio replacement | high | Stamps verify a unique Windows header and completed file version; replaced-file rejection passed. Stale WAV is not selected for such audio. |
| V1 segmentation can regress without verification | medium | Deterministic silence/speech flush, source/times, payload decode and cue commit now run in the default suite; hardware smoke also requires a queued segment. |
| V2 attachment import not covered | medium | Original preservation, collision handling and Markdown context copies now tested. |
| V3 WPF AI-notes save path not covered | medium | Default WPF suite checks button response in summary.md and the editor, including a pending user edit. |
| V4 question not checked by fake server | medium | Fake answer depends on the submitted question; WPF test requires that exact answer. |
| V5 attachment conversion boundary not covered | medium | Default WPF suite verifies selected filename, context output and untouched original. |
| V6 transcript cue seek not covered | medium | Hardware GUI suite first seeks elsewhere, double-clicks the known cue, and verifies actual playback time moved to the cue. |

## Remaining validation limits

Long simultaneous microphone/loopback clock drift requires a longer hardware run. Real account transcription/AI turns and actual macOS recording provenance were not exercised; the app uses the existing protocol and independent/synthetic verification, with those limits documented in README and VERIFICATION.
