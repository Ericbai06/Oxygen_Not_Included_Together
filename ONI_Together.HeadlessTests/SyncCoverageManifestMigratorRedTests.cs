using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ONI_Together.HeadlessTests;

internal static class SyncCoverageManifestMigratorRedTests
{
    public static void MigratorBuildsFailClosedCurrentCatalogWorklist()
    {
        ISyncCoverageManifestMigrator migrator =
            SyncCoverageManifestMigratorLoader.Load();
        ActualFixture actual = LoadActualFixture();
        InputDigestShapeContract(migrator, actual);
        Console.WriteLine("MIGRATOR_COVERAGE_DIGEST " +
            $"expected={actual.Receipt.CoverageDigest}");
        WorklistContract(migrator, actual);
        CoverageDigestShapeContract(migrator, actual);
        ReceiptMutationContract(migrator, actual);
        RegisteredDisabledContract(migrator);
        CliContract(migrator, actual);
    }

    private static void InputDigestShapeContract(
        ISyncCoverageManifestMigrator migrator,
        ActualFixture fixture)
    {
        SyncCoverageMigrationInput valid = Input(
            fixture, StaleCoverage(), [fixture.Receipt]);
        InvalidUnprefixedDigestShapes(fixture.InventoryDigest).ToList()
            .ForEach(value => RejectExact(migrator, valid with {
                InventoryDigest = value
            }, "inventory hash is invalid"));
        InvalidUnprefixedDigestShapes(fixture.DllHash).ToList()
            .ForEach(value => RejectExact(migrator, valid with {
                DllHash = value
            }, "DLL hash is invalid"));
        InvalidUnprefixedDigestShapes(fixture.PdbHash).ToList()
            .ForEach(value => RejectExact(migrator, valid with {
                PdbHash = value
            }, "PDB hash is invalid"));
    }

    private static void CoverageDigestShapeContract(
        ISyncCoverageManifestMigrator migrator,
        ActualFixture fixture)
    {
        SyncCoverageMigrationInput valid = Input(
            fixture, StaleCoverage(), [fixture.Receipt]);
        foreach (string digest in InvalidCoverageDigestShapes(
                     fixture.Receipt.CoverageDigest))
        {
            RejectExact(migrator, valid with {
                Receipts = [fixture.Receipt with { CoverageDigest = digest }]
            }, "receipt coverage hash is invalid");
        }
    }

    private static void WorklistContract(
        ISyncCoverageManifestMigrator migrator,
        ActualFixture fixture)
    {
        SyncEntry mapped = fixture.Catalog.Entries.Single(entry =>
            entry.Id == fixture.Receipt.ExecutedEntryIds[0]);
        SyncEntry preserved = fixture.Catalog.Entries.First(entry =>
            entry.Id != mapped.Id);
        SyncCoverageManifest stale = StaleCoverage(
            Unmapped(mapped, "cursor"),
            new SyncCoverageEntry(
                preserved.Id, "legacy-domain", [fixture.TestId], [], [],
                ["stale/variant"], preserved.Status, "legacy-runtime-reason"),
            Orphan());
        SyncCoverageMigrationInput input = Input(fixture, stale, [fixture.Receipt]);

        SyncCoverageMigrationResult first = migrator.Migrate(input);
        SyncCoverageMigrationResult second = migrator.Migrate(input);
        Equal(first.CoverageJson, second.CoverageJson);
        Equal(1, first.RemovedOrphanCount);
        Equal(fixture.Receipt.ExecutedEntryIds.Count,
            first.AddedPositiveMappingCount);
        SyncCoverageManifest output = SyncCoverageManifest.Parse(first.CoverageJson);
        Equal(fixture.InventoryDigest, output.InventoryDigest);
        EqualSet(fixture.Catalog.Entries.Select(entry => entry.Id),
            output.Entries.Select(entry => entry.Id));
        Equal(output.Entries.Count, output.Entries
            .Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count());

        SyncCoverageEntry mappedOutput = output.Entries.Single(entry =>
            entry.Id == mapped.Id);
        True(mappedOutput.TestIds.Contains(fixture.TestId, StringComparer.Ordinal),
            "real observed UnitTest receipt did not add its mapping");
        SyncCoverageEntry preservedOutput = output.Entries.Single(entry =>
            entry.Id == preserved.Id);
        Equal("legacy-domain", preservedOutput.Domain);
        EqualSequence([fixture.TestId], preservedOutput.TestIds);
        EqualSequence([], preservedOutput.NegativeTestIds);
        EqualSequence([], preservedOutput.ScenarioIds);
        Equal("legacy-runtime-reason",
            preservedOutput.HeadlessUnsupportedReason);
        Equal(preserved.Status, preservedOutput.Status);
        EqualSequence(preserved.Variants.Select(item => item.Key)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            preservedOutput.Variants);

        foreach (SyncCoverageEntry added in output.Entries.Where(entry =>
                     stale.Entries.All(old => old.Id != entry.Id)))
        {
            Equal("unassigned", added.Domain);
            bool executed = fixture.Receipt.ExecutedEntryIds.Contains(
                added.Id, StringComparer.Ordinal);
            bool absent = fixture.Receipt.AbsentEntryIds.Contains(
                added.Id, StringComparer.Ordinal);
            EqualSequence(
                executed ? [fixture.TestId] : [],
                added.TestIds);
            EqualSequence(
                absent ? [fixture.TestId] : [],
                added.NegativeTestIds);
            Equal(executed || absent ? 1 : 0,
                added.TestIds.Count + added.NegativeTestIds.Count);
            Equal(0, added.ScenarioIds.Count);
        }
        IReadOnlyList<SurfaceError> remaining = SyncCoverageValidator.Validate(
            fixture.Catalog, output, fixture.Registry.Ids,
            fixture.Registry.ScenarioIds);
        True(remaining.Any(error =>
                error.Code == "manifest_missing_execution_test"),
            "migration incorrectly treated the remaining worklist as covered");
        True(first.RemainingUnmappedCount > 0,
            "migration reported no remaining unmapped entries");
    }

