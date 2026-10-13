# Simbi for Windows

Native Windows desktop app built with WPF and WASAPI. The existing Swift/macOS app is unchanged.

## Run

Open `Simbi.Windows.exe` from a Windows build artifact. The self-contained x64 publish includes the .NET runtime: WSL, Python, FFmpeg and a separate .NET installation are not needed to run it. Windows 10/11 x64 is the intended target; this implementation was verified on Windows 11 x64. The executable is currently unsigned.

1. Choose a notes folder (defaults to `%USERPROFILE%\Simbi`). Existing `note.md` folders appear in the library.
2. Create a note. Markdown edits save locally after a short pause; `Ctrl+S` saves immediately.
3. Select microphone and/or system audio, then press **Record** (`Ctrl+R`). Press **Stop recording** to finish. Recording works without a ChatGPT account.
4. Use **Play**, the seek slider, or double-click a transcript cue to hear its source.
5. **Add files** preserves originals in `files/`. Text attachments also get a Markdown copy in `context/`; select another attachment and use **Convert selected** for Codex conversion.

`Ctrl+N` creates a note. The search box filters the library. Files edited externally are reloaded. If another app changes an edited document before autosave, Simbi preserves your version in `.simbi/conflicts/` and reloads the external file. If the change happens during atomic replacement, the exact replaced version is retained in that folder instead. The status bar reports the preserved file's location, so neither version is silently lost.

## ChatGPT and Codex

Install/sign in to Codex with your ChatGPT account. Simbi discovers `codex.exe` on PATH or from the installed Codex desktop package; you can also select the executable in Settings. The app reads credentials from `%CODEX_HOME%\auth.json`, or `%USERPROFILE%\.codex\auth.json` when CODEX_HOME is unset. No API key or separate Simbi login is needed. Credentials are never stored in Simbi settings or written to logs.

Transcription is **off by default**. Enable it in Settings to send speech segments directly to `https://chatgpt.com/backend-api/transcribe`, matching the original app's integration. This is a private ChatGPT endpoint and can change independently of Simbi. Failed/offline segments remain in `.simbi/windows-pending/`; **Retry transcript** resends them after you sign in or reconnect. A successful retry commits each cue once before removing its audio queue entry. Explicit retry also works while automatic transcription is disabled.

**Generate AI notes**, **Ask** and attachment conversion use your local Codex app-server. They run with a read-only sandbox; Simbi itself saves returned Markdown after checking for conflicting edits. Each action starts a fresh thread and archives it when finished. AI responses may be inaccurate; review them against the transcript.

## Files and platform differences

```text
Meeting/
├── note.md
├── summary.md
├── transcript.vtt
├── audio.webm             streamed Opus, compatible plain recording
├── files/                 imported originals
├── context/               Markdown attachment copies
└── .simbi/
    ├── audio.wav          recoverable PCM recording for Windows playback
    ├── windows-state.json Windows-only recording bookkeeping
    └── windows-pending/   durable WebM chunks and metadata
```

- Microphone capture uses the selected input device (or the default communications input); system audio uses loopback from the Windows default output endpoint. Reconnecting or changing a device while recording can require restarting capture. A capture buffer overrun stops recording with a visible error instead of silently dropping samples.
- Energy-based speech segmentation identifies **Microphone** and **System audio** sources. It does **not** recognize separate people sharing one source. FluidAudio/CoreML speaker diarization, which the macOS app uses, is not available in this version.
- Windows recordings can be resumed in the same note. A stale active marker after interruption is reported when reopening it; the WAV header is repaired from its actual sample data when recording resumes. Disk space remains the user's responsibility; PCM uses about 346 MB/hour, in addition to Opus and pending chunks. The current single WAV has a roughly 12-hour / 4-GB limit.
- Existing macOS notes/transcripts can be opened. The decoder supports mono/stereo, unlaced Opus WebM; playback from an actual macOS capture has not yet been verified with a representative fixture. Starting a Windows recording in a note with existing or externally changed audio is rejected to protect that recording: create a new note instead. Windows stamps fingerprint the recording header and completed file version, so stale state cannot authorize appending to a replacement. macOS `.simbi/state.json` is never overwritten.
- There is no embedded Ghostty terminal, automatic updater, media-import/transcription pipeline, or individual-speaker diarization. The native editors display Markdown source; summary citations are text, while transcript cues support playback seeking.
- Speech segmentation queues audio even in local-only mode. The queue is retained until explicitly transcribed. Turning transcription off stops automatic uploads; it does not delete previously retained audio. Right-click **Retry transcript** and choose **Discard pending speech…** to delete that note's queue after confirmation; its complete recording and transcript remain.
- Interrupted segment writes retain source/timing manifests before encoding. On reopening, completed or partial WebM segments are recovered. If encoding stopped before speech packets were available, checkpointed PCM supplies the recovery audio and the source label explicitly says **Recovered audio (mixed)**.
- Settings live at `%LOCALAPPDATA%\Simbi\settings.json`. Recording audio and notes remain in the chosen library. Starting with `--notes-root <folder>` uses an isolated library and does not save changed application settings.

## Build and verify

Install the .NET SDK 10.0.400 pinned in global.json on Windows, then run from PowerShell:

```powershell
./windows/build.ps1
# Optional local hardware / visible-WPF / installed-Codex checks:
./windows/build.ps1 -CaptureSmoke -GuiSmoke -CodexSmoke
```

Default output is `windows/publish/win-x64/Simbi.Windows.exe`. `-OutputDirectory` changes it. `-Runtime win-arm64` creates an ARM64 artifact but ARM64 has not been verified on hardware. The CI workflow builds, runs storage/codec/queue/JSON-RPC contracts and uploads the self-contained artifact; hosted runners do not have an audio device or a signed-in Codex account, so hardware and account checks are opt-in locally.

Tests use temporary synthetic notes. The default suite also shows a WPF window and verifies editor autosave/conflicts, New note, AI notes, questions and attachment conversion with a local fake app-server; CI is configured to run that same path without an audio device or credentials. Hardware smoke plays a quiet generated tone and captures system loopback only, never the microphone. **GuiSmoke** additionally exercises Record/Stop/Play/Pause, keyboard seek and transcript-cue seek. `--screenshot <path>` captures the rendered test window outside the source tree. The installed-Codex check performs initialize and account/read only; no real AI turn or transcription upload is performed. Transcription retries and AI turn responses are verified with local synthetic transport fixtures.

When FFmpeg is already installed on the development machine, the test executable's optional `--ffmpeg` flag validates the generated container and both appended sessions using independent Windows ffprobe/ffmpeg executables. FFmpeg is only a verification tool, not an application dependency.

The Windows native implementation was built and executed locally. See `VERIFICATION.md` for the measured results and remaining verification limits.
