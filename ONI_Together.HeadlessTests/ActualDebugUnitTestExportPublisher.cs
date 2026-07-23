using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestExportPublisher
{
    internal static void Publish(
        string outputDirectory,
        string batchJson,
        SyncCoverageMigrationResult migration,
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestBatchMilestoneResult milestone)
    {
        SyncCoverageManifest migrated =
            SyncCoverageManifest.Parse(migration.CoverageJson);
        string summaryJson = SummaryJson(
            input, milestone, migration, migrated);
        string parent = Path.GetDirectoryName(outputDirectory)!;
        string staging = Path.Combine(parent,
            $".{Path.GetFileName(outputDirectory)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(staging);
            Write(staging, ActualDebugUnitTestExportSchema.BatchFileName,
                batchJson);
            Write(staging, ActualDebugUnitTestExportSchema.CoverageFileName,
                migration.CoverageJson);
            Write(staging, ActualDebugUnitTestExportSchema.SummaryFileName,
                summaryJson);
            Directory.Move(staging, outputDirectory);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private static void Write(
        string directory,
        string fileName,
        string contents) =>
        File.WriteAllText(Path.Combine(directory, fileName), contents);

    private static string SummaryJson(
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestBatchMilestoneResult milestone,
        SyncCoverageMigrationResult migration,
        SyncCoverageManifest migrated)
    {
        ActualDebugUnitTestBatchResult first = milestone.First;
        bool cleanControlMatched =
            milestone.OutcomesMatched && milestone.EntrySetsMatched;
        bool hasFailures = first.Results.Any(result =>
                result.Outcome == ActualDebugUnitTestOutcome.Failed) ||
            milestone.CleanControl.Results.Any(result =>
                result.Outcome == ActualDebugUnitTestOutcome.Failed);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            runId = first.RunId,
            success = first.Success && milestone.CleanControl.Success,
            executionMode = "milestone",
            cleanControlMatched,
            preflightCount = milestone.PreflightCount,
            batchRunCount = milestone.BatchRunCount,
            firstInstrumentationCount =
                milestone.FirstCache.InstrumentationCount,
            cleanControlInstrumentationCount =
                milestone.CleanControlCache.InstrumentationCount,
            cleanControlCacheHit = milestone.CleanControlCache.CacheHit,
            testCount = first.Results.Count,
            passedCount = Count(first, ActualDebugUnitTestOutcome.Passed),
            failedCount = Count(first, ActualDebugUnitTestOutcome.Failed),
            notRunCount = Count(first, ActualDebugUnitTestOutcome.NotRun),
            observedEntryClaimCount = first.Results.Sum(
                result => result.ObservedEntryIds.Count),
            uniqueMappedEntryCount = migrated.Entries.Count(entry =>
                entry.TestIds.Count != 0 ||
                entry.NegativeTestIds.Count != 0),
            addedPositiveMappingCount = migration.AddedPositiveMappingCount,
            addedNegativeMappingCount = migration.AddedNegativeMappingCount,
            remainingUnmappedCount = migration.RemainingUnmappedCount,
            coverageReady = migration.RemainingUnmappedCount == 0 &&
                !hasFailures && cleanControlMatched,
            dllHash = input.DllHash,
            pdbHash = input.PdbHash,
            inventoryDigest = input.InventoryDigest,
            coverageDigest = input.CoverageDigest,
            migratedCoverageDigest = migrated.CoverageDigest!
        });
    }

    private static int Count(
        ActualDebugUnitTestBatchResult batch,
        ActualDebugUnitTestOutcome outcome) =>
        batch.Results.Count(result => result.Outcome == outcome);
}