    private static void ReceiptMutationContract(
        ISyncCoverageManifestMigrator migrator,
        ActualFixture fixture)
    {
        SyncEntry mapped = fixture.Catalog.Entries.Single(entry =>
            entry.Id == fixture.Receipt.ExecutedEntryIds[0]);
        SyncCoverageManifest stale = StaleCoverage(Unmapped(mapped, "cursor"));
        SyncCoverageMigrationInput valid = Input(fixture, stale, [fixture.Receipt]);

        Reject(migrator, valid with {
            StaleCoverage = StaleCoverage(new SyncCoverageEntry(
                mapped.Id, "cursor", [fixture.TestId], [], [],
                mapped.Variants.Select(item => item.Key).ToArray(),
                DifferentStatus(mapped.Status), null))
        }, "mapped status drift was silently rewritten");
        Reject(migrator, valid with {
            Receipts = [fixture.Receipt, fixture.Receipt]
        }, "duplicate receipt was accepted");
        Reject(migrator, valid with {
            Receipts = [Clone(fixture.Receipt,
                testId: "headless:unit:unknown")]
        }, "unknown UnitTest receipt was accepted");
        Reject(migrator, valid with {
            Receipts = [Clone(fixture.Receipt,
                inventoryDigest: Changed(fixture.InventoryDigest))]
        }, "receipt inventory drift was accepted");
        Reject(migrator, valid with {
            Receipts = [Clone(fixture.Receipt,
                dllHash: Changed(fixture.DllHash))]
        }, "receipt DLL hash drift was accepted");
        Reject(migrator, valid with {
            Receipts = [Clone(fixture.Receipt,
                pdbHash: Changed(fixture.PdbHash))]
        }, "receipt PDB hash drift was accepted");
        Reject(migrator, valid with {
            Receipts = [Clone(fixture.Receipt)]
        }, "manual copied receipt without observed origin was accepted");

        SyncCatalogScan syntheticCatalog = SyncExecutionProbeFixtureCatalog.Scan();
        SyncExecutionFixtureAssembly syntheticAssembly =
            SyncExecutionProbeFixtureCatalog.Compile();
        string syntheticDigest = InventoryDigest(syntheticCatalog);
        ISyncExecutionProbeSession synthetic = SyncExecutionProbeFactoryLoader.Load()
            .Start(Binding("headless:synthetic", syntheticDigest,
                    SyncExecutionPolarity.Positive),
                syntheticCatalog, syntheticAssembly);
        synthetic.RuntimeAssembly.GetType("PacketRuntime")!
            .GetMethod("RunFirstOnly")!.Invoke(null, null);
        SyncExecutionReceipt syntheticReceipt = synthetic.Complete();
        Reject(migrator, valid with { Receipts = [syntheticReceipt] },
            "synthetic receipt was accepted for the current catalog");
    }

