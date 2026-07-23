using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static partial class ActualDebugUnitTestExporterAcceptanceTests
{
    internal static void RealBatchCreatesAuthenticAtomicBundle()
    {
        IActualDebugUnitTestExporter exporter =
            ActualDebugUnitTestExporterLoader.Load();
        string root = ActualDebugUnitTestBatchFixture.RepositoryRoot();
        string gameLibs =
            ActualDebugUnitTestExporterContractTests.RequireGameLibs();
        string coveragePath = Path.Combine(root, "sync-entry-coverage.json");
        byte[] sourceBytes = File.ReadAllBytes(coveragePath);
        ActualDebugUnitTestBatchInput expected =
            ActualDebugUnitTestBatchFixture.Load(gameLibs);
        string temporary = Path.Combine(Path.GetTempPath(),
            "oni-actual-unit-export-" + Guid.NewGuid().ToString("N"));
        string output = Path.Combine(temporary, "bundle");
        Directory.CreateDirectory(temporary);
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int exitCode = exporter.RunCli([
                "actual-unit-export",
                "--game-libs", gameLibs,
                "--coverage", coveragePath,
                "--output-dir", output
            ], stdout, stderr);

            Equal(0, exitCode);
            Equal("", stderr.ToString());
            True(sourceBytes.SequenceEqual(File.ReadAllBytes(coveragePath)),
                "successful export changed source coverage bytes");
            EqualSequence([
                ActualDebugUnitTestExportSchema.BatchFileName,
                ActualDebugUnitTestExportSchema.SummaryFileName,
                ActualDebugUnitTestExportSchema.CoverageFileName
            ], Directory.EnumerateFiles(output).Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));
            Equal(0, Directory.EnumerateDirectories(output).Count());

            using JsonDocument batch = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(output,
                    ActualDebugUnitTestExportSchema.BatchFileName)));
            using JsonDocument summary = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(output,
                    ActualDebugUnitTestExportSchema.SummaryFileName)));
            SyncCoverageManifest source =
                SyncCoverageManifest.Parse(
                    System.Text.Encoding.UTF8.GetString(sourceBytes));
            SyncCoverageManifest migrated = SyncCoverageManifest.Parse(
                File.ReadAllText(Path.Combine(output,
                    ActualDebugUnitTestExportSchema.CoverageFileName)));
            var artifacts = new ExportArtifacts
            {
                Expected = expected,
                Batch = batch.RootElement,
                Summary = summary.RootElement,
                Source = source,
                Migrated = migrated
            };
            ValidateSummary(artifacts);
            ValidateAuthenticMappings(artifacts);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static void ValidateSummary(ExportArtifacts artifacts)
    {
        ActualDebugUnitTestBatchInput expected = artifacts.Expected;
        JsonElement batch = artifacts.Batch;
        JsonElement summary = artifacts.Summary;
        Exact(summary, ActualDebugUnitTestExportSchema.SummaryFields);
        Equal(1, Integer(summary, "schemaVersion"));
        Equal(expected.RunId, String(summary, "runId"));
        True(Boolean(summary, "success"), "export summary reports failure");
        Equal("milestone", String(summary, "executionMode"));
        True(Boolean(summary, "cleanControlMatched"),
            "export omitted a matching clean-control run");
        Equal(1, Integer(summary, "preflightCount"));
        Equal(2, Integer(summary, "batchRunCount"));
        Equal(1, Integer(summary, "firstInstrumentationCount"));
        Equal(0, Integer(summary, "cleanControlInstrumentationCount"));
        True(Boolean(summary, "cleanControlCacheHit"),
            "export clean-control did not reuse instrumentation");
        Equal(expected.DllHash, String(summary, "dllHash"));
        Equal(expected.PdbHash, String(summary, "pdbHash"));
        Equal(expected.InventoryDigest, String(summary, "inventoryDigest"));
        Equal(expected.CoverageDigest!, String(summary, "coverageDigest"));
        Equal(artifacts.Migrated.CoverageDigest!,
            String(summary, "migratedCoverageDigest"));

        Equal(expected.RunId, String(batch, "runId"));
        Equal(expected.DllHash, String(batch, "dllHash"));
        Equal(expected.PdbHash, String(batch, "pdbHash"));
        Equal(expected.InventoryDigest, String(batch, "inventoryDigest"));
        True(Boolean(batch, "success"), "exported batch reports failure");
        JsonElement[] results = Array(batch, "results").ToArray();
        Equal(expected.ExpectedTests.Count, results.Length);
        EqualSet(expected.ExpectedTests.Select(test => test.TestId),
            results.Select(result => String(result, "testId")));
        Equal(results.Length, Integer(summary, "testCount"));
        Equal(Count(results, "Passed"), Integer(summary, "passedCount"));
        Equal(Count(results, "Failed"), Integer(summary, "failedCount"));
        Equal(Count(results, "NotRun"), Integer(summary, "notRunCount"));
        Equal(0, Integer(summary, "failedCount"));
        Equal(results.Sum(result =>
                Array(result, "observedEntryIds").Count()),
            Integer(summary, "mappedEntryCount"));
    }

    private static void ValidateAuthenticMappings(ExportArtifacts artifacts)
    {
        ActualDebugUnitTestBatchInput expected = artifacts.Expected;
        JsonElement batch = artifacts.Batch;
        JsonElement summary = artifacts.Summary;
        HashSet<Mapping> expectedPositive = [];
        HashSet<Mapping> expectedNegative = [];
        foreach (JsonElement result in Array(batch, "results"))
        {
            JsonElement receipt = result.GetProperty("receipt");
            if (receipt.ValueKind == JsonValueKind.Null)
                continue;
            Equal("Passed", String(result, "outcome"));
            Equal(String(result, "testId"), String(receipt, "testId"));
            Equal(expected.InventoryDigest,
                String(receipt, "inventoryDigest"));
            Equal(expected.CoverageDigest!,
                String(receipt, "coverageDigest"));
            Equal(expected.DllHash, String(receipt, "dllHash"));
            Equal(expected.PdbHash, String(receipt, "pdbHash"));
            AddMappings(expectedPositive, receipt, "executedEntryIds");
            AddMappings(expectedNegative, receipt, "absentEntryIds");
        }

        HashSet<Mapping> sourcePositive =
            Mappings(artifacts.Source, negative: false);
        HashSet<Mapping> sourceNegative =
            Mappings(artifacts.Source, negative: true);
        HashSet<Mapping> outputPositive =
            Mappings(artifacts.Migrated, negative: false);
        HashSet<Mapping> outputNegative =
            Mappings(artifacts.Migrated, negative: true);
        True(sourcePositive.IsSubsetOf(outputPositive),
            "export removed an existing positive mapping");
        True(sourceNegative.IsSubsetOf(outputNegative),
            "export removed an existing negative mapping");
        EqualSet(expectedPositive.Except(sourcePositive),
            outputPositive.Except(sourcePositive));
        EqualSet(expectedNegative.Except(sourceNegative),
            outputNegative.Except(sourceNegative));
        Equal(outputPositive.Count - sourcePositive.Count,
            Integer(summary, "addedPositiveMappingCount"));
        Equal(outputNegative.Count - sourceNegative.Count,
            Integer(summary, "addedNegativeMappingCount"));
        True(Integer(summary, "addedPositiveMappingCount") +
                Integer(summary, "addedNegativeMappingCount") > 0,
            "export added no real receipt mapping");

        SyncTestRegistry registry = Registry(
            Array(batch, "results").Select(result => String(result, "testId")));
        IReadOnlyList<SurfaceError> errors = SyncCoverageValidator.Validate(
            expected.Catalog, artifacts.Migrated,
            registry.Ids, registry.ScenarioIds);
        int remaining = errors.Count(error =>
            error.Code == "manifest_missing_execution_test");
        True(remaining > 0,
            "migrated coverage no longer fails closed for unmapped entries");
        Equal(remaining, Integer(summary, "remainingUnmappedCount"));
    }

}
