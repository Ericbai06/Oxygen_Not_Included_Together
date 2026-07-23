using System.Reflection;
using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class SyncCoverageProvenReceiptFixture
{
    private const string InventoryDigest =
        "1111111111111111111111111111111111111111111111111111111111111111";
    private const string PositiveTestId = "headless:packet-roundtrip";
    private const string NegativeTestId = "headless:disabled-hook-negative";
    private const string ProvenRunId = "run-proven-receipts";

    public static IReadOnlyList<SurfaceError> ValidateActive()
    {
        SyncCatalogScan scanned = SyncExecutionProbeFixtureCatalog.Scan();
        SyncEntry state = scanned.Entries.Single(entry =>
            entry.Kind == SyncEntryKind.StateMachine &&
            entry.FullyQualifiedSymbol.Contains(
                "StateRuntime.Apply", StringComparison.Ordinal));
        SyncCoverageManifest manifest = Manifest([
            Row(state, [PositiveTestId], []),
        ]);
        ISyncExecutionProbeSession session = Start(scanned, Binding(
            PositiveTestId, SyncExecutionPolarity.Positive,
            manifest.CoverageDigest!));

        Invoke(session, "StateRuntime", "Apply");
        SyncExecutionReceipt receipt = session.Complete();

        return Validate(new GateFixture
        {
            Catalog = Subset(state),
            Manifest = manifest,
            Registry = Registry(includeNegative: false),
            Receipts = [receipt],
        });
    }

    public static IReadOnlyList<SurfaceError> ValidateRegisteredDisabled()
    {
        SyncCatalogScan scanned = SyncExecutionProbeFixtureCatalog.Scan();
        SyncEntry registration = scanned.Entries.Single(entry =>
            entry.Kind == SyncEntryKind.PacketRegistration &&
            entry.FullyQualifiedSymbol.Contains(
                "DisabledPacketRuntime.Register", StringComparison.Ordinal));
        SyncEntry disabledSend = scanned.Entries.Single(entry =>
            entry.Kind == SyncEntryKind.PacketSend &&
            entry.Status == SyncEntryStatus.RegisteredDisabled);
        SyncCoverageManifest manifest = Manifest([
            Row(registration, [PositiveTestId], [NegativeTestId]),
            Row(disabledSend, [], [NegativeTestId]),
        ]);
        SyncExecutionReceipt positive = RunRegistration(
            scanned, manifest.CoverageDigest!);
        SyncExecutionReceipt negative = RunDisabledSend(
            scanned, manifest.CoverageDigest!);

        return Validate(new GateFixture
        {
            Catalog = Subset(registration, disabledSend),
            Manifest = manifest,
            Registry = Registry(includeNegative: true),
            Receipts = [positive, negative],
        });
    }

    private static SyncExecutionReceipt RunRegistration(
        SyncCatalogScan catalog,
        string coverageDigest)
    {
        ISyncExecutionProbeSession session = Start(catalog,
            Binding(PositiveTestId, SyncExecutionPolarity.Positive, coverageDigest));
        Invoke(session, "DisabledPacketRuntime", "Register");
        return session.Complete();
    }

    private static SyncExecutionReceipt RunDisabledSend(
        SyncCatalogScan catalog,
        string coverageDigest)
    {
        ISyncExecutionProbeSession session = Start(catalog,
            Binding(NegativeTestId, SyncExecutionPolarity.Negative, coverageDigest));
        Invoke(session, "DisabledPacketRuntime", "Register");
        Invoke(session, "DisabledPacketRuntime", "Send");
        return session.Complete();
    }

    private static ISyncExecutionProbeSession Start(
        SyncCatalogScan catalog,
        SyncExecutionProbeBinding binding)
    {
        return SyncExecutionProbeFactoryLoader.Load().Start(
            binding, catalog, SyncExecutionProbeFixtureCatalog.Compile());
    }

    private static SyncExecutionProbeBinding Binding(
        string testId,
        SyncExecutionPolarity polarity,
        string coverageDigest)
    {
        return new SyncExecutionProbeBinding
        {
            RunId = ProvenRunId,
            TestId = testId,
            Tier = SyncExecutionTier.Headless,
            Polarity = polarity,
            InventoryDigest = InventoryDigest,
            CoverageDigest = coverageDigest,
        };
    }

    private static void Invoke(
        ISyncExecutionProbeSession session,
        string typeName,
        string methodName)
    {
        Type type = session.RuntimeAssembly.GetType(typeName, throwOnError: true)!;
        MethodInfo method = type.GetMethod(
            methodName, BindingFlags.Public | BindingFlags.Static)!;
        object?[]? arguments = method.GetParameters().Length == 0
            ? null
            : [Activator.CreateInstance(method.GetParameters()[0].ParameterType)];
        _ = method.Invoke(null, arguments);
    }

    private static IReadOnlyList<SurfaceError> Validate(GateFixture fixture)
    {
        return SyncCoverageExecutionValidator.Validate(new SyncCoverageExecutionInput
        {
            Catalog = fixture.Catalog,
            Manifest = fixture.Manifest,
            Registry = fixture.Registry,
            Receipts = fixture.Receipts,
            Envelope = new SyncExecutionEnvelope
            {
                RunId = ProvenRunId,
                InventoryDigest = InventoryDigest,
                CoverageDigest = fixture.Manifest.CoverageDigest!,
            },
        });
    }

    private static SyncCatalogScan Subset(params SyncEntry[] entries)
    {
        return new SyncCatalogScan(entries, []);
    }

    private static SyncTestRegistry Registry(bool includeNegative)
    {
        var definitions = new List<SyncTestDefinition>
        {
            new(PositiveTestId, SyncExecutionTier.Headless, null),
            new("ingame:proven-receipt-placeholder", SyncExecutionTier.Ingame, null),
            new("python:proven-receipt-placeholder", SyncExecutionTier.Python, null),
            new("real:proven-receipt-placeholder", SyncExecutionTier.Real, null),
        };
        if (includeNegative)
        {
            definitions.Add(new SyncTestDefinition(
                NegativeTestId, SyncExecutionTier.Headless, null));
        }
        return SyncTestRegistry.Create(definitions);
    }

    private static SyncCoverageManifest Manifest(
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
        SyncEntry entry,
        IReadOnlyList<string> testIds,
        IReadOnlyList<string> negativeTestIds)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = entry.Id,
            ["domain"] = "execution-probe-fixture",
            ["testIds"] = testIds,
            ["negativeTestIds"] = negativeTestIds,
            ["scenarioIds"] = Array.Empty<string>(),
            ["variants"] = entry.Variants.Select(variant => variant.Key).ToArray(),
            ["status"] = entry.Status.ToString(),
        };
    }

    private sealed class GateFixture
    {
        public required SyncCatalogScan Catalog { get; init; }
        public required SyncCoverageManifest Manifest { get; init; }
        public required SyncTestRegistry Registry { get; init; }
        public required IReadOnlyList<SyncExecutionReceipt> Receipts { get; init; }
    }
}
