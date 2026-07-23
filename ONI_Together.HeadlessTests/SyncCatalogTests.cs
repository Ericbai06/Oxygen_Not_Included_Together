using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class SyncCatalogTests
{
    private const string FixturePath = "SyncCatalogFixture.cs";
    private const string KnownTestId = "headless.sync-catalog";
    private const string KnownNegativeTestId = "headless.sync-catalog-negative";

    private static readonly SyncBuildVariant[] Variants =
    [
        new("Debug", "OS_MAC", new HashSet<string>(["DEBUG", "OS_MAC"])),
        new("Debug", "OS_WINDOWS", new HashSet<string>(["DEBUG", "OS_WINDOWS"])),
        new("Release", "OS_MAC", new HashSet<string>(["OS_MAC"])),
        new("Release", "OS_WINDOWS", new HashSet<string>(["OS_WINDOWS"]))
    ];

    private const string CatalogFixture = """
        using System;
        using System.Collections;
        using System.Reflection;

        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
        public sealed class HarmonyPatchAttribute : Attribute
        {
            public HarmonyPatchAttribute() { }
            public HarmonyPatchAttribute(Type type, string method) { }
        }
        [AttributeUsage(AttributeTargets.Method)]
        public sealed class HarmonyPrefixAttribute : Attribute { }
        [AttributeUsage(AttributeTargets.Method)]
        public sealed class HarmonyPostfixAttribute : Attribute { }
        public sealed class HarmonyMethod
        {
            public HarmonyMethod(Type type, string method) { }
        }
        public sealed class Harmony
        {
            public void Patch(MethodInfo original, HarmonyMethod? prefix = null) { }
        }
        public static class AccessTools
        {
            public static MethodInfo? Method(Type type, string name) => null;
        }

        public interface IPacket
        {
            void OnDispatched();
        }
        public sealed class FixturePacket : IPacket
        {
            public void OnDispatched() { }
        }
        public static class PacketRegistry
        {
            public static void Register<T>() where T : IPacket, new() { }
        }
        public static class PacketSender
        {
            public static void Send(IPacket packet) { }
        }
        public static class PacketDispatcher
        {
            public static void Dispatch(IPacket packet) => packet.OnDispatched();
        }

        public static class Game
        {
            public static void Subscribe(string key, Action handler) { }
            public static void Trigger(string key, object? data = null) { }
        }
        public static class EventHub
        {
            public static event Action? Changed;
            public static void Attach()
            {
                Changed += OnChanged;
                Changed -= OnChanged;
            }
            private static void OnChanged() { }
        }
        public static class EventBootstrap
        {
            public static void Install()
            {
                Game.Subscribe("fixture", OnGameEvent);
            }
            private static void OnGameEvent() { }
            public static void Publish() => Game.Trigger("fixture", null);
        }

        public abstract class CoroutineOwner
        {
            protected object StartCoroutine(IEnumerator routine) => routine;
        }
        public sealed class CoroutineFixture : CoroutineOwner
        {
            public object Begin() => StartCoroutine(WaitForSync());
            private IEnumerator WaitForSync()
            {
                yield return null;
            }
        }

        public sealed class StateNode { }
        public sealed class StateGraph
        {
            public StateNode ready { get; } = new();
        }
        public sealed class StateMachineInstance
        {
            public StateGraph sm { get; } = new();
            public void StartSM() { }
            public void GoTo(StateNode state) { }
            public bool IsInsideState(StateNode state) => true;
            public object? Get(string key) => null;
            public void Set(string key, object value) { }
            public void Delta(string key, int value) { }
            public void Trigger(string key) { }
        }
        [HarmonyPatch(typeof(StateMachineInstance), nameof(StateMachineInstance.StartSM))]
        public static class StateMachineStartPatch
        {
            [HarmonyPostfix]
            public static void Postfix() { }
        }
        public static class StateMachineActions
        {
            public static void Apply(StateMachineInstance smi)
            {
                smi.GoTo(smi.sm.ready);
                _ = smi.IsInsideState(smi.sm.ready);
                _ = smi.Get("state");
                smi.Set("state", 1);
                smi.Delta("state", 1);
                smi.Trigger("state");
            }
        }

        public static class MethodAttributedPatch
        {
            [HarmonyPatch(typeof(SyncTarget), nameof(SyncTarget.Receive))]
            [HarmonyPrefix]
            public static void Prefix() { }
        }
        public static class DynamicPatchBootstrap
        {
            public static void Register(Harmony harmony)
            {
                harmony.Patch(
                    AccessTools.Method(typeof(SyncTarget), "MissingTarget"),
                    prefix: new HarmonyMethod(typeof(DynamicPatch), nameof(DynamicPatch.Prefix)));
            }
        }
        public static class DynamicPatch
        {
            public static void Prefix() { }
        }
        public static class SyncTarget
        {
            public static void Receive() { }
        }
        public static class PacketBootstrap
        {
            public static void Register()
            {
                PacketRegistry.Register<FixturePacket>();
            }
            public static void Send()
            {
                PacketSender.Send(new FixturePacket());
            }
            public static void Dispatch()
            {
                PacketDispatcher.Dispatch(new FixturePacket());
            }
        }

        public static class DisabledPatch
        {
            [HarmonyPatch(typeof(SyncTarget), nameof(SyncTarget.Receive))]
            [HarmonyPostfix]
            public static void Postfix()
            {
                return;
                // Registered but disabled until a negative execution test is mapped.
            }
        }
        """;

    public static void CatalogFindsMethodHarmonyAndDynamicPatch()
    {
        SyncCatalogScan catalog = Scan();
        string methodPatch = EntryText(catalog.Entries.Single(entry =>
            entry.FullyQualifiedSymbol.Contains("MethodAttributedPatch", StringComparison.Ordinal)));
        True(methodPatch.Contains("HarmonyPrefix", StringComparison.Ordinal) ||
            methodPatch.Contains("Prefix", StringComparison.Ordinal),
            "method-level HarmonyPrefix was not catalogued");

        string dynamicPatch = EntryText(catalog.Entries.Single(entry =>
            entry.FullyQualifiedSymbol.Contains("DynamicPatch", StringComparison.Ordinal) ||
            entry.Bootstrap.Contains("DynamicPatch", StringComparison.Ordinal)));
        True(dynamicPatch.Contains("MissingTarget", StringComparison.Ordinal),
            "dynamic Harmony.Patch target was not retained");
    }

    public static void CatalogFindsEventSubscribeAndUnsubscribe()
    {
        SyncCatalogScan catalog = Scan();
        string[] eventEntries = catalog.Entries
            .Where(entry => entry.Kind == SyncEntryKind.EventSubscribe)
            .Select(EntryText)
            .ToArray();

        True(eventEntries.Length >= 3, "Game.Subscribe and +=/-= event wiring were not all catalogued");
        True(eventEntries.Any(text => text.Contains("Game.Subscribe", StringComparison.Ordinal) ||
            text.Contains("OnGameEvent", StringComparison.Ordinal)),
            "Game.Subscribe subscriber was not catalogued");
        True(eventEntries.Count(text => text.Contains("Changed", StringComparison.Ordinal)) >= 2,
            "both += and -= event wiring were not catalogued");
    }

    public static void CatalogFindsTriggerPublisher()
    {
        SyncCatalogScan catalog = Scan();
        string[] publishers = catalog.Entries
            .Where(entry => entry.Kind == SyncEntryKind.EventPublish)
            .Select(EntryText)
            .ToArray();

        True(publishers.Any(text => text.Contains("Game.Trigger", StringComparison.Ordinal) ||
            text.Contains("Publish", StringComparison.Ordinal)),
            "Game.Trigger publisher was not catalogued");
    }

    public static void CatalogLinksCoroutineToIterator()
    {
        SyncCatalogScan catalog = Scan();
        SyncEntry entry = catalog.Entries.Single(item => item.Kind == SyncEntryKind.Coroutine &&
            EntryText(item).Contains("WaitForSync", StringComparison.Ordinal));

        True(entry.ResolvedTargetSignature.Contains("IEnumerator", StringComparison.Ordinal) ||
            entry.Bootstrap.Contains("WaitForSync", StringComparison.Ordinal),
            "StartCoroutine was not linked to its IEnumerator method");
    }

    public static void CatalogFindsStateMachineHooks()
    {
        SyncCatalogScan catalog = Scan();
        string stateText = string.Join("\n", catalog.Entries
            .Where(entry => entry.Kind == SyncEntryKind.StateMachine)
            .Select(EntryText));

        foreach (string token in new[]
        {
            "StartSM", "GoTo", "IsInsideState", "Get", "Set", "Delta", "Trigger"
        })
        {
            True(stateText.Contains(token, StringComparison.Ordinal),
                $"state-machine hook {token} was not catalogued");
        }
    }

    public static void CatalogTracksDebugReleaseAndOsVariants()
    {
        SyncCatalogScan catalog = Scan();
        string[] expected = Variants.Select(VariantKey).Order(StringComparer.Ordinal).ToArray();
        string[] actual = catalog.Entries
            .SelectMany(entry => entry.Variants)
            .Select(VariantKey)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        EqualSequence(expected, actual);
    }

    public static void CatalogStableIdsIgnoreLineNumbers()
    {
        SyncCatalogScan first = Scan(CatalogFixture);
        SyncCatalogScan shifted = Scan("\n\n\n\n\n\n" + CatalogFixture);
        var firstIds = StableEntryMap(first);
        var shiftedIds = StableEntryMap(shifted);

        Equal(firstIds.Count, shiftedIds.Count);
        foreach ((string key, string id) in firstIds)
            Equal(id, shiftedIds[key]);
    }

    public static void ManifestAcceptsCompleteCatalogMapping()
    {
        SyncCatalogScan catalog = ScanValidManifestCatalog();
        IReadOnlyList<SurfaceError> errors = Validate(catalog, ManifestFor(catalog.Entries));
        Equal(0, errors.Count);
    }

    public static void ManifestRejectsMissingEntry()
    {
        SyncCatalogScan catalog = Scan();
        SyncEntry[] entries = catalog.Entries.Skip(1).ToArray();
        ExpectError(catalog, ManifestFor(entries), "manifest_missing_entry");
    }

    public static void ManifestRejectsOrphanEntry()
    {
        SyncCatalogScan catalog = Scan();
        string manifest = ManifestFor(catalog.Entries, extra: new
        {
            id = "orphan-entry-id",
            domain = "catalog-fixture",
            testIds = new[] { KnownTestId },
            negativeTestIds = Array.Empty<string>(),
            scenarioIds = Array.Empty<string>(),
            variants = Variants.Select(VariantKey).ToArray(),
            status = SyncEntryStatus.Active.ToString()
        });

        ExpectError(catalog, manifest, "manifest_orphan_entry");
    }

    public static void ManifestRejectsDuplicateEntry()
    {
        SyncCatalogScan catalog = Scan();
        string manifest = ManifestFor(catalog.Entries, duplicateFirst: true);
        ExpectError(catalog, manifest, "manifest_duplicate_entry");
    }

    public static void ManifestRejectsUnknownTestId()
    {
        SyncCatalogScan catalog = Scan();
        string manifest = ManifestFor(catalog.Entries, testIdOverride: "unknown-test-id");
        ExpectError(catalog, manifest, "manifest_unknown_test_id");
    }

    public static void ManifestRejectsUnresolvedActiveTarget()
    {
        SyncCatalogScan catalog = Scan();
        True(catalog.Entries.Any(entry => string.IsNullOrWhiteSpace(entry.ResolvedTargetSignature) &&
            entry.Status == SyncEntryStatus.Active),
            "fixture did not produce an unresolved active target");
        ExpectError(catalog, ManifestFor(catalog.Entries), "manifest_unresolved_active_target");
    }

    public static void ManifestRejectsUndeclaredVariant()
    {
        SyncCatalogScan catalog = Scan();
        string manifest = ManifestFor(catalog.Entries, variantOverride: "Debug/OS_FREEBSD");
        ExpectError(catalog, manifest, "manifest_undeclared_variant");
    }

    public static void ManifestRequiresNegativeExecutionForRegisteredDisabled()
    {
        SyncCatalogScan catalog = Scan();
        SyncEntry disabled = catalog.Entries.Single(entry =>
            entry.Status == SyncEntryStatus.RegisteredDisabled);
        string manifest = ManifestFor(catalog.Entries, omitNegativeTestId: true);
        ExpectError(catalog, manifest, "registered_disabled_missing_negative_test");

        string mapped = ManifestFor(catalog.Entries, omitNegativeTestId: false);
        IReadOnlyList<SurfaceError> mappedErrors = Validate(catalog, mapped);
        True(!mappedErrors.Any(error => error.Code == "registered_disabled_missing_negative_test" &&
            error.Subject == disabled.Id),
            "registered-disabled entry remained unmapped after negative test was declared");
    }

    private static SyncCatalogScan Scan(string? source = null)
    {
        return SyncSurfaceScanner.ScanCatalogSources(
            new Dictionary<string, string>
            {
                [FixturePath] = source ?? CatalogFixture
            },
            Variants);
    }

    private static SyncCatalogScan ScanValidManifestCatalog()
    {
        string resolvedDynamicTarget = CatalogFixture.Replace(
            "\"MissingTarget\"", "\"Receive\"", StringComparison.Ordinal);
        return Scan(resolvedDynamicTarget);
    }

    private static IReadOnlyList<SurfaceError> Validate(
        SyncCatalogScan catalog,
        string manifest)
    {
        SyncCoverageManifest parsed = SyncCoverageManifest.Parse(manifest);
        return SyncCoverageValidator.Validate(
            catalog,
            parsed,
            new HashSet<string>([KnownTestId, KnownNegativeTestId]),
            new HashSet<string>(["remote-dig"]));
    }

    private static void ExpectError(
        SyncCatalogScan catalog,
        string manifest,
        string code)
    {
        IReadOnlyList<SurfaceError> errors = Validate(catalog, manifest);
        True(errors.Any(error => error.Code == code),
            $"expected manifest error {code}, got [{string.Join(", ", errors.Select(error => error.Code))}]");
    }

    private static string ManifestFor(
        IReadOnlyList<SyncEntry> entries,
        object? extra = null,
        bool duplicateFirst = false,
        string? testIdOverride = null,
        string? variantOverride = null,
        bool omitNegativeTestId = false)
    {
        var rows = entries.Select(entry => (object)new
        {
            id = entry.Id,
            domain = "catalog-fixture",
            testIds = new[] { testIdOverride ?? KnownTestId },
            negativeTestIds = entry.Status == SyncEntryStatus.RegisteredDisabled && !omitNegativeTestId
                ? new[] { KnownNegativeTestId }
                : Array.Empty<string>(),
            scenarioIds = Array.Empty<string>(),
            variants = variantOverride is null
                ? entry.Variants.Select(VariantKey).Distinct(StringComparer.Ordinal).ToArray()
                : new[] { variantOverride },
            status = entry.Status.ToString()
        }).ToList();

        if (duplicateFirst && rows.Count > 0)
            rows.Add(rows[0]);
        if (extra is not null)
            rows.Add(extra);
        return JsonSerializer.Serialize(new { entries = rows });
    }

    private static Dictionary<string, string> StableEntryMap(SyncCatalogScan catalog)
    {
        return catalog.Entries
            .OrderBy(entry => entry.Kind.ToString(), StringComparer.Ordinal)
            .ThenBy(entry => entry.FullyQualifiedSymbol, StringComparer.Ordinal)
            .ThenBy(entry => entry.ResolvedTargetSignature, StringComparer.Ordinal)
            .ThenBy(entry => entry.Bootstrap, StringComparer.Ordinal)
            .ToDictionary(StableKey, entry => entry.Id, StringComparer.Ordinal);
    }

    private static string StableKey(SyncEntry entry)
    {
        return string.Join("|", entry.Kind, entry.FullyQualifiedSymbol,
            entry.ResolvedTargetSignature, entry.Bootstrap);
    }

    private static string EntryText(SyncEntry entry)
    {
        return string.Join("|", entry.Id, entry.Kind, entry.FullyQualifiedSymbol,
            entry.ResolvedTargetSignature, entry.Bootstrap);
    }

    private static string VariantKey(SyncBuildVariant variant)
    {
        return $"{variant.Configuration}/{variant.Platform}";
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"expected {expected}, actual {actual}");
    }

    private static void EqualSequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(
                $"expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}]");
    }
}
