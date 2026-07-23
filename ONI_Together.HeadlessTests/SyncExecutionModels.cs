using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;

namespace ONI_Together.HeadlessTests;

internal enum SyncExecutionTier
{
    Headless,
    Ingame,
    Python,
    Real
}

internal enum SyncExecutionPolarity
{
    Positive,
    Negative
}

internal sealed record SyncTestDefinition(
    string Id,
    SyncExecutionTier Tier,
    string? ScenarioId);

internal sealed class SyncTestRegistry
{
    private readonly IReadOnlyDictionary<string, SyncTestDefinition> definitions;

    private SyncTestRegistry(
        IReadOnlyDictionary<string, SyncTestDefinition> definitions)
    {
        this.definitions = definitions;
        Tiers = definitions.Values.Select(item => item.Tier).ToHashSet();
    }

    public IReadOnlySet<SyncExecutionTier> Tiers { get; }
    public IReadOnlySet<string> Ids => definitions.Keys.ToHashSet(StringComparer.Ordinal);
    public IReadOnlySet<string> ScenarioIds => definitions.Values
        .Where(item => item.ScenarioId is not null)
        .Select(item => item.ScenarioId!)
        .ToHashSet(StringComparer.Ordinal);

    public static SyncTestRegistry Create(
        IEnumerable<SyncTestDefinition> definitions)
    {
        var byId = new Dictionary<string, SyncTestDefinition>(StringComparer.Ordinal);
        foreach (SyncTestDefinition definition in definitions)
        {
            ValidateDefinition(definition);
            if (!byId.TryAdd(definition.Id, definition))
                throw new FormatException($"duplicate sync test id {definition.Id}");
        }
        IReadOnlySet<SyncExecutionTier> actual = byId.Values
            .Select(item => item.Tier).ToHashSet();
        if (!actual.SetEquals(Enum.GetValues<SyncExecutionTier>()))
            throw new FormatException("sync test registry requires all four tiers");
        return new SyncTestRegistry(byId);
    }

    public bool TryGet(
        string id,
        [NotNullWhen(true)] out SyncTestDefinition? definition)
    {
        return definitions.TryGetValue(id, out definition);
    }

    public SyncTestDefinition Get(string id)
    {
        return definitions.TryGetValue(id, out SyncTestDefinition? definition)
            ? definition
            : throw new KeyNotFoundException(id);
    }

    private static void ValidateDefinition(SyncTestDefinition definition)
    {
        string expectedPrefix = SyncExecutionText.Tier(definition.Tier) + ":";
        if (!definition.Id.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            definition.Id.Length == expectedPrefix.Length)
        {
            throw new FormatException(
                $"sync test id {definition.Id} does not match tier {definition.Tier}");
        }
        if (definition.ScenarioId is not null &&
            string.IsNullOrWhiteSpace(definition.ScenarioId))
            throw new FormatException($"sync test {definition.Id} has empty scenario");
    }
}

internal sealed record SyncExecutionArtifact(
    string Kind,
    string Path,
    string Sha256);

internal sealed record SyncRegistrationWitness(
    string EntryId,
    string RegistrationEntryId);

