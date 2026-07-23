namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestPreflightContractTests
{
    internal static void AggregatesAllDeterministicIssues()
    {
        PreflightFixture fixture =
            ActualDebugUnitTestPreflightFixture.CreateInvalid();
        IActualDebugUnitTestPreflight preflight =
            ActualDebugUnitTestPreflightLoader.Load();

        ActualDebugUnitTestPreflightResult result =
            preflight.Analyze(fixture.Input);

        Equal(1, result.SchemaVersion);
        Equal(fixture.Input.Catalog.Entries.Count,
            result.AnalyzedEntryCount);
        Equal(fixture.Input.DllHash, result.DllHash);
        Equal(fixture.Input.PdbHash, result.PdbHash);
        Equal(fixture.Input.InventoryDigest, result.InventoryDigest);
        EqualSequence(fixture.ExpectedIssues, result.Issues);
        EqualSequence(result.Issues.OrderBy(issue => issue.EntryId,
                StringComparer.Ordinal)
            .ThenBy(issue => issue.Kind)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal)
            .ThenBy(issue => issue.Symbol, StringComparer.Ordinal)
            .ThenBy(issue => issue.Message, StringComparer.Ordinal),
            result.Issues);
        True(result.Issues.Select(issue => issue.Code).ToHashSet(
                StringComparer.Ordinal).IsSupersetOf([
                "missing_pdb_callsite",
                "unresolved_coroutine_target",
                "ambiguous_coroutine_target"
            ]), "preflight failed before aggregating all deterministic issues");
    }

    internal static void CurrentDebugCatalogHasNoPreflightIssues()
    {
        ActualDebugUnitTestBatchInput actual =
            ActualDebugUnitTestBatchFixture.Load();
        var input = new ActualDebugUnitTestPreflightInput
        {
            Catalog = actual.Catalog,
            Assembly = actual.Assembly,
            InventoryDigest = actual.InventoryDigest,
            DllHash = actual.DllHash,
            PdbHash = actual.PdbHash
        };

        ActualDebugUnitTestPreflightResult result =
            ActualDebugUnitTestPreflightLoader.Load().Analyze(input);

        Equal(actual.Catalog.Entries.Count, result.AnalyzedEntryCount);
        EqualSequence([], result.Issues);
    }

    private static void EqualSequence<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException("sequences differ");
    }

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
}
