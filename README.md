<div align="center">

<img src="assets/icon.svg" width="96" height="96" alt="">

# PatchDiff

**Watch .NET / Unity-game DLLs — get a decompiled diff every time they're patched.**

Decompiles with the ILSpy engine · snapshot baselines · automatic patch detection ·
workstation UI (rail ▸ tabs ▸ intel rail) · pins & notes · revision archive with history,
blame & restore · patch intelligence · HTML diff reports · GUI and CLI.

*Licensed under the [WTFPL](#license) — do what the fuck you want to.*

</div>

---

## What is PatchDiff?

PatchDiff is a Windows tool (GUI + CLI) that keeps an eye on compiled .NET assemblies —
typically a Unity game's `Assembly-CSharp.dll` — and shows you **exactly what a patch
changed** inside them, in plain decompiled C#.

When a game updates, the old version is gone: the installer replaces the DLL and whatever
code it contained is overwritten. PatchDiff snapshots every version you've seen, so after
a patch you can ask "what actually changed?" and get a real answer: which types were added,
removed or modified, which constants were buffed or nerfed, and a side-by-side diff of the
decompiled source — without ever touching the game files themselves.

## Features

- **Workstation UI (v0.3+)** — one unified window: a **left rail** (type search, watched
  assemblies with live status pills, per-DLL timeline), a **tabbed center** (Overview /
  Browse / Diff / History / Notes — closable, one tab per open view), and a **patch
  intelligence rail** on the right.
- **Watch mode** — point PatchDiff at one or more DLLs; it detects every update automatically
  (`FileSystemWatcher` + polling fallback), re-decompiles, and diffs against the baseline.
- **Compare mode** — one-shot diff of any two DLLs (`patchdiff compare old.dll new.dll`).
- **Decompiled diffs** — full C# decompilation via `ICSharpCode.Decompiler` (the ILSpy engine),
  type-by-type with line-level side-by-side highlighting (DiffPlex).
- **Assembly explorer (Browse tab)** — ILSpy-style namespace → type → member tree with the
  decompiled source beside it; clicking a member scrolls to and highlights it; **★ pin** any
  type; one-click *diff vs previous / current* and `.cs` export from the tab.
- **Patch intelligence** — each diff gets a plain-terms summary, a *namespace heat* breakdown
  (where the patch concentrated its changes), and detection of numeric tuning with
  **buff / nerf** classification (e.g. `Demo.Player.Health: 100 → 150 (+50%) [buff]`).
- **Revision archive** — every version of every watched DLL is kept: a manifest per revision
  (SHA-256, assembly version, timestamp, type count) plus the original DLL bytes in a local
  archive, so you can study or restore any old build.
- **History tab & blame** — compare any two archived revisions on demand (*diff vs previous* /
  *diff vs current*), restore an old build with one click, and walk the full revision history
  member-by-member to see exactly when each member changed and in which version.
- **Pins & notes** — pin types across patches and keep a persistent note on each; entries
  light up with a *changed this patch* chip whenever their type is touched by a new patch.
- **Settings** — tray balloon alerts, watch poll interval, revision retention (keep all / last
  10 / 50 / 100), and start-minimized-to-tray.
- **HTML reports** — self-contained, start-collapsed diff reports for every detected patch,
  written next to the snapshots.
- **Type search** — search across every decompiled type in all watched baselines at once.
- **Persistence & auto-start** — watch list, timeline, baselines and window state survive
  restarts; re-checks on launch; optional *Start with Windows* so a rebooted PC keeps watching.
- **System tray** — close-to-tray, tray menu and balloon alerts on detected patches;
  single-instance (a second launch focuses the running app); drag & drop DLLs or folders.

## Install on Windows

### Option A — download a release (easiest)

Grab a build from the [Releases](../../releases) page — every `v*` tag ships prebuilt binaries:

| Artifact | What it is |
|---|---|
| `patchdiff-win-x64.zip` | CLI for Windows (single exe, self-contained) |
| `patchdiff-linux-x64.tar.gz` | CLI for Linux |
| `patchdiff-osx-arm64.tar.gz` | CLI for Apple Silicon |
| `PatchDiff-app-win-x64.zip` | WPF desktop app (Windows) |

1. Download and extract the zip.
2. For the CLI: put `patchdiff.exe` somewhere on your `PATH` (or just run it from its folder).
3. For the GUI: run `PatchDiff.exe` — no installer needed, everything lives in
   `%LOCALAPPDATA%\PatchDiff`.

### Option B — build from source

Requires [git](https://git-scm.com) and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bat
git clone https://github.com/Tester12-SR-24/PatchDiff.git
cd PatchDiff
dotnet build
```

That builds the whole solution — Core library, CLI and WPF app:

```bat
:: run the CLI
dotnet run --project src\PatchDiff.Cli -- compare a.dll b.dll

:: run the GUI
start src\PatchDiff.App\bin\Debug\net8.0-windows\PatchDiff.exe
```

#### Self-contained single-file builds (what the releases contain)

```bat
:: CLI — runs on any x64 Windows machine, no .NET install required
dotnet publish src\PatchDiff.Cli -c Release -r win-x64 --self-contained true ^
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true ^
  -o publish\cli

:: GUI app
dotnet publish src\PatchDiff.App -c Release -r win-x64 --self-contained true ^
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true ^
  -o publish\app
```

To create portable bundles like the release assets, zip `publish\cli` as
`patchdiff-win-x64.zip` and `publish\app` as `PatchDiff-app-win-x64.zip`.

## Usage

### CLI

```bash
# one-shot: compare two versions of a DLL
patchdiff compare old/Assembly-CSharp.dll new/Assembly-CSharp.dll -o diff.html

# monitor a Unity game — writes baseline, then auto-diffs on every patch
patchdiff watch "C:\Games\MyGame_Data\Managed\Assembly-CSharp.dll"

# watch several DLLs with a custom poll interval
patchdiff watch --poll 5 Assembly-CSharp.dll MyGame.Network.dll

# list every archived revision of a watched DLL (SHA, version, timestamp, types)
patchdiff history "C:\Games\MyGame_Data\Managed\Assembly-CSharp.dll"

patchdiff version
```

`watch` runs until Ctrl+C. On each detected update it prints `+added / −removed / ~modified`
type counts, the plain-terms patch summary and buff/nerf number changes to the console,
and writes a self-contained HTML report.

### GUI

The desktop app is a **workstation shell**:

- **Left rail** — search box over every decompiled `.cs` file in all baselines (results open
  the source directly), your watched assemblies with live status pills
  (`UP TO DATE` / `ANALYZING…` / `NEW BASELINE` / `UPDATED`), and a per-patch timeline.
- **Center tabs** — an Overview tab, plus closable **Browse** (assembly explorer), **Diff**
  (side-by-side colored diff), **History** (revision archive: compare any two versions,
  restore old builds) and **Notes** (pins + persistent notes with *changed this patch*
  chips) tabs — one per assembly or diff you open.
- **Right intel rail** — the patch-intelligence readout for the selected diff: plain-terms
  summary, namespace heat, and buff/nerf number changes.
- Toolbar actions cover adding DLLs or a whole `…_Data/Managed` folder, re-checking,
  exporting decompiled sources, opening HTML reports, and the settings dialog (⚙).

### Persistence & auto-refresh (ILSpy-style)

- Watch list, timeline history, last diffs, and window position survive closing the app
  or a full PC restart.
- On the next launch PatchDiff re-checks every watched DLL against its stored baseline —
  cheap hash comparison first; decompile + diff only if the file changed while it was away.
- **File ▸ Start with Windows** arms PatchDiff at logon, so a rebooted PC is watching again
  before you even open the app.

## How it works

1. **Baseline** — the first scan decompiles every top-level type of the DLL to C#
   (via `ICSharpCode.Decompiler`) and stores the snapshot in
   `%LOCALAPPDATA%\PatchDiff\Snapshots`. The revision archive
   (`%LOCALAPPDATA%\PatchDiff\Revisions` + `Archive`) keeps the manifest and the DLL bytes
   for every version seen from then on.
2. **Detection** — `FileSystemWatcher` (rename/change, debounced) plus a polling fallback
   (default every 10 s) hash the DLL; read-retries tolerate files still being copied, which
   is exactly how Unity replaces `Assembly-CSharp.dll` on a patch.
3. **Diff** — changed hash ⇒ re-decompile ⇒ type-by-type **added / removed / modified** diff
   against the baseline (DiffPlex line-level side-by-side).
4. **Intelligence** — the diff is summarized in plain terms: which namespaces took the heat,
   which numeric constants moved and whether that reads as a buff or a nerf, plus a
   member-level blame pass over the whole revision history.
5. **Report & archive** — an HTML report is written to `%LOCALAPPDATA%\PatchDiff\Reports`,
   the revision is archived, the baseline is updated, and the GUI timeline/log are notified.

## Notes

- Works with **Mono-scripted** Unity games (managed `Assembly-CSharp.dll` and friends in
  `<game>_Data/Managed`). IL2CPP builds compile to native code — nothing to decompile.
- References resolve from the DLL's own folder first (Unity's Managed folder ships its
  own BCL), falling back to the .NET Framework 4.x directories.
- Only top-level types are tracked individually; nested types ride along in their parent.
- The GUI is Windows-only (WPF); the CLI also runs on Linux and macOS.

## Building blocks

- [ICSharpCode.Decompiler](https://github.com/icsharpcode/ILSpy) — C# decompilation engine
- [DiffPlex](https://github.com/mmanela/diffplex) — line-level diffing
- WPF (.NET 8) for the desktop app, `System.CommandLine`-free hand-rolled CLI parser

## License

PatchDiff is released under the **WTFPL Version 2** — see [LICENSE](LICENSE).

> *DO WHAT THE FUCK YOU WANT TO PUBLIC LICENSE*
> Everyone is permitted to copy and distribute verbatim or modified copies of this
> license document, and changing it is allowed as long as the name is changed.
>
> TERMS AND CONDITIONS FOR COPYING, DISTRIBUTION AND MODIFICATION:
> **0. You just DO WHAT THE FUCK YOU WANT TO.**
