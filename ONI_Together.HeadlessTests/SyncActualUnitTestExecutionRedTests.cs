using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
namespace ONI_Together.HeadlessTests;

internal static class SyncActualUnitTestExecutionRedTests
{
    private const string GameLibsEnvironment = "ONI_GAME_LIBS";
    private static ActualRun? lastRun;

    public static void ActualDebugUnitTestReceiptRequiresBinaryHashes()
    {
        ActualContext context = LoadContext();
        using var resolver = new RuntimeResolver(
            context.RepositoryRoot, context.GameLibsFolder);
        RegisteredTest[] discovered = Discover(
            Assembly.Load(context.Assembly.PeImage, context.Assembly.PdbImage));
        True(discovered.Length > 0, "real UnitTestRegistry discovered no tests");

        RunSelection selection = SelectPlan(context, discovered);
        ExecutedRecord executed = Execute(context, selection.Safe);
        var notRun = new UnitTestRecord(
            selection.Unsafe.Id, UnitOutcome.NotRun,
            selection.NotRunReason, null, context.DllHash,
            context.PdbHash, context.InventoryDigest);
        var allowlist = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [selection.Unsafe.Id] = selection.NotRunReason
        };
        lastRun = new ActualRun(context, discovered.Select(test => test.Id)
            .ToHashSet(StringComparer.Ordinal), allowlist, executed.Record, notRun);
        Validate(executed.Record, lastRun);
        Validate(notRun, lastRun);
        WriteEvidence(context, selection, executed);

