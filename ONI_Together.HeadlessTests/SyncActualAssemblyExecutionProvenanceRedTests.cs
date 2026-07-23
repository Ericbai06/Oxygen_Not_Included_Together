using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ONI_Together.HeadlessTests;

internal static class SyncActualAssemblyExecutionProvenanceRedTests
{
    private const string GameLibsEnvironment = "ONI_GAME_LIBS";

    public static void ActualAssemblyReceiptRequiresBinaryBoundOrigin()
    {
        ActualCatalog actual = LoadActualCatalog();
        SyncExecutionFixtureAssembly fixture = LoadDebugAssembly(actual.RepositoryRoot);
        ISyncExecutionProbeFactory factory = SyncExecutionProbeFactoryLoader.Load();
        SyncExecutionProbeBinding binding = Binding(actual.InventoryDigest);
        AssertInvalidImagesFail(factory, binding, actual.Catalog, fixture);
        AssertSyntheticAssemblyCannotClaimCatalog(factory, binding, actual.Catalog);

        using AssemblyResolver resolver = new(actual.RepositoryRoot, actual.GameLibsFolder);
        PureInvocation invocation = DiscoverPureInvocation(
            Assembly.Load(fixture.PeImage, fixture.PdbImage), fixture, actual.Catalog);
        SyncCatalogScan probeCatalog = ProbeCatalog(actual.Catalog, invocation.Symbol);
        WriteDiscoveryEvidence(actual, fixture, invocation, probeCatalog);
        ISyncExecutionProbeSession session = factory.Start(
            binding, probeCatalog, fixture);
        Invoke(session.RuntimeAssembly, invocation);
        SyncExecutionReceipt receipt = session.Complete();
        SyncEntry[] executed = ExecutedEntries(actual.Catalog, receipt, invocation.Symbol);
        True(executed.Length > 0,
            "actual production invocation produced no catalog entry receipts");
        foreach (SyncEntry entry in executed)
            True(SyncExecutionProvenance.MatchesOrigin(receipt, entry),
                $"actual receipt origin mismatch: {entry.Id}");

        AssertManualCatalogClaimsCannotAuthenticate(actual.Catalog, binding, executed[0]);
    }

    private static SyncCatalogScan ProbeCatalog(
        SyncCatalogScan actual,
        string symbol)
    {
        SyncEntry[] entries = actual.Entries.Where(entry =>
                entry.FullyQualifiedSymbol == symbol)
            .ToArray();
        True(entries.Length > 0,
            "discovered production method has no catalog entries");
        return new SyncCatalogScan(entries, []);
    }

    private static void WriteDiscoveryEvidence(
        ActualCatalog actual,
        SyncExecutionFixtureAssembly fixture,
        PureInvocation invocation,
        SyncCatalogScan probeCatalog)
    {
        string ids = string.Join(",", probeCatalog.Entries
            .Select(entry => entry.Id).Order(StringComparer.Ordinal));
        Console.WriteLine("ACTUAL_PROBE " +
            $"inventory={actual.InventoryDigest} " +
            $"assembly={Digest(fixture.PeImage)} pdb={Digest(fixture.PdbImage)} " +
            $"symbol={invocation.Symbol} entries={ids}");
    }

    private static string Digest(byte[] content)
    {
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }
    private static ActualCatalog LoadActualCatalog()
    {
        string root = FindRepositoryRoot();
        string project = Path.Combine(root, "ONI_Together", "ONI_Together.csproj");
        string gameLibs = Environment.GetEnvironmentVariable(GameLibsEnvironment) ??
            throw new InvalidOperationException($"{GameLibsEnvironment} is required");
        SyncBuildVariant[] variants = Variants();
        IReadOnlyList<SyncVariantInput> inputs = SyncMsBuildProjectLoader.Load(
            project, variants, new Dictionary<string, string>
            {
                ["GameLibsFolder"] = gameLibs
            });
        SyncCatalogScan catalog = SyncSurfaceScanner.ScanCatalogVariants(inputs);
        True(catalog.Errors.Count == 0,
            "actual eight-variant catalog contains errors");
        string[] keys = catalog.Entries.SelectMany(entry => entry.Variants)
            .Select(variant => variant.Key).Distinct(StringComparer.Ordinal).ToArray();
        True(keys.ToHashSet(StringComparer.Ordinal).SetEquals(
            variants.Select(variant => variant.Key)),
            "actual catalog does not cover the requested eight variants");
        string inventory = SyncInventoryJson.Serialize(catalog);
        using JsonDocument document = JsonDocument.Parse(inventory);
        string digest = document.RootElement.GetProperty("digest").GetString()!;
        return new ActualCatalog(root, gameLibs, catalog, digest);
    }

