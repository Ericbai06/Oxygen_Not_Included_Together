using System.Reflection;
using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestBatchOnceFailureJsonContractTests
{
    internal static void Validate()
    {
        string json = Serialize(Result());
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement batch = document.RootElement.GetProperty("batch");
        if (!batch.TryGetProperty("failures", out JsonElement failures))
            throw new InvalidOperationException(
                "single-run batch JSON omitted failures array");
        JsonElement[] items = failures.EnumerateArray().ToArray();
        Equal(2, items.Length);
        AssertFailure(items[0], new ExpectedFailure(
            "headless:b-failure",
            "Fixture.BFailure()",
            "b failed"));
        AssertFailure(items[1], new ExpectedFailure(
            "headless:z-failure",
            "Fixture.ZFailure()",
            "z failed"));
    }

    private static string Serialize(ActualDebugUnitTestBatchOnceResult result)
    {
        MethodInfo method = typeof(ActualDebugUnitTestBatchOnce).GetMethod(
            "ResultJson",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("ResultJson is missing");
        return (string)method.Invoke(null, [result])!;
    }

    private static ActualDebugUnitTestBatchOnceResult Result() => new()
    {
        Preflight = new ActualDebugUnitTestPreflightResult
        {
            SchemaVersion = 1,
            AnalyzedEntryCount = 3,
            DllHash = Hash('a'),
            PdbHash = Hash('b'),
            InventoryDigest = "sha256:" + Hash('c'),
            Issues = []
        },
        Cache = new ActualDebugUnitTestInstrumentationCacheResult
        {
            Key = new ActualDebugUnitTestInstrumentationCacheKey
            {
                SchemaVersion = 1,
                DllHash = Hash('a'),
                PdbHash = Hash('b'),
                InventoryDigest = "sha256:" + Hash('c'),
                KeyDigest = Hash('d')
            },
            Assembly = new SyncExecutionInstrumentedAssembly(
                [], [], Hash('a'), Hash('b')),
            InstrumentationCount = 1,
            CacheHit = false
        },
        Batch = Batch(),
        PreflightCount = 1,
        BatchRunCount = 1,
        CleanControlRunCount = 0,
        MigrationCount = 0
    };

    private static ActualDebugUnitTestBatchResult Batch()
    {
        ActualDebugUnitTestResult[] results =
        [
            Test(
                "headless:z-failure",
                "Fixture.ZFailure()",
                "z failed"),
            Test(
                "headless:a-pass",
                "Fixture.APass()",
                null),
            Test(
                "headless:b-failure",
                "Fixture.BFailure()",
                "b failed")
        ];
        return new ActualDebugUnitTestBatchResult(
            1,
            "failure-json-contract",
            Hash('a'),
            Hash('b'),
            "sha256:" + Hash('c'),
            1,
            1,
            false,
            results);
    }

    private static ActualDebugUnitTestResult Test(
        string testId,
        string methodSymbol,
        string? message) => new(
            testId,
            methodSymbol,
            message is null
                ? ActualDebugUnitTestOutcome.Passed
                : ActualDebugUnitTestOutcome.Failed,
            message,
            0,
            Hash('a'),
            Hash('b'),
            "sha256:" + Hash('c'),
            1,
            [],
            [],
            null);

    private static void AssertFailure(
        JsonElement failure,
        ExpectedFailure expected)
    {
        string[] fields = failure.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        True(fields.SequenceEqual(
                new[] { "message", "methodSymbol", "testId" }),
            "failure JSON fields are not exact");
        Equal(expected.TestId, failure.GetProperty("testId").GetString());
        Equal(expected.MethodSymbol,
            failure.GetProperty("methodSymbol").GetString());
        Equal(expected.Message, failure.GetProperty("message").GetString());
    }

    private static string Hash(char value) => new(value, 64);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"expected {expected}, actual {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record ExpectedFailure(
        string TestId,
        string MethodSymbol,
        string Message);
}
