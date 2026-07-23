namespace ONI_Together.HeadlessTests;

internal sealed partial class ActualDebugUnitTestBatchRunner
{
    public ActualDebugUnitTestBatchResult Run(
        ActualDebugUnitTestBatchInput input)
    {
        ValidateInput(input);
        SyncExecutionInstrumentedAssembly instrumented =
            SyncExecutionIlInstrumenter.Instrument(
                ProbeCatalog(input), input.Assembly,
                input.GameLibsDirectory);
        return RunInstrumented(input, instrumented);
    }

    internal ActualDebugUnitTestBatchResult ExecuteInstrumented(
        ActualDebugUnitTestBatchInput input,
        SyncExecutionInstrumentedAssembly instrumented)
    {
        ValidateInput(input);
        if (instrumented.DllHash != input.DllHash ||
            instrumented.PdbHash != input.PdbHash)
            throw new InvalidOperationException(
                "instrumented assembly input identity drift");
        return RunInstrumented(input, instrumented);
    }

    private ActualDebugUnitTestBatchResult RunInstrumented(
        ActualDebugUnitTestBatchInput input,
        SyncExecutionInstrumentedAssembly instrumented)
    {
        TextWriter output = Console.Out;
        TextWriter error = Console.Error;
        try
        {
            return RunCore(input, instrumented, new(output, error));
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }

    private sealed record ConsoleWriters(
        TextWriter Output,
        TextWriter Error);
}