    private static SyncExecutionFixtureAssembly LoadDebugAssembly(string root)
    {
        string output = Path.Combine(root, "ONI_Together", "bin", "Debug",
            "netstandard2.1");
        string assembly = Path.Combine(output, "ONI_Together.dll");
        string symbols = Path.Combine(output, "ONI_Together.pdb");
        True(File.Exists(assembly), $"Debug assembly is missing: {assembly}");
        True(File.Exists(symbols), $"portable PDB is missing: {symbols}");
        byte[] pe = File.ReadAllBytes(assembly);
        byte[] pdb = File.ReadAllBytes(symbols);
        True(pe.Length > 0 && pdb.Length > 0,
            "Debug assembly and portable PDB must be non-empty");
        return new SyncExecutionFixtureAssembly(pe, pdb);
    }

    private static void AssertInvalidImagesFail(
        ISyncExecutionProbeFactory factory,
        SyncExecutionProbeBinding binding,
        SyncCatalogScan catalog,
        SyncExecutionFixtureAssembly actual)
    {
        Throws<FormatException>(() => factory.Start(binding, catalog,
            new SyncExecutionFixtureAssembly(actual.PeImage, [])),
            "missing portable PDB was accepted");
        SyncExecutionFixtureAssembly synthetic = SyncExecutionProbeFixtureCatalog.Compile();
        Throws<FormatException>(() => factory.Start(binding, catalog,
            new SyncExecutionFixtureAssembly(actual.PeImage, synthetic.PdbImage)),
            "mismatched assembly/PDB pair was accepted");
    }

    private static void AssertSyntheticAssemblyCannotClaimCatalog(
        ISyncExecutionProbeFactory factory,
        SyncExecutionProbeBinding binding,
        SyncCatalogScan catalog)
    {
        ISyncExecutionProbeSession synthetic = factory.Start(
            binding, catalog, SyncExecutionProbeFixtureCatalog.Compile());
        Throws<InvalidOperationException>(() => synthetic.Complete(),
            "synthetic fixture authenticated actual production catalog entries");
    }

