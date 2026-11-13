using System;
using System.IO;

namespace PatchDiff.Services;

/// <summary>
/// Turns an assembly path into a human label that says where the DLL came from.
/// Every Unity game ships an Assembly-CSharp.dll, so the file name alone cannot
/// identify a watch — the game or mod folder has to be part of the label.
/// </summary>
public static class AssemblyNaming
{
    /// <summary>Folder names that describe build output rather than a game or mod.</summary>
    static bool IsGenericFolder(string name) =>
        name.Equals("Managed", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Debug", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Release", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("x64", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("x86", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("AnyCPU", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("lib", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("libs", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("plugins", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("runtimes", StringComparison.OrdinalIgnoreCase) ||
        (name.StartsWith("net", StringComparison.OrdinalIgnoreCase) && name.Length > 3 && char.IsDigit(name[3]));

    /// <summary>
    /// The game or mod folder for an assembly path, or an empty string when the path
    /// sits somewhere that carries no name (a drive root, a bare temp folder).
    ///
    /// Unity layout: &lt;game&gt;/&lt;game&gt;_Data/Managed/Assembly-CSharp.dll → "&lt;game&gt;".
    /// Otherwise walk up past build-output folders and take the first real name.
    /// </summary>
    public static string Root(string? assemblyPath)
    {
        try
        {
            if (string.IsNullOrEmpty(assemblyPath))
                return "";
            var dir = Path.GetDirectoryName(assemblyPath);
            if (string.IsNullOrEmpty(dir))
                return "";
            var managed = new DirectoryInfo(dir);

            // .../<game>_Data/Managed → the game folder is the parent of <game>_Data
            if (managed.Name.Equals("Managed", StringComparison.OrdinalIgnoreCase) &&
                managed.Parent is { } dataDir &&
                dataDir.Name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            {
                return dataDir.Parent?.Name ?? dataDir.Name;
            }

            for (var d = managed; d is not null; d = d.Parent)
            {
                if (d.Parent is null)
                    break; // drive root or filesystem root — nothing useful here
                if (!IsGenericFolder(d.Name))
                    return d.Name;
            }
            return "";
        }
        catch { return ""; }
    }

    /// <summary>"Assembly-CSharp.dll (ATLYSS)". Falls back to the bare file name.</summary>
    public static string DisplayName(string? assemblyPath)
    {
        var name = string.IsNullOrEmpty(assemblyPath) ? "" : Path.GetFileName(assemblyPath);
        if (name.Length == 0)
            return "";
        var root = Root(assemblyPath);
        return root.Length == 0 ? name : $"{name} ({root})";
    }
}
