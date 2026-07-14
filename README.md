# Claude Supervisor

A lightweight Windows desktop utility (WPF, .NET 8) that lists and manages
Claude-related processes — `claude`, `node`, and `anthropic` processes by
default — showing PID, memory, thread count, and start time, with one-click
refresh, auto-refresh, name filtering, and process termination.

It ships as a **single, self-contained `ClaudeSupervisor.exe`**: no .NET
runtime and no Visual Studio are required on the target machine.

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
│       ├── ClaudeSupervisor.csproj    # net8.0-windows, WPF, self-contained
│       ├── app.manifest               # DPI awareness, supported OS
│       ├── App.xaml / App.xaml.cs     # application entry + shared styles
│       └── MainWindow.xaml / .cs      # process list UI + logic
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
