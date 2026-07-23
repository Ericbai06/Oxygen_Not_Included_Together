using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class SyncCoverageExecutionReceiptTests
{
    private const string InventoryDigest =
        "1111111111111111111111111111111111111111111111111111111111111111";
    private const string CoverageDigest =
        "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string ArtifactDigest =
        "sha256:3333333333333333333333333333333333333333333333333333333333333333";
    private const string EntryId = "sync:receipt-contract-entry";
    private const string OtherEntryId = "sync:other-entry";
    private const string HeadlessTestId = "headless:packet-roundtrip";
    private const string IngameTestId = "ingame:door-harmony";
    private const string PythonTestId = "python:evidence-parser";
    private const string RealTestId = "real:door-dispatch";
    private const string NegativeTestId = "headless:disabled-hook-negative";
    private const string ScenarioId = "door";

    public static void RegistryRequiresStableIdsForAllFourTiers()
    {
        SyncTestRegistry registry = Registry();

        Equal(4, registry.Tiers.Count);
        Equal(SyncExecutionTier.Headless, registry.Get(HeadlessTestId).Tier);
        Equal(SyncExecutionTier.Ingame, registry.Get(IngameTestId).Tier);
        Equal(SyncExecutionTier.Python, registry.Get(PythonTestId).Tier);
        Equal(SyncExecutionTier.Real, registry.Get(RealTestId).Tier);
        Throws<FormatException>(() => SyncTestRegistry.Create([
            new SyncTestDefinition(HeadlessTestId, SyncExecutionTier.Real, null),
        ]));
        Throws<FormatException>(() => SyncTestRegistry.Create([
            new SyncTestDefinition(HeadlessTestId, SyncExecutionTier.Headless, null),
            new SyncTestDefinition(HeadlessTestId, SyncExecutionTier.Headless, null),
        ]));
    }

    public static void RegistryRejectsMissingExecutionTier()
    {
        Throws<FormatException>(() => SyncTestRegistry.Create([
            new SyncTestDefinition(HeadlessTestId, SyncExecutionTier.Headless, null),
            new SyncTestDefinition(IngameTestId, SyncExecutionTier.Ingame, ScenarioId),
            new SyncTestDefinition(RealTestId, SyncExecutionTier.Real, ScenarioId),
        ]));
    }

    public static void ReceiptJsonUsesExactFailClosedSchema()
    {
        SyncExecutionReceipt receipt = HeadlessReceipt();

        Equal(1, receipt.SchemaVersion);
        Equal("run-receipt-001", receipt.RunId);
        Equal(InventoryDigest, receipt.InventoryDigest);
        Equal(CoverageDigest, receipt.CoverageDigest);
        Equal(HeadlessTestId, receipt.TestId);
        Equal(SyncExecutionTier.Headless, receipt.Tier);
        Equal(SyncExecutionPolarity.Positive, receipt.Polarity);
        EqualSequence([EntryId], receipt.ExecutedEntryIds);
        Throws<FormatException>(() => SyncExecutionReceipt.Parse(ReceiptJson(
            new ReceiptJsonFixture(),
            new Dictionary<string, object?>
            {
                ["classNames"] = new[] { "PacketSender" },
            })));
        Throws<FormatException>(() => SyncExecutionReceipt.Parse(ReceiptJson(
            new ReceiptJsonFixture { ExecutedEntryIds = [] })));
    }

    public static void GateRequiresMappedEntryInActualReceipt()
    {
        SyncCoverageManifest manifest = ActiveManifest([HeadlessTestId], [], null);
        SyncExecutionReceipt wrongEntry = Bind(
            manifest, HeadlessReceipt(entryId: OtherEntryId));

        IReadOnlyList<SurfaceError> errors = Validate(manifest, [wrongEntry]);

        HasError(errors, "execution_unknown_entry_receipt", OtherEntryId);
        HasError(errors, "execution_missing_entry_receipt", EntryId);
    }

    public static void GateRejectsUnknownAndDuplicateReceipts()
    {
        SyncCoverageManifest manifest = ActiveManifest([HeadlessTestId], [], null);
        SyncExecutionReceipt valid = Bind(manifest, HeadlessReceipt());
        SyncExecutionReceipt unknown = Bind(
            manifest, HeadlessReceipt("headless:not-registered"));

        IReadOnlyList<SurfaceError> errors = Validate(manifest, [valid, valid, unknown]);

        HasError(errors, "execution_duplicate_receipt", HeadlessTestId);
        HasError(errors, "execution_unknown_test_receipt", "headless:not-registered");
    }

    public static void GateRejectsDigestScenarioAndTierDrift()
    {
        SyncCoverageManifest manifest = ActiveManifest([IngameTestId], [ScenarioId], null);
        SyncExecutionReceipt wrongDigest = IngameReceipt(
            inventoryDigest: new string('9', 64));
        SyncExecutionReceipt wrongScenario = IngameReceipt("toggle");
        SyncExecutionReceipt wrongTier = IngameReceipt(tier: SyncExecutionTier.Real);

        IReadOnlyList<SurfaceError> errors = Validate(manifest, [
            Bind(manifest, wrongDigest),
            Bind(manifest, wrongScenario),
            Bind(manifest, wrongTier),
        ]);

        HasError(errors, "execution_inventory_digest_mismatch", IngameTestId);
        HasError(errors, "execution_scenario_mismatch", IngameTestId);
        HasError(errors, "execution_tier_mismatch", IngameTestId);
    }

    public static void UnityOnlyMappingRequiresRuntimeArtifact()
    {
        SyncCoverageManifest manifest = ActiveManifest(
            [IngameTestId], [ScenarioId], "requires ONI GameObject lifecycle");
        SyncExecutionReceipt missing = IngameReceiptWithoutArtifact();
        SyncExecutionReceipt wrongKind = IngameReceiptWithWrongArtifact();

        IReadOnlyList<SurfaceError> errors = Validate(manifest, [
            Bind(manifest, missing), Bind(manifest, wrongKind),
        ]);

        HasError(errors, "unity_only_missing_runtime_artifact", EntryId);
    }

    public static void RegisteredDisabledRequiresNegativeReceipt()
    {
        SyncCoverageManifest manifest = DisabledManifest();
        SyncExecutionReceipt positive = Bind(
            manifest, HeadlessReceipt(NegativeTestId));

        IReadOnlyList<SurfaceError> errors = Validate(
            manifest, [positive], SyncEntryStatus.RegisteredDisabled);

        HasError(errors, "registered_disabled_missing_negative_receipt", EntryId);
    }

    public static void CompleteExecutionReceiptsSatisfyGate()
    {
        Equal(0, SyncCoverageProvenReceiptFixture.ValidateActive().Count);
        Equal(0, SyncCoverageProvenReceiptFixture.ValidateRegisteredDisabled().Count);
    }

    private static IReadOnlyList<SurfaceError> Validate(
        SyncCoverageManifest manifest,
        IReadOnlyList<SyncExecutionReceipt> receipts,
        SyncEntryStatus status = SyncEntryStatus.Active)
    {
        return SyncCoverageExecutionValidator.Validate(new SyncCoverageExecutionInput
        {
            Catalog = Catalog(status),
            Manifest = manifest,
            Registry = Registry(includeNegative: status == SyncEntryStatus.RegisteredDisabled),
            Receipts = receipts,
            Envelope = new SyncExecutionEnvelope
            {
                RunId = "run-receipt-001",
                InventoryDigest = InventoryDigest,
                CoverageDigest = manifest.CoverageDigest!,
            },
        });
    }

    private static SyncTestRegistry Registry(bool includeNegative = false)
    {
        var definitions = new List<SyncTestDefinition>
        {
            new(HeadlessTestId, SyncExecutionTier.Headless, null),
            new(IngameTestId, SyncExecutionTier.Ingame, ScenarioId),
            new(PythonTestId, SyncExecutionTier.Python, null),
            new(RealTestId, SyncExecutionTier.Real, ScenarioId),
        };
        if (includeNegative)
        {
            definitions.Add(new SyncTestDefinition(
                NegativeTestId, SyncExecutionTier.Headless, null));
        }
        return SyncTestRegistry.Create(definitions);
    }

    private static SyncCatalogScan Catalog(SyncEntryStatus status)
    {
        return new SyncCatalogScan([
            new SyncEntry(
                EntryId,
                SyncEntryKind.HarmonyPatch,
                "Fixture.DoorPatch.Postfix()",
                "Door.SetOpen(bool)",
                "HarmonyPatch -> Postfix",
                [new SyncBuildVariant(
                    "Debug", "OS_MAC", new HashSet<string>(["DEBUG", "OS_MAC"]))],
                status),
        ], []);
    }

    private static SyncCoverageManifest ActiveManifest(
        IReadOnlyList<string> testIds,
        IReadOnlyList<string> scenarioIds,
        string? headlessUnsupportedReason = null)
    {
        return Manifest(new ManifestFixture
        {
            TestIds = testIds,
            ScenarioIds = scenarioIds,
            HeadlessUnsupportedReason = headlessUnsupportedReason,
        });
    }

    private static SyncCoverageManifest DisabledManifest()
    {
        return Manifest(new ManifestFixture
        {
            NegativeTestIds = [NegativeTestId],
            Status = SyncEntryStatus.RegisteredDisabled,
        });
    }

    private static SyncCoverageManifest Manifest(ManifestFixture fixture)
    {
        var row = new Dictionary<string, object?>
        {
            ["id"] = EntryId,
            ["domain"] = "door",
            ["testIds"] = fixture.TestIds,
            ["negativeTestIds"] = fixture.NegativeTestIds,
            ["scenarioIds"] = fixture.ScenarioIds,
            ["variants"] = new[] { "Debug/OS_MAC" },
            ["status"] = fixture.Status.ToString(),
        };
        if (fixture.HeadlessUnsupportedReason is not null)
            row["headlessUnsupportedReason"] = fixture.HeadlessUnsupportedReason;
        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            inventoryDigest = InventoryDigest,
            entries = new[] { row },
        });
        return SyncCoverageManifest.Parse(json);
    }

    private static SyncExecutionReceipt HeadlessReceipt(
        string testId = HeadlessTestId,
        SyncExecutionPolarity polarity = SyncExecutionPolarity.Positive,
        string entryId = EntryId)
    {
        return SyncExecutionReceipt.Parse(ReceiptJson(new ReceiptJsonFixture
        {
            TestId = testId,
            Polarity = polarity == SyncExecutionPolarity.Positive
                ? "positive"
                : "negative",
            ExecutedEntryIds = [entryId],
        }));
    }

    private static SyncExecutionReceipt Bind(
        SyncCoverageManifest manifest,
        SyncExecutionReceipt receipt)
    {
        return receipt with { CoverageDigest = manifest.CoverageDigest! };
    }

    private static SyncExecutionReceipt IngameReceipt(
        string scenarioId = ScenarioId,
        SyncExecutionTier tier = SyncExecutionTier.Ingame,
        string inventoryDigest = InventoryDigest)
    {
        return SyncExecutionReceipt.Parse(ReceiptJson(new ReceiptJsonFixture
        {
            TestId = IngameTestId,
            Tier = TierText(tier),
            ScenarioId = scenarioId,
            InventoryDigest = inventoryDigest,
            ArtifactKind = ArtifactKind(tier),
        }));
    }

    private static SyncExecutionReceipt IngameReceiptWithoutArtifact()
    {
        return SyncExecutionReceipt.Parse(ReceiptJson(new ReceiptJsonFixture
        {
            TestId = IngameTestId,
            Tier = "ingame",
            ScenarioId = ScenarioId,
            ArtifactKind = null,
        }));
    }

    private static SyncExecutionReceipt IngameReceiptWithWrongArtifact()
    {
        return SyncExecutionReceipt.Parse(ReceiptJson(new ReceiptJsonFixture
        {
            TestId = IngameTestId,
            Tier = "ingame",
            ScenarioId = ScenarioId,
            ArtifactKind = "headless-log",
        }));
    }

    private static string ReceiptJson(
        ReceiptJsonFixture fixture,
        IReadOnlyDictionary<string, object?>? extra = null)
    {
        var values = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["runId"] = "run-receipt-001",
            ["inventoryDigest"] = fixture.InventoryDigest,
            ["coverageDigest"] = CoverageDigest,
            ["dllHash"] = new string('d', 64),
            ["pdbHash"] = new string('e', 64),
            ["testId"] = fixture.TestId,
            ["tier"] = fixture.Tier,
            ["scenarioId"] = fixture.ScenarioId,
            ["polarity"] = fixture.Polarity,
            ["executedEntryIds"] = fixture.ExecutedEntryIds,
            ["absentEntryIds"] = fixture.AbsentEntryIds,
            ["registrationWitnesses"] = fixture.RegistrationWitnesses,
            ["artifact"] = fixture.ArtifactKind is null
                ? null
                : new Dictionary<string, object?>
                {
                    ["kind"] = fixture.ArtifactKind,
                    ["path"] = "artifacts/execution.log",
                    ["sha256"] = ArtifactDigest,
                },
        };
        if (extra is not null)
        {
            foreach ((string key, object? value) in extra)
                values[key] = value;
        }
        return JsonSerializer.Serialize(values);
    }

    private static string TierText(SyncExecutionTier tier)
    {
        return tier switch
        {
            SyncExecutionTier.Headless => "headless",
            SyncExecutionTier.Ingame => "ingame",
            SyncExecutionTier.Python => "python",
            SyncExecutionTier.Real => "real",
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null),
        };
    }

    private static string ArtifactKind(SyncExecutionTier tier)
    {
        return tier switch
        {
            SyncExecutionTier.Headless => "headless-log",
            SyncExecutionTier.Ingame => "ingame-result",
            SyncExecutionTier.Python => "python-log",
            SyncExecutionTier.Real => "real-run",
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null),
        };
    }

    private sealed class ManifestFixture
    {
        public IReadOnlyList<string> TestIds { get; init; } = [];
        public IReadOnlyList<string> NegativeTestIds { get; init; } = [];
        public IReadOnlyList<string> ScenarioIds { get; init; } = [];
        public SyncEntryStatus Status { get; init; } = SyncEntryStatus.Active;
        public string? HeadlessUnsupportedReason { get; init; }
    }

    private sealed class ReceiptJsonFixture
    {
        public string TestId { get; init; } = HeadlessTestId;
        public string Tier { get; init; } = "headless";
        public string? ScenarioId { get; init; }
        public string Polarity { get; init; } = "positive";
        public IReadOnlyList<string> ExecutedEntryIds { get; init; } = [EntryId];
        public IReadOnlyList<string> AbsentEntryIds { get; init; } = [];
        public IReadOnlyList<object> RegistrationWitnesses { get; init; } = [];
        public string? ArtifactKind { get; init; } = "headless-log";
        public string InventoryDigest { get; init; } =
            SyncCoverageExecutionReceiptTests.InventoryDigest;
    }

    private static void HasError(
        IEnumerable<SurfaceError> errors,
        string code,
        string subject)
    {
        if (!errors.Any(error => error.Code == code && error.Subject == subject))
        {
            throw new InvalidOperationException(
                $"expected {code}:{subject}, got {string.Join(",", errors)}");
        }
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"expected {typeof(T).Name}");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"expected {expected}, actual {actual}");
    }

    private static void EqualSequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"expected [{string.Join(",", expected)}], actual [{string.Join(",", actual)}]");
        }
    }
}
