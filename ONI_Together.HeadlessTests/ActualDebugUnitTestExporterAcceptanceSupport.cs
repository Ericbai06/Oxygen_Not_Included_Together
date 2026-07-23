using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static partial class ActualDebugUnitTestExporterAcceptanceTests
{
    private static void AddMappings(
        ISet<Mapping> target,
        JsonElement receipt,
        string property)
    {
        string testId = String(receipt, "testId");
        foreach (JsonElement entry in Array(receipt, property))
            target.Add(new Mapping(entry.GetString()!, testId));
    }

    private static HashSet<Mapping> Mappings(
        SyncCoverageManifest manifest,
        bool negative) => manifest.Entries.SelectMany(entry =>
            (negative ? entry.NegativeTestIds : entry.TestIds)
                .Select(testId => new Mapping(entry.Id, testId))).ToHashSet();

    private static SyncTestRegistry Registry(IEnumerable<string> headlessIds)
    {
        IEnumerable<SyncTestDefinition> headless = headlessIds
            .Distinct(StringComparer.Ordinal)
            .Select(id => new SyncTestDefinition(
                id, SyncExecutionTier.Headless, null));
        return SyncTestRegistry.Create(headless.Concat([
            new SyncTestDefinition("ingame:export-placeholder",
                SyncExecutionTier.Ingame, null),
            new SyncTestDefinition("python:export-placeholder",
                SyncExecutionTier.Python, null),
            new SyncTestDefinition("real:export-placeholder",
                SyncExecutionTier.Real, null)
        ]));
    }

    private static int Count(IEnumerable<JsonElement> results, string outcome) =>
        results.Count(result => String(result, "outcome") == outcome);

    private static void Exact(
        JsonElement value,
        IReadOnlySet<string> fields)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.EnumerateObject().Select(item => item.Name)
                .ToHashSet(StringComparer.Ordinal).SetEquals(fields))
            throw new InvalidOperationException(
                "summary fields are not exact");
    }

    private static JsonElement.ArrayEnumerator Array(
        JsonElement value,
        string property) => value.GetProperty(property).ValueKind ==
        JsonValueKind.Array
            ? value.GetProperty(property).EnumerateArray()
            : throw new InvalidOperationException(
                $"{property} must be an array");

    private static string String(JsonElement value, string property) =>
        value.GetProperty(property).GetString() ??
        throw new InvalidOperationException($"{property} must be a string");

    private static int Integer(JsonElement value, string property) =>
        value.GetProperty(property).TryGetInt32(out int result)
            ? result
            : throw new InvalidOperationException(
                $"{property} must be an integer");

    private static bool Boolean(JsonElement value, string property) =>
        value.GetProperty(property).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException(
                $"{property} must be a boolean")
        };

    private static void EqualSet<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual)
    {
        if (!expected.ToHashSet().SetEquals(actual))
            throw new InvalidOperationException("sets differ");
    }

    private static void EqualSequence<T>(
        IEnumerable<T> expected,
        IEnumerable<T?> actual)
    {
        if (!expected.Cast<T?>().SequenceEqual(actual))
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

    private sealed record ExportArtifacts
    {
        internal required ActualDebugUnitTestBatchInput Expected { get; init; }
        internal required JsonElement Batch { get; init; }
        internal required JsonElement Summary { get; init; }
        internal required SyncCoverageManifest Source { get; init; }
        internal required SyncCoverageManifest Migrated { get; init; }
    }

    private sealed record Mapping(string EntryId, string TestId);
}
