using System.Reflection;

namespace ONI_Together.HeadlessTests;

internal sealed class SyncExecutionProbeBinding
{
    public required string RunId { get; init; }
    public required string TestId { get; init; }
    public required SyncExecutionTier Tier { get; init; }
    public string? ScenarioId { get; init; }
    public required SyncExecutionPolarity Polarity { get; init; }
    public required string InventoryDigest { get; init; }
    public required string CoverageDigest { get; init; }
}

internal sealed record SyncExecutionFixtureAssembly(
    byte[] PeImage,
    byte[] PdbImage);

internal interface ISyncExecutionProbeSession
{
    Assembly RuntimeAssembly { get; }

    SyncExecutionReceipt Complete(SyncExecutionArtifact? artifact = null);
}

internal interface ISyncExecutionProbeFactory
{
    ISyncExecutionProbeSession Start(
        SyncExecutionProbeBinding binding,
        SyncCatalogScan catalog,
        SyncExecutionFixtureAssembly fixture);
}

internal static class SyncExecutionProbeFactoryLoader
{
    public static ISyncExecutionProbeFactory Load()
    {
        Type? type = typeof(SyncExecutionProbeFactoryLoader).Assembly.GetType(
            "ONI_Together.HeadlessTests.SyncExecutionProbeFactory",
            throwOnError: false,
            ignoreCase: false);
        if (type is null)
            throw new InvalidOperationException(
                "missing production SyncExecutionProbeFactory");
        if (Activator.CreateInstance(type) is not ISyncExecutionProbeFactory factory)
            throw new InvalidOperationException(
                "SyncExecutionProbeFactory must implement ISyncExecutionProbeFactory");
        return factory;
    }
}
