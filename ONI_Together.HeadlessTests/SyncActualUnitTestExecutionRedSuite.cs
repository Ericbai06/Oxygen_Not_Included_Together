namespace ONI_Together.HeadlessTests;

internal static class SyncActualUnitTestExecutionRedSuite
{
    public static int Run()
    {
        (string Name, Action Test)[] tests =
        [
            ("actual Debug UnitTest receipts require binary hashes",
                SyncActualUnitTestExecutionRedTests
                    .ActualDebugUnitTestReceiptRequiresBinaryHashes),
            ("actual Debug UnitTest receipt mutations fail closed",
                SyncActualUnitTestExecutionRedTests
                    .UnitTestReceiptMutationsFailClosed),
        ];
        int failures = 0;
        foreach ((string name, Action test) in tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {error.Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }
}
