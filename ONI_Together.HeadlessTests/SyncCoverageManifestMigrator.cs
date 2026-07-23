using System.Text.Json;
using System.Text.Json.Serialization;

namespace ONI_Together.HeadlessTests;

internal sealed class SyncCoverageManifestMigrator :
    ISyncCoverageManifestMigrator
{
    private static readonly SyncBuildVariant[] CatalogVariants =
    [
        Variant("Debug", "OS_MAC", "DEBUG", "OS_MAC"),
        Variant("Debug", "OS_WINDOWS", "DEBUG", "OS_WINDOWS"),
        Variant("Debug", "OS_LINUX", "DEBUG", "OS_LINUX"),
        Variant("Debug", "OS_FREEBSD", "DEBUG", "OS_FREEBSD"),
        Variant("Release", "OS_MAC", "OS_MAC"),
        Variant("Release", "OS_WINDOWS", "OS_WINDOWS"),
        Variant("Release", "OS_LINUX", "OS_LINUX"),
        Variant("Release", "OS_FREEBSD", "OS_FREEBSD")
    ];

    public SyncCoverageMigrationResult Migrate(
        SyncCoverageMigrationInput input)
    {
        ValidateInput(input);
        Dictionary<string, SyncEntry> catalog = input.Catalog.Entries
            .ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        Dictionary<string, SyncCoverageEntry> stale = input.StaleCoverage.Entries
            .ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        ValidateMappedStatus(stale, catalog);
        ValidateReceipts(input, catalog);

        int positive = 0;
        int negative = 0;
        var migrated = new List<SyncCoverageEntry>(catalog.Count);
        foreach (SyncEntry entry in catalog.Values.OrderBy(
                     item => item.Id, StringComparer.Ordinal))
        {
            SyncCoverageEntry row = stale.TryGetValue(
                entry.Id, out SyncCoverageEntry? existing)
                ? Refresh(existing, entry)
                : NewRow(entry);
            (row, int addedPositive, int addedNegative) =
                ApplyReceipts(input.Receipts, entry, row, catalog);
            positive += addedPositive;
            negative += addedNegative;
            migrated.Add(row);
        }

        int orphanCount = stale.Keys.Count(id => !catalog.ContainsKey(id));
        int remaining = migrated.Count(entry =>
            entry.TestIds.Count == 0 && entry.NegativeTestIds.Count == 0);
        return new SyncCoverageMigrationResult(
            Serialize(input.InventoryDigest, migrated),
            orphanCount, positive, negative, remaining);
    }

    public int RunCli(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr)
    {
        try
        {
            CliOptions options = ParseCli(args);
            ValidatePaths(options);
            SyncCatalogScan catalog = LoadCatalog(options);
            string inventoryDigest = ReadInventoryDigest(catalog);
            SyncCoverageManifest stale = SyncCoverageManifest.Parse(
                File.ReadAllText(options.CoveragePath));
            SyncCoverageMigrationResult result = Migrate(new(
                catalog, inventoryDigest, stale, PlaceholderRegistry(), [],
                new string('0', 64), new string('0', 64)));
            WriteAtomically(options.OutputPath, result.CoverageJson);
            stdout.WriteLine($"removedOrphans={result.RemovedOrphanCount}");
            stdout.WriteLine($"remainingUnmapped={result.RemainingUnmappedCount}");
            return 0;
        }
        catch (Exception error)
        {
            stderr.WriteLine($"coverage migration failed: {error.Message}");
            return 1;
        }
    }

    private static void ValidateInput(SyncCoverageMigrationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Catalog.Errors.Count != 0)
            throw new InvalidOperationException("catalog contains scan errors");
        RequireDigest(input.InventoryDigest, "inventory");
        RequireDigest(input.DllHash, "DLL");
        RequireDigest(input.PdbHash, "PDB");
        if (ReadInventoryDigest(input.Catalog) != input.InventoryDigest)
            throw new InvalidOperationException("catalog inventory digest drift");
        if (input.Catalog.Entries.GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
            throw new InvalidOperationException("catalog contains duplicate entry IDs");
        if (input.StaleCoverage.Entries
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
            throw new InvalidOperationException("coverage contains duplicate entry IDs");
    }

    private static void ValidateMappedStatus(
        IReadOnlyDictionary<string, SyncCoverageEntry> stale,
        IReadOnlyDictionary<string, SyncEntry> catalog)
    {
        foreach ((string id, SyncCoverageEntry row) in stale)
        {
            if (!catalog.TryGetValue(id, out SyncEntry? current))
                continue;
            bool mapped = row.TestIds.Count != 0 ||
                row.NegativeTestIds.Count != 0;
            if (mapped && row.Status != current.Status)
                throw new InvalidOperationException(
                    $"mapped coverage status drift: {id}");
        }
    }

    private static void ValidateReceipts(
        SyncCoverageMigrationInput input,
        IReadOnlyDictionary<string, SyncEntry> catalog)
    {
        if (input.Receipts.GroupBy(receipt => receipt.TestId,
                StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidOperationException("duplicate execution receipt");
        foreach (SyncExecutionReceipt receipt in input.Receipts)
        {
            if (!input.TestRegistry.TryGet(
                    receipt.TestId, out SyncTestDefinition? definition))
                throw new InvalidOperationException(
                    $"unknown execution test: {receipt.TestId}");
            if (definition.Tier != receipt.Tier ||
                definition.ScenarioId != receipt.ScenarioId)
                throw new InvalidOperationException(
                    $"execution test binding drift: {receipt.TestId}");
            if (receipt.InventoryDigest != input.InventoryDigest)
                throw new InvalidOperationException(
                    $"execution inventory digest drift: {receipt.TestId}");
            if (receipt.DllHash != input.DllHash ||
                receipt.PdbHash != input.PdbHash)
                throw new InvalidOperationException(
                    $"execution binary hash drift: {receipt.TestId}");
            RequireCoverageDigest(receipt.CoverageDigest);
            ValidateReceiptEntries(receipt, catalog);
        }
    }

    private static void ValidateReceiptEntries(
        SyncExecutionReceipt receipt,
        IReadOnlyDictionary<string, SyncEntry> catalog)
    {
        IEnumerable<string> claims = receipt.ExecutedEntryIds
            .Concat(receipt.AbsentEntryIds);
        if (claims.GroupBy(id => id, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
            throw new InvalidOperationException(
                $"duplicate execution entry claim: {receipt.TestId}");
        foreach (string id in receipt.ExecutedEntryIds)
        {
            if (!catalog.TryGetValue(id, out SyncEntry? entry))
                throw new InvalidOperationException($"unknown execution entry: {id}");
            if (!SyncExecutionProvenance.IsObserved(receipt, id) ||
                !SyncExecutionProvenance.MatchesOrigin(receipt, entry))
                throw new InvalidOperationException(
                    $"unproven execution entry: {id}");
        }
        foreach (string id in receipt.AbsentEntryIds)
        {
            if (!catalog.TryGetValue(id, out SyncEntry? entry))
                throw new InvalidOperationException($"unknown absent entry: {id}");
            if (!SyncExecutionProvenance.IsAbsent(receipt, id) ||
                !SyncExecutionProvenance.MatchesOrigin(receipt, entry))
                throw new InvalidOperationException(
                    $"unproven absent entry: {id}");
        }
    }

    private static (
        SyncCoverageEntry Row,
        int AddedPositive,
        int AddedNegative) ApplyReceipts(
        IReadOnlyList<SyncExecutionReceipt> receipts,
        SyncEntry entry,
        SyncCoverageEntry row,
        IReadOnlyDictionary<string, SyncEntry> catalog)
    {
        string[] positive = receipts
            .Where(receipt =>
                receipt.Polarity == SyncExecutionPolarity.Positive &&
                receipt.ExecutedEntryIds.Contains(entry.Id, StringComparer.Ordinal))
            .Select(receipt => receipt.TestId)
            .Where(id => !row.TestIds.Contains(id, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] negative = receipts
            .Where(receipt => IsValidNegativeReceipt(
                receipt, entry, catalog))
            .Select(receipt => receipt.TestId)
            .Where(id => !row.NegativeTestIds.Contains(id, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return (row with
        {
            TestIds = row.TestIds.Concat(positive).ToArray(),
            NegativeTestIds = row.NegativeTestIds.Concat(negative).ToArray()
        }, positive.Length, negative.Length);
    }

    private static bool IsValidNegativeReceipt(
        SyncExecutionReceipt receipt,
        SyncEntry entry,
        IReadOnlyDictionary<string, SyncEntry> catalog)
    {
        if (entry.Status != SyncEntryStatus.RegisteredDisabled ||
            receipt.Polarity != SyncExecutionPolarity.Negative ||
            !receipt.AbsentEntryIds.Contains(entry.Id, StringComparer.Ordinal))
            return false;
        return receipt.RegistrationWitnesses.Any(witness =>
            witness.EntryId == entry.Id &&
            receipt.ExecutedEntryIds.Contains(
                witness.RegistrationEntryId, StringComparer.Ordinal) &&
            catalog.TryGetValue(
                witness.RegistrationEntryId, out SyncEntry? registration) &&
            registration.Kind == SyncEntryKind.PacketRegistration &&
            Owner(registration.FullyQualifiedSymbol) ==
                Owner(entry.FullyQualifiedSymbol));
    }

    private static SyncCoverageEntry Refresh(
        SyncCoverageEntry stale,
        SyncEntry current) => stale with
    {
        Variants = CurrentVariants(current),
        Status = current.Status
    };

    private static SyncCoverageEntry NewRow(SyncEntry entry) => new(
        entry.Id, "unassigned", [], [], [], CurrentVariants(entry),
        entry.Status, null);

    private static string[] CurrentVariants(SyncEntry entry) =>
        entry.Variants.Select(variant => variant.Key)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string Serialize(
        string inventoryDigest,
        IReadOnlyList<SyncCoverageEntry> entries)
    {
        return JsonSerializer.Serialize(new
        {
            inventoryDigest,
            entries = entries.Select(entry => new
            {
                id = entry.Id,
                domain = entry.Domain,
                testIds = entry.TestIds,
                negativeTestIds = entry.NegativeTestIds,
                scenarioIds = entry.ScenarioIds,
                variants = entry.Variants,
                status = entry.Status.ToString(),
                headlessUnsupportedReason = entry.HeadlessUnsupportedReason
            })
        }, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }

    private static string Owner(string symbol)
    {
        if (!symbol.Contains('('))
            return symbol;
        string member = symbol[..symbol.IndexOf('(')];
        int dot = member.LastIndexOf('.');
        return dot < 0 ? member : member[..dot];
    }

    private static string ReadInventoryDigest(SyncCatalogScan catalog)
    {
        using JsonDocument inventory = JsonDocument.Parse(
            SyncInventoryJson.Serialize(catalog));
        return inventory.RootElement.GetProperty("digest").GetString()!;
    }

    private static void RequireDigest(string value, string subject)
    {
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')))
            throw new InvalidOperationException($"{subject} hash is invalid");
    }

    private static void RequireCoverageDigest(string value)
    {
        if (value.Length != 71 ||
            !value.StartsWith("sha256:", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "receipt coverage hash is invalid");
        RequireDigest(value[7..], "receipt coverage");
    }

    private static CliOptions ParseCli(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "coverage-migrate")
            throw new ArgumentException(
                "first argument must be coverage-migrate");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 1; index < args.Count; index += 2)
        {
            string option = args[index];
            if (!AllowedOptions.Contains(option))
                throw new ArgumentException($"unknown option: {option}");
            if (index + 1 >= args.Count ||
                string.IsNullOrWhiteSpace(args[index + 1]) ||
                args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"option requires a value: {option}");
            if (!values.TryAdd(option, args[index + 1]))
                throw new ArgumentException($"duplicate option: {option}");
        }
        return new CliOptions(
            Required(values, "--project"),
            Required(values, "--game-libs"),
            Required(values, "--coverage"),
            Required(values, "--output"));
    }

    private static void ValidatePaths(CliOptions options)
    {
        if (!File.Exists(options.ProjectPath))
            throw new FileNotFoundException(options.ProjectPath);
        if (!Directory.Exists(options.GameLibsFolder))
            throw new DirectoryNotFoundException(options.GameLibsFolder);
        if (!File.Exists(options.CoveragePath))
            throw new FileNotFoundException(options.CoveragePath);
        if (Path.GetFullPath(options.CoveragePath) ==
            Path.GetFullPath(options.OutputPath))
            throw new InvalidOperationException(
                "coverage output must differ from stale input");
        string? outputDirectory = Path.GetDirectoryName(
            Path.GetFullPath(options.OutputPath));
        if (outputDirectory is null || !Directory.Exists(outputDirectory))
            throw new DirectoryNotFoundException(outputDirectory);
    }

    private static SyncCatalogScan LoadCatalog(CliOptions options)
    {
        IReadOnlyList<SyncVariantInput> inputs = SyncMsBuildProjectLoader.Load(
            options.ProjectPath, CatalogVariants,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GameLibsFolder"] = options.GameLibsFolder
            });
        SyncCatalogScan catalog = SyncSurfaceScanner.ScanCatalogVariants(inputs);
        if (catalog.Errors.Count != 0)
            throw new InvalidOperationException("catalog scan failed");
        return catalog;
    }

    private static SyncTestRegistry PlaceholderRegistry() =>
        SyncTestRegistry.Create([
            new("headless:coverage-migrate", SyncExecutionTier.Headless, null),
            new("ingame:coverage-migrate", SyncExecutionTier.Ingame, null),
            new("python:coverage-migrate", SyncExecutionTier.Python, null),
            new("real:coverage-migrate", SyncExecutionTier.Real, null)
        ]);

    private static string Required(
        IReadOnlyDictionary<string, string> values,
        string option) => values.TryGetValue(option, out string? value)
            ? value
            : throw new ArgumentException($"missing option: {option}");

    private static void WriteAtomically(string path, string content)
    {
        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination)!;
        string staging = Path.Combine(directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(staging, content);
            File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }

    private static SyncBuildVariant Variant(
        string configuration,
        string platform,
        params string[] symbols) => new(
            configuration, platform,
            new HashSet<string>(symbols, StringComparer.Ordinal));

    private static readonly IReadOnlySet<string> AllowedOptions =
        new HashSet<string>([
            "--project", "--game-libs", "--coverage", "--output"
        ], StringComparer.Ordinal);

    private sealed record CliOptions(
        string ProjectPath,
        string GameLibsFolder,
        string CoveragePath,
        string OutputPath);
}
