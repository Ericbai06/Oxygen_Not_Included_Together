using System.Reflection;
namespace ONI_Together.HeadlessTests;

internal enum ActualDebugUnitTestOutcome
{
    Passed,
    Failed,
    NotRun
}

internal sealed record ActualDebugUnitTestDescriptor(
    string TestId,
    string MethodSymbol,
    IReadOnlyList<string> DirectRuntimeReferences,
    string? HeadlessUnsupportedReason = null);

internal sealed record ActualDebugUnitTestDirectCall(
    string MethodSymbol,
    string AssemblyName,
    bool IsInternalCall,
    bool IsRuntime,
    bool IsNative,
    bool IsPInvoke);

internal sealed record ActualDebugUnitTestRuntimeClassificationInput(
    string MethodSymbol,
    string? HeadlessUnsupportedReason,
    IReadOnlyList<ActualDebugUnitTestDirectCall> DirectCalls);

internal sealed record ActualDebugPublicizedAssemblyInput(
    string Path,
    byte[] PeImage,
    string Sha256,
    string AssemblyIdentity);

internal sealed record ActualDebugRuntimeAssemblyInput(
    string Path,
    byte[] PeImage,
    string Sha256,
    string AssemblyIdentity);

internal interface IActualDebugUnitTestRuntimeClassifier
{
    IReadOnlyList<string> Classify(
        ActualDebugUnitTestRuntimeClassificationInput input);
}

internal static class ActualDebugUnitTestRuntimeClassifierLoader
{
    private const string ImplementationName =
        "ONI_Together.HeadlessTests.ActualDebugUnitTestRuntimeClassifier";

    internal static IActualDebugUnitTestRuntimeClassifier Load()
    {
        Type? implementation = typeof(ActualDebugUnitTestRuntimeClassifierLoader)
            .Assembly.GetType(ImplementationName, throwOnError: false);
        if (implementation is null)
            throw new InvalidOperationException(
                "ActualDebugUnitTestRuntimeClassifier implementation is missing");
        if (!typeof(IActualDebugUnitTestRuntimeClassifier)
            .IsAssignableFrom(implementation))
            throw new InvalidOperationException(
                "ActualDebugUnitTestRuntimeClassifier does not implement its frozen contract");
        return (IActualDebugUnitTestRuntimeClassifier)Activator.CreateInstance(
            implementation, nonPublic: true)!;
    }
}

internal sealed record ActualDebugUnitTestBatchInput(
    string RunId,
    SyncExecutionFixtureAssembly Assembly,
    SyncCatalogScan Catalog,
    string InventoryDigest,
    string DllHash,
    string PdbHash,
    IReadOnlyList<ActualDebugUnitTestDescriptor> ExpectedTests,
    IReadOnlyList<ActualDebugRuntimeAssemblyInput>? RuntimeAssemblies = null,
    IReadOnlyList<ActualDebugPublicizedAssemblyInput>? PublicizedAssemblies = null,
    string? CoverageDigest = null,
    string? GameLibsDirectory = null);

internal sealed record ActualDebugUnitTestResult(
    string TestId,
    string MethodSymbol,
    ActualDebugUnitTestOutcome Outcome,
    string? Message,
    double DurationMs,
    string DllHash,
    string PdbHash,
    string InventoryDigest,
    int ObservationEpoch,
    IReadOnlyList<string> ObservedEntryIds,
    IReadOnlyList<string> RuntimeReferenceEvidence,
    SyncExecutionReceipt? Receipt);

internal sealed record ActualDebugUnitTestBootstrapEvidence(
    string MethodSymbol,
    int InvocationCount,
    int RegisteredPacketCountBefore,
    int RegisteredPacketCountAfter,
    bool RegistryWasInitiallyEmpty);

internal sealed record ActualDebugPublicizedAssemblyEvidence(
    string Path,
    string Sha256,
    string AssemblyIdentity);

internal sealed record ActualDebugPublicizedBootstrapEvidence(
    int LoadCount,
    IReadOnlyList<ActualDebugPublicizedAssemblyEvidence> Assemblies);

internal sealed record ActualDebugRuntimeAssemblyEvidence(
    string Path,
    string Sha256,
    string AssemblyIdentity);

internal sealed record ActualDebugAccessBypassBootstrapEvidence(
    IReadOnlyList<string> AccessBypassTargets,
    string InstrumentedDllHash,
    string InstrumentedPdbHash,
    IReadOnlyList<ActualDebugRuntimeAssemblyEvidence> RuntimeAssemblies,
    int InstrumentedTypeCount,
    int CannotReduceAccessTypeLoadExceptionCount);

internal sealed record ActualDebugUnitTestBatchResult(
    int SchemaVersion,
    string RunId,
    string DllHash,
    string PdbHash,
    string InventoryDigest,
    int InstrumentationCount,
    int AssemblyLoadCount,
    bool Success,
    IReadOnlyList<ActualDebugUnitTestResult> Results)
{
    public ActualDebugUnitTestBootstrapEvidence? Bootstrap { get; init; }
    public ActualDebugPublicizedBootstrapEvidence? PublicizedBootstrap {
        get;
        init;
    }
    public ActualDebugAccessBypassBootstrapEvidence? AccessBypassBootstrap {
        get;
        init;
    }
}

internal interface IActualDebugUnitTestBatchRunner
{
    ActualDebugUnitTestBatchResult Run(ActualDebugUnitTestBatchInput input);

    string Serialize(ActualDebugUnitTestBatchResult result);

    ActualDebugUnitTestBatchResult Parse(
        string json,
        ActualDebugUnitTestBatchInput expected);

    void Validate(
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestBatchResult result);
}

internal static class ActualDebugUnitTestBatchRunnerLoader
{
    private const string ImplementationName =
        "ONI_Together.HeadlessTests.ActualDebugUnitTestBatchRunner";

    internal static IActualDebugUnitTestBatchRunner Load()
    {
        Type? implementation = typeof(ActualDebugUnitTestBatchRunnerLoader)
            .Assembly.GetType(ImplementationName, throwOnError: false);
        if (implementation is null)
            throw new InvalidOperationException(
                "ActualDebugUnitTestBatchRunner implementation is missing");
        if (!typeof(IActualDebugUnitTestBatchRunner)
            .IsAssignableFrom(implementation))
            throw new InvalidOperationException(
                "ActualDebugUnitTestBatchRunner does not implement its frozen contract");
        return (IActualDebugUnitTestBatchRunner)Activator.CreateInstance(
            implementation, BindingFlags.Instance | BindingFlags.Public |
            BindingFlags.NonPublic, binder: null, args: null,
            culture: null)!;
    }
}
