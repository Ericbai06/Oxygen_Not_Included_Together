using System.Reflection;

namespace ONI_Together.HeadlessTests;

internal sealed record SyncCoverageMigrationInput(
    SyncCatalogScan Catalog,
    string InventoryDigest,
    SyncCoverageManifest StaleCoverage,
    SyncTestRegistry TestRegistry,
    IReadOnlyList<SyncExecutionReceipt> Receipts,
    string DllHash,
    string PdbHash);

internal sealed record SyncCoverageMigrationResult(
    string CoverageJson,
    int RemovedOrphanCount,
    int AddedPositiveMappingCount,
    int AddedNegativeMappingCount,
    int RemainingUnmappedCount);

internal interface ISyncCoverageManifestMigrator
{
    SyncCoverageMigrationResult Migrate(SyncCoverageMigrationInput input);

    int RunCli(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr);
}

internal static class SyncCoverageManifestMigratorLoader
{
    private const string ImplementationName =
        "ONI_Together.HeadlessTests.SyncCoverageManifestMigrator";

    internal static ISyncCoverageManifestMigrator Load()
    {
        Type? implementation = typeof(SyncCoverageManifestMigratorLoader)
            .Assembly.GetType(ImplementationName, throwOnError: false);
        if (implementation is null)
            throw new InvalidOperationException(
                "SyncCoverageManifestMigrator implementation is missing");
        if (!typeof(ISyncCoverageManifestMigrator).IsAssignableFrom(implementation))
            throw new InvalidOperationException(
                "SyncCoverageManifestMigrator does not implement its frozen contract");
        return (ISyncCoverageManifestMigrator)Activator.CreateInstance(
            implementation, nonPublic: true)!;
    }
}
