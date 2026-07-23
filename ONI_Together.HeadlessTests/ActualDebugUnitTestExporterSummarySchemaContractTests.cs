using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestExporterSummarySchemaContractTests
{
    private static readonly IReadOnlySet<string> ExpectedFields =
        new HashSet<string>([
            "schemaVersion", "runId", "success", "executionMode",
            "cleanControlMatched", "preflightCount", "batchRunCount",
            "firstInstrumentationCount",
            "cleanControlInstrumentationCount", "cleanControlCacheHit",
            "testCount", "passedCount", "failedCount", "notRunCount",
            "observedEntryClaimCount", "uniqueMappedEntryCount",
            "remainingUnmappedCount", "coverageReady",
            "addedPositiveMappingCount", "addedNegativeMappingCount",
            "dllHash", "pdbHash", "inventoryDigest", "coverageDigest",
            "migratedCoverageDigest"
        ], StringComparer.Ordinal);

    internal static void Validate()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "oni-export-summary-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ValidateCase(root, ReadyCase());
            ValidateCase(root, UnmappedCase());
            ValidateCase(root, FailedCase());
            ValidateCase(root, CleanControlMismatchCase());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ValidateCase(string root, SummaryCase fixture)
    {
        string output = Path.Combine(root, fixture.Name);
        ActualDebugUnitTestExportPublisher.Publish(
            output,
            "{}",
            new SyncCoverageMigrationResult(
                fixture.CoverageJson, 0, 0, 0,
                fixture.RemainingUnmappedCount),
            BatchInput(),
            Milestone(fixture));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(output, ActualDebugUnitTestExportSchema.SummaryFileName)));
        JsonElement summary = document.RootElement;
        if (summary.TryGetProperty("mappedEntryCount", out _))
            throw new InvalidOperationException(
                "export summary still exposes ambiguous mappedEntryCount");
        ExactFields(summary);
        Equal(4, Integer(summary, "observedEntryClaimCount"),
            "passing receipt observed claim total");
        Equal(3, Integer(summary, "uniqueMappedEntryCount"),
            "unique migrated coverage mapping total");
        Equal(fixture.RemainingUnmappedCount,
            Integer(summary, "remainingUnmappedCount"),
            "remaining unmapped total");
        Equal(fixture.ExpectedCoverageReady,
            Boolean(summary, "coverageReady"), "coverage readiness");
    }

    private static ActualDebugUnitTestBatchInput BatchInput() =>
        new(
            "summary-contract-run",
            new SyncExecutionFixtureAssembly([], []),
            new SyncCatalogScan([], []),
            "inventory-digest",
            "dll-hash",
            "pdb-hash",
            [],
            CoverageDigest: "source-coverage-digest");

    private static ActualDebugUnitTestBatchMilestoneResult Milestone(
        SummaryCase fixture)
    {
        ActualDebugUnitTestBatchResult first = Batch(fixture.HasFailure);
        return new ActualDebugUnitTestBatchMilestoneResult
        {
            Preflight = Preflight(),
            FirstCache = Cache(cacheHit: false, instrumentationCount: 1),
            CleanControlCache = Cache(cacheHit: true, instrumentationCount: 0),
            First = first,
            CleanControl = Batch(fixture.HasFailure),
            PreflightCount = 1,
            BatchRunCount = 2,
            OutcomesMatched = fixture.CleanControlMatched,
            EntrySetsMatched = true
        };
    }

    private static ActualDebugUnitTestBatchResult Batch(bool hasFailure)
    {
        ActualDebugUnitTestResult[] results = [
            Result("headless:first", ActualDebugUnitTestOutcome.Passed,
                ["entry-a", "entry-b"]),
            Result("headless:second", ActualDebugUnitTestOutcome.Passed,
                ["entry-a", "entry-c"])
        ];
        if (hasFailure)
            results[1] = Result("headless:second",
                ActualDebugUnitTestOutcome.Failed, ["entry-a", "entry-c"]);
        return new ActualDebugUnitTestBatchResult(
            1, "summary-contract-run", "dll-hash", "pdb-hash",
            "inventory-digest", 1, 1, !hasFailure, results);
    }

    private static ActualDebugUnitTestResult Result(
        string testId,
        ActualDebugUnitTestOutcome outcome,
        IReadOnlyList<string> entryIds)
    {
        SyncExecutionReceipt? receipt = outcome == ActualDebugUnitTestOutcome.Passed
            ? Receipt(testId, entryIds)
            : null;
        return new ActualDebugUnitTestResult(
            testId, "Fixture.Tests.Run()", outcome,
            outcome == ActualDebugUnitTestOutcome.Failed ? "synthetic failure" : null,
            1, "dll-hash", "pdb-hash", "inventory-digest", 1,
            entryIds, [], receipt);
    }

    private static SyncExecutionReceipt Receipt(
        string testId,
        IReadOnlyList<string> entryIds) =>
        new(
            1, "summary-contract-run", "inventory-digest",
            "source-coverage-digest", testId, SyncExecutionTier.Headless,
            null, SyncExecutionPolarity.Positive, entryIds, [], [], null);

    private static ActualDebugUnitTestPreflightResult Preflight() =>
        new()
        {
            SchemaVersion = 1,
            AnalyzedEntryCount = 3,
            DllHash = "dll-hash",
            PdbHash = "pdb-hash",
            InventoryDigest = "inventory-digest",
            Issues = []
        };

    private static ActualDebugUnitTestInstrumentationCacheResult Cache(
        bool cacheHit,
        int instrumentationCount) =>
        new()
        {
            Key = new ActualDebugUnitTestInstrumentationCacheKey
            {
                SchemaVersion = 3,
                DllHash = "dll-hash",
                PdbHash = "pdb-hash",
                InventoryDigest = "inventory-digest",
                KeyDigest = "cache-key"
            },
            Assembly = new SyncExecutionInstrumentedAssembly(
                [], [], "dll-hash", "pdb-hash"),
            InstrumentationCount = instrumentationCount,
            CacheHit = cacheHit
        };

    private static SummaryCase ReadyCase() =>
        new()
        {
            Name = "ready",
            CoverageJson = Coverage(includeUnmapped: false),
            RemainingUnmappedCount = 0,
            ExpectedCoverageReady = true,
            CleanControlMatched = true
        };

    private static SummaryCase UnmappedCase() =>
        new()
        {
            Name = "unmapped",
            CoverageJson = Coverage(includeUnmapped: true),
            RemainingUnmappedCount = 1,
            ExpectedCoverageReady = false,
            CleanControlMatched = true
        };

    private static SummaryCase FailedCase() =>
        new()
        {
            Name = "failed",
            CoverageJson = Coverage(includeUnmapped: false),
            RemainingUnmappedCount = 0,
            ExpectedCoverageReady = false,
            CleanControlMatched = true,
            HasFailure = true
        };

    private static SummaryCase CleanControlMismatchCase() =>
        new()
        {
            Name = "clean-control-mismatch",
            CoverageJson = Coverage(includeUnmapped: false),
            RemainingUnmappedCount = 0,
            ExpectedCoverageReady = false,
            CleanControlMatched = false
        };

    private static string Coverage(bool includeUnmapped)
    {
        var entries = new List<object>
        {
            CoverageEntry("entry-a", ["headless:first"], []),
            CoverageEntry("entry-b", ["headless:first"], []),
            CoverageEntry("entry-c", [], ["headless:second"])
        };
        if (includeUnmapped)
            entries.Add(CoverageEntry("entry-d", [], []));
        return JsonSerializer.Serialize(new
        {
            inventoryDigest = "inventory-digest",
            entries
        });
    }

    private static object CoverageEntry(
        string id,
        IReadOnlyList<string> testIds,
        IReadOnlyList<string> negativeTestIds) =>
        new
        {
            id,
            domain = "contract",
            testIds,
            negativeTestIds,
            scenarioIds = Array.Empty<string>(),
            variants = Array.Empty<string>(),
            status = "Active"
        };

    private static void ExactFields(JsonElement summary)
    {
        string[] actual = summary.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expected = ExpectedFields.Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"summary fields mismatch: [{string.Join(", ", actual)}]");
    }

    private static int Integer(JsonElement root, string name) =>
        root.GetProperty(name).GetInt32();

    private static bool Boolean(JsonElement root, string name) =>
        root.GetProperty(name).GetBoolean();

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{description}: expected {expected}, actual {actual}");
    }

    private sealed record SummaryCase
    {
        internal required string Name { get; init; }
        internal required string CoverageJson { get; init; }
        internal required int RemainingUnmappedCount { get; init; }
        internal required bool ExpectedCoverageReady { get; init; }
        internal required bool CleanControlMatched { get; init; }
        internal bool HasFailure { get; init; }
    }
}
