namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestCoverageDigestContractSuite
{
    internal static int Run()
    {
        try
        {
            ActualDebugUnitTestCoverageDigestContractTests.Validate();
            Console.WriteLine(
                "PASS actual Debug UnitTest receipts use current coverage digest");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "FAIL actual Debug UnitTest coverage digest contract: " +
                error.Message);
            return 1;
        }
    }
}
