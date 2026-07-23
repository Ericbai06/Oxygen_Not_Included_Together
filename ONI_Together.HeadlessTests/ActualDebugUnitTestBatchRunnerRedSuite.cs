namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestBatchRunnerRedSuite
{
    public static int Run()
    {
        try
        {
            ActualDebugUnitTestExecutionFrequencyContractTests.Validate();
            ActualDebugAccessBypassContractTests
                .InstrumentedAssemblyDefinesExactAccessBypass();
            SyncExecutionHarmonyEntryPdbContractTests.Validate();
            SyncExecutionCoroutineOverloadContractTests.Validate();
            SyncExecutionCoroutineAliasContractTests.Validate();
            Console.WriteLine(
                "PASS actual UnitTest execution frequency contracts");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "FAIL actual UnitTest execution frequency contracts: " +
                error.Message);
            return 1;
        }
    }
}
