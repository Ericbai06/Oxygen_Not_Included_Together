using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal sealed class ActualDebugUnitTestBatchOnce :
    IActualDebugUnitTestBatchOnce
{
    public ActualDebugUnitTestBatchOnceResult Run(
        ActualDebugUnitTestBatchModeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ActualDebugUnitTestPreflightResult preflight =
            ActualDebugUnitTestPreflightLoader.Load().Analyze(
                PreflightInput(input.Batch));
        if (preflight.Issues.Count != 0)
            throw new InvalidOperationException(
                "actual Debug UnitTest preflight contains issues");
        ActualDebugUnitTestInstrumentationCacheResult cache =
            ActualDebugUnitTestInstrumentationCacheLoader.Load().GetOrCreate(
                new ActualDebugUnitTestInstrumentationCacheInput
                {
                    Catalog = input.Batch.Catalog,
                    Assembly = input.Batch.Assembly,
                    InventoryDigest = input.Batch.InventoryDigest,
                    CacheDirectory = input.CacheDirectory,
                    GameLibsDirectory = input.Batch.GameLibsDirectory
                });
        ActualDebugUnitTestBatchResult batch =
            input.Kernel.Execute(input.Batch, cache.Assembly);
        return new ActualDebugUnitTestBatchOnceResult
        {
            Preflight = preflight,
            Cache = cache,
            Batch = batch,
            PreflightCount = 1,
            BatchRunCount = 1,
            CleanControlRunCount = 0,
            MigrationCount = 0
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
            ActualDebugUnitTestBatchOnceResult result = Run(new()
            {
                Batch = batch,
                CacheDirectory = options.CacheDirectory,
                Kernel = new ActualDebugUnitTestBatchExecutionKernel()
            });
            stdout.WriteLine(ResultJson(result));
            return result.Batch.Success ? 0 : 1;
        }
        catch (Exception error)
        {
            stderr.WriteLine($"actual unit batch once failed: {error.Message}");
            return 1;
        }
    }

    private static ActualDebugUnitTestPreflightInput PreflightInput(
        ActualDebugUnitTestBatchInput batch) => new()
    {
        Catalog = batch.Catalog,
        Assembly = batch.Assembly,
        InventoryDigest = batch.InventoryDigest,
        DllHash = batch.DllHash,
        PdbHash = batch.PdbHash
    };

    private static string ResultJson(
        ActualDebugUnitTestBatchOnceResult result) =>
        JsonSerializer.Serialize(new
        {
            preflight = new
            {
                schemaVersion = result.Preflight.SchemaVersion,
                analyzedEntryCount = result.Preflight.AnalyzedEntryCount,
                dllHash = result.Preflight.DllHash,
                pdbHash = result.Preflight.PdbHash,
                inventoryDigest = result.Preflight.InventoryDigest,
                issues = result.Preflight.Issues.Select(issue => new
                {
                    entryId = issue.EntryId,
                    kind = issue.Kind.ToString(),
                    code = issue.Code,
                    symbol = issue.Symbol,
                    message = issue.Message
                }).ToArray()
            },
            cache = new
            {
                schemaVersion = result.Cache.Key.SchemaVersion,
                keyDigest = result.Cache.Key.KeyDigest,
                cacheHit = result.Cache.CacheHit,
                instrumentationCount = result.Cache.InstrumentationCount
            },
            batch = new
            {
                schemaVersion = result.Batch.SchemaVersion,
                runId = result.Batch.RunId,
                success = result.Batch.Success,
                testCount = result.Batch.Results.Count,
                passedCount = Count(
                    result.Batch, ActualDebugUnitTestOutcome.Passed),
                failedCount = Count(
                    result.Batch, ActualDebugUnitTestOutcome.Failed),
                notRunCount = Count(
                    result.Batch, ActualDebugUnitTestOutcome.NotRun),
                failures = result.Batch.Results
                    .Where(item =>
                        item.Outcome == ActualDebugUnitTestOutcome.Failed)
                    .OrderBy(item => item.TestId, StringComparer.Ordinal)
                    .Select(item => new
                    {
                        testId = item.TestId,
                        methodSymbol = item.MethodSymbol,
                        message = item.Message
                    }).ToArray()
            },
            preflightCount = result.PreflightCount,
            batchRunCount = result.BatchRunCount,
            cleanControlRunCount = result.CleanControlRunCount,
            migrationCount = result.MigrationCount
        });

    private static int Count(
        ActualDebugUnitTestBatchResult batch,
        ActualDebugUnitTestOutcome outcome) =>
        batch.Results.Count(result => result.Outcome == outcome);

    private static CliOptions ParseCli(IReadOnlyList<string> args)
    {
        if (args.Count != 5 ||
            args[0] != ActualDebugUnitTestExecutionCommands.BatchOnce ||
            args[1] != "--game-libs" ||
            string.IsNullOrWhiteSpace(args[2]) ||
            args[3] != "--cache-dir" ||
            string.IsNullOrWhiteSpace(args[4]))
            throw new ArgumentException(
                "usage: actual-unit-batch-once --game-libs <path> " +
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