internal sealed record SyncExecutionReceipt(
    int SchemaVersion,
    string RunId,
    string InventoryDigest,
    string CoverageDigest,
    string TestId,
    SyncExecutionTier Tier,
    string? ScenarioId,
    SyncExecutionPolarity Polarity,
    IReadOnlyList<string> ExecutedEntryIds,
    IReadOnlyList<string> AbsentEntryIds,
    IReadOnlyList<SyncRegistrationWitness> RegistrationWitnesses,
    SyncExecutionArtifact? Artifact)
{
    private static readonly IReadOnlySet<string> RootFields = new HashSet<string>(
    [
        "schemaVersion", "runId", "inventoryDigest", "coverageDigest",
        "testId", "tier", "scenarioId", "polarity", "executedEntryIds",
        "absentEntryIds", "registrationWitnesses", "artifact", "dllHash",
        "pdbHash"
    ], StringComparer.Ordinal);

    public string DllHash { get; private set; } = "";
    public string PdbHash { get; private set; } = "";

    public static SyncExecutionReceipt Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        ExactObject(root, RootFields, "execution receipt");
        int schemaVersion = RequiredInt(root, "schemaVersion");
        if (schemaVersion != 1)
            throw new FormatException("execution receipt schemaVersion must be 1");
        string inventoryDigest = RequiredString(root, "inventoryDigest");
        string coverageDigest = RequiredString(root, "coverageDigest");
        RequireDigest(inventoryDigest, false, "inventoryDigest");
        RequireDigest(coverageDigest, true, "coverageDigest");
        IReadOnlyList<string> executed = RequiredEntryIds(
            root, "executedEntryIds", allowEmpty: false);
        IReadOnlyList<string> absent = RequiredEntryIds(
            root, "absentEntryIds", allowEmpty: true);
        IReadOnlyList<SyncRegistrationWitness> witnesses =
            RequiredRegistrationWitnesses(root);
        SyncExecutionPolarity polarity = SyncExecutionText.ParsePolarity(
            RequiredString(root, "polarity"));
        ValidateProvenance(polarity, executed, absent, witnesses);
        var receipt = new SyncExecutionReceipt(
            schemaVersion,
            RequiredNonEmptyString(root, "runId"),
            inventoryDigest,
            coverageDigest,
            RequiredNonEmptyString(root, "testId"),
            SyncExecutionText.ParseTier(RequiredString(root, "tier")),
            OptionalString(root, "scenarioId"),
            polarity,
            executed,
            absent,
            witnesses,
            ParseArtifact(root));
        receipt.BindBinaryHashes(
            RequiredString(root, "dllHash"),
            RequiredString(root, "pdbHash"));
        return receipt;
    }

    internal void BindBinaryHashes(string dllHash, string pdbHash)
    {
        if (DllHash.Length != 0 || PdbHash.Length != 0)
            throw new InvalidOperationException(
                "execution receipt binary hashes are already bound");
        RequireDigest(dllHash, false, "dllHash");
        RequireDigest(pdbHash, false, "pdbHash");
        DllHash = dllHash;
        PdbHash = pdbHash;
    }

    private static IReadOnlyList<string> RequiredEntryIds(
        JsonElement root,
        string property,
        bool allowEmpty)
    {
        if (!root.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
            throw new FormatException($"execution receipt requires {property}");
        string[] ids = value.EnumerateArray().Select(RequiredArrayString).ToArray();
        if ((!allowEmpty && ids.Length == 0) ||
            ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new FormatException($"{property} has invalid IDs");
        return ids;
    }

    private static IReadOnlyList<SyncRegistrationWitness>
        RequiredRegistrationWitnesses(JsonElement root)
    {
        if (!root.TryGetProperty("registrationWitnesses", out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
            throw new FormatException(
                "execution receipt requires registrationWitnesses");
        var fields = new HashSet<string>(
            ["entryId", "registrationEntryId"], StringComparer.Ordinal);
        SyncRegistrationWitness[] witnesses = value.EnumerateArray().Select(item =>
        {
            ExactObject(item, fields, "registration witness");
            return new SyncRegistrationWitness(
                RequiredNonEmptyString(item, "entryId"),
                RequiredNonEmptyString(item, "registrationEntryId"));
        }).ToArray();
        if (witnesses.Distinct().Count() != witnesses.Length)
            throw new FormatException("registrationWitnesses must be unique");
        return witnesses;
    }

    private static void ValidateProvenance(
        SyncExecutionPolarity polarity,
        IReadOnlyList<string> executed,
        IReadOnlyList<string> absent,
        IReadOnlyList<SyncRegistrationWitness> witnesses)
    {
        if (polarity == SyncExecutionPolarity.Positive &&
            (absent.Count != 0 || witnesses.Count != 0))
            throw new FormatException("positive receipt cannot declare absence");
        if (polarity == SyncExecutionPolarity.Negative &&
            (absent.Count == 0 || witnesses.Count == 0))
            throw new FormatException("negative receipt requires absence provenance");
        if (absent.Intersect(executed, StringComparer.Ordinal).Any())
            throw new FormatException("absent and executed IDs overlap");
        if (!absent.ToHashSet(StringComparer.Ordinal).SetEquals(
                witnesses.Select(item => item.EntryId)))
            throw new FormatException("witnesses do not exactly cover absent IDs");
        if (witnesses.Any(item => !executed.Contains(
                item.RegistrationEntryId, StringComparer.Ordinal)))
            throw new FormatException("witness registration was not executed");
    }

    private static SyncExecutionArtifact? ParseArtifact(JsonElement root)
    {
        if (!root.TryGetProperty("artifact", out JsonElement value))
            throw new FormatException("execution receipt requires artifact");
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        ExactObject(value, new HashSet<string>(["kind", "path", "sha256"]), "artifact");
        string sha256 = RequiredString(value, "sha256");
        RequireDigest(sha256, true, "artifact sha256");
        return new SyncExecutionArtifact(
            RequiredNonEmptyString(value, "kind"),
            RequiredNonEmptyString(value, "path"),
            sha256);
    }

    private static void ExactObject(
        JsonElement value,
        IReadOnlySet<string> fields,
        string subject)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new FormatException($"{subject} fields are not exact");
        string[] names = value.EnumerateObject().Select(item => item.Name).ToArray();
        if (names.Length != fields.Count ||
            !names.ToHashSet(StringComparer.Ordinal).SetEquals(fields))
            throw new FormatException($"{subject} fields are not exact");
    }

    private static int RequiredInt(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value) ||
            !value.TryGetInt32(out int result))
            throw new FormatException($"execution receipt requires integer {property}");
        return result;
    }

    private static string RequiredString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
            throw new FormatException($"execution receipt requires string {property}");
        return value.GetString()!;
    }

    private static string RequiredNonEmptyString(JsonElement root, string property)
    {
        string value = RequiredString(root, property);
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException($"execution receipt requires nonempty {property}");
        return value;
    }

    private static string? OptionalString(JsonElement root, string property)
    {
        JsonElement value = root.GetProperty(property);
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new FormatException($"execution receipt has invalid {property}");
        return value.GetString();
    }

    private static string RequiredArrayString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new FormatException("executedEntryIds values must be nonempty strings");
        return value.GetString()!;
    }

    private static void RequireDigest(string value, bool prefixed, string subject)
    {
        string pattern = prefixed ? "^sha256:[0-9a-f]{64}$" : "^[0-9a-f]{64}$";
        if (!Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant))
            throw new FormatException($"execution receipt has invalid {subject}");
    }
}

internal static class SyncExecutionText
{
    public static string Tier(SyncExecutionTier tier) => tier switch
    {
        SyncExecutionTier.Headless => "headless",
        SyncExecutionTier.Ingame => "ingame",
        SyncExecutionTier.Python => "python",
        SyncExecutionTier.Real => "real",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null)
    };

    public static SyncExecutionTier ParseTier(string value) => value switch
    {
        "headless" => SyncExecutionTier.Headless,
        "ingame" => SyncExecutionTier.Ingame,
        "python" => SyncExecutionTier.Python,
        "real" => SyncExecutionTier.Real,
        _ => throw new FormatException($"invalid execution tier {value}")
    };

    public static SyncExecutionPolarity ParsePolarity(string value) => value switch
    {
        "positive" => SyncExecutionPolarity.Positive,
        "negative" => SyncExecutionPolarity.Negative,
        _ => throw new FormatException($"invalid execution polarity {value}")
    };
}
