using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using Mono.Cecil;

namespace ONI_Together.HeadlessTests;

internal sealed partial class ActualDebugUnitTestBatchRunner :
    IActualDebugUnitTestBatchRunner
{
    private const string ObserverField = "Observer";
    private readonly Dictionary<string, SyncExecutionReceipt> authenticReceipts =
        new(StringComparer.Ordinal);

    private ActualDebugUnitTestBatchResult RunCore(
        ActualDebugUnitTestBatchInput input,
        SyncExecutionInstrumentedAssembly instrumented,
        ConsoleWriters console)
    {
        SyncCatalogScan probeCatalog = ProbeCatalog(input);
        using var resolver = new RuntimeResolver();
        IReadOnlyList<ActualDebugRuntimeAssemblyEvidence> runtimeAssemblies =
            BootstrapRuntimeAssemblies(input);
        Assembly assembly = Assembly.Load(
            instrumented.PeImage, instrumented.PdbImage);
        ActualDebugAccessBypassBootstrapEvidence accessBypassBootstrap =
            CompleteAccessBypassBootstrap(
                instrumented, assembly, runtimeAssemblies);
        ActualDebugUnitTestBootstrapEvidence bootstrap = Bootstrap(assembly);
        Console.SetOut(console.Output);
        Console.SetError(console.Error);
        Type observer = assembly.GetType(
            SyncExecutionIlInstrumenter.ObserverTypeName, true)!;
        FieldInfo observerField = observer.GetField(
            ObserverField, BindingFlags.Public | BindingFlags.Static)!;
        IReadOnlyDictionary<string, RegisteredTest> tests = Discover(assembly);
        Console.SetOut(console.Output);
        Console.SetError(console.Error);
        var results = new List<ActualDebugUnitTestResult>(
            input.ExpectedTests.Count);
        int epoch = 0;
        foreach (ActualDebugUnitTestDescriptor descriptor in
                 input.ExpectedTests.OrderBy(test => test.TestId,
                     StringComparer.Ordinal))
        {
            if (!tests.TryGetValue(
                    descriptor.TestId, out RegisteredTest? test))
                throw new InvalidOperationException(
                    $"UnitTestRegistry omitted {descriptor.TestId}");
            results.Add(!IsUnsupported(descriptor)
                ? Execute(input, descriptor, test, observerField, ++epoch,
                    assembly, probeCatalog)
                : NotRun(input, descriptor));
        }
        observerField.SetValue(null, null);
        var result = new ActualDebugUnitTestBatchResult(
            1, input.RunId, input.DllHash, input.PdbHash,
            input.InventoryDigest, 1, 1,
            !results.Any(item =>
                item.Outcome == ActualDebugUnitTestOutcome.Failed),
            results)
        {
            Bootstrap = bootstrap,
            AccessBypassBootstrap = accessBypassBootstrap
        };
        Validate(input, result);
        return result;
    }

    public void Validate(
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestBatchResult result)
    {
        ValidateInput(input);
        ValidateCore(input, result, requireLiveProvenance: true);
    }

    private ActualDebugUnitTestResult Execute(
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestDescriptor descriptor,
        RegisteredTest test,
        FieldInfo observerField,
        int epoch,
        Assembly assembly,
        SyncCatalogScan probeCatalog)
    {
        var binding = new SyncExecutionProbeBinding
        {
            RunId = input.RunId + ":" + descriptor.TestId,
            TestId = descriptor.TestId,
            Tier = SyncExecutionTier.Headless,
            Polarity = SyncExecutionPolarity.Positive,
            InventoryDigest = input.InventoryDigest,
            CoverageDigest = input.CoverageDigest!
        };
        var session = new SyncExecutionProbeSession(
            binding, probeCatalog, assembly, input.DllHash, input.PdbHash);
        bool observed = false;
        TextWriter output = Console.Out;
        TextWriter error = Console.Error;
        observerField.SetValue(null, new Action<string, string>((id, phase) =>
        {
            bool previous = observed;
            observed = session.TryObserve(id, phase);
            observed |= previous;
        }));
        try
        {
            InvokeRun(test.Instance);
        }
        finally
        {
            observerField.SetValue(null, null);
            Console.SetOut(output);
            Console.SetError(error);
        }
        ActualDebugUnitTestOutcome outcome = Outcome(test.Instance);
        string? message = Property(test.Instance, "Message") as string;
        double duration = Convert.ToDouble(
            Property(test.Instance, "DurationMs"));
        SyncExecutionReceipt? receipt = outcome ==
            ActualDebugUnitTestOutcome.Passed && observed
                ? session.Complete()
                : null;
        string[] entries = receipt?.ExecutedEntryIds.ToArray() ?? [];
        if (outcome == ActualDebugUnitTestOutcome.Failed)
        {
            entries = [];
            receipt = null;
            Console.WriteLine("ACTUAL_UNIT_BATCH_FAILURE " +
                $"test={descriptor.TestId} method={descriptor.MethodSymbol} " +
                $"message={message}");
        }
        return Result(input, descriptor, outcome, message, duration, epoch,
            entries, [], receipt);
    }

    private static ActualDebugUnitTestResult NotRun(
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestDescriptor descriptor) => Result(
        input, descriptor, ActualDebugUnitTestOutcome.NotRun,
        NotRunReason(descriptor), 0, 0, [],
        descriptor.DirectRuntimeReferences, null);

    private static ActualDebugUnitTestResult Result(
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestDescriptor descriptor,
        ActualDebugUnitTestOutcome outcome,
        string? message,
        double duration,
        int epoch,
        IReadOnlyList<string> observed,
        IReadOnlyList<string> runtimeReferences,
        SyncExecutionReceipt? receipt) => new(
        descriptor.TestId, descriptor.MethodSymbol, outcome, message, duration,
        input.DllHash, input.PdbHash, input.InventoryDigest, epoch, observed,
        runtimeReferences, receipt);

    private void ValidateCore(
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestBatchResult result,
        bool requireLiveProvenance)
    {
        if (result.SchemaVersion != 1 || result.RunId != input.RunId ||
            result.DllHash != input.DllHash || result.PdbHash != input.PdbHash ||
            result.InventoryDigest != input.InventoryDigest ||
            result.InstrumentationCount != 1 || result.AssemblyLoadCount != 1)
            throw new InvalidOperationException("batch identity drift");
        ValidateBootstrap(result.Bootstrap);
        ValidateAccessBypassBootstrap(input, result.AccessBypassBootstrap);
        if (result.Success != !result.Results.Any(item =>
                item.Outcome == ActualDebugUnitTestOutcome.Failed))
            throw new InvalidOperationException("batch success drift");
        Dictionary<string, ActualDebugUnitTestDescriptor> expected =
            input.ExpectedTests.ToDictionary(
                test => test.TestId, StringComparer.Ordinal);
        if (result.Results.Count != expected.Count ||
            result.Results.GroupBy(item => item.TestId, StringComparer.Ordinal)
                .Any(group => group.Count() != 1) ||
            !result.Results.Select(item => item.TestId).ToHashSet(
                StringComparer.Ordinal).SetEquals(expected.Keys))
            throw new InvalidOperationException("batch UnitTest set drift");
        var epochs = new HashSet<int>();
        foreach (ActualDebugUnitTestResult item in result.Results)
        {
            ActualDebugUnitTestDescriptor descriptor = expected[item.TestId];
            ValidateResult(input, descriptor, item, epochs,
                requireLiveProvenance);
        }
        int executed = result.Results.Count(item =>
            item.Outcome != ActualDebugUnitTestOutcome.NotRun);
        if (!epochs.SetEquals(Enumerable.Range(1, executed)))
            throw new InvalidOperationException("observer epoch drift");
    }

    private void ValidateResult(
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestDescriptor descriptor,
        ActualDebugUnitTestResult result,
        ISet<int> epochs,
        bool requireLiveProvenance)
    {
        if (result.MethodSymbol != descriptor.MethodSymbol ||
            result.DllHash != input.DllHash || result.PdbHash != input.PdbHash ||
            result.InventoryDigest != input.InventoryDigest ||
            result.DurationMs < 0 || double.IsNaN(result.DurationMs) ||
            double.IsInfinity(result.DurationMs))
            throw new InvalidOperationException(
                $"UnitTest result identity drift: {result.TestId}");
        if (IsUnsupported(descriptor))
        {
            if (result.Outcome != ActualDebugUnitTestOutcome.NotRun ||
                result.ObservationEpoch != 0 ||
                result.ObservedEntryIds.Count != 0 ||
                !result.RuntimeReferenceEvidence.SequenceEqual(
                    descriptor.DirectRuntimeReferences) ||
                result.Message != NotRunReason(descriptor) ||
                result.Receipt is not null)
                throw new InvalidOperationException(
                    $"invalid NotRun result: {result.TestId}");
            return;
        }
        if (result.Outcome == ActualDebugUnitTestOutcome.NotRun ||
            result.ObservationEpoch <= 0 ||
            !epochs.Add(result.ObservationEpoch) ||
            result.RuntimeReferenceEvidence.Count != 0)
            throw new InvalidOperationException(
                $"invalid executed result: {result.TestId}");
        if (result.Outcome == ActualDebugUnitTestOutcome.Failed)
        {
            if (result.ObservedEntryIds.Count != 0 || result.Receipt is not null)
                throw new InvalidOperationException(
                    $"failed result created a mapping: {result.TestId}");
            return;
        }
        if (result.ObservedEntryIds.Count == 0)
        {
            if (result.Receipt is not null)
                throw new InvalidOperationException(
                    $"empty mapping created a receipt: {result.TestId}");
            return;
        }
        ValidateReceipt(input, result, requireLiveProvenance);
    }

    private void ValidateReceipt(
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestResult result,
        bool requireLiveProvenance)
    {
        SyncExecutionReceipt receipt = result.Receipt ??
            throw new InvalidOperationException(
                $"observed result lacks receipt: {result.TestId}");
        if (receipt.RunId != input.RunId + ":" + result.TestId ||
            receipt.TestId != result.TestId ||
            receipt.Tier != SyncExecutionTier.Headless ||
            receipt.ScenarioId is not null ||
            receipt.Polarity != SyncExecutionPolarity.Positive ||
            receipt.InventoryDigest != input.InventoryDigest ||
            receipt.CoverageDigest != input.CoverageDigest ||
            receipt.DllHash != input.DllHash ||
            receipt.PdbHash != input.PdbHash ||
            receipt.AbsentEntryIds.Count != 0 ||
            receipt.RegistrationWitnesses.Count != 0 ||
            receipt.Artifact is not null ||
            !receipt.ExecutedEntryIds.ToHashSet(StringComparer.Ordinal)
                .SetEquals(result.ObservedEntryIds))
            throw new InvalidOperationException(
                $"execution receipt drift: {result.TestId}");
        foreach (string id in result.ObservedEntryIds)
        {
            SyncEntry entry = input.Catalog.Entries.SingleOrDefault(
                item => item.Id == id) ??
                throw new InvalidOperationException($"unknown observed entry: {id}");
            if (requireLiveProvenance &&
                (!SyncExecutionProvenance.IsObserved(receipt, id) ||
                 !SyncExecutionProvenance.MatchesOrigin(receipt, entry)))
                throw new InvalidOperationException(
                    $"unproven observed entry: {id}");
        }
    }

    private static IReadOnlyDictionary<string, RegisteredTest> Discover(
        Assembly assembly)
    {
        Type registry = assembly.GetType(
            "ONI_Together.DebugTools.UnitTestRegistry", true)!;
        bool discovered = (bool)registry.GetMethod(
            "DiscoverTests", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!;
        if (!discovered)
            throw new InvalidOperationException(
                "real UnitTestRegistry discovery failed");
        var tests = (IEnumerable)registry.GetProperty(
            "Tests", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        return tests.Cast<object>().Select(instance =>
        {
            MethodInfo method = (MethodInfo)instance.GetType().GetField(
                "_method", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(instance)!;
            string symbol = MethodSymbol(method);
            return new RegisteredTest(StableId(symbol), symbol, instance);
        }).ToDictionary(test => test.TestId, StringComparer.Ordinal);
    }

    private static void InvokeRun(object test) =>
        test.GetType().GetMethod(
            "Run", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(test, [false]);

    private static ActualDebugUnitTestOutcome Outcome(object test)
    {
        string value = Property(test, "State")?.ToString() ??
            throw new InvalidOperationException("UnitTest state is missing");
        return Enum.TryParse(value, false,
            out ActualDebugUnitTestOutcome outcome)
            ? outcome
            : throw new InvalidOperationException(
                $"unsupported UnitTest state {value}");
    }

    private static object? Property(object value, string name) =>
        value.GetType().GetProperty(
            name, BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(value);

    private static string StableId(string methodSymbol) =>
        "headless:unit:" + Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(methodSymbol)))
            .ToLowerInvariant()[..24];

    private static string MethodSymbol(MethodInfo method)
    {
        string parameters = string.Join(",", method.GetParameters()
            .Select(parameter => TypeName(parameter.ParameterType)));
        return $"{TypeName(method.DeclaringType!)}.{method.Name}({parameters})";
    }

    private static string TypeName(Type type) =>
        (type.FullName ?? type.Name).Replace('/', '+').Replace("::", ".");

    private static string NotRunReason(
        ActualDebugUnitTestDescriptor descriptor) =>
        descriptor.HeadlessUnsupportedReason ??
        "not-headless: direct native terminal: " +
        string.Join(",", descriptor.DirectRuntimeReferences);

    private static bool IsUnsupported(
        ActualDebugUnitTestDescriptor descriptor) =>
        descriptor.HeadlessUnsupportedReason is not null ||
        descriptor.DirectRuntimeReferences.Count != 0;

    private static SyncCatalogScan ProbeCatalog(
        ActualDebugUnitTestBatchInput input)
    {
        IReadOnlySet<string> safeTests = input.ExpectedTests
            .Where(test => test.DirectRuntimeReferences.Count == 0)
            .Select(test => test.MethodSymbol)
            .ToHashSet(StringComparer.Ordinal);
        using var stream = new MemoryStream(
            input.Assembly.PeImage, writable: false);
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
            stream);
        IReadOnlySet<string> targets = AllTypes(assembly.MainModule)
            .SelectMany(type => type.Methods)
            .Where(method => safeTests.Contains(MethodSymbol(method)))
            .Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions)
            .Select(instruction => instruction.Operand as MethodReference)
            .Where(method => method is not null)
            .Select(method => MethodSymbol(method!))
            .ToHashSet(StringComparer.Ordinal);
        SyncEntry[] entries = input.Catalog.Entries.Where(entry =>
            targets.Contains(entry.FullyQualifiedSymbol)).ToArray();
        if (entries.Length == 0)
            throw new InvalidOperationException(
                "headless-safe UnitTests reference no sync entries");
        return new SyncCatalogScan(entries, []);
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

    private static string MethodSymbol(MethodReference method)
    {
        string parameters = string.Join(",", method.Parameters
            .Select(parameter => TypeName(parameter.ParameterType.FullName)));
        return $"{TypeName(method.DeclaringType.FullName)}." +
            $"{method.Name}({parameters})";
    }

    private static string MethodSymbol(MethodDefinition method) =>
        MethodSymbol((MethodReference)method);

    private static string TypeName(string name) =>
        name.Replace('/', '+').Replace("::", ".");

    private sealed record RegisteredTest(
        string TestId,
        string MethodSymbol,
        object Instance);

    private sealed class RuntimeResolver : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string> assemblies;

        internal RuntimeResolver()
        {
            string root = ActualDebugUnitTestBatchFixture.RepositoryRoot();
            string gameLibs = Environment.GetEnvironmentVariable(
                "ONI_GAME_LIBS") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library/Application Support/Steam/steamapps/common/" +
                "OxygenNotIncluded/OxygenNotIncluded.app/Contents/Resources/" +
                "Data/Managed");
            assemblies = Index([
                Path.Combine(root, "ONI_Together", "bin", "Debug",
                    "netstandard2.1"),
                Path.Combine(root, "Shared", "bin", "Debug",
                    "netstandard2.1"),
                gameLibs
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
