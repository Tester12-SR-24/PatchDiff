using System.IO;
using System.Security.Cryptography;
using PatchDiff.Models;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace PatchDiff.Services;

/// <summary>
/// Builds an <see cref="AssemblySnapshot"/> (one decompiled C# source per top-level type)
/// using the same decompiler engine as ILSpy.
/// </summary>
public sealed class DecompilerService
{
    /// <summary>Directories searched for referenced assemblies (dependency + framework fallbacks).</summary>
    static readonly string[] FallbackReferenceDirs =
    {
        @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319",
        @"C:\Windows\Microsoft.NET\Framework\v4.0.30319",
        @"C:\Windows\Microsoft.NET\assembly\GAC_MSIL",
    };

    public AssemblySnapshot Build(string dllPath, Action<int, int>? progress = null)
    {
        byte[] bytes;
        {
            using var fs = OpenWithRetry(dllPath);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            bytes = ms.ToArray();
        }

        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        using var stream = new MemoryStream(bytes);
        var module = new PEFile(dllPath, stream);

        var version = module.Metadata.GetAssemblyDefinition().Version.ToString();

        var resolver = new DirectoryAssemblyResolver(Path.GetDirectoryName(dllPath)!);
        var typeSystem = new DecompilerTypeSystem(module, resolver);
        var decompiler = new CSharpDecompiler(typeSystem, new DecompilerSettings());

        var types = new List<TypeSource>();
        var all = typeSystem.MainModule.TopLevelTypeDefinitions.ToList();
        int i = 0;
        foreach (var def in all)
        {
            i++;
            progress?.Invoke(i, all.Count);

            // Skip compiler-generated artifacts (<PrivateImplementationDetails>, lambdas, anonymous types...)
            if (def.Name.StartsWith("<"))
                continue;
            if (def.Kind == TypeKind.Unknown)
                continue;

            string source;
            try
            {
                source = decompiler.DecompileTypeAsString(new FullTypeName(def.FullName));
            }
            catch (Exception ex)
            {
                source = $"// Decompilation failed for {def.FullName}: {ex.Message}";
            }
    
        // Collect short member signatures for the assembly explorer tree.
        List<string>? signatures = null;
        try
        {
            signatures = new List<string>();
            string Short(string? fullName)
            {
                if (string.IsNullOrEmpty(fullName))
                    return "?";
                var name = fullName;
                var dot = name.LastIndexOf('.');
                if (dot >= 0)
                    name = name[(dot + 1)..];
                var tick = name.IndexOf('`');
                if (tick > 0)
                    name = name[..tick];
                return name;
            }
            static string Acc(IEntity m) => m.Accessibility switch
            {
                Accessibility.Public => "public",
                Accessibility.Protected => "protected",
                Accessibility.Internal => "internal",
                Accessibility.ProtectedOrInternal => "protected internal",
                Accessibility.ProtectedAndInternal => "private protected",
                Accessibility.Private => "private",
                _ => "",
            };

            var eventNames = new HashSet<string>(def.Events.Select(ev => ev.Name), StringComparer.Ordinal);
            foreach (var f in def.Fields)
            {
                if (f.Name.StartsWith("<") || eventNames.Contains(f.Name))
                    continue; // skip compiler-generated backing fields of field-like events
                signatures.Add($"{Acc(f)} {Short(f.ReturnType.FullName)} {f.Name};");
            }
            foreach (var pr in def.Properties)
            {
                if (pr.Name.StartsWith("<"))
                    continue;
                signatures.Add($"{Acc(pr)} {Short(pr.ReturnType.FullName)} {pr.Name} {{ get; set; }}");
            }
            foreach (var m in def.Methods)
            {
                if (m.Name.StartsWith("<"))
                    continue;
                var pars = string.Join(", ", m.Parameters.Select(pa => $"{Short(pa.Type.FullName)} {pa.Name}"));
                var ret = m.IsConstructor ? "" : Short(m.ReturnType.FullName) + " ";
                signatures.Add($"{Acc(m)} {ret}{m.Name}({pars});");
            }
            foreach (var ev in def.Events)
            {
                if (ev.Name.StartsWith("<"))
                    continue;
                signatures.Add($"{Acc(ev)} event {Short(ev.ReturnType.FullName)} {ev.Name};");
            }
        }
        catch
        {
            signatures = null; // fall back to types-only tree
        }
        types.Add(new TypeSource { FullName = def.FullName, Source = source, Signatures = signatures });

        }

        return new AssemblySnapshot
        {
            AssemblyPath = Path.GetFullPath(dllPath),
            Sha256 = sha,
            AssemblyVersion = version,
            CapturedUtc = DateTime.UtcNow,
            SchemaVersion = AssemblySnapshot.CurrentSchemaVersion,
            Types = types,
        };
    }

    static FileStream OpenWithRetry(string path, int attempts = 24, int delayMs = 500)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                // Shared read/write: tolerate files still being copied (e.g. Unity replacing the DLL).
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException) when (i < attempts)
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    sealed class DirectoryAssemblyResolver : IAssemblyResolver
    {
        readonly List<string> _searchDirs;
        readonly Dictionary<string, PEFile> _cache = new(StringComparer.OrdinalIgnoreCase);

        public DirectoryAssemblyResolver(string mainDirectory)
        {
            _searchDirs = new List<string> { mainDirectory };
            _searchDirs.AddRange(FallbackReferenceDirs.Where(Directory.Exists));
        }

        public MetadataFile? Resolve(IAssemblyReference reference)
        {
            if (_cache.TryGetValue(reference.Name, out var cached))
                return cached;

            foreach (var dir in _searchDirs)
            {
                foreach (var ext in new[] { ".dll", ".exe" })
                {
                    var candidate = Path.Combine(dir, reference.Name + ext);
                    if (!File.Exists(candidate))
                        continue;
                    try
                    {
                        var pe = new PEFile(candidate, new FileStream(
                            candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                        _cache[reference.Name] = pe;
                        return pe;
                    }
                    catch
                    {
                        // Bad/locked candidate; keep searching.
                    }
                }
            }
            return null; // unresolved: decompiler renders those references as-is
        }

        public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Task.FromResult(Resolve(reference));

        public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult(ResolveModule(mainModule, moduleName));

        public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName)
        {
            var name = Path.GetFileNameWithoutExtension(moduleName);
            foreach (var dir in _searchDirs)
            {
                var candidate = Path.Combine(dir, name + ".dll");
                if (!File.Exists(candidate))
                    continue;
                return new PEFile(candidate, new FileStream(
                    candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            }
            return null;
        }
    }
}
