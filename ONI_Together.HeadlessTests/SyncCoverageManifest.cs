using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal sealed record SyncCoverageEntry(
    string Id,
    string Domain,
    IReadOnlyList<string> TestIds,
    IReadOnlyList<string> NegativeTestIds,
    IReadOnlyList<string> ScenarioIds,
    IReadOnlyList<string> Variants,
    SyncEntryStatus Status,
    string? HeadlessUnsupportedReason);

internal sealed record SyncCoverageManifest(
    string? InventoryDigest,
    string? CoverageDigest,
    IReadOnlyList<SyncCoverageEntry> Entries)
{
    public static SyncCoverageManifest Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("entries", out JsonElement entries) ||
            entries.ValueKind != JsonValueKind.Array)
            throw new FormatException("coverage manifest requires an entries array");
        if (document.RootElement.TryGetProperty("coverageDigest", out _) ||
            document.RootElement.TryGetProperty("digest", out _))
            throw new FormatException(
                "coverage manifest must not declare a synthetic digest");

        return new SyncCoverageManifest(
            OptionalRootString(document.RootElement, "inventoryDigest"),
            SyncCanonicalJson.Sha256(document.RootElement),
            entries.EnumerateArray().Select(ParseEntry).ToArray());
    }

    private static string? OptionalRootString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value))
            return null;
        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new FormatException($"coverage manifest has invalid {property}");
        return value.GetString();
    }

    private static SyncCoverageEntry ParseEntry(JsonElement element)
    {
        string id = RequiredString(element, "id");
        string statusText = RequiredString(element, "status");
        if (!Enum.TryParse(statusText, ignoreCase: false, out SyncEntryStatus status))
            throw new FormatException($"coverage entry {id} has invalid status {statusText}");
        return new SyncCoverageEntry(
            id,
            RequiredNonEmptyString(element, "domain"),
            RequiredStrings(element, "testIds"),
            RequiredStrings(element, "negativeTestIds"),
            RequiredStrings(element, "scenarioIds"),
            RequiredStrings(element, "variants"),
            status,
            OptionalNonEmptyString(element, "headlessUnsupportedReason"));
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
            throw new FormatException($"coverage entry requires string {property}");
        return value.GetString()!;
    }

    private static string RequiredNonEmptyString(
        JsonElement element,
        string property)
    {
        string value = RequiredString(element, property);
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException($"coverage entry requires nonempty {property}");
        return value;
    }

    private static string? OptionalNonEmptyString(
        JsonElement element,
        string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
            return null;
        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new FormatException($"coverage entry requires nonempty {property}");
        return value.GetString();
    }

    private static string[] RequiredStrings(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
            throw new FormatException($"coverage entry requires array {property}");
        return value.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw new FormatException(
                    $"coverage entry {property} values must be strings"))
            .ToArray();
    }
}

internal static class SyncCoverageValidator
{
    public static IReadOnlyList<SurfaceError> Validate(
        SyncCatalogScan catalog,
        SyncCoverageManifest manifest,
        IReadOnlySet<string> knownTestIds,
        IReadOnlySet<string> knownScenarioIds)
    {
        var errors = new List<SurfaceError>();
        Dictionary<string, SyncEntry[]> catalogGroups = catalog.Entries
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        Dictionary<string, SyncCoverageEntry[]> manifestGroups = manifest.Entries
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        AddMappingErrors(catalogGroups, manifestGroups, errors);
        AddCatalogErrors(catalog.Entries, errors);
        var context = new CoverageValidationContext(
            catalogGroups, knownTestIds, knownScenarioIds, errors);
        foreach (SyncCoverageEntry entry in manifest.Entries)
            ValidateEntry(entry, context);
        return errors.Distinct().ToArray();
    }

