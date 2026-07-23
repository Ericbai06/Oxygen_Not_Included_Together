using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class SyncInventoryJson
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string Serialize(SyncCatalogScan catalog)
    {
        InventoryEntry[] entries = catalog.Entries
            .OrderBy(entry => entry.Id, StringComparer.Ordinal)
            .Select(ToInventoryEntry)
            .ToArray();
        InventoryError[] errors = catalog.Errors
            .OrderBy(error => error.Code, StringComparer.Ordinal)
            .ThenBy(error => error.Subject, StringComparer.Ordinal)
            .Select(error => new InventoryError(error.Code, error.Subject))
            .ToArray();
        var content = new InventoryContent(entries, errors);
        string canonicalContent = JsonSerializer.Serialize(content, JsonOptions);
        string digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonicalContent))).ToLowerInvariant();
        return JsonSerializer.Serialize(
            new InventoryDocument(SchemaVersion, digest, entries, errors),
            JsonOptions);
    }

    private static InventoryEntry ToInventoryEntry(SyncEntry entry)
    {
        string[] variants = entry.Variants.Select(variant => variant.Key)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new InventoryEntry(
            entry.Id,
            entry.Kind.ToString(),
            entry.FullyQualifiedSymbol,
            entry.ResolvedTargetSignature,
            entry.Bootstrap,
            variants,
            entry.Status.ToString());
    }

    private sealed record InventoryDocument(
        int SchemaVersion,
        string Digest,
        IReadOnlyList<InventoryEntry> Entries,
        IReadOnlyList<InventoryError> Errors);

    private sealed record InventoryContent(
        IReadOnlyList<InventoryEntry> Entries,
        IReadOnlyList<InventoryError> Errors);

    private sealed record InventoryEntry(
        string Id,
        string Kind,
        string FullyQualifiedSymbol,
        string ResolvedTargetSignature,
        string Bootstrap,
        IReadOnlyList<string> Variants,
        string Status);

    private sealed record InventoryError(string Code, string Subject);
}
