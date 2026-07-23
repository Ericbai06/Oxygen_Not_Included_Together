namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestExecutionFrequencyContractTests
{
    internal static void Validate()
    {
        SyncExecutionPrefixAdjacencyContractTests.Validate();
        SyncExecutionShortBranchContractTests.Validate();
        SyncExecutionMaxStackContractTests.Validate();
        ActualDebugUnitTestBatchOnceFailureJsonContractTests.Validate();
        SyncExecutionExceptionBoundaryContractTests.Validate();
        SyncExecutionProbeObservationFilterContractTests.Validate();
        ActualDebugUnitTestResolverDirectoryContractTests
            .ExplicitDirectoryFlowsThroughOnceAndCache();
        ActualDebugUnitTestPreflightContractTests
            .AggregatesAllDeterministicIssues();
        ActualDebugUnitTestPreflightContractTests
            .CurrentDebugCatalogHasNoPreflightIssues();
        ActualDebugUnitTestInstrumentationCacheContractTests
            .ReusesExactBytesAndRejectsInvalidCacheState();
        ActualDebugUnitTestBatchModesContractTests
            .DailyRunsOnceAndMilestoneRunsTwice();
        ActualDebugUnitTestExecutionRouteContractTests
            .RoutesAndExporterUseExplicitModes();
    }
}