    private static void RegisteredDisabledContract(
        ISyncCoverageManifestMigrator migrator)
    {
        SyncCatalogScan catalog = SyncExecutionProbeFixtureCatalog.Scan();
        string digest = InventoryDigest(catalog);
        ISyncExecutionProbeSession session = SyncExecutionProbeFactoryLoader.Load()
            .Start(Binding("headless:registered-disabled", digest,
                    SyncExecutionPolarity.Negative),
                catalog, SyncExecutionProbeFixtureCatalog.Compile());
        session.RuntimeAssembly.GetType("DisabledPacketRuntime")!
            .GetMethod("Register")!.Invoke(null, null);
        SyncExecutionReceipt receipt = session.Complete();
        SyncEntry disabled = catalog.Entries.Single(entry =>
            entry.Status == SyncEntryStatus.RegisteredDisabled);
        SyncTestRegistry registry = Registry(receipt.TestId);
        SyncCoverageMigrationInput input = new(
            catalog, digest, StaleCoverage(Unmapped(disabled, "disabled")),
            registry, [receipt], receipt.DllHash, receipt.PdbHash);

        SyncCoverageMigrationResult result = migrator.Migrate(input);
        SyncCoverageEntry output = SyncCoverageManifest.Parse(
            result.CoverageJson).Entries.Single(entry => entry.Id == disabled.Id);
        EqualSequence([receipt.TestId], output.NegativeTestIds);
        Equal(1, result.AddedNegativeMappingCount);
        Reject(migrator, input with {
            Receipts = [Clone(receipt)]
        }, "unproven registered-disabled absence was accepted");
    }

