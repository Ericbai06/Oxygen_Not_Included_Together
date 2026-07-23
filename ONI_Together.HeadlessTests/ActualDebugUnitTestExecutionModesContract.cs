namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestExecutionCommands
{
    internal const string Preflight = "actual-unit-preflight";
    internal const string BatchOnce = "actual-unit-batch-once";
    internal const string BatchMilestone = "actual-unit-batch-milestone";
}

internal static class ActualDebugUnitTestInstrumentationCacheSchema
{
    internal const int Version = 3;
    internal const string Namespace = "actual-unit-instrumentation-v3";
    internal const string MetadataFileName = "metadata.json";
    internal const string PeFileName = "instrumented.dll";
    internal const string PdbFileName = "instrumented.pdb";

    internal static readonly IReadOnlySet<string> MetadataFields =
        new HashSet<string>([
            "schemaVersion",
            "keyDigest",
            "dllHash",
            "pdbHash",
            "inventoryDigest",
            "instrumentedDllHash",
            "instrumentedPdbHash"
        ], StringComparer.Ordinal);
}

internal sealed record ActualDebugUnitTestPreflightInput
{
    internal required SyncCatalogScan Catalog { get; init; }
    internal required SyncExecutionFixtureAssembly Assembly { get; init; }
    internal required string InventoryDigest { get; init; }
    internal required string DllHash { get; init; }
    internal required string PdbHash { get; init; }
}

internal sealed record ActualDebugUnitTestPreflightIssue
{
    internal required string EntryId { get; init; }
    internal required SyncEntryKind Kind { get; init; }
    internal required string Code { get; init; }
    internal required string Symbol { get; init; }
    internal required string Message { get; init; }
}

internal sealed record ActualDebugUnitTestPreflightResult
{
    internal required int SchemaVersion { get; init; }
    internal required int AnalyzedEntryCount { get; init; }
    internal required string DllHash { get; init; }
    internal required string PdbHash { get; init; }
    internal required string InventoryDigest { get; init; }
    internal required IReadOnlyList<ActualDebugUnitTestPreflightIssue> Issues {
        get;
        init;
    }
}

internal interface IActualDebugUnitTestPreflight
{
    ActualDebugUnitTestPreflightResult Analyze(
        ActualDebugUnitTestPreflightInput input);

    int RunCli(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr);
}

internal sealed record ActualDebugUnitTestInstrumentationCacheInput
{
    internal required SyncCatalogScan Catalog { get; init; }
    internal required SyncExecutionFixtureAssembly Assembly { get; init; }
    internal required string InventoryDigest { get; init; }
    internal required string CacheDirectory { get; init; }
    internal string? GameLibsDirectory { get; init; }
}

internal sealed record ActualDebugUnitTestInstrumentationCacheKey
{
    internal required int SchemaVersion { get; init; }
    internal required string DllHash { get; init; }
    internal required string PdbHash { get; init; }
    internal required string InventoryDigest { get; init; }
    internal required string KeyDigest { get; init; }
}

internal sealed record ActualDebugUnitTestInstrumentationCacheResult
{
    internal required ActualDebugUnitTestInstrumentationCacheKey Key {
        get;
        init;
    }
    internal required SyncExecutionInstrumentedAssembly Assembly { get; init; }
    internal required int InstrumentationCount { get; init; }
    internal required bool CacheHit { get; init; }
}

internal interface IActualDebugUnitTestInstrumentationCache
{
    ActualDebugUnitTestInstrumentationCacheResult GetOrCreate(
        ActualDebugUnitTestInstrumentationCacheInput input);
}

internal interface IActualDebugUnitTestBatchExecutionKernel
{
    ActualDebugUnitTestBatchResult Execute(
        ActualDebugUnitTestBatchInput input,
        SyncExecutionInstrumentedAssembly assembly);
}

internal sealed record ActualDebugUnitTestBatchModeInput
{
    internal required ActualDebugUnitTestBatchInput Batch { get; init; }
    internal required string CacheDirectory { get; init; }
    internal required IActualDebugUnitTestBatchExecutionKernel Kernel {
        get;
        init;
    }
}

internal sealed record ActualDebugUnitTestBatchOnceResult
{
    internal required ActualDebugUnitTestPreflightResult Preflight { get; init; }
    internal required ActualDebugUnitTestInstrumentationCacheResult Cache {
        get;
        init;
    }
    internal required ActualDebugUnitTestBatchResult Batch { get; init; }
    internal required int PreflightCount { get; init; }
    internal required int BatchRunCount { get; init; }
    internal required int CleanControlRunCount { get; init; }
    internal required int MigrationCount { get; init; }
}

internal interface IActualDebugUnitTestBatchOnce
{
    ActualDebugUnitTestBatchOnceResult Run(
        ActualDebugUnitTestBatchModeInput input);

    int RunCli(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr);
}

internal sealed record ActualDebugUnitTestBatchMilestoneResult
{
    internal required ActualDebugUnitTestPreflightResult Preflight { get; init; }
    internal required ActualDebugUnitTestInstrumentationCacheResult FirstCache {
        get;
        init;
    }
    internal required ActualDebugUnitTestInstrumentationCacheResult
        CleanControlCache { get; init; }
    internal required ActualDebugUnitTestBatchResult First { get; init; }
    internal required ActualDebugUnitTestBatchResult CleanControl { get; init; }
    internal required int PreflightCount { get; init; }
    internal required int BatchRunCount { get; init; }
    internal required bool OutcomesMatched { get; init; }
    internal required bool EntrySetsMatched { get; init; }
}

internal interface IActualDebugUnitTestBatchMilestone
{
    ActualDebugUnitTestBatchMilestoneResult Run(
        ActualDebugUnitTestBatchModeInput input);

    int RunCli(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr);
}