    private static PureInvocation DiscoverPureInvocation(
        Assembly assembly,
        SyncExecutionFixtureAssembly fixture,
        SyncCatalogScan catalog)
    {
        HashSet<int> safeTokens = SafeMethodTokens(fixture);
        foreach (IGrouping<string, SyncEntry> group in catalog.Entries
                     .Where(IsExecutableCandidate)
                     .GroupBy(entry => entry.FullyQualifiedSymbol, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            foreach (MethodInfo method in ResolveMethods(assembly, group.Key))
            {
                if (!safeTokens.Contains(method.MetadataToken) ||
                    !TryArguments(method, out _))
                    continue;
                return new PureInvocation(group.Key, method.Name,
                    method.GetParameters().Length);
            }
        }
        throw new InvalidOperationException(
            "actual catalog has no deterministic pure invocable sync entry owner");
    }

    private static HashSet<int> SafeMethodTokens(
        SyncExecutionFixtureAssembly fixture)
    {
        using var stream = new MemoryStream(fixture.PeImage, writable: false);
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(stream);
        return AllTypes(assembly.MainModule)
            .SelectMany(type => type.Methods)
            .Where(IsPureCandidateBody)
            .Select(method => method.MetadataToken.ToInt32())
            .ToHashSet();
    }

    private static bool IsPureCandidateBody(MethodDefinition method)
    {
        if (!method.IsStatic || !method.HasBody ||
            method.Parameters.Count > 2 ||
            method.ReturnType.FullName is not ("System.Boolean" or "System.Int32" or
                "System.String" or "System.Void"))
            return false;
        if (!method.Name.StartsWith("Try", StringComparison.Ordinal) &&
            !method.Name.StartsWith("Can", StringComparison.Ordinal) &&
            !method.Name.StartsWith("Should", StringComparison.Ordinal) &&
            !method.Name.StartsWith("Get", StringComparison.Ordinal) &&
            !method.Name.StartsWith("Is", StringComparison.Ordinal))
            return false;
        return method.Body.Instructions.All(IsWorldFreeInstruction);
    }

    private static bool IsWorldFreeInstruction(Instruction instruction)
    {
        if (instruction.OpCode == OpCodes.Stsfld)
            return false;
        if (instruction.Operand is MethodReference method)
            return IsAllowedScope(method.DeclaringType);
        if (instruction.Operand is FieldReference field)
            return IsAllowedScope(field.DeclaringType);
        if (instruction.Operand is TypeReference type)
            return IsAllowedScope(type);
        return true;
    }

    private static bool IsAllowedScope(TypeReference type)
    {
        string scope = type.Scope?.Name ?? "";
        return scope is "ONI_Together" or "mscorlib" or "System.Runtime" ||
            type.Namespace.StartsWith("System", StringComparison.Ordinal);
    }

    private static IEnumerable<MethodInfo> ResolveMethods(
        Assembly assembly,
        string symbol)
    {
        string member = symbol[..symbol.IndexOf('(')];
        int separator = member.LastIndexOf('.');
        if (separator <= 0)
            return [];
        string typeName = member[..separator];
        string methodName = member[(separator + 1)..];
        Type? type = ResolveType(assembly, typeName);
        return type?.GetMethods(BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic)
            .Where(method => method.Name == methodName) ?? [];
    }

    private static Type? ResolveType(Assembly assembly, string name)
    {
        Type? type = assembly.GetType(name, throwOnError: false);
        for (int dot = name.LastIndexOf('.'); type is null && dot > 0;
             dot = name.LastIndexOf('.', dot - 1))
            type = assembly.GetType(name[..dot] + "+" + name[(dot + 1)..],
                throwOnError: false);
        return type;
    }

    private static bool TryArguments(MethodInfo method, out object?[] arguments)
    {
        ParameterInfo[] parameters = method.GetParameters();
        arguments = new object?[parameters.Length];
        for (int index = 0; index < parameters.Length; index++)
        {
            Type type = parameters[index].ParameterType;
            if (type.IsByRef || type.FullName?.StartsWith(
                    "UnityEngine.", StringComparison.Ordinal) == true)
                return false;
            if (!TryValue(type, out arguments[index]))
                return false;
        }
        return true;
    }

    private static bool TryValue(Type type, out object? value)
    {
        value = null;
        if (type == typeof(string))
        {
            value = "";
            return true;
        }
        if (type.IsArray)
        {
            value = Array.CreateInstance(type.GetElementType()!, 0);
            return true;
        }
        if (type.IsValueType)
        {
            value = Activator.CreateInstance(type);
            return true;
        }
        if (type.Assembly.GetName().Name != "ONI_Together")
            return false;
        try
        {
            value = Activator.CreateInstance(type, nonPublic: true);
            if (value is not null)
                foreach (string name in new[] { "PlayerID", "SenderConnectionGeneration", "Revision" })
                {
                    FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
                    object? positive = field?.FieldType == typeof(ulong) ? 1UL
                        : field?.FieldType == typeof(long) ? 1L : null;
                    if (positive is not null) field!.SetValue(value, positive);
                }
            return value is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryInvoke(
        MethodInfo method,
        object?[] arguments,
        out object? result)
    {
        try
        {
            result = method.Invoke(null, arguments);
            return true;
        }
        catch (TargetInvocationException)
        {
            result = null;
            return false;
        }
    }

    private static void Invoke(Assembly assembly, PureInvocation invocation)
    {
        MethodInfo method = ResolveMethods(assembly, invocation.Symbol)
            .Single(candidate => candidate.Name == invocation.MethodName &&
                candidate.GetParameters().Length == invocation.ParameterCount &&
                TryArguments(candidate, out _));
        True(TryArguments(method, out object?[] arguments),
            "selected production method is no longer invocable");
        if (!TryInvoke(method, arguments, out _))
            throw new InvalidOperationException(
                "selected production method failed after instrumentation");
    }

    private static SyncEntry[] ExecutedEntries(
        SyncCatalogScan catalog,
        SyncExecutionReceipt receipt,
        string symbol)
    {
        IReadOnlySet<string> ids = receipt.ExecutedEntryIds
            .ToHashSet(StringComparer.Ordinal);
        return catalog.Entries.Where(entry =>
                entry.FullyQualifiedSymbol == symbol && ids.Contains(entry.Id))
            .ToArray();
    }

    private static void AssertManualCatalogClaimsCannotAuthenticate(
        SyncCatalogScan catalog,
        SyncExecutionProbeBinding binding,
        SyncEntry actualEntry)
    {
        var forged = new SyncExecutionReceipt(
            1, binding.RunId + "-forged", binding.InventoryDigest,
            binding.CoverageDigest, binding.TestId, binding.Tier,
            binding.ScenarioId, binding.Polarity, [actualEntry.Id], [], [], null);
        SyncExecutionProvenance.AttachFromCatalog(
            forged, catalog, [actualEntry.Id], []);
        True(!SyncExecutionProvenance.MatchesOrigin(forged, actualEntry),
            "manual entry ID/catalog origin authenticated an unexecuted production entry");
    }

    private static SyncExecutionProbeBinding Binding(string inventoryDigest)
    {
        return new SyncExecutionProbeBinding
        {
            RunId = "actual-production-assembly",
            TestId = "headless:actual-production-assembly",
            Tier = SyncExecutionTier.Headless,
            ScenarioId = null,
            Polarity = SyncExecutionPolarity.Positive,
            InventoryDigest = inventoryDigest,
            CoverageDigest = new string('a', 64)
        };
    }

    private static bool IsExecutableCandidate(SyncEntry entry)
    {
        return entry.Status == SyncEntryStatus.Active &&
            entry.Variants.Any(variant => variant.Key == "Debug/OS_MAC") &&
            entry.FullyQualifiedSymbol.Contains('(');
    }
    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (TypeDefinition type in module.Types)
        {
            yield return type;
            foreach (TypeDefinition nested in AllNested(type))
                yield return nested;
        }
    }

    private static IEnumerable<TypeDefinition> AllNested(TypeDefinition parent)
    {
        foreach (TypeDefinition nested in parent.NestedTypes)
        {
            yield return nested;
            foreach (TypeDefinition child in AllNested(nested))
                yield return child;
        }
    }

    private static SyncBuildVariant[] Variants()
    {
        return
        [
            Variant("Debug", "OS_MAC", "DEBUG", "OS_MAC"),
            Variant("Debug", "OS_WINDOWS", "DEBUG", "OS_WINDOWS"),
            Variant("Debug", "OS_LINUX", "DEBUG", "OS_LINUX"),
            Variant("Debug", "OS_FREEBSD", "DEBUG", "OS_FREEBSD"),
            Variant("Release", "OS_MAC", "OS_MAC"),
            Variant("Release", "OS_WINDOWS", "OS_WINDOWS"),
            Variant("Release", "OS_LINUX", "OS_LINUX"),
            Variant("Release", "OS_FREEBSD", "OS_FREEBSD")
        ];
    }

    private static SyncBuildVariant Variant(
        string configuration,
        string platform,
        params string[] symbols)
    {
        return new SyncBuildVariant(configuration, platform,
            symbols.ToHashSet(StringComparer.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
             directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "Shared")) &&
                Directory.Exists(Path.Combine(directory.FullName, "ONI_Together")))
                return directory.FullName;
        throw new InvalidOperationException("ONI_Together repository root was not found");
    }

    private static void Throws<T>(Action action, string message)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record ActualCatalog(string RepositoryRoot, string GameLibsFolder,
        SyncCatalogScan Catalog, string InventoryDigest);

    private sealed record PureInvocation(string Symbol, string MethodName, int ParameterCount);

    private sealed class AssemblyResolver : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string> assemblies;

        public AssemblyResolver(string repositoryRoot, string gameLibsFolder)
        {
            string[] directories =
            [
                Path.Combine(repositoryRoot, "ONI_Together", "bin", "Debug",
                    "netstandard2.1"),
                Path.Combine(repositoryRoot, "Shared", "bin", "Debug", "netstandard2.1"),
                gameLibsFolder
            ];
            assemblies = IndexAssemblies(directories);
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.AssemblyResolve -= Resolve;
        }

        private Assembly? Resolve(object? sender, ResolveEventArgs arguments)
        {
            string? name = new AssemblyName(arguments.Name).Name;
            return name is not null && assemblies.TryGetValue(name, out string? path)
                ? Assembly.LoadFrom(path)
                : null;
        }

        private static IReadOnlyDictionary<string, string> IndexAssemblies(
            IEnumerable<string> directories)
        {
            var result = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string path in directories.Where(Directory.Exists)
                         .SelectMany(directory => Directory.EnumerateFiles(
                             directory, "*.dll", SearchOption.TopDirectoryOnly)))
            {
                try
                {
                    string? name = AssemblyName.GetAssemblyName(path).Name;
                    if (name is not null)
                        result.TryAdd(name, path);
                }
                catch (BadImageFormatException)
                {
                }
            }
            return result;
        }
    }
}
