using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class SyncCoverageBlockerAdversarialTests
{
    private const string InventoryDigest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TestId = "headless:origin-proof";
    private static readonly SyncBuildVariant Variant = new(
        "Debug", "OS_MAC", new HashSet<string>(["DEBUG", "OS_MAC"]));

    public static void CoroutineIdentityResolvesDirectLocalAndAssignedForms()
    {
        SyncCatalogScan catalog = SyncSurfaceScanner.ScanCatalogSources(
            new Dictionary<string, string> { ["CoroutineForms.cs"] = CoroutineSource },
            [Variant]);
        SyncEntry[] entries = catalog.Entries.Where(entry =>
            entry.Kind == SyncEntryKind.Coroutine).ToArray();

        Equal(3, entries.Length);
        True(entries.All(entry => entry.ResolvedTargetSignature.Contains(
            "WaitForSync", StringComparison.Ordinal)),
            "a StartCoroutine call lost its IEnumerator target identity");
        Equal(3, entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count());
    }

    public static void SyntheticProofCannotAuthenticateAnotherCatalogOrigin()
    {
        SyncCatalogScan catalog = SyncSurfaceScanner.ScanCatalogSources(
            new Dictionary<string, string> { ["ProductionSurface.cs"] = PacketSource },
            [Variant]);
        SyncEntry entry = catalog.Entries.Single(item =>
            item.Kind == SyncEntryKind.PacketSend);
        SyncCoverageManifest manifest = Manifest(entry);
        SyncExecutionReceipt receipt = Receipt(entry.Id, manifest.CoverageDigest!);
        SyncExecutionProvenance.Attach(receipt, [entry.Id], []);

        IReadOnlyList<SurfaceError> errors = SyncCoverageExecutionValidator.Validate(
            new SyncCoverageExecutionInput
            {
                Catalog = catalog,
                Manifest = manifest,
                Registry = SyncTestRegistry.Create([
                    new SyncTestDefinition(TestId, SyncExecutionTier.Headless, null),
                    new SyncTestDefinition(
                        "ingame:origin-placeholder", SyncExecutionTier.Ingame, null),
                    new SyncTestDefinition(
                        "python:origin-placeholder", SyncExecutionTier.Python, null),
                    new SyncTestDefinition(
                        "real:origin-placeholder", SyncExecutionTier.Real, null),
                ]),
                Receipts = [receipt],
                Envelope = new SyncExecutionEnvelope
                {
                    RunId = receipt.RunId,
                    InventoryDigest = InventoryDigest,
                    CoverageDigest = manifest.CoverageDigest!,
                },
            });

        True(errors.Any(error => error.Code == "execution_provenance_origin_mismatch"),
            "a synthetic proof object authenticated an unrelated catalog origin");
    }

    private static SyncCoverageManifest Manifest(SyncEntry entry)
    {
        return SyncCoverageManifest.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            inventoryDigest = InventoryDigest,
            entries = new[]
            {
                new
                {
                    id = entry.Id,
                    domain = "packet",
                    testIds = new[] { TestId },
                    negativeTestIds = Array.Empty<string>(),
                    scenarioIds = Array.Empty<string>(),
                    variants = new[] { Variant.Key },
                    status = "Active",
                },
            },
        }));
    }

    private static SyncExecutionReceipt Receipt(string entryId, string coverageDigest)
    {
        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            runId = "run-origin-proof",
            inventoryDigest = InventoryDigest,
            coverageDigest,
            dllHash = new string('d', 64),
            pdbHash = new string('e', 64),
            testId = TestId,
            tier = "headless",
            scenarioId = (string?)null,
            polarity = "positive",
            executedEntryIds = new[] { entryId },
            absentEntryIds = Array.Empty<string>(),
            registrationWitnesses = Array.Empty<object>(),
            artifact = (object?)null,
        });
        return SyncExecutionReceipt.Parse(json);
    }

    private static void Equal(int expected, int actual)
    {
        if (expected != actual)
            throw new InvalidOperationException($"expected {expected}, got {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private const string CoroutineSource = """
        using System.Collections;
        public abstract class Owner
        {
            protected object StartCoroutine(IEnumerator routine) => routine;
        }
        public sealed class Runtime : Owner
        {
            public object Direct() => StartCoroutine(WaitForSync());
            public object Local()
            {
                IEnumerator routine = WaitForSync();
                return StartCoroutine(routine);
            }
            public object Assigned()
            {
                IEnumerator routine;
                routine = WaitForSync();
                return StartCoroutine(routine);
            }
            private IEnumerator WaitForSync() { yield return null; }
        }
        """;

    private const string PacketSource = """
        public interface IPacket { }
        public sealed class Packet : IPacket { }
        public static class PacketSender
        {
            public static void Send(IPacket packet) { }
        }
        public static class ProductionSurface
        {
            public static void Send() => PacketSender.Send(new Packet());
        }
        """;
}
