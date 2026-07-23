using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestPreflightJson
{
    internal static string Serialize(
        ActualDebugUnitTestPreflightResult result) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = result.SchemaVersion,
            analyzedEntryCount = result.AnalyzedEntryCount,
            dllHash = result.DllHash,
            pdbHash = result.PdbHash,
            inventoryDigest = result.InventoryDigest,
            issues = result.Issues.Select(issue => new
            {
                entryId = issue.EntryId,
                kind = issue.Kind.ToString(),
                code = issue.Code,
                symbol = issue.Symbol,
                message = issue.Message
            }).ToArray()
        });
}
