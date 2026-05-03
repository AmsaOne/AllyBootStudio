# Ally Boot Studio

> **Status: Work in progress / experimental.** This project is incomplete. Things work, but it has not been stress-tested against every Armoury Crate SE version, every ROG Ally generation, or every video file you might throw at it. Use at your own risk and keep a backup of your original boot animation. See "Known limitations" below.

A small Windows desktop app that streamlines replacing the **Armoury Crate SE** boot animation on the **ASUS ROG Ally / Ally X** with a custom MP4. It auto-detects the ACSE animation slots, validates the source video, optionally transcodes it to a compatible codec via `ffmpeg`, and swaps the animation in place — with an automatic backup of the original so you can restore at any time.

---

## What it does

Replacing the Armoury Crate boot animation manually means:

1. Hunting through `C:\ProgramData\ASUS\…` for the right numbered slot folder.
2. Making sure your video is an MP4 with H.264 or H.265 — anything else won't play.
3. Backing up the original yourself (or losing it forever).
4. Setting the new file read-only so Armoury Crate doesn't silently restore the original.
5. Restarting Armoury Crate.

Ally Boot Studio collapses that into: **drag in a video → click Apply.**

Specifically, the app:

- **Auto-detects** the ACSE animation root by scanning known ASUS install paths. Falls back to a manual "Pick folder" picker if the layout shifts in a future ACSE update.
- Lists every **3-digit animation slot** it finds (e.g. `352` Cult of the Lamb, `353` Starfield, `358` Robocop, `359` Default), with friendly names.
- **Validates** the source video by inspecting the MP4 container and looking for `avc1` (H.264) or `hvc1`/`hev1` (H.265) magic bytes. Tells you if it's compatible without launching ffmpeg.
- **Transcodes** non-compatible videos via ffmpeg (auto-detected on PATH, scoop, winget, or chocolatey) to H.264 yuv420p, AAC, faststart, capped at 1080p.
- **Backs up** the original MP4 to `<slot>\original\` on first replace — and never overwrites that backup on subsequent replaces, so your true original is always recoverable.
- **Locks** the new MP4 read-only after replace, so Armoury Crate can't silently restore the original.
- One-click **Restore** to bring back the original animation.
- "Open slot folder" button to drop you into Explorer at the active slot.

---

## How to use it

### Get it onto your ROG Ally

The simplest way:

1. Go to the [Releases](../../releases) page on this repo.
2. Download the latest `AllyBootStudio.exe` from the release assets.
3. Copy it anywhere on your Ally — Desktop, a `Tools` folder, doesn't matter. It's a self-contained ~68 MB binary; **no .NET install required**.

### First run

1. Double-click `AllyBootStudio.exe`.
2. Windows SmartScreen will warn you on first run because the binary isn't code-signed. Click **More info** → **Run anyway**. (One-time per machine.)
3. The left panel auto-fills with your ACSE animation slots. If it shows "(not detected)", click **Pick folder** and navigate to your ACSE animation root manually — please open an issue with the path so it can be added to auto-detection.

### Replace an animation

1. **Pick a slot** on the left (e.g. `359 Default / ROG`). The app uses whichever slot is currently selected in Armoury Crate's *Settings → General → Personalization*, so make sure that matches.
2. **Drag your video** onto the window — or click **Browse**.
3. The app validates the file. Three outcomes:
   - **MP4 + H.264 / H.265** → the **Apply** button enables.
   - **MP4 with a different codec, or non-MP4 container** → click **Transcode** to re-encode to a compatible MP4 via ffmpeg (requires ffmpeg on PATH; install with `winget install Gyan.FFmpeg`). Once transcoded, the source switches to the converted file and Apply enables.
   - **Not a video file** → fix the source.
4. Click **Apply to selected slot.** The app:
   - Backs up the original to `<slot>\original\<name>.mp4` (only if no backup is there yet).
   - Copies your video over the slot's MP4.
   - Sets the new file read-only.
5. **Restart Armoury Crate.** Right-click its tray icon → Exit, then relaunch. The new boot animation is live.

### Restore the original

1. Pick the slot you want to restore.
2. Click **Restore original.** The app un-locks the current MP4 and copies the backup over it.
3. Restart Armoury Crate.

---

## Requirements

- **Windows 11** (Windows 10 may work, untested).
- **ROG Ally / Ally X** (or any device running ASUS Armoury Crate SE 1.5+ with the launch-animation feature).
- **(Optional) ffmpeg** if you want the Transcode button to handle non-H.264 inputs. Install with `winget install Gyan.FFmpeg`.

---

## Known limitations

- The auto-detection candidate list is heuristic — built from common ASUS install paths and may need updating for future ACSE versions. The "Pick folder" fallback always works as long as you can locate the numbered animation folders yourself.
- ACSE 1.5+ "Personalization" UI must already have the matching animation selected for the slot you replace. The app doesn't change which slot ACSE points at; it only swaps the file inside it.
- Validator is a heuristic (scans first 4 MB of the file for codec FourCCs). It's fast but can theoretically miss edge cases. ffmpeg's actual decode is the ground truth.
- No code signing — Windows SmartScreen will warn on first run.
- Single-file self-contained build is ~68 MB because it bundles the .NET 8 runtime.
- No video preview / scrubbing yet (planned).
- No persistent settings yet (last-used slot, ffmpeg path override are not remembered between launches).

---

## Roadmap (rough)

- [ ] Embedded video preview (MediaElement) for both the current slot and the source file
- [ ] Persisted settings (last slot, ffmpeg path)
- [ ] Code-signed binary so SmartScreen stops warning
- [ ] Bundle a portable ffmpeg so Transcode works with zero setup
- [ ] Per-slot multiple-backup history (restore to any prior version)
- [ ] Animation gallery / community presets
- [ ] Verified compatibility list for ACSE versions

If you hit a bug or have a request, [open an issue](../../issues).

---

## Building from source

```powershell
git clone https://github.com/<your-handle>/AllyBootStudio.git
cd AllyBootStudio
# requires .NET 8 SDK
dotnet build src/AllyBootStudio/AllyBootStudio.csproj
# or for a self-contained single-file release exe:
dotnet publish src/AllyBootStudio/AllyBootStudio.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o ./publish
```

Open `AllyBootStudio.sln` in Visual Studio, Rider, or VS Code (with the C# Dev Kit) for development.

---

## Project structure

```
AllyBootStudio.sln
src/AllyBootStudio/
  Models/AnimationSlot.cs           - record describing a discovered slot
  Services/
    AcseFolderLocator.cs            - auto-detect ACSE animation root + enumerate slots
    BootAnimationService.cs         - backup + replace + restore, with read-only locking
    Mp4Validator.cs                 - heuristic MP4/H.264/H.265 detection
    FfmpegService.cs                - locate ffmpeg + transcode to compatible MP4
  ViewModels/
    MainViewModel.cs                - INotifyPropertyChanged, RelayCommands, all UI state
    RelayCommand.cs                 - minimal ICommand impl
  App.xaml / App.xaml.cs            - application + dark theme resources
  MainWindow.xaml / .xaml.cs        - main window UI + drag-drop
  app.manifest                      - DPI awareness + supportedOS
```

---

## Credits

Co-created by:

- **[AmsaOne](https://github.com/AmsaOne)**
- **[Claude Code](https://claude.com/claude-code)** (Anthropic) 

Boot-animation file-format research and slot numbering informed by [mlbl/ACSESVC](https://github.com/mlbl/ACSESVC) and the [ROG Ally Life community guide](https://rogallylife.com/2024/08/04/armoury-crate-se-startup-video-changer/) — full credit to those projects for figuring out the underlying mechanics.

---

## License

This project is currently unlicensed (work in progress). A formal license will be chosen before a 1.0 release. Until then, treat the source as "available to read; ask before redistributing."
