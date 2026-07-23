namespace ONI_Together.HeadlessTests;

internal static class SyncCoverageMigrateProcessRedSuite
{
    public static int Run()
    {
        try
        {
            SyncCoverageMigrateProcessRedTests
                .RealProcessDispatchesCoverageMigrateCli();
            Console.WriteLine(
                "PASS real process dispatches coverage-migrate CLI");
            Console.WriteLine("1/1 passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "FAIL real process dispatches coverage-migrate CLI: " +
                error.Message);
            Console.WriteLine("0/1 passed");
            return 1;
        }
    }
}
