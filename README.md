<img src="assets/icon.svg" width="64" height="64" alt="">

# ILSpy Comparison C#

Created in DeepAI Code.

A Windows (WPF, .NET 8) app that watches .NET/Unity-game DLLs, decompiles them with the
same engine as ILSpy (`ICSharpCode.Decompiler`), and when a DLL is replaced or patched
on disk it automatically diffs the new version against a stored baseline, raises a
"GAME PATCHED" event in the patch-feed timeline, and writes a self-contained HTML report.

## Build

    dotnet build src/DllCompare/DllCompare.csproj

Requires the .NET 8 SDK. Run `src/DllCompare/bin/Debug/net8.0-windows/DllCompare.exe`.

## Usage

1. **File → Add DLL to watch…** — pick e.g. `<game>_Data/Managed/Assembly-CSharp.dll`.
   The first scan stores a **baseline snapshot** (decompiled source of every top-level type)
   under `%LOCALAPPDATA%\DllCompare\Snapshots`.
2. Leave the app running. Whenever the game updates and rewrites the DLL — Unity replaces
   files via rename, so `FileSystemWatcher` + rename/debounce + a 10 s polling fallback
   catch it — the app:
   - hashes the file, re-decompiles it,
   - diffs it type-by-type against the baseline (**added / removed / modified**),
   - updates the baseline snapshot,
   - writes an HTML report to `%LOCALAPPDATA%\DllCompare\Reports`,
   - shows an `UPDATED` pill on the card and a timeline entry. *Open diff* shows a
     side-by-side viewer; *HTML report* opens the saved report.
3. **File → Compare two DLLs…** ad-hoc compares any two assemblies without touching the watch list.

Watched DLLs persist across restarts (`settings.json`).

## Notes / limitations

- Works for **Mono-scripted** Unity games (managed `Assembly-CSharp.dll` etc. in
  `<game>_Data/Managed`). IL2CPP builds compile to native code and have no managed DLLs to diff.
- Only top-level types are tracked individually (nested types ride along in their parent's source).
- Referenced assemblies are resolved from the DLL's own folder first (covers Unity's Managed
  folder, which ships `mscorlib`/`netstandard`), falling back to the .NET Framework 4.x directories.