    private static void CliContract(
        ISyncCoverageManifestMigrator migrator,
        ActualFixture fixture)
    {
        string root = FindRepositoryRoot();
        string gameLibs = Environment.GetEnvironmentVariable("ONI_GAME_LIBS")!;
        string temporary = Path.Combine(Path.GetTempPath(),
            "oni-coverage-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string stalePath = Path.Combine(temporary, "stale.json");
            string outputA = Path.Combine(temporary, "a.json");
            string outputB = Path.Combine(temporary, "b.json");
            File.WriteAllText(stalePath, CoverageJson(StaleCoverage(
                Orphan())));
            string originalHash = FileDigest(stalePath);
            string[] valid = [
                "coverage-migrate",
                "--project", Path.Combine(root, "ONI_Together", "ONI_Together.csproj"),
                "--game-libs", gameLibs,
                "--coverage", stalePath,
                "--output", outputA
            ];
            Equal(1, migrator.RunCli(
                ["coverage-migrate", "--unknown", "x"],
                TextWriter.Null, TextWriter.Null));
            Equal(1, migrator.RunCli([
                "coverage-migrate", "--project", "missing.csproj",
                "--game-libs", gameLibs, "--coverage", stalePath,
                "--output", outputA
            ], TextWriter.Null, TextWriter.Null));
            Equal(0, migrator.RunCli(valid,
                TextWriter.Null, TextWriter.Null));
            string[] second = valid.ToArray();
            second[^1] = outputB;
            Equal(0, migrator.RunCli(second,
                TextWriter.Null, TextWriter.Null));
            Equal(File.ReadAllText(outputA), File.ReadAllText(outputB));
            Equal(originalHash, FileDigest(stalePath));
            Equal(fixture.InventoryDigest,
                SyncCoverageManifest.Parse(File.ReadAllText(outputA))
                    .InventoryDigest);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static ActualFixture LoadActualFixture()
    {
        ActualDebugUnitTestBatchInput input =
            ActualDebugUnitTestBatchFixture.Load();
        IActualDebugUnitTestBatchRunner runner =
            ActualDebugUnitTestBatchRunnerLoader.Load();
        ActualDebugUnitTestBatchResult batch = runner.Run(input);
        runner.Validate(input, batch);
        SyncExecutionReceipt receipt = batch.Results
            .Select(result => result.Receipt)
            .FirstOrDefault(value => value is not null) ??
            throw new InvalidOperationException(
                "actual Debug batch produced no mapped receipt");
        return new ActualFixture(
            input.Catalog, input.InventoryDigest, input.DllHash, input.PdbHash,
            receipt, receipt.TestId, Registry(receipt.TestId));
    }

    private static SyncExecutionReceipt Clone(
        SyncExecutionReceipt source,
        string? testId = null,
        string? inventoryDigest = null,
        string? dllHash = null,
        string? pdbHash = null)
    {
        var clone = new SyncExecutionReceipt(
            source.SchemaVersion, source.RunId + "-copy",
            inventoryDigest ?? source.InventoryDigest, source.CoverageDigest,
            testId ?? source.TestId, source.Tier, source.ScenarioId,
            source.Polarity, source.ExecutedEntryIds, source.AbsentEntryIds,
            source.RegistrationWitnesses, source.Artifact);
        clone.BindBinaryHashes(
            dllHash ?? source.DllHash, pdbHash ?? source.PdbHash);
        return clone;
    }

    private static SyncCoverageMigrationInput Input(
        ActualFixture fixture,
        SyncCoverageManifest stale,
        IReadOnlyList<SyncExecutionReceipt> receipts) => new(
            fixture.Catalog, fixture.InventoryDigest, stale, fixture.Registry,
            receipts, fixture.DllHash, fixture.PdbHash);

    private static SyncCoverageManifest StaleCoverage(
        params SyncCoverageEntry[] entries) => new(
            new string('0', 64), null, entries);

    private static SyncCoverageEntry Unmapped(
        SyncEntry entry,
        string domain) => new(
            entry.Id, domain, [], [], [], ["stale/variant"],
            entry.Status, null);

    private static SyncCoverageEntry Orphan() => new(
        "sync:000000000000000000000000", "orphan", [], [], [],
        ["Debug/OS_MAC"], SyncEntryStatus.Active, null);

    private static SyncEntryStatus DifferentStatus(SyncEntryStatus current) =>
        Enum.GetValues<SyncEntryStatus>().First(status => status != current);

    private static SyncTestRegistry Registry(string headlessId) =>
        SyncTestRegistry.Create([
            new SyncTestDefinition(headlessId, SyncExecutionTier.Headless, null),
            new SyncTestDefinition("ingame:migration-placeholder",
                SyncExecutionTier.Ingame, null),
            new SyncTestDefinition("python:migration-placeholder",
                SyncExecutionTier.Python, null),
            new SyncTestDefinition("real:migration-placeholder",
                SyncExecutionTier.Real, null)
        ]);

    private static SyncExecutionProbeBinding Binding(
        string testId,
        string digest,
        SyncExecutionPolarity polarity) => new()
    {
        RunId = "coverage-migration-contract",
        TestId = testId,
        Tier = SyncExecutionTier.Headless,
        Polarity = polarity,
        InventoryDigest = digest,
        CoverageDigest = "sha256:" + new string('c', 64)
    };

    private static IEnumerable<string> InvalidUnprefixedDigestShapes(
        string valid)
    {
        yield return "";
        yield return "sha256:" + valid;
        yield return valid.ToUpperInvariant();
        yield return valid[..^1];
        yield return "g" + valid[1..];
    }

    private static IEnumerable<string> InvalidCoverageDigestShapes(
        string valid)
    {
        string hex = valid["sha256:".Length..];
        yield return hex;
        yield return "sha256:" + hex.ToUpperInvariant();
        yield return "sha512:" + hex;
        yield return "sha256:" + hex[..^1];
        yield return "sha256:g" + hex[1..];
    }

    private static string InventoryDigest(SyncCatalogScan catalog)
    {
        using JsonDocument json = JsonDocument.Parse(
            SyncInventoryJson.Serialize(catalog));
        return json.RootElement.GetProperty("digest").GetString()!;
    }

    private static string CoverageJson(SyncCoverageManifest manifest) =>
        JsonSerializer.Serialize(new
        {
            inventoryDigest = manifest.InventoryDigest,
            entries = manifest.Entries.Select(entry => new
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

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
             directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "Shared")) &&
                Directory.Exists(Path.Combine(directory.FullName, "ONI_Together")))
                return directory.FullName;
        throw new InvalidOperationException("repository root was not found");
    }

    private static string FileDigest(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();

    private static string Changed(string value) =>
        (value[0] == '0' ? "1" : "0") + value[1..];

    private static void Reject(
        ISyncCoverageManifestMigrator migrator,
        SyncCoverageMigrationInput input,
        string message)
    {
        try { migrator.Migrate(input); }
        catch (Exception) { return; }
        throw new InvalidOperationException(message);
    }

    private static void RejectExact(
        ISyncCoverageManifestMigrator migrator,
        SyncCoverageMigrationInput input,
        string expected)
    {
        try { migrator.Migrate(input); }
        catch (InvalidOperationException error)
            when (error.Message == expected) { return; }
        throw new InvalidOperationException(
            $"expected exact failure: {expected}");
    }

    private static void EqualSet<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual)
    {
        if (!expected.ToHashSet().SetEquals(actual))
            throw new InvalidOperationException("sets differ");
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

    private sealed record ActualFixture(
        SyncCatalogScan Catalog,
        string InventoryDigest,
        string DllHash,
        string PdbHash,
        SyncExecutionReceipt Receipt,
        string TestId,
        SyncTestRegistry Registry);
}
