namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestSkipMetadataContractSuite
{
    internal static int Run()
    {
        try
        {
            ActualDebugUnitTestSkipMetadataContractTests.Validate();
            Console.WriteLine(
                "PASS Skip-capable UnitTests declare explicit metadata");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "FAIL Skip-capable UnitTest metadata contract: " +
                error.Message);
            return 1;
        }
    }
}
