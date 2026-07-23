namespace ONI_Together.HeadlessTests;

internal static class SyncCoverageManifestMigratorRedSuite
{
    public static int Run()
    {
        try
        {
            SyncCoverageManifestMigratorRedTests
                .MigratorBuildsFailClosedCurrentCatalogWorklist();
            Console.WriteLine(
                "PASS coverage migrator builds fail-closed current-catalog worklist");
            Console.WriteLine("1/1 passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "FAIL coverage migrator builds fail-closed current-catalog worklist: " +
                error.Message);
            Console.WriteLine("0/1 passed");
            return 1;
        }
    }
}
