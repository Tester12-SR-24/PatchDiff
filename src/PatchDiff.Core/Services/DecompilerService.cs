using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
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

    /// <summary>
    /// Decompiler settings modelled on ILSpy's own defaults (ILSpy's DecompilerSettings is
    /// the reference implementation for this engine): modern C# constructs are rendered as
    /// such instead of being lowered to verbose older syntax, dead code/stores are folded
    /// away, and unresolvable references degrade gracefully instead of throwing.
    /// </summary>
    public static DecompilerSettings CreateSettings() => new()
    {
        UsingDeclarations = true,               // file-scoped using blocks instead of fully-qualified names
        SwitchExpressions = true,               // `x switch { ... }` over long if-chains
        RemoveDeadCode = true,                  // ILSpy behaviour: drop unreachable branches
        RemoveDeadStores = true,                // and stores whose value is never read
        AnonymousMethods = true,                // lambdas instead of anonymous delegates
        StringInterpolation = true,             // $"{x}" instead of string.Format
        PatternMatching = true,                 // is-patterns and switch on type
        UseImplicitMethodGroupConversion = true,
        ThrowOnAssemblyResolveErrors = false,   // Unresolved refs render as-is, never abort the type
        ShowXmlDocumentation = false,           // keep diff noise down (docs are not code)
        AlwaysUseBraces = false,                // ILSpy keeps single-statement bodies brace-less
        SeparateLocalVariableDeclarations = false,
    };

    public AssemblySnapshot Build(string dllPath, Action<int, int>? progress = null) =>
        BuildWithBytes(dllPath, progress).snapshot;

    public (AssemblySnapshot snapshot, byte[] bytes) BuildWithBytes(string dllPath,
        Action<int, int>? progress = null, CancellationToken cancellationToken = default)
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
        var decompiler = new CSharpDecompiler(typeSystem, CreateSettings())
        {
            CancellationToken = cancellationToken,
        };

        var types = new List<TypeSource>();
        var fullNameSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        var all = typeSystem.MainModule.TopLevelTypeDefinitions.ToList();
        int i = 0;
        foreach (var def in all)
        {
            i++;
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(i, all.Count);

            // Skip compiler-generated artifacts (<PrivateImplementationDetails>, lambdas, anonymous types...)
            if (def.Name.StartsWith("<"))
                continue;
            if (def.Kind == TypeKind.Unknown)
                continue;

            string source;
            try
            {
                // def.FullTypeName is the decompiler's own canonical type name; going through
                // the FullName string round-trip can mis-render generic arity in edge cases.
                source = decompiler.DecompileTypeAsString(def.FullTypeName);
            }
            catch (Exception ex)
            {
                // ILSpy's fallback strategy: when C# decompilation fails, show the IL rather
                // than nothing. That keeps the type in the diff (so a change is still visible)
                // and gives a human something actionable to read.
                source = DisassembleOrExplain(module, def.MetadataToken, def.FullName, ex);
            }
    
            string fullName = def.FullName;
            if (fullNameSeen.TryGetValue(fullName, out var seen))
            {
                fullNameSeen[fullName] = seen + 1;
                fullName = $"{fullName}#{seen + 1}";
            }
            else
            {
                fullNameSeen[fullName] = 1;
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
        types.Add(new TypeSource
        {
            // Distinct types can collide on FullName in edge cases (obfuscated/dynamic
            // metadata). Suffix duplicates instead of silently merging (the old dedupe
            // dropped all but the first) so every type stays visible in diffs.
            FullName = fullName,
            Source = source,
            Signatures = signatures,
        });

        }

        var snapshot = new AssemblySnapshot
        {
            AssemblyPath = Path.GetFullPath(dllPath),
            Sha256 = sha,
            AssemblyVersion = version,
            CapturedUtc = DateTime.UtcNow,
            SchemaVersion = AssemblySnapshot.CurrentSchemaVersion,
            Types = types,
        };
        return (snapshot, bytes);
    }

    /// <summary>
    /// Renders the type's IL as a readable listing. Falls back to a diagnostic comment only
    /// if even the disassembler fails (corrupt metadata etc.).
    /// </summary>
    static string DisassembleOrExplain(PEFile module, EntityHandle handle,
        string fullName, Exception decompileError)
    {
        try
        {
            if (handle.Kind != HandleKind.TypeDefinition)
                throw new InvalidOperationException($"unexpected metadata handle kind: {handle.Kind}");
            var typeHandle = (TypeDefinitionHandle)handle;
            var output = new PlainTextOutput();
            var disassembler = new ICSharpCode.Decompiler.Disassembler.ReflectionDisassembler(output, CancellationToken.None);
            disassembler.DisassembleType(module, typeHandle);
            var il = output.ToString().Trim();
            if (il.Length == 0)
                throw new InvalidOperationException("empty IL listing");

            return $"""
// C# decompilation failed for {fullName} ({decompileError.GetType().Name}: {decompileError.Message})
// Falling back to the IL listing, as ILSpy does. This type still participates in diffs.
{il}
""";
        }
        catch (Exception ilError)
        {
            // Surface SOMETHING per member (QA requirement): a failed member must show up in
            // diffs as a changed source block, never silently disappear.
            return $"""
// Decompilation failed for {fullName}: {decompileError.Message}
// IL fallback also failed: {ilError.GetType().Name}: {ilError.Message}
""";
        }
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
