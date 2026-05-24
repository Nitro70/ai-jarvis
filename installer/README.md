# Jarvis Installer

A WPF (.NET 8) installer and settings app for the Jarvis voice/text assistant.

End users **don't read this** — they just download `JarvisInstaller.exe` from
the [Releases page](https://github.com/Nitro70/ai-jarvis/releases) and run it.

This document is for developers building the installer locally.

## What's in here

```
installer/
├── Jarvis.Installer.sln         # 3-project solution
├── build.bat                    # local build helper
└── src/
    ├── Jarvis.Setup/            # shared lib: models, services, shared WPF views
    │   ├── Models/InstallConfig.cs
    │   ├── Services/
    │   │   ├── ConfigYamlWriter.cs   # serializes InstallConfig → config.yaml
    │   │   ├── PythonInstaller.cs    # finds or silently installs Python 3.12
    │   │   ├── JarvisDownloader.cs   # pulls main.zip from GitHub, extracts
    │   │   ├── ApiTester.cs          # "Test connection" button logic
    │   │   ├── InstallLocator.cs     # %LocalAppData%\Jarvis\install-info.json
    │   │   ├── Shortcuts.cs          # Start Menu .lnk via PowerShell COM
    │   │   └── Installer.cs          # orchestrator
    │   └── Views/                    # UserControls reused by both apps
    │       ├── LlmView, VoiceView, MemoryView, ToolsView
    │
    ├── Jarvis.Installer/        # first-run wizard
    │   ├── MainWindow.xaml      # stepper + Back/Next/Cancel chrome
    │   └── Pages/               # Welcome → InstallLoc → Llm → Voice →
    │                            #   Memory → Tools → Progress → Done
    │
    └── Jarvis.Settings/         # tabbed settings editor, ships next to jarvis.py
        └── MainWindow.xaml      # tabs reuse the views in Jarvis.Setup
```

## Build

Requires the .NET 8 SDK:
```
winget install Microsoft.DotNet.SDK.8
```

Then from `installer/`:
```
build.bat
```

Output: `publish/installer/JarvisInstaller.exe` and `publish/settings/JarvisSettings.exe`.
Both are self-contained single-file (no .NET runtime required on the user's machine).
Approximate size after compression: ~70 MB each.

## How it all fits together at install time

1. User runs `JarvisInstaller.exe` from GitHub Releases.
2. Wizard collects: install dir, LLM backend, API key, voice prefs, memory text,
   tools selection.
3. Click **Install**:
   - `PythonInstaller` finds Python 3.10–3.13 on the system or downloads 3.12.8.
   - `JarvisDownloader` pulls
     `https://github.com/Nitro70/ai-jarvis/archive/refs/heads/main.zip`
     and extracts into the install dir.
   - `pip install -r requirements-all.txt` runs (verbose log shown in the page).
   - `ConfigYamlWriter` writes `config.yaml` + `memory.md` into the install dir.
   - `InstallLocator` writes `%LocalAppData%\Jarvis\install-info.json` so
     `Jarvis Settings` can find the install later.
   - `Shortcuts.Create` writes `run-jarvis.cmd` in the install dir, plus a
     Start Menu group with "Jarvis" and "Jarvis Settings" `.lnk` files.

`JarvisSettings.exe` is **not bundled inside `JarvisInstaller.exe`** — it's
attached as a separate artifact on the same GitHub release. The installer
downloads it via `JarvisDownloader.TryDownloadSettingsAppAsync` during the
"Download" step (best-effort — a 404 means "no release exists yet" and the
installer carries on). After the first GitHub release the Start Menu entry
"Jarvis Settings" will work; before that, users can still install and run
Jarvis fine, they just edit `config.yaml` manually.

## CI

`.github/workflows/installer.yml` builds both .exes on every push to `main`
that touches `installer/**`. On a `v*` tag push, the .exes are attached to
the GitHub release automatically.
