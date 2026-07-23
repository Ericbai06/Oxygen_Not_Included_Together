namespace ONI_Together.HeadlessTests;

internal sealed class ActualDebugUnitTestExporter : IActualDebugUnitTestExporter
{
    private const string Command = "actual-unit-export";
    private static readonly IReadOnlySet<string> AllowedOptions =
        new HashSet<string>([
            "--game-libs", "--coverage", "--output-dir"
        ], StringComparer.Ordinal);

    public int RunCli(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr)
    {
        try
        {
            Export(ParseCli(args));
            return 0;
        }
        catch (Exception error)
        {
            stderr.WriteLine($"actual unit export failed: {error.Message}");
            return 1;
        }
    }

    private static void Export(ExportOptions options)
    {
        ValidatePaths(options);
        string sourceJson = File.ReadAllText(options.CoveragePath);
        SyncCoverageManifest source = SyncCoverageManifest.Parse(sourceJson);
        ActualDebugUnitTestBatchInput fixture =
            ActualDebugUnitTestBatchFixture.Load(options.GameLibsFolder);
        if (source.InventoryDigest != fixture.InventoryDigest)
            throw new InvalidOperationException(
                "coverage inventory digest does not match the current catalog");
        ActualDebugUnitTestBatchInput input = fixture with
        {
            CoverageDigest = source.CoverageDigest
        };

        string parent = Path.GetDirectoryName(options.OutputDirectory)!;
        var modeInput = new ActualDebugUnitTestBatchModeInput
        {
            Batch = input,
            CacheDirectory = Path.Combine(parent, ".actual-unit-cache"),
            Kernel = new ActualDebugUnitTestBatchExecutionKernel()
        };
        IActualDebugUnitTestBatchMilestone milestone =
            ActualDebugUnitTestBatchMilestoneLoader.Load();
        ActualDebugUnitTestBatchMilestoneResult result =
            milestone.Run(modeInput);
        IActualDebugUnitTestBatchRunner runner =
            ActualDebugUnitTestBatchRunnerLoader.Load();
        runner.Validate(input, result.First);
        runner.Validate(input, result.CleanControl);
        RequirePassingBatch(result.First);
        RequirePassingBatch(result.CleanControl);

        IReadOnlyList<SyncExecutionReceipt> receipts =
            AuthenticPassingReceipts(result.First);
        SyncCoverageMigrationResult migration =
            SyncCoverageManifestMigratorLoader.Load().Migrate(new(
                input.Catalog, input.InventoryDigest, source,
                Registry(receipts), receipts, input.DllHash, input.PdbHash));
        ActualDebugUnitTestExportPublisher.Publish(
            options.OutputDirectory, runner.Serialize(result.First),
            migration, input, result);
    }

    private static void RequirePassingBatch(
        ActualDebugUnitTestBatchResult batch)
    {
        if (!batch.Success ||
            batch.Results.Any(result =>
                result.Outcome == ActualDebugUnitTestOutcome.Failed))
            throw new InvalidOperationException(
                "actual Debug UnitTest batch contains failures");
    }

    private static IReadOnlyList<SyncExecutionReceipt>
        AuthenticPassingReceipts(ActualDebugUnitTestBatchResult batch)
    {
        SyncExecutionReceipt[] receipts = batch.Results
            .Where(result =>
                result.Outcome == ActualDebugUnitTestOutcome.Passed &&
                result.Receipt is not null)
            .Select(result => result.Receipt!)
            .ToArray();
        if (receipts.Length == 0)
            throw new InvalidOperationException(
                "actual Debug execution produced no passing receipt");
        return receipts;
    }

    private static SyncTestRegistry Registry(
        IEnumerable<SyncExecutionReceipt> receipts)
    {
        IEnumerable<SyncTestDefinition> authentic = receipts.Select(
            receipt => new SyncTestDefinition(
                receipt.TestId, receipt.Tier, receipt.ScenarioId));
        return SyncTestRegistry.Create(authentic.Concat([
            new SyncTestDefinition(
                "ingame:export-placeholder", SyncExecutionTier.Ingame, null),
            new SyncTestDefinition(
                "python:export-placeholder", SyncExecutionTier.Python, null),
            new SyncTestDefinition(
                "real:export-placeholder", SyncExecutionTier.Real, null)
        ]));
    }

    private static ExportOptions ParseCli(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != Command)
            throw new ArgumentException(
                $"first argument must be {Command}");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 1; index < args.Count; index += 2)
        {
            string option = args[index];
            if (!AllowedOptions.Contains(option))
                throw new ArgumentException($"unknown option: {option}");
            if (index + 1 >= args.Count ||
                string.IsNullOrWhiteSpace(args[index + 1]) ||
                args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(
                    $"option requires a value: {option}");
            if (!values.TryAdd(option, args[index + 1]))
                throw new ArgumentException($"duplicate option: {option}");
        }
        return new ExportOptions(
            Required(values, "--game-libs"),
            Required(values, "--coverage"),
            Required(values, "--output-dir"));
    }

    private static string Required(
        IReadOnlyDictionary<string, string> values,
        string option) =>
        values.TryGetValue(option, out string? value)
            ? value
            : throw new ArgumentException($"missing option: {option}");

    private static void ValidatePaths(ExportOptions options)
    {
        if (!Directory.Exists(options.GameLibsFolder))
            throw new DirectoryNotFoundException(options.GameLibsFolder);
        if (!File.Exists(options.CoveragePath))
            throw new FileNotFoundException(options.CoveragePath);
        if (Directory.Exists(options.OutputDirectory) ||
            File.Exists(options.OutputDirectory))
            throw new InvalidOperationException(
                "output directory must not already exist");
        string? parent = Path.GetDirectoryName(options.OutputDirectory);
        if (parent is null || !Directory.Exists(parent))
            throw new DirectoryNotFoundException(parent);
        if (Path.GetFullPath(options.CoveragePath) ==
                Path.GetFullPath(options.OutputDirectory) ||
            IsWithin(options.OutputDirectory, options.GameLibsFolder))
            throw new InvalidOperationException(
                "output directory overlaps an input");
    }

    private static bool IsWithin(string child, string parent)
    {
        string root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(child).StartsWith(
            root, StringComparison.Ordinal);
    }

    private sealed record ExportOptions(
        string GameLibsFolder,
        string CoveragePath,
        string OutputDirectory)
    {
        internal string GameLibsFolder { get; } =
            Path.GetFullPath(GameLibsFolder);
        internal string CoveragePath { get; } =
            Path.GetFullPath(CoveragePath);
        internal string OutputDirectory { get; } =
            Path.GetFullPath(OutputDirectory);
    }
}
