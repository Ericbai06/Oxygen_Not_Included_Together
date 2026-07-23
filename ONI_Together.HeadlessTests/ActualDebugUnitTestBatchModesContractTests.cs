namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestBatchModesContractTests
{
    internal static void DailyRunsOnceAndMilestoneRunsTwice()
    {
        IActualDebugUnitTestBatchOnce once =
            ActualDebugUnitTestBatchOnceLoader.Load();
        IActualDebugUnitTestBatchMilestone milestone =
            ActualDebugUnitTestBatchMilestoneLoader.Load();
        string temporary = Path.Combine(Path.GetTempPath(),
            "oni-actual-unit-modes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var onceKernel = new CountingKernel();
            ActualDebugUnitTestBatchOnceResult daily = once.Run(
                Input(Path.Combine(temporary, "once"), onceKernel));
            Equal(1, daily.PreflightCount);
            Equal(1, daily.BatchRunCount);
            Equal(0, daily.CleanControlRunCount);
            Equal(0, daily.MigrationCount);
            Equal(1, onceKernel.ExecutionCount);

            var milestoneKernel = new CountingKernel();
            ActualDebugUnitTestBatchMilestoneResult doubled = milestone.Run(
                Input(Path.Combine(temporary, "milestone"), milestoneKernel));
            Equal(1, doubled.PreflightCount);
            Equal(2, doubled.BatchRunCount);
            Equal(2, milestoneKernel.ExecutionCount);
            Equal(1, doubled.FirstCache.InstrumentationCount);
            True(!doubled.FirstCache.CacheHit,
                "milestone first run unexpectedly hit cache");
            Equal(0, doubled.CleanControlCache.InstrumentationCount);
            True(doubled.CleanControlCache.CacheHit,
                "milestone clean-control did not reuse instrumentation");
            True(doubled.FirstCache.Assembly.PeImage.SequenceEqual(
                    doubled.CleanControlCache.Assembly.PeImage) &&
                doubled.FirstCache.Assembly.PdbImage.SequenceEqual(
                    doubled.CleanControlCache.Assembly.PdbImage),
                "milestone clean-control reused different bytes");
            True(doubled.OutcomesMatched && doubled.EntrySetsMatched,
                "matching milestone runs were reported as different");

            var mismatchKernel = new CountingKernel(differOnSecond: true);
            Throws<InvalidOperationException>(() => milestone.Run(
                    Input(Path.Combine(temporary, "mismatch"), mismatchKernel)),
                "milestone accepted different outcomes or entry sets");
            Equal(2, mismatchKernel.ExecutionCount);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static ActualDebugUnitTestBatchModeInput Input(
        string cacheDirectory,
        IActualDebugUnitTestBatchExecutionKernel kernel)
    {
        ActualDebugUnitTestPreflightInput fixture =
            ActualDebugUnitTestPreflightFixture.CreateValid();
        var descriptor = new ActualDebugUnitTestDescriptor(
            "headless:unit:synthetic", "Synthetic.Test()", []);
        var batch = new ActualDebugUnitTestBatchInput(
            "synthetic-frequency-contract", fixture.Assembly, fixture.Catalog,
            fixture.InventoryDigest, fixture.DllHash, fixture.PdbHash,
            [descriptor], CoverageDigest: "sha256:" + new string('c', 64));
        return new ActualDebugUnitTestBatchModeInput
        {
            Batch = batch,
            CacheDirectory = cacheDirectory,
            Kernel = kernel
        };
    }

    private static void Throws<T>(Action action, string message)
        where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"expected {expected}, actual {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CountingKernel(bool differOnSecond = false) :
        IActualDebugUnitTestBatchExecutionKernel
    {
        internal int ExecutionCount { get; private set; }

        public ActualDebugUnitTestBatchResult Execute(
            ActualDebugUnitTestBatchInput input,
            SyncExecutionInstrumentedAssembly assembly)
        {
            ExecutionCount++;
            bool differs = differOnSecond && ExecutionCount == 2;
            var result = new ActualDebugUnitTestResult(
                input.ExpectedTests.Single().TestId,
                input.ExpectedTests.Single().MethodSymbol,
                differs ? ActualDebugUnitTestOutcome.Failed :
                    ActualDebugUnitTestOutcome.Passed,
                differs ? "synthetic mismatch" : null,
                0, input.DllHash, input.PdbHash, input.InventoryDigest,
                1, differs ? ["sync:fffffffffffffffffffffff3"] : [],
                [], null);
            return new ActualDebugUnitTestBatchResult(
                1, input.RunId, input.DllHash, input.PdbHash,
                input.InventoryDigest, 0, 1, !differs, [result]);
        }
    }
}
