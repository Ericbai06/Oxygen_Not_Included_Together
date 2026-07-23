using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class SyncCoverageAdversarialTests
{
    private const string InventoryDigest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CoverageDigest =
        "sha256:5032d24bd3f34b3f984737f4fbb99b8eb317923ae89d7b3f01e394e6413b22d7";
    private const string EntryId = "sync:canonical-entry";
    private const string TestId = "headless:canonical";
    private const string NegativeTestId = "headless:negative-only";

    public static void CoverageWithoutRootDigestUsesCanonicalParityHash()
    {
        SyncCoverageManifest compact = SyncCoverageManifest.Parse(CoverageJson());
        using JsonDocument document = JsonDocument.Parse(CoverageJson());
        string reordered = JsonSerializer.Serialize(new
        {
            entries = document.RootElement.GetProperty("entries"),
            inventoryDigest = InventoryDigest,
            schemaVersion = 1,
        }, new JsonSerializerOptions { WriteIndented = true });
        SyncCoverageManifest pretty = SyncCoverageManifest.Parse(reordered);

        Equal(CoverageDigest, compact.CoverageDigest);
        Equal(CoverageDigest, pretty.CoverageDigest);
    }

    public static void SyntheticCoverageDigestCannotOverrideContent()
    {
        foreach (string field in new[] { "digest", "coverageDigest" })
        {
            string json = CoverageJson().Insert(
                1, $"\"{field}\":\"sha256:{new string('9', 64)}\",");
            Throws<FormatException>(() => SyncCoverageManifest.Parse(json));
        }
    }

    public static void MissingCoverageRowsAndKnownOrphanReceiptsFail()
    {
        SyncCoverageManifest empty = ParseManifest([]);
        IReadOnlyList<SurfaceError> missing = Validate(empty, []);
        IReadOnlyList<SurfaceError> orphan = Validate(empty, [Receipt()]);

        HasError(missing, "manifest_missing_entry");
        HasError(orphan, "execution_unmapped_entry_receipt");
    }

    public static void ActivePositiveProofCannotUseNegativeMapping()
    {
        SyncCoverageManifest manifest = ParseManifest([
            Row([], [NegativeTestId], SyncEntryStatus.Active),
        ]);
        SyncExecutionReceipt positive = Receipt(testId: NegativeTestId);

        IReadOnlyList<SurfaceError> errors = Validate(manifest, [positive]);

        HasError(errors, "execution_missing_entry_receipt");
    }

    public static void ReceiptBindsToRunAndEnvelopeDigests()
    {
        SyncExecutionReceipt stale = Receipt(runId: "run-stale");
        SyncCoverageExecutionInput input = Input(ParseManifest([
            Row([TestId], [], SyncEntryStatus.Active),
        ]), [stale]);
        SetEnvelope(input, new EnvelopeFixture(
            "run-current", new string('9', 64),
            "sha256:" + new string('8', 64)));

        IReadOnlyList<SurfaceError> errors = SyncCoverageExecutionValidator.Validate(input);

        HasError(errors, "execution_run_id_mismatch");
        HasError(errors, "execution_envelope_inventory_digest_mismatch");
        HasError(errors, "execution_envelope_coverage_digest_mismatch");
    }

    public static void MissingEnvelopeFailsClosed()
    {
        SyncCoverageExecutionInput input = new()
        {
            Catalog = Catalog(),
            Manifest = ParseManifest([
                Row([TestId], [], SyncEntryStatus.Active),
            ]),
            Registry = Registry(),
            Receipts = [Receipt()],
            Envelope = null!,
        };

        IReadOnlyList<SurfaceError> errors =
            SyncCoverageExecutionValidator.Validate(input);

        HasError(errors, "execution_envelope_missing");
    }

    public static void MixedReceiptRunsFailClosed()
    {
        SyncCoverageManifest manifest = ParseManifest([
            Row([TestId, NegativeTestId], [], SyncEntryStatus.Active),
        ]);
        SyncCoverageExecutionInput input = Input(manifest, [
            Receipt(), Receipt(NegativeTestId, "run-stale"),
        ]);

        IReadOnlyList<SurfaceError> errors =
            SyncCoverageExecutionValidator.Validate(input);

        HasError(errors, "execution_run_id_mismatch");
    }

    public static void RuntimeArtifactMustBindControlledPathAndControlFlow()
    {
        string root = Path.Combine(Path.GetTempPath(),
            $"oni-adversarial-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            SyncCoverageManifest manifest = UnityManifest();
            SyncExecutionReceipt receipt = RuntimeReceipt(
                "missing/artifact.json", "sha256:" + new string('7', 64));
            receipt = receipt with { CoverageDigest = manifest.CoverageDigest! };
            SyncCoverageExecutionInput input = Input(manifest, [receipt]);
            SetRequiredProperty(input, "EvidenceRoot", root);

            IReadOnlyList<SurfaceError> errors =
                SyncCoverageExecutionValidator.Validate(input);

            HasError(errors, "runtime_artifact_missing");
            HasError(errors, "unity_only_missing_runtime_artifact");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void RuntimeArtifactRejectsHashRootAndManualObserve()
    {
        string root = Path.Combine(Path.GetTempPath(),
            $"oni-adversarial-artifact-{Guid.NewGuid():N}");
        string outside = root + "-outside.json";
        Directory.CreateDirectory(root);
        try
        {
            string artifact = Path.Combine(root, "artifact.json");
            WriteArtifact(artifact);
            IReadOnlyList<SurfaceError> hashErrors = ValidateRuntime(
                root, RuntimeReceipt("artifact.json", "sha256:" + new string('7', 64)));

            WriteArtifact(outside);
            IReadOnlyList<SurfaceError> rootErrors = ValidateRuntime(
                root, RuntimeReceipt(outside, FileDigest(outside)));
            IReadOnlyList<SurfaceError> controlErrors = ValidateRuntime(
                root, RuntimeReceipt("artifact.json", FileDigest(artifact)));

            HasError(hashErrors, "runtime_artifact_hash_mismatch");
            HasError(rootErrors, "runtime_artifact_outside_root");
            HasError(controlErrors, "runtime_control_path_mismatch");
        }
        finally
        {
            if (File.Exists(outside))
                File.Delete(outside);
            Directory.Delete(root, recursive: true);
        }
    }

    private static IReadOnlyList<SurfaceError> ValidateRuntime(
        string root,
        SyncExecutionReceipt receipt)
    {
        SyncCoverageManifest manifest = UnityManifest();
        receipt = receipt with { CoverageDigest = manifest.CoverageDigest! };
        SyncCoverageExecutionInput input = Input(manifest, [receipt]);
        SetRequiredProperty(input, "EvidenceRoot", root);
        return SyncCoverageExecutionValidator.Validate(input);
    }

    private static IReadOnlyList<SurfaceError> Validate(
        SyncCoverageManifest manifest,
        IReadOnlyList<SyncExecutionReceipt> receipts)
    {
        return SyncCoverageExecutionValidator.Validate(Input(manifest, receipts));
    }

    private static SyncCoverageExecutionInput Input(
        SyncCoverageManifest manifest,
        IReadOnlyList<SyncExecutionReceipt> receipts)
    {
        return new SyncCoverageExecutionInput
        {
            Catalog = Catalog(),
            Manifest = manifest,
            Registry = Registry(),
            Receipts = receipts,
            Envelope = new SyncExecutionEnvelope
            {
                RunId = "run-current",
                InventoryDigest = InventoryDigest,
                CoverageDigest = manifest.CoverageDigest!,
            },
        };
    }

    private static SyncCatalogScan Catalog()
    {
        return new SyncCatalogScan([
            new SyncEntry(
                EntryId, SyncEntryKind.HarmonyPatch, "Fixture.Patch.Postfix()",
                "Door.SetOpen(bool)", "HarmonyPatch -> Postfix",
                [new SyncBuildVariant(
                    "Debug", "OS_MAC", new HashSet<string>(["DEBUG", "OS_MAC"]))],
                SyncEntryStatus.Active),
        ], []);
    }

    private static SyncTestRegistry Registry()
    {
        return SyncTestRegistry.Create([
            new SyncTestDefinition(TestId, SyncExecutionTier.Headless, null),
            new SyncTestDefinition(NegativeTestId, SyncExecutionTier.Headless, null),
            new SyncTestDefinition("ingame:door", SyncExecutionTier.Ingame, "door"),
            new SyncTestDefinition("python:gate", SyncExecutionTier.Python, null),
            new SyncTestDefinition("real:door", SyncExecutionTier.Real, "door"),
        ]);
    }

    private static SyncCoverageManifest UnityManifest()
    {
        var row = Row(["ingame:door"], [], SyncEntryStatus.Active);
        row["scenarioIds"] = new[] { "door" };
        row["headlessUnsupportedReason"] = "requires ONI runtime";
        return ParseManifest([row]);
    }

    private static SyncCoverageManifest ParseManifest(
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        return SyncCoverageManifest.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            inventoryDigest = InventoryDigest,
            entries = rows,
        }));
    }

    private static Dictionary<string, object?> Row(
        IReadOnlyList<string> testIds,
        IReadOnlyList<string> negativeTestIds,
        SyncEntryStatus status)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = EntryId,
            ["domain"] = "door",
            ["testIds"] = testIds,
            ["negativeTestIds"] = negativeTestIds,
            ["scenarioIds"] = Array.Empty<string>(),
            ["variants"] = new[] { "Debug/OS_MAC" },
            ["status"] = status.ToString(),
        };
    }

    private static SyncExecutionReceipt Receipt(
        string testId = TestId,
        string runId = "run-current")
    {
        return ParseReceipt(new ReceiptFixture
        {
            TestId = testId,
            RunId = runId,
            Tier = "headless",
        });
    }

    private static SyncExecutionReceipt RuntimeReceipt(string path, string digest)
    {
        return ParseReceipt(new ReceiptFixture
        {
            TestId = "ingame:door",
            RunId = "run-current",
            Tier = "ingame",
            ScenarioId = "door",
            Artifact = new
            {
                kind = "ingame-result",
                path,
                sha256 = digest,
            },
        });
    }

    private static void WriteArtifact(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            runId = "run-current",
            testId = "ingame:door",
            scenarioId = "door",
            tier = "ingame",
            executedEntryIds = new[] { EntryId },
            controlPath = new { driver = "manual-observe" },
        }));
    }

    private static string FileDigest(string path)
    {
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    private static SyncExecutionReceipt ParseReceipt(ReceiptFixture fixture)
    {
        return SyncExecutionReceipt.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            runId = fixture.RunId,
            inventoryDigest = InventoryDigest,
            coverageDigest = CoverageDigest,
            dllHash = new string('d', 64),
            pdbHash = new string('e', 64),
            testId = fixture.TestId,
            tier = fixture.Tier,
            scenarioId = fixture.ScenarioId,
            polarity = "positive",
            executedEntryIds = new[] { EntryId },
            absentEntryIds = Array.Empty<string>(),
            registrationWitnesses = Array.Empty<object>(),
            artifact = fixture.Artifact,
        }));
    }

    private static string CoverageJson()
    {
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            inventoryDigest = InventoryDigest,
            entries = new[] { Row([TestId], [], SyncEntryStatus.Active) },
        });
    }

    private static void SetEnvelope(
        SyncCoverageExecutionInput input,
        EnvelopeFixture fixture)
    {
        PropertyInfo property = RequiredProperty(input, "Envelope");
        object envelope = Activator.CreateInstance(property.PropertyType) ??
            throw new InvalidOperationException("execution envelope needs a public constructor");
        SetRequiredProperty(envelope, "RunId", fixture.RunId);
        SetRequiredProperty(envelope, "InventoryDigest", fixture.InventoryDigest);
        SetRequiredProperty(envelope, "CoverageDigest", fixture.CoverageDigest);
        property.SetValue(input, envelope);
    }

    private sealed record ReceiptFixture
    {
        public required string TestId { get; init; }
        public required string RunId { get; init; }
        public required string Tier { get; init; }
        public string? ScenarioId { get; init; }
        public object? Artifact { get; init; }
    }

    private sealed record EnvelopeFixture(
        string RunId,
        string InventoryDigest,
        string CoverageDigest);

    private static void SetRequiredProperty(object target, string name, object value)
    {
        RequiredProperty(target, name).SetValue(target, value);
    }

    private static PropertyInfo RequiredProperty(object target, string name)
    {
        return target.GetType().GetProperty(name) ??
            throw new InvalidOperationException($"missing production property {name}");
    }

    private static void HasError(IEnumerable<SurfaceError> errors, string code)
    {
        if (!errors.Any(error => error.Code == code))
            throw new InvalidOperationException($"missing {code}");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"expected {expected}, got {actual}");
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
}