    private static void AddMappingErrors(
        IReadOnlyDictionary<string, SyncEntry[]> catalog,
        IReadOnlyDictionary<string, SyncCoverageEntry[]> manifest,
        ICollection<SurfaceError> errors)
    {
        foreach ((string id, SyncCoverageEntry[] entries) in manifest)
        {
            if (entries.Length > 1)
                errors.Add(new SurfaceError("manifest_duplicate_entry", id));
            if (!catalog.ContainsKey(id))
                errors.Add(new SurfaceError("manifest_orphan_entry", id));
        }
        foreach (string id in catalog.Keys.Where(id => !manifest.ContainsKey(id)))
            errors.Add(new SurfaceError("manifest_missing_entry", id));
    }

    private static void AddCatalogErrors(
        IEnumerable<SyncEntry> entries,
        ICollection<SurfaceError> errors)
    {
        foreach (SyncEntry entry in entries)
        {
            if (entry.Status == SyncEntryStatus.Active &&
                string.IsNullOrWhiteSpace(entry.ResolvedTargetSignature))
            {
                errors.Add(new SurfaceError(
                    "manifest_unresolved_active_target", entry.Id));
            }
        }
        foreach (IGrouping<string, SyncEntry> group in entries
                     .GroupBy(entry => entry.Id, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
                errors.Add(new SurfaceError("catalog_duplicate_id", group.Key));
        }
    }

    private static void ValidateEntry(
        SyncCoverageEntry manifestEntry,
        CoverageValidationContext context)
    {
        foreach (string testId in manifestEntry.TestIds.Concat(
                     manifestEntry.NegativeTestIds))
        {
            if (!context.KnownTestIds.Contains(testId))
                context.Errors.Add(new SurfaceError("manifest_unknown_test_id", testId));
        }
        foreach (string scenarioId in manifestEntry.ScenarioIds)
        {
            if (!context.KnownScenarioIds.Contains(scenarioId))
                context.Errors.Add(new SurfaceError(
                    "manifest_unknown_scenario_id", scenarioId));
        }
        if (!context.Catalog.TryGetValue(manifestEntry.Id, out SyncEntry[]? matches) ||
            matches.Length != 1)
            return;
        ValidateMappedEntry(manifestEntry, matches[0], context.Errors);
    }

    private static void ValidateMappedEntry(
        SyncCoverageEntry manifestEntry,
        SyncEntry catalogEntry,
        ICollection<SurfaceError> errors)
    {
        string[] declaredVariants = manifestEntry.Variants
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] actualVariants = catalogEntry.Variants
            .Select(variant => variant.Key)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!declaredVariants.SequenceEqual(actualVariants, StringComparer.Ordinal))
            errors.Add(new SurfaceError("manifest_undeclared_variant", catalogEntry.Id));
        if (manifestEntry.Status != catalogEntry.Status)
            errors.Add(new SurfaceError("manifest_status_mismatch", catalogEntry.Id));
        string[] executionTests = manifestEntry.TestIds
            .Concat(manifestEntry.NegativeTestIds)
            .ToArray();
        if (executionTests.Length == 0)
            errors.Add(new SurfaceError(
                "manifest_missing_execution_test", catalogEntry.Id));
        if (manifestEntry.HeadlessUnsupportedReason is not null &&
            !executionTests.Any(IsRuntimeTest))
        {
            errors.Add(new SurfaceError(
                "unity_only_missing_runtime_test", catalogEntry.Id));
        }
        if (catalogEntry.Status == SyncEntryStatus.RegisteredDisabled &&
            manifestEntry.NegativeTestIds.Count == 0)
        {
            errors.Add(new SurfaceError(
                "registered_disabled_missing_negative_test", catalogEntry.Id));
        }
    }

    private static bool IsRuntimeTest(string testId)
    {
        return testId.StartsWith("ingame:", StringComparison.Ordinal) ||
            testId.StartsWith("real:", StringComparison.Ordinal);
    }

    private sealed record CoverageValidationContext(
        IReadOnlyDictionary<string, SyncEntry[]> Catalog,
        IReadOnlySet<string> KnownTestIds,
        IReadOnlySet<string> KnownScenarioIds,
        ICollection<SurfaceError> Errors);
}
