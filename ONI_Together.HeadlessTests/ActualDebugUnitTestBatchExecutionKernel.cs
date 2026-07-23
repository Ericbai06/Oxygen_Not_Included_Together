namespace ONI_Together.HeadlessTests;

internal sealed class ActualDebugUnitTestBatchExecutionKernel :
    IActualDebugUnitTestBatchExecutionKernel
{
    private readonly ActualDebugUnitTestBatchRunner runner = new();

    public ActualDebugUnitTestBatchResult Execute(
        ActualDebugUnitTestBatchInput input,
        SyncExecutionInstrumentedAssembly assembly) =>
        runner.ExecuteInstrumented(input, assembly);
}
