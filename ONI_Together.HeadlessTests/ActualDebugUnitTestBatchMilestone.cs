using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal sealed class ActualDebugUnitTestBatchMilestone :
    IActualDebugUnitTestBatchMilestone
{
    public ActualDebugUnitTestBatchMilestoneResult Run(
        ActualDebugUnitTestBatchModeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ActualDebugUnitTestPreflightResult preflight =
            ActualDebugUnitTestPreflightLoader.Load().Analyze(
                PreflightInput(input.Batch));
        if (preflight.Issues.Count != 0)
            throw new InvalidOperationException(
                "actual Debug UnitTest preflight contains issues");

        IActualDebugUnitTestInstrumentationCache cache =
            ActualDebugUnitTestInstrumentationCacheLoader.Load();
        ActualDebugUnitTestInstrumentationCacheInput cacheInput =
            CacheInput(input);
        ActualDebugUnitTestInstrumentationCacheResult firstCache =
            cache.GetOrCreate(cacheInput);
        ActualDebugUnitTestBatchResult first =
            input.Kernel.Execute(input.Batch, firstCache.Assembly);
        ActualDebugUnitTestInstrumentationCacheResult controlCache =
            cache.GetOrCreate(cacheInput);
        RequireCacheReuse(firstCache, controlCache);
        ActualDebugUnitTestBatchResult control =
            input.Kernel.Execute(input.Batch, controlCache.Assembly);
        RequireMatchingRuns(first, control);

        return new ActualDebugUnitTestBatchMilestoneResult
        {
            Preflight = preflight,
            FirstCache = firstCache,
            CleanControlCache = controlCache,
            First = first,
            CleanControl = control,
            PreflightCount = 1,
            BatchRunCount = 2,
            OutcomesMatched = true,
            EntrySetsMatched = true
        };
    }

    public int RunCli(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr)
    {
        try
        {
            CliOptions options = ParseCli(args);
            ActualDebugUnitTestBatchInput batch =
                ActualDebugUnitTestBatchFixture.Load(options.GameLibsFolder);
            ActualDebugUnitTestBatchMilestoneResult result = Run(new()
            {
                Batch = batch,
                CacheDirectory = options.CacheDirectory,
                Kernel = new ActualDebugUnitTestBatchExecutionKernel()
            });
            stdout.WriteLine(ResultJson(result));
            return result.First.Success && result.CleanControl.Success ? 0 : 1;
        }
        catch (Exception error)
        {
            stderr.WriteLine(
                $"actual unit batch milestone failed: {error.Message}");
            return 1;
        }
    }

    private static void RequireCacheReuse(
        ActualDebugUnitTestInstrumentationCacheResult first,
        ActualDebugUnitTestInstrumentationCacheResult control)
    {
        if (!control.CacheHit || control.InstrumentationCount != 0 ||
            first.Key != control.Key ||
            !first.Assembly.PeImage.SequenceEqual(control.Assembly.PeImage) ||
            !first.Assembly.PdbImage.SequenceEqual(control.Assembly.PdbImage))
            throw new InvalidOperationException(
                "milestone clean-control did not reuse exact cache bytes");
    }

    private static void RequireMatchingRuns(
        ActualDebugUnitTestBatchResult first,
        ActualDebugUnitTestBatchResult control)
    {
        Dictionary<string, ActualDebugUnitTestResult> expected =
            Results(first);
        Dictionary<string, ActualDebugUnitTestResult> actual =
            Results(control);
        if (!expected.Keys.ToHashSet(StringComparer.Ordinal)
                .SetEquals(actual.Keys))
            throw new InvalidOperationException(
                "milestone test sets differ");
        foreach ((string id, ActualDebugUnitTestResult result) in expected)
            if (result.Outcome != actual[id].Outcome ||
                !EntrySet(result).SetEquals(EntrySet(actual[id])))
                throw new InvalidOperationException(
                    $"milestone outcome or entry set differs: {id}");
        HashSet<string> expectedUnion = expected.Values
            .SelectMany(result => result.ObservedEntryIds)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> actualUnion = actual.Values
            .SelectMany(result => result.ObservedEntryIds)
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedUnion.SetEquals(actualUnion))
            throw new InvalidOperationException(
                "milestone overall entry union differs");
    }

    private static Dictionary<string, ActualDebugUnitTestResult> Results(
        ActualDebugUnitTestBatchResult batch)
    {
        if (batch.Results.GroupBy(
                result => result.TestId, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
            throw new InvalidOperationException(
                "milestone batch contains duplicate test IDs");
        return batch.Results.ToDictionary(
            result => result.TestId, StringComparer.Ordinal);
    }

    private static HashSet<string> EntrySet(
        ActualDebugUnitTestResult result) =>
        result.ObservedEntryIds.ToHashSet(StringComparer.Ordinal);

    private static ActualDebugUnitTestPreflightInput PreflightInput(
        ActualDebugUnitTestBatchInput batch) => new()
    {
        Catalog = batch.Catalog,
        Assembly = batch.Assembly,
        InventoryDigest = batch.InventoryDigest,
        DllHash = batch.DllHash,
        PdbHash = batch.PdbHash
    };

    private static ActualDebugUnitTestInstrumentationCacheInput CacheInput(
        ActualDebugUnitTestBatchModeInput input) => new()
    {
        Catalog = input.Batch.Catalog,
        Assembly = input.Batch.Assembly,
        InventoryDigest = input.Batch.InventoryDigest,
        CacheDirectory = input.CacheDirectory,
        GameLibsDirectory = input.Batch.GameLibsDirectory
    };

    private static string ResultJson(
        ActualDebugUnitTestBatchMilestoneResult result) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            runId = result.First.RunId,
            success = result.First.Success && result.CleanControl.Success,
            preflightCount = result.PreflightCount,
            batchRunCount = result.BatchRunCount,
            outcomesMatched = result.OutcomesMatched,
            entrySetsMatched = result.EntrySetsMatched,
            firstCacheHit = result.FirstCache.CacheHit,
            firstInstrumentationCount =
                result.FirstCache.InstrumentationCount,
            cleanControlCacheHit = result.CleanControlCache.CacheHit,
            cleanControlInstrumentationCount =
                result.CleanControlCache.InstrumentationCount,
            testCount = result.First.Results.Count,
            failedCount = result.First.Results.Count(item =>
                item.Outcome == ActualDebugUnitTestOutcome.Failed),
            cleanControlFailedCount = result.CleanControl.Results.Count(item =>
                item.Outcome == ActualDebugUnitTestOutcome.Failed)
        });

    private static CliOptions ParseCli(IReadOnlyList<string> args)
    {
        if (args.Count != 5 ||
            args[0] != ActualDebugUnitTestExecutionCommands.BatchMilestone ||
            args[1] != "--game-libs" ||
            string.IsNullOrWhiteSpace(args[2]) ||
            args[3] != "--cache-dir" ||
            string.IsNullOrWhiteSpace(args[4]))
            throw new ArgumentException(
                "usage: actual-unit-batch-milestone --game-libs <path> " +
                "--cache-dir <path>");
        string gameLibs = Path.GetFullPath(args[2]);
        string cache = Path.GetFullPath(args[4]);
        if (!Directory.Exists(gameLibs))
            throw new DirectoryNotFoundException(gameLibs);
        if (!Directory.Exists(cache))
            throw new DirectoryNotFoundException(cache);
        return new CliOptions(gameLibs, cache);
    }

    private sealed record CliOptions(
        string GameLibsFolder,
        string CacheDirectory);
}
