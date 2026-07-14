# Claude Supervisor

A lightweight Windows desktop utility (WPF, .NET 8) that **auto-resumes the
Claude desktop app when your usage limit resets**. When you hit a limit, Claude
shows a message with the reset time. Claude Supervisor reads that time off the
screen with OCR, waits until the limit is back, then brings the Claude window
forward and types your resume text (default `resume`) followed by Enter — so
your session continues on its own.

It ships as a **single, self-contained `ClaudeSupervisor.exe`**: no .NET
runtime and no Visual Studio are required on the target machine.

## How it works

1. **Detect** — on launch it finds the Claude desktop window automatically
   (a visible top-level window owned by the `Claude` process).
2. **Read reset time (OCR)** — click this when you've hit the limit. It captures
   the **Claude window only** (via `PrintWindow`, never a screen grab, so this
   app's own window can't leak into the capture) and runs the OCR engine built
   into Windows 10/11 (no external data files). A regex-based parser (no LLM
   backend) understands the real Claude phrasings:
   - relative durations — `resets in 3 hr 55 min` → now + 3h55m
   - `reset at 3:00 PM`, `Resets Mon 2:29 AM`, `15:00`
   - `wait until 9:20 PM when your plan usage resets` (time before "resets")

   It collects every candidate on screen and picks the **soonest future** one.
   All times are handled and shown in **IST (UTC+5:30)**.
3. **Arm / Schedule** — schedules a resume at the reset time plus a small buffer
   (default 30 s). A live countdown shows in the status bar.
4. **Send** — at the scheduled moment it re-finds the Claude window, brings it
   to the foreground, and submits. What it submits depends on your choice:
   - **Just press Enter** (checkbox) — sends only Enter. Use this when you've
     already typed the whole prompt into Claude and it just needs to start.
   - **Send text** (multi-line box) — the text is appended to the **end** of
     whatever is already in Claude's composer (caret jumps to the end first),
     then Enter submits. Multi-line text uses Shift+Enter for internal line
     breaks so it isn't submitted early. Leave the box empty to just press
     Enter. Default text is `resume`.

You can also type the reset time in manually (e.g. `3pm`) and Arm without OCR,
or hit **Send now (test)** to verify it works against your Claude window before
relying on it.

### Notes & limitations

- **Manual arm, Claude desktop app only.** It targets the desktop app (not
  Claude Code in a terminal) and is armed by you when you hit the limit — it
  does not poll continuously.
- Keep the Claude window open. At resume time the app briefly steals focus to
  type; don't type elsewhere during that second.
- Typing goes to whatever field has focus in the Claude window — normally the
  message composer. If Claude's layout leaves something else focused, click the
  composer once before the scheduled time.
- OCR needs an English (or other) language pack installed in Windows (present by
  default on English installs).

---

## Download & run (no build required)

Every push to `main` publishes a GitHub Release with the standalone executable
attached:

1. Open the repository's **Releases** page on GitHub.
2. Download `ClaudeSupervisor.exe` from the latest release.
3. Double-click it. That's it — nothing to install.

> Windows SmartScreen may warn about an unrecognized publisher because the
> binary is unsigned. Choose **More info → Run anyway**, or sign the
> executable with your own certificate.

---

## Project structure

```
Claude Supervisor/
├── .github/
│   └── workflows/
│       └── build.yml                 # CI: build, publish, artifact, release
├── src/
│   └── ClaudeSupervisor/
│       ├── ClaudeSupervisor.csproj     # net8.0-windows10.0.19041.0, WPF, self-contained
│       ├── app.manifest                # DPI awareness, supported OS
│       ├── App.xaml / App.xaml.cs      # application entry + shared styles
│       ├── MainWindow.xaml / .cs       # UI + orchestration
│       ├── Native/
│       │   └── NativeMethods.cs        # Win32 P/Invoke (enum, capture, focus, SendInput)
│       └── Services/
│           ├── ClaudeWindow.cs         # find window, capture, foreground, type
│           ├── OcrService.cs           # Windows.Media.Ocr wrapper
│           └── ResetTimeParser.cs      # parse reset time from OCR / field text
├── ClaudeSupervisor.sln
├── .gitignore
└── README.md
```

---

## Building locally

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
Visual Studio is **not** required.

```bash
# Restore dependencies
dotnet restore

# Run in Debug from source
dotnet run --project src/ClaudeSupervisor/ClaudeSupervisor.csproj
```

### Produce the self-contained executable

```bash
dotnet publish src/ClaudeSupervisor/ClaudeSupervisor.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o publish
```

The finished binary is `publish/ClaudeSupervisor.exe`. Copy it anywhere and run
it on any Windows x64 machine.

---

## Continuous build & release (GitHub Actions)

[`.github/workflows/build.yml`](.github/workflows/build.yml) runs on
**`windows-latest`** runners and does the following:

| Trigger | Action |
| --- | --- |
| Pull request to `main` | Restore + publish (validation only) |
| Push to `main` | Publish, upload artifact, **create a prerelease** `build-1.0.<run#>` with `ClaudeSupervisor.exe` |
| Push a tag `v1.2.3` | Publish, upload artifact, **create a full release** `v1.2.3` |
| Manual (`workflow_dispatch`) | Publish + upload artifact |

The workflow:

1. Checks out the repo and installs the .NET 8 SDK.
2. Derives a version — `1.0.<run_number>` for branch builds, or the tag value
   for `v*` tags — and injects it via `-p:Version=`.
3. Publishes a **self-contained, single-file, win-x64** build.
4. Uploads `ClaudeSupervisor.exe` as a workflow **artifact**.
5. On pushes, creates/updates a **GitHub Release** with the executable attached
   (uses the built-in `GITHUB_TOKEN`; no extra secrets needed).

### Cutting a versioned release

```bash
git tag v1.0.0
git push origin v1.0.0
```

This produces a non-prerelease GitHub Release named `v1.0.0` with the
executable attached.

---

## Notes

- **Target framework:** `net8.0-windows` (WPF).
- **Runtime identifier:** `win-x64`. To target Arm64, change `RUNTIME` in the
  workflow and the `-r` flag to `win-arm64`.
- **Terminating processes** may require running Claude Supervisor as
  administrator if the target process is owned by another user or elevated.
- Trimming is intentionally disabled because WPF is not fully trim-safe; this
  keeps the single-file build reliable at the cost of a larger binary.