        Equal(context.DllHash, RequiredReceiptHash(
            executed.Receipt, "DllHash"));
        Equal(context.PdbHash, RequiredReceiptHash(
            executed.Receipt, "PdbHash"));
    }

    public static void UnitTestReceiptMutationsFailClosed()
    {
        ActualRun run = lastRun ??
            throw new InvalidOperationException("actual UnitTest run was not recorded");
        Validate(run.Executed, run);
        Validate(run.NotRun, run);

        Reject(run.Executed with { TestId = "headless:unit:unknown" }, run,
            "unknown UnitTest ID was accepted");
        Reject(run.Executed with { DllHash = Changed(run.Executed.DllHash) }, run,
            "changed DLL hash was accepted");
        Reject(run.Executed with { PdbHash = Changed(run.Executed.PdbHash) }, run,
            "changed PDB hash was accepted");
        Reject(run.Executed with {
            InventoryDigest = Changed(run.Executed.InventoryDigest)
        }, run, "changed inventory digest was accepted");
        Reject(run.NotRun with { TestId = run.Executed.TestId }, run,
            "unallowlisted notRun was accepted");

        SyncExecutionReceipt empty = run.Executed.Receipt! with {
            ExecutedEntryIds = []
        };
        Reject(run.Executed with { Receipt = empty }, run,
            "empty executed entries were accepted");
        SyncExecutionReceipt forged = run.Executed.Receipt! with {
            RunId = run.Executed.Receipt!.RunId + "-forged"
        };
        Reject(run.Executed with { Receipt = forged }, run,
            "forged receipt without observed provenance was accepted");

        ISyncExecutionProbeFactory factory = SyncExecutionProbeFactoryLoader.Load();
        Throws<InvalidOperationException>(() => factory.Start(
            Binding(run.Executed.TestId, run.Context.InventoryDigest),
            run.Context.Catalog, SyncExecutionProbeFixtureCatalog.Compile()).Complete(),
            "synthetic fixture authenticated actual UnitTest catalog entries");
    }

    private static ExecutedRecord Execute(
        ActualContext context,
        Candidate candidate)
    {
        SyncCatalogScan probeCatalog = new(
            context.Catalog.Entries.Where(entry =>
                candidate.TargetSymbols.Contains(entry.FullyQualifiedSymbol)).ToArray(),
            []);
        ISyncExecutionProbeSession session = SyncExecutionProbeFactoryLoader.Load()
            .Start(Binding(candidate.Test.Id, context.InventoryDigest),
                probeCatalog, context.Assembly);
        RegisteredTest test = Discover(session.RuntimeAssembly)
            .Single(item => item.Id == candidate.Test.Id);
        InvokeRun(test.Instance);
        UnitOutcome outcome = ParseOutcome(test.Instance);
        string? message = Property(test.Instance, "Message") as string;
        SyncExecutionReceipt receipt = session.Complete();
        var record = new UnitTestRecord(
            test.Id, outcome, message, receipt, context.DllHash,
            context.PdbHash, context.InventoryDigest);
        return new ExecutedRecord(record, receipt);
    }

    private static RunSelection SelectPlan(
        ActualContext context,
        IReadOnlyList<RegisteredTest> tests)
    {
        using var stream = new MemoryStream(context.Assembly.PeImage, writable: false);
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(stream);
        Dictionary<int, MethodDefinition> methods = AllTypes(assembly.MainModule)
            .SelectMany(type => type.Methods)
            .ToDictionary(method => method.MetadataToken.ToInt32());
        Dictionary<string, SyncEntry[]> groups = context.Catalog.Entries
            .Where(entry => entry.Status == SyncEntryStatus.Active)
            .GroupBy(entry => entry.FullyQualifiedSymbol, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(),
                StringComparer.Ordinal);
        foreach (RegisteredTest test in tests.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            if (!methods.TryGetValue(test.Method.MetadataToken, out MethodDefinition? method) ||
                !HeadlessSafe(method))
                continue;
            string[] targets = method.Body.Instructions
                .Select(instruction => instruction.Operand as MethodReference)
                .Where(reference => reference is not null)
                .Select(reference => Symbol(reference!))
                .Where(groups.ContainsKey)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (targets.Length == 0)
                continue;
            InvokeRun(test.Instance);
            UnitOutcome outcome = ParseOutcome(test.Instance);
            Console.WriteLine($"UNIT_CANDIDATE test={test.Id} " +
                $"method={MethodSymbol(test.Method)} outcome={outcome} " +
                $"message={Property(test.Instance, "Message")}");
            if (outcome != UnitOutcome.Passed)
                continue;
            RegisteredTest? unsafeTest = tests.FirstOrDefault(item =>
                methods.TryGetValue(item.Method.MetadataToken, out MethodDefinition? other) &&
                !HeadlessSafe(other));
            True(unsafeTest is not null,
                "real registry has no structurally Unity-only notRun candidate");
            return new RunSelection(
                new Candidate(test, targets.ToHashSet(StringComparer.Ordinal)),
                unsafeTest!,
                "not-headless: method references Unity or game-world runtime");
        }
        throw new InvalidOperationException(
            "real registry has no passing headless-safe UnitTest that executes sync callsites");
    }
    private static bool HeadlessSafe(MethodDefinition method)
    {
        if (!method.IsStatic || !method.HasBody || method.Parameters.Count != 0 ||
            method.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Stsfld))
            return false;
        return method.Body.Instructions.All(instruction =>
        {
            TypeReference? type = instruction.Operand switch
            {
                MethodReference member => member.DeclaringType,
                FieldReference member => member.DeclaringType,
                TypeReference referenced => referenced,
                _ => null
            };
            string scope = type?.Scope?.Name ?? "";
            return !scope.StartsWith("UnityEngine", StringComparison.Ordinal) &&
                scope is not "Assembly-CSharp" and not "Assembly-CSharp-firstpass";
        });
    }
    private static RegisteredTest[] Discover(Assembly assembly)
    {
        Type registry = assembly.GetType(
            "ONI_Together.DebugTools.UnitTestRegistry", throwOnError: true)!;
        bool discovered = (bool)registry.GetMethod(
            "DiscoverTests", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!;
        True(discovered, "real UnitTestRegistry discovery failed");
        var tests = (IEnumerable)registry.GetProperty(
            "Tests", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        return tests.Cast<object>().Select(instance =>
        {
            MethodInfo method = (MethodInfo)instance.GetType().GetField(
                "_method", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(instance)!;
            True(method.GetCustomAttributes().Any(attribute =>
                    attribute.GetType().FullName ==
                    "ONI_Together.DebugTools.UnitTestAttribute"),
                "registry returned a test without UnitTestAttribute");
            return new RegisteredTest(StableTestId(method), instance, method);
        }).ToArray();
    }
    private static string StableTestId(MethodInfo method)
    {
        string identity = MethodSymbol(method);
        string digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
        return "headless:unit:" + digest;
    }
    private static string MethodSymbol(MethodInfo method)
    {
        string parameters = string.Join(",", method.GetParameters()
            .Select(parameter => TypeName(parameter.ParameterType)));
        return $"{TypeName(method.DeclaringType!)}.{method.Name}({parameters})";
    }
    private static string Symbol(MethodReference method)
    {
        string parameters = string.Join(",", method.Parameters
            .Select(parameter => TypeName(parameter.ParameterType.FullName)));
        return $"{TypeName(method.DeclaringType.FullName)}.{method.Name}({parameters})";
    }

    private static string TypeName(Type type) =>
        TypeName(type.FullName ?? type.Name);

    private static string TypeName(string name) =>
        name.Replace('/', '+').Replace("::", ".");

    private static void InvokeRun(object test)
    {
        test.GetType().GetMethod("Run", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(test, [false]);
    }

    private static UnitOutcome ParseOutcome(object test)
    {
        string state = Property(test, "State")?.ToString() ??
            throw new InvalidOperationException("UnitTest state is missing");
        return Enum.TryParse(state, ignoreCase: false, out UnitOutcome outcome)
            ? outcome
            : throw new InvalidOperationException($"unknown UnitTest state {state}");
    }

    private static object? Property(object value, string name) =>
        value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(value);

    private static void Validate(UnitTestRecord record, ActualRun run)
    {
        True(run.KnownTestIds.Contains(record.TestId), "unknown UnitTest ID");
        Equal(run.Context.DllHash, record.DllHash);
        Equal(run.Context.PdbHash, record.PdbHash);
        Equal(run.Context.InventoryDigest, record.InventoryDigest);
        if (record.Outcome == UnitOutcome.NotRun)
        {
            True(record.Receipt is null, "notRun record contains execution receipt");
            True(run.NotRunAllowlist.TryGetValue(record.TestId, out string? reason) &&
                reason == record.Message, "notRun is not explicitly allowlisted");
            return;
        }
        SyncExecutionReceipt receipt = record.Receipt ??
            throw new InvalidOperationException("executed UnitTest has no receipt");
        True(receipt.ExecutedEntryIds.Count > 0,
            "executed UnitTest receipt has no entry IDs");
        Equal(record.TestId, receipt.TestId);
        Equal(record.InventoryDigest, receipt.InventoryDigest);
        foreach (string id in receipt.ExecutedEntryIds)
        {
            SyncEntry entry = run.Context.Catalog.Entries.Single(item => item.Id == id);
            True(SyncExecutionProvenance.IsObserved(receipt, id) &&
                SyncExecutionProvenance.MatchesOrigin(receipt, entry),
                $"entry is not authenticated by observed callsite: {id}");
        }
    }

    private static string RequiredReceiptHash(
        SyncExecutionReceipt receipt,
        string property)
    {
        PropertyInfo? field = receipt.GetType().GetProperty(
            property, BindingFlags.Public | BindingFlags.Instance);
        return field?.GetValue(receipt) as string ??
            throw new InvalidOperationException(
                $"binary-bound UnitTest receipt requires {property}");
    }

    private static ActualContext LoadContext()
    {
        string root = FindRepositoryRoot();
        string gameLibs = Environment.GetEnvironmentVariable(GameLibsEnvironment) ??
            throw new InvalidOperationException($"{GameLibsEnvironment} is required");
        string project = Path.Combine(root, "ONI_Together", "ONI_Together.csproj");
        SyncBuildVariant[] variants = Variants();
        IReadOnlyList<SyncVariantInput> inputs = SyncMsBuildProjectLoader.Load(
            project, variants, new Dictionary<string, string> {
                ["GameLibsFolder"] = gameLibs
            });
        SyncCatalogScan catalog = SyncSurfaceScanner.ScanCatalogVariants(inputs);
        True(catalog.Errors.Count == 0, "actual catalog contains errors");
        string inventory = SyncInventoryJson.Serialize(catalog);
        using JsonDocument json = JsonDocument.Parse(inventory);
        string digest = json.RootElement.GetProperty("digest").GetString()!;
        string output = Path.Combine(root, "ONI_Together", "bin", "Debug",
            "netstandard2.1");
        byte[] pe = File.ReadAllBytes(Path.Combine(output, "ONI_Together.dll"));
        byte[] pdb = File.ReadAllBytes(Path.Combine(output, "ONI_Together.pdb"));
        return new ActualContext(root, gameLibs, catalog,
            new SyncExecutionFixtureAssembly(pe, pdb),
            Digest(pe), Digest(pdb), digest);
    }

    private static SyncExecutionProbeBinding Binding(
        string testId,
        string inventoryDigest) => new()
    {
        RunId = "actual-debug-unit-tests",
        TestId = testId,
        Tier = SyncExecutionTier.Headless,
        Polarity = SyncExecutionPolarity.Positive,
        InventoryDigest = inventoryDigest,
        CoverageDigest = new string('b', 64)
    };

    private static SyncBuildVariant[] Variants() =>
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

    private static SyncBuildVariant Variant(
        string configuration,
        string platform,
        params string[] symbols) => new(
            configuration, platform, symbols.ToHashSet(StringComparer.Ordinal));

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

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
             directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "Shared")) &&
                Directory.Exists(Path.Combine(directory.FullName, "ONI_Together")))
                return directory.FullName;
        throw new InvalidOperationException("ONI_Together repository root was not found");
    }

    private static string Digest(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static string Changed(string value) =>
        (value[0] == '0' ? "1" : "0") + value[1..];

    private static void Reject(UnitTestRecord record, ActualRun run, string message) =>
        Throws<InvalidOperationException>(() => Validate(record, run), message);

    private static void WriteEvidence(
        ActualContext context,
        RunSelection selection,
        ExecutedRecord executed)
    {
        Console.WriteLine("ACTUAL_UNIT_TEST " +
            $"test={selection.Safe.Test.Id} outcome={executed.Record.Outcome} " +
            $"method={MethodSymbol(selection.Safe.Test.Method)} " +
            $"entries={string.Join(',', executed.Receipt.ExecutedEntryIds)} " +
            $"notRun={selection.Unsafe.Id} reason={selection.NotRunReason} " +
            $"dll={context.DllHash} pdb={context.PdbHash} " +
            $"inventory={context.InventoryDigest}");
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"expected {expected}, actual {actual}");
    }

    private sealed record ActualContext(
        string RepositoryRoot,
        string GameLibsFolder,
        SyncCatalogScan Catalog,
        SyncExecutionFixtureAssembly Assembly,
        string DllHash,
        string PdbHash,
        string InventoryDigest);

    private sealed record RegisteredTest(
        string Id,
        object Instance,
        MethodInfo Method);

    private sealed record Candidate(
        RegisteredTest Test,
        IReadOnlySet<string> TargetSymbols);

    private sealed record RunSelection(
        Candidate Safe,
        RegisteredTest Unsafe,
        string NotRunReason);

    private sealed record ExecutedRecord(
        UnitTestRecord Record,
        SyncExecutionReceipt Receipt);

    private sealed record UnitTestRecord(
        string TestId,
        UnitOutcome Outcome,
        string? Message,
        SyncExecutionReceipt? Receipt,
        string DllHash,
        string PdbHash,
        string InventoryDigest);

    private sealed record ActualRun(
        ActualContext Context,
        IReadOnlySet<string> KnownTestIds,
        IReadOnlyDictionary<string, string> NotRunAllowlist,
        UnitTestRecord Executed,
        UnitTestRecord NotRun);

    private enum UnitOutcome
    {
        NotRun,
        InProgress,
        Passed,
        Failed
    }

    private sealed class RuntimeResolver : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string> assemblies;

        internal RuntimeResolver(string repositoryRoot, string gameLibsFolder)
        {
            assemblies = Index([
                Path.Combine(repositoryRoot, "ONI_Together", "bin", "Debug",
                    "netstandard2.1"),
                Path.Combine(repositoryRoot, "Shared", "bin", "Debug",
                    "netstandard2.1"),
                gameLibsFolder
            ]);
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        public void Dispose() =>
            AppDomain.CurrentDomain.AssemblyResolve -= Resolve;

        private Assembly? Resolve(object? sender, ResolveEventArgs arguments)
        {
            string? name = new AssemblyName(arguments.Name).Name;
            return name is not null &&
                assemblies.TryGetValue(name, out string? path)
                ? Assembly.LoadFrom(path) : null;
        }

        private static IReadOnlyDictionary<string, string> Index(
            IEnumerable<string> directories)
        {
            var result = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string path in directories.Where(Directory.Exists)
                         .SelectMany(directory => Directory.EnumerateFiles(
                             directory, "*.dll", SearchOption.TopDirectoryOnly)))
                try
                {
                    string? name = AssemblyName.GetAssemblyName(path).Name;
                    if (name is not null) result.TryAdd(name, path);
                }
                catch (BadImageFormatException) { }
            return result;
        }
    }
}
