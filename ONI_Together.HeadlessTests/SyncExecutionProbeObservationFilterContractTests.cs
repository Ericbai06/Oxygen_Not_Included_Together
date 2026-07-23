using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ONI_Together.HeadlessTests;

internal static class SyncExecutionProbeObservationFilterContractTests
{
    internal static void Validate()
    {
        MethodInfo tryObserve = RequireTypedTryObserve();
        UnknownEntryDoesNotPolluteSession(tryObserve);
        RelevantEntryCompletesReceipt(tryObserve);
        RunnerUsesFilteredObservationResult();
    }

    private static MethodInfo RequireTypedTryObserve()
    {
        MethodInfo? method = typeof(SyncExecutionProbeSession).GetMethod(
            "TryObserve", BindingFlags.Instance | BindingFlags.Public,
            binder: null, [typeof(string), typeof(string)], modifiers: null);
        if (method is null || method.ReturnType != typeof(bool))
            throw new InvalidOperationException(
                "SyncExecutionProbeSession must expose bool TryObserve(string entryId, string phase)");
        return method;
    }

    private static void UnknownEntryDoesNotPolluteSession(MethodInfo method)
    {
        SyncCatalogScan catalog = SyncExecutionProbeFixtureCatalog.Scan();
        SyncExecutionProbeSession session = Session(catalog, "unknown");

        bool observed = Invoke(method, session, "entry:outside-probe-catalog");

        False(observed, "unrelated full-catalog hit must return false");
        ThrowsNoObservation(session);
    }

    private static void RelevantEntryCompletesReceipt(MethodInfo method)
    {
        SyncCatalogScan catalog = SyncExecutionProbeFixtureCatalog.Scan();
        SyncEntry entry = catalog.Entries.First(item =>
            item.Kind != SyncEntryKind.Coroutine);
        SyncExecutionProbeSession session = Session(catalog, "relevant");

        bool observed = Invoke(method, session, entry.Id);
        SyncExecutionReceipt receipt = session.Complete();

        True(observed, "probe-catalog hit must return true");
        True(receipt.ExecutedEntryIds.SequenceEqual([entry.Id]),
            "relevant hit must complete only its probe-catalog entry");
    }

    private static void RunnerUsesFilteredObservationResult()
    {
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
            typeof(ActualDebugUnitTestBatchRunner).Assembly.Location);
        TypeDefinition runner = assembly.MainModule.Types.Single(type =>
            type.FullName ==
            "ONI_Together.HeadlessTests.ActualDebugUnitTestBatchRunner");
        MethodDefinition[] methods = runner.NestedTypes.Prepend(runner)
            .SelectMany(type => type.Methods)
            .Where(item => item.HasBody)
            .ToArray();
        Instruction? call = methods.SelectMany(item => item.Body.Instructions)
            .FirstOrDefault(instruction =>
                instruction.Operand is MethodReference target &&
                target.DeclaringType.FullName ==
                "ONI_Together.HeadlessTests.SyncExecutionProbeSession" &&
                target.Name == "TryObserve" &&
                target.ReturnType.FullName == "System.Boolean");
        True(call is not null,
            "batch runner observer must call typed TryObserve");
        True(call!.Next?.OpCode.Code is Code.Stloc or Code.Stfld,
            "batch runner must use TryObserve result as its observed flag");
    }

    private static SyncExecutionProbeSession Session(
        SyncCatalogScan catalog,
        string suffix) => new(
            new SyncExecutionProbeBinding
            {
                RunId = "probe-filter-" + suffix,
                TestId = "headless:probe-filter",
                Tier = SyncExecutionTier.Headless,
                Polarity = SyncExecutionPolarity.Positive,
                InventoryDigest = "inventory",
                CoverageDigest = "coverage"
            },
            catalog,
            typeof(SyncExecutionProbeObservationFilterContractTests).Assembly,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

    private static bool Invoke(
        MethodInfo method,
        SyncExecutionProbeSession session,
        string entryId) =>
        (bool)method.Invoke(session, [entryId, "hit"])!;

    private static void ThrowsNoObservation(SyncExecutionProbeSession session)
    {
        try
        {
            session.Complete();
        }
        catch (InvalidOperationException error) when (
            error.Message == "execution probe observed no entries")
        {
            return;
        }
        throw new InvalidOperationException(
            "unknown entry polluted probe completion state");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void False(bool condition, string message) =>
        True(!condition, message);
}
