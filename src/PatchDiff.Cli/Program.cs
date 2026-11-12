using PatchDiff.Models;
using PatchDiff.Services;

return await PatchDiffCli.RunAsync(args);

internal static class PatchDiffCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            return Help();
        if (args[0] is "--version" or "-v" or "version")
        {
            Console.WriteLine($"patchdiff {typeof(PatchDiffCli).Assembly.GetName().Version?.ToString(3)}");
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "compare" => await CompareAsync(args[1..]),
                "watch" => await WatchAsync(args[1..]),
                "history" => History(args[1..]),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Error(ex.Message);
            return 2;
        }
    }

    // ---------------- compare ----------------

    static async Task<int> CompareAsync(string[] rest)
    {
        if (rest.Any(a => a is "-h" or "--help"))
            return Help("compare");

        string? output = null, old = null, current = null;
        for (int i = 0; i < rest.Length; i++)
        {
            var arg = rest[i];
            if (arg is "-o" or "--out")
            {
                if (i + 1 >= rest.Length)
                {
                    Error($"option '{arg}' requires a value (report file path)");
                    return 1;
                }
                output = rest[++i];
            }
            else if (arg.StartsWith('-'))
            {
                Error($"unknown option '{arg}' — usage: patchdiff compare <old.dll> <new.dll> [-o report.html]");
                return 1;
            }
            else if (old is null)
            {
                old = arg;
            }
            else if (current is null)
            {
                current = arg;
            }
            else
            {
                Error($"unexpected argument '{arg}' — usage: patchdiff compare <old.dll> <new.dll> [-o report.html]");
                return 1;
            }
        }
        if (old is null || current is null)
        {
            Error("usage: patchdiff compare <old.dll> <new.dll> [-o report.html]");
            return 1;
        }
        if (!File.Exists(old))
        {
            Error($"file not found: {old}");
            return 1;
        }
        if (!File.Exists(current))
        {
            Error($"file not found: {current}");
            return 1;
        }

        var oldSnap = await Task.Run(() => new DecompilerService().Build(old, Progress));
        var newSnap = await Task.Run(() => new DecompilerService().Build(current, Progress));
        var diff = new DiffService().Compare(oldSnap, newSnap,
            title: $"{Path.GetFileName(old)} → {Path.GetFileName(current)}");

        PrintSummary(diff);

        if (diff.Intelligence is { } intel)
        {
            if (intel.PlainTerms.Count > 0)
            {
                Console.WriteLine("  in plain terms:");
                foreach (var t in intel.PlainTerms)
                    Console.WriteLine($"    • {t}");
            }
            if (intel.Numbers.Count > 0)
            {
                Console.WriteLine($"  number changes ({intel.Numbers.Count}):");
                foreach (var n in intel.Numbers.Take(10))
                    Console.WriteLine($"    [{n.Direction}] {n.Type}.{n.Member}: {n.OldText} -> {n.NewText} ({n.PercentDelta:+0.##;-0.##}%)");
                if (intel.Numbers.Count > 10)
                    Console.WriteLine($"    ... and {intel.Numbers.Count - 10} more");
            }
        }

        var reportPath = output ?? SnapshotStore.WriteHtmlReport(diff);
        SnapshotStore.WriteHtmlReportTo(diff, reportPath);
        Console.WriteLine($"Report: {reportPath}");
        return 0;
    }

    // ---------------- watch ----------------

    static async Task<int> WatchAsync(string[] rest)
    {
        if (rest.Any(a => a is "-h" or "--help"))
            return Help("watch");

        double pollSeconds = 10;
        var paths = new List<string>();
        for (int i = 0; i < rest.Length; i++)
        {
            var arg = rest[i];
            if (arg == "--poll")
            {
                if (i + 1 >= rest.Length)
                {
                    Error("option '--poll' requires a value (seconds)");
                    return 1;
                }
                if (!double.TryParse(rest[++i], out var p) || p <= 0)
                {
                    Error($"'--poll' expects a positive number of seconds, got '{rest[i]}'");
                    return 1;
                }
                pollSeconds = p;
            }
            else if (arg.StartsWith('-'))
            {
                Error($"unknown option '{arg}' — usage: patchdiff watch [--poll seconds] <dll...>");
                return 1;
            }
            else
            {
                paths.Add(arg);
            }
        }
        if (paths.Count == 0)
        {
            Error("usage: patchdiff watch [--poll seconds] <dll...>");
            return 1;
        }

        var mgr = WatchManager.Instance;
        mgr.PollInterval = TimeSpan.FromSeconds(Math.Max(2, pollSeconds));

        mgr.Logged += m => Console.WriteLine("  " + m);
        mgr.DiffProduced += (w, diff) => Console.WriteLine(
            $"\n[{w.Name}] UPDATED  +{diff.Added} added, −{diff.Removed} removed, ~{diff.Modified} modified\n");
        mgr.BaselineCreated += (w, _) => Console.WriteLine($"[{w.Name}] baseline captured\n");

        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                Error($"File not found: {path}");
                mgr.Dispose();
                return 1;
            }
            mgr.Add(path);
        }

        Console.WriteLine("Watching for updates… press Ctrl+C to stop.");
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.TrySetResult(); };
        await tcs.Task;
        mgr.Dispose();
        return 0;
    }

    // ---------------- history ----------------

    static int History(string[] rest)
    {
        if (rest.Length == 1 && rest[0] is "-h" or "--help")
            return Help("history");
        if (rest.Length == 0)
        {
            Error("usage: patchdiff history <watched.dll>");
            return 1;
        }
        if (rest.Length > 1)
        {
            Error($"unexpected argument '{rest[1]}' — usage: patchdiff history <watched.dll>");
            return 1;
        }
        var dllPath = rest[0];
        if (!File.Exists(dllPath))
        {
            Error($"file not found: {dllPath}");
            return 1;
        }

        var store = new RevisionStore();
        var revs = store.List(dllPath);
        if (revs.Count == 0)
        {
            Console.WriteLine("No revisions recorded for this DLL.");
            return 0;
        }
        Console.WriteLine($"Revisions of {Path.GetFileName(dllPath)} (oldest → newest):");
        foreach (var r in revs)
            Console.WriteLine($"  {r.Sha256[..12]}…  v{r.AssemblyVersion}  {r.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {r.TypeCount} types");
        return 0;
    }

    // ---------------- helpers ----------------

    static void Progress(int done, int total) =>
        Console.Write($"\r  decompiling {done}/{total}…".PadRight(48));

    static void PrintSummary(AssemblyDiffResult diff)
    {
        Console.WriteLine();
        Console.WriteLine($"{diff.Title}");
        Console.WriteLine($"  baseline {diff.OldSha}… → current {diff.NewSha}…");
        Console.WriteLine(diff.HasChanges
            ? $"  +{diff.Added} added, −{diff.Removed} removed, ~{diff.Modified} modified"
            : "  no type changes");
        Console.WriteLine();
        foreach (var td in diff.TypeDiffs)
            Console.WriteLine($"  [{td.Change switch { ChangeKind.Added => "+", ChangeKind.Removed => "-", _ => "~" }}] {td.DisplayChange}: {td.FullName}");
        Console.WriteLine();
    }

    static void Error(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(msg);
        Console.ResetColor();
    }

    static int Unknown(string cmd)
    {
        Error($"Unknown command '{cmd}'.");
        Console.WriteLine();
        return Help();
    }

    static int Help(string? cmd = null)
    {
        switch (cmd)
        {
            case "compare":
                Console.WriteLine("""
usage:
  patchdiff compare <old.dll> <new.dll> [-o report.html]

Decompiles both assemblies and writes a side-by-side HTML report
(and prints a summary). Without -o, the report is written to the
reports directory and its path is printed.
""");
                return 0;
            case "watch":
                Console.WriteLine("""
usage:
  patchdiff watch [--poll seconds] <dll...>

Watches DLLs for changes; on each update the DLL is re-decompiled
and diffed against the baseline snapshot. Press Ctrl+C to stop.
""");
                return 0;
            case "history":
                Console.WriteLine("""
usage:
  patchdiff history <watched.dll>

Lists archived revisions of a watched DLL.
""");
                return 0;
        }

        Console.WriteLine("""
PatchDiff — decompile, snapshot and diff .NET/Unity DLLs

usage:
  patchdiff compare <old.dll> <new.dll> [-o report.html]   one-shot diff of two assemblies
  patchdiff watch [--poll seconds] <dll...>                watch DLLs, auto-diff on update
  patchdiff history <watched.dll>                          list archived revisions
  patchdiff version

Watch keeps a baseline snapshot under the PatchDiff data folder; whenever a DLL changes
on disk (e.g. a Unity game patch), it re-decompiles, diffs against the baseline, updates
the snapshot and writes an HTML report to the same folder's Reports subdirectory.
The data folder is PATCHDIFF_DATA_DIR when set, otherwise the per-user default
(%LOCALAPPDATA%\PatchDiff on Windows, ~/.local/share/PatchDiff on Linux).
""");
        return 0;
    }
}
