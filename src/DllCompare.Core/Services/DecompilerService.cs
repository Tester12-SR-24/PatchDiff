using System.IO;
using System.Security.Cryptography;
using DllCompare.Models;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace DllCompare.Services;

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
            types.Add(new TypeSource { FullName = def.FullName, Source = source });
        }

        return new AssemblySnapshot
        {
            AssemblyPath = Path.GetFullPath(dllPath),
            Sha256 = sha,
            AssemblyVersion = version,
            CapturedUtc = DateTime.UtcNow,
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
