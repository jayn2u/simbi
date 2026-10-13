# Windows verification — 2026-10-13

Performed on the actual Windows host using PowerShell and Windows .NET SDK 10.0.400 / runtime 10.0.11, not a Linux cross-build.

## Build and application execution

- Release build: zero errors and zero warnings.
- Locked NuGet dependency restore and self-contained single-file `win-x64` publish succeeded.
- Published executable started directly from a local `C:\Users\jiwoo\.codex\artifacts\simbi-windows\app` directory.
- The live process exposed `Simbi · Windows`, a nonzero native window handle, and `Responding = true`.
- The published executable is about 72 MiB and includes .NET; execution does not require WSL, Python or FFmpeg.

## Automated contracts

The executable test suite verified:

- Unicode note creation/reload, safe Windows filenames, duplicate-title preservation.
- WebVTT timestamps, source voice tags, escaped text and idempotent cue appending.
- Existing macOS state preservation and rejection of recording over macOS audio.
- Resuming WAV and WebM recordings without losing earlier samples.
- Managed Opus/WebM encode/decode producing nonzero audio.
- Failed transcription retaining both metadata and audio; successful retry committing once; recovering an interrupted post-commit cleanup without re-upload.
- Rejecting mismatched pending-segment identifiers before upload.
- Preserving external edits and writing the local conflict copy.
- Codex thread/turn lifecycle, Unicode responses and notifications arriving before the turn-start response, using a synthetic local child server.

## Windows hardware and GUI

- Actual WASAPI loopback capture of a generated tone: representative run decoded 108,480 PCM samples, peak 5,641, one durable pending segment, and successful playback seek.
- The microphone was excluded from these synthetic capture tests.
- The actual WPF window was shown and rendered. Native controls loaded a note, edited it, autosaved it, recorded/stopped system audio, played/paused it, and closed cleanly.
- GUI testing exposed a reentrant Close call in the async Closing handler; it was corrected by scheduling Close after the first Closing event returned, then retested successfully.
- Independent Windows ffprobe recognized the recording as mono, 48-kHz Opus. Independent ffmpeg decoded both appended sessions without errors.
- The rendered synthetic window image was saved outside the repository, not included in source or commits.
- Independent 16-kHz-input mono/stereo WebM fixtures with 20-ms Opus packets decoded and sought correctly. These reproduce the original app's packet rate/input convention but were created independently on Windows, not captured on macOS.
- The default WPF suite also checks summary saving after an unsaved edit, actual question transmission, selected-attachment conversion, concurrent edit recovery and creation under an excluding search. The hardware suite checks keyboard seek and cue double-click against the output device's played sample position.
- Review fixes added manifest-first queue commits, recovery before the first encoded packet from checkpointed PCM, recording identity stamps, failed-start cleanup and explicit overflow stopping. Deterministic regression tests cover those data-preservation paths.

## Codex account integration

- The installed Windows Codex desktop package's CLI was found automatically without a hardcoded package version.
- The real Windows app-server initialize handshake and `account/read` request succeeded.
- Real transcription uploads, real AI summary/chat turns, and real binary-attachment conversion were not executed. Those paths use the original application's protocol/endpoint and synthetic transport tests; endpoint access and model availability depend on the user's account.

## Limits

- Windows x64 was verified. ARM64 can be published but has not been run on ARM hardware.
- Capture-device disconnection, very long meetings and actual microphone speech have not been exercised on this host.
- Long simultaneous microphone/loopback clock drift and provenance from a real macOS recording remain unverified hardware/input cases. The CI workflow is configured but has not been executed remotely because the branch was not pushed.
- The desktop Computer Use helper was unavailable in this session. GUI interaction was verified through WPF integration tests exercising real routed control events in a visible native window, plus direct execution of the published app, rather than external pointer/keyboard automation.
- The Windows feature differences are listed in README.md; individual-speaker diarization, embedded terminal and automatic updater are not present.
- Existing tracked files in the original checkout have not been edited. Development is isolated in `codex/windows-native` under `C:\Users\jiwoo\.codex\worktrees\simbi-windows-native\simbi`.
