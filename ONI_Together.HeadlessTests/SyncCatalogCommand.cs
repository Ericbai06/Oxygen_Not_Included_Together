using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal sealed class SyncCatalogCommandOptions
{
    public string ProjectPath { get; init; } = "";
    public IReadOnlyList<SyncBuildVariant> Variants { get; init; } =
        Array.Empty<SyncBuildVariant>();
    public string InventoryPath { get; init; } = "";
    public string? CoveragePath { get; init; }
    public IReadOnlyDictionary<string, string> GlobalProperties { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlySet<string> KnownTestIds { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> KnownScenarioIds { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
}

internal sealed record SyncCatalogCommandResult(
    SyncCatalogScan Catalog,
    string InventoryDigest,
    IReadOnlyList<SurfaceError> CoverageErrors);

internal static class SyncCatalogCommand
{
    public static SyncCatalogCommandResult Run(SyncCatalogCommandOptions options)
    {
        Validate(options);
        IReadOnlyList<SyncVariantInput> inputs = SyncMsBuildProjectLoader.Load(
            options.ProjectPath, options.Variants, options.GlobalProperties);
        SyncCatalogScan catalog = SyncSurfaceScanner.ScanCatalogVariants(inputs);
        ThrowForCatalogErrors(catalog);
        string inventory = SyncInventoryJson.Serialize(catalog);
        string digest = ReadDigest(inventory);
        IReadOnlyList<SurfaceError> coverageErrors = ValidateCoverage(
            options, catalog, digest);
        WriteAtomically(options.InventoryPath, inventory);
        return new SyncCatalogCommandResult(catalog, digest, coverageErrors);
    }

    private static void Validate(SyncCatalogCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ProjectPath))
            throw new ArgumentException("project path is required", nameof(options));
        if (string.IsNullOrWhiteSpace(options.InventoryPath))
            throw new ArgumentException("inventory path is required", nameof(options));
        ArgumentNullException.ThrowIfNull(options.Variants);
        ArgumentNullException.ThrowIfNull(options.GlobalProperties);
        ArgumentNullException.ThrowIfNull(options.KnownTestIds);
        ArgumentNullException.ThrowIfNull(options.KnownScenarioIds);
    }

    private static void ThrowForCatalogErrors(SyncCatalogScan catalog)
    {
        if (catalog.Errors.Count == 0)
            return;
        string summary = string.Join(Environment.NewLine,
            catalog.Errors.Select(error => $"{error.Code}: {error.Subject}"));
        throw new InvalidOperationException($"catalog scan failed:{Environment.NewLine}{summary}");
    }

    private static IReadOnlyList<SurfaceError> ValidateCoverage(
        SyncCatalogCommandOptions options,
        SyncCatalogScan catalog,
        string inventoryDigest)
    {
        if (options.CoveragePath is null)
            return Array.Empty<SurfaceError>();
        string coveragePath = Path.GetFullPath(options.CoveragePath);
        string json = File.ReadAllText(coveragePath);
        SyncCoverageManifest manifest = SyncCoverageManifest.Parse(json);
        var errors = SyncCoverageValidator.Validate(catalog, manifest,
            options.KnownTestIds, options.KnownScenarioIds).ToList();
        if (!StringComparer.Ordinal.Equals(
                manifest.InventoryDigest, inventoryDigest))
            errors.Add(new SurfaceError(
                "coverage_inventory_digest_mismatch", coveragePath));
        return errors.Distinct().ToArray();
    }

    private static string ReadDigest(string inventory)
    {
        using JsonDocument document = JsonDocument.Parse(inventory);
        if (!document.RootElement.TryGetProperty("digest", out JsonElement digest) ||
            digest.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(digest.GetString()))
            throw new FormatException("inventory JSON requires a digest");
        return digest.GetString()!;
    }

    private static void WriteAtomically(string path, string content)
    {
        string destination = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException(
                $"inventory directory does not exist: {directory}");
        string temporary = Path.Combine(directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
