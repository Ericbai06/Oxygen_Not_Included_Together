using System.Reflection;

namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestExportSchema
{
    internal const string BatchFileName = "actual-unit-batch.json";
    internal const string CoverageFileName = "sync-entry-coverage.json";
    internal const string SummaryFileName = "summary.json";

    internal static readonly IReadOnlySet<string> SummaryFields =
        new HashSet<string>([
            "schemaVersion",
            "runId",
            "success",
            "executionMode",
            "cleanControlMatched",
            "preflightCount",
            "batchRunCount",
            "firstInstrumentationCount",
            "cleanControlInstrumentationCount",
            "cleanControlCacheHit",
            "testCount",
            "passedCount",
            "failedCount",
            "notRunCount",
            "observedEntryClaimCount",
            "uniqueMappedEntryCount",
            "addedPositiveMappingCount",
            "addedNegativeMappingCount",
            "remainingUnmappedCount",
            "coverageReady",
            "dllHash",
            "pdbHash",
            "inventoryDigest",
            "coverageDigest",
            "migratedCoverageDigest"
        ], StringComparer.Ordinal);
}

internal interface IActualDebugUnitTestExporter
{
    int RunCli(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr);
}

internal static class ActualDebugUnitTestExporterLoader
{
    private const string ImplementationName =
        "ONI_Together.HeadlessTests.ActualDebugUnitTestExporter";

    internal static IActualDebugUnitTestExporter Load()
    {
        Type? implementation = typeof(ActualDebugUnitTestExporterLoader)
            .Assembly.GetType(ImplementationName, throwOnError: false);
        if (implementation is null)
            throw new InvalidOperationException(
                "ActualDebugUnitTestExporter implementation is missing");
        if (!typeof(IActualDebugUnitTestExporter).IsAssignableFrom(implementation))
            throw new InvalidOperationException(
                "ActualDebugUnitTestExporter does not implement its frozen contract");
        return (IActualDebugUnitTestExporter)Activator.CreateInstance(
            implementation, BindingFlags.Instance | BindingFlags.Public |
            BindingFlags.NonPublic, binder: null, args: null,
            culture: null)!;
    }
}
