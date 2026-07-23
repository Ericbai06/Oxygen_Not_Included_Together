namespace ONI_Together.HeadlessTests;

internal static class SyncCatalogClassificationTests
{
    private static readonly SyncBuildVariant DebugMac = new(
        "Debug", "OS_MAC", new HashSet<string>(["DEBUG", "OS_MAC"]));

    private static readonly SyncBuildVariant ReleaseMac = new(
        "Release", "OS_MAC", new HashSet<string>(["OS_MAC"]));

    private static readonly SyncBuildVariant[] AllVariants =
    [
        DebugMac,
        new("Debug", "OS_WINDOWS", new HashSet<string>(["DEBUG", "OS_WINDOWS"])),
        new("Debug", "OS_LINUX", new HashSet<string>(["DEBUG", "OS_LINUX"])),
        new("Debug", "OS_FREEBSD", new HashSet<string>(["DEBUG", "OS_FREEBSD"])),
        ReleaseMac,
        new("Release", "OS_WINDOWS", new HashSet<string>(["OS_WINDOWS"])),
        new("Release", "OS_LINUX", new HashSet<string>(["OS_LINUX"])),
        new("Release", "OS_FREEBSD", new HashSet<string>(["OS_FREEBSD"]))
    ];

    private const string ClassificationSupport = """
        using System;
        public interface IPacket { }
        public interface IPacketSkipsRegistration { }
        public sealed class HarmonyPatchAttribute : Attribute
        {
            public HarmonyPatchAttribute() { }
            public HarmonyPatchAttribute(Type type, string method) { }
        }
        public sealed class HarmonyPostfixAttribute : Attribute { }
        public sealed class HarmonyPrefixAttribute : Attribute { }
        public static class PacketSender
        {
            public static void Send(IPacket packet) { }
        }
        public static class Target
        {
            public static void Receive() { }
        }
        """;

    public static void CatalogClassifiesPathOwnership()
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Shared/ClassificationSupport.cs"] = ClassificationSupport,
            ["DebugTools/UnitTests/UnitOnlyPatch.cs"] = Patch("UnitOnlyPatch"),
            ["Tests/TestsOnlyPatch.cs"] = Patch("TestsOnlyPatch"),
            ["UI/lib/VendorOnlyPatch.cs"] = Patch("VendorOnlyPatch"),
            ["Networking/ProductionPatch.cs"] = Patch("ProductionPatch")
        };
        SyncCatalogScan catalog = SyncSurfaceScanner.ScanCatalogSources(
            sources, new[] { DebugMac });

        EqualStatus(catalog, "UnitOnlyPatch", SyncEntryStatus.TestOnly);
        EqualStatus(catalog, "TestsOnlyPatch", SyncEntryStatus.TestOnly);
        EqualStatus(catalog, "VendorOnlyPatch", SyncEntryStatus.Vendor);
        EqualStatus(catalog, "ProductionPatch", SyncEntryStatus.Active);

        var first = new SyncVariantInput(DebugMac,
            new Dictionary<string, string>
            {
                ["Networking/StatusConflictPatch.cs"] = Patch("StatusConflictPatch")
            }, Array.Empty<string>());
        var second = new SyncVariantInput(ReleaseMac,
            new Dictionary<string, string>
            {
                ["Tests/StatusConflictPatch.cs"] = Patch("StatusConflictPatch")
            }, Array.Empty<string>());
        SyncCatalogScan conflict = SyncSurfaceScanner.ScanCatalogVariants([first, second]);
        SyncEntry[] matches = conflict.Entries.Where(entry =>
            entry.FullyQualifiedSymbol.Contains("StatusConflictPatch", StringComparison.Ordinal))
            .ToArray();
        bool separated = matches.Select(entry => entry.Status).Distinct().Count() > 1;
        bool rejected = conflict.Errors.Any(error =>
            error.Code.Contains("status", StringComparison.OrdinalIgnoreCase) ||
            error.Code.Contains("classification", StringComparison.OrdinalIgnoreCase));
        True(separated || rejected,
            "incompatible path statuses were silently merged across variants");
    }

    public static void RegisteredDisabledPropagatesToPacketSend()
    {
        SyncCatalogScan catalog = SyncSurfaceScanner.ScanCatalogSources(
            new Dictionary<string, string>
            {
                ["Networking/DisabledFixture.cs"] = ClassificationSupport + DisabledFixture
            }, new[] { DebugMac });

        SyncEntry disabledPatch = Find(catalog, "DisabledPatch.Postfix", SyncEntryKind.HarmonyPatch);
        SyncEntry disabledSend = Find(catalog, "DisabledPatch.Postfix", SyncEntryKind.PacketSend);
        Equal(SyncEntryStatus.RegisteredDisabled, disabledPatch.Status);
        Equal(SyncEntryStatus.RegisteredDisabled, disabledSend.Status);

        SyncEntry conditionalPatch = Find(catalog, "ConditionalPatch.Postfix", SyncEntryKind.HarmonyPatch);
        SyncEntry conditionalSend = Find(catalog, "ConditionalPatch.Postfix", SyncEntryKind.PacketSend);
        Equal(SyncEntryStatus.Active, conditionalPatch.Status);
        Equal(SyncEntryStatus.Active, conditionalSend.Status);
    }

    public static void CatalogRecordsReflectionPacketRegistrations()
    {
        SyncCatalogScan catalog = SyncSurfaceScanner.ScanCatalogSources(
            new Dictionary<string, string>
            {
                ["Networking/Packets/ReflectionPackets.cs"] = ReflectionPacketFixture
            }, new[] { DebugMac });
        SyncEntry[] registrations = catalog.Entries
            .Where(entry => entry.Kind == SyncEntryKind.PacketRegistration).ToArray();

        foreach (string packet in new[] { "ConcretePacket", "IndirectPacket" })
        {
            SyncEntry entry = registrations.SingleOrDefault(item =>
                item.FullyQualifiedSymbol.Contains(packet, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"missing PacketRegistration for {packet}");
            True(entry.Bootstrap.Contains("RegisterDefaults", StringComparison.Ordinal) ||
                entry.Bootstrap.Contains("AutoRegisterPackets", StringComparison.Ordinal),
                $"{packet} registration has no reflection bootstrap chain");
        }

        foreach (string excluded in new[] { "AbstractPacket", "InterfacePacket", "SkippedPacket" })
            True(!registrations.Any(item => item.FullyQualifiedSymbol.Contains(
                excluded, StringComparison.Ordinal)), $"excluded packet {excluded} was registered");
    }

    public static void PacketRegistrationNamesAreExact()
    {
        SyncCatalogScan catalog = SyncSurfaceScanner.ScanCatalogSources(
            new Dictionary<string, string>
            {
                ["Networking/Packets/FingerprintFixture.cs"] = FingerprintFixture
            }, new[] { DebugMac });
        SyncEntry[] registrations = catalog.Entries
            .Where(entry => entry.Kind == SyncEntryKind.PacketRegistration).ToArray();

        True(registrations.Any(entry => EntryText(entry).Contains(
            "RegisterDefaults", StringComparison.Ordinal)),
            "RegisterDefaults bootstrap was not catalogued");
        True(registrations.Any(entry => EntryText(entry).Contains(
            "AutoRegisterPackets", StringComparison.Ordinal)),
            "AutoRegisterPackets bootstrap was not catalogued");
        True(!registrations.Any(entry => EntryText(entry).Contains(
            "GetRegisteredPacketFingerprint", StringComparison.Ordinal)),
            "GetRegisteredPacketFingerprint was misclassified as PacketRegistration");
    }

    public static void ActualOniCatalogStatusClassification()
    {
        string root = FindRepositoryRoot();
        string gameLibs = "/Users/eric/Library/Application Support/Steam/steamapps/common/" +
            "OxygenNotIncluded/OxygenNotIncluded.app/Contents/Resources/Data/Managed";
        True(Directory.Exists(gameLibs), $"GameLibsFolder is unavailable: {gameLibs}");
        string project = Path.Combine(root, "ONI_Together", "ONI_Together.csproj");
        IReadOnlyList<SyncVariantInput> inputs = SyncMsBuildProjectLoader.Load(
            project, AllVariants, new Dictionary<string, string>
            {
                ["GameLibsFolder"] = gameLibs
            });
        SyncCatalogScan catalog = SyncSurfaceScanner.ScanCatalogVariants(inputs);

        foreach (SyncEntryStatus status in Enum.GetValues<SyncEntryStatus>())
            True(catalog.Entries.Any(entry => entry.Status == status),
                $"actual catalog has no {status} entries");
        int legacyPacketCount = SyncSurfaceScanner.ScanDirectory(root).Packets.Count;
        int registrationCount = catalog.Entries.Count(entry =>
            entry.Kind == SyncEntryKind.PacketRegistration);
        True(registrationCount >= legacyPacketCount,
            $"packet registration entries {registrationCount} < legacy surface {legacyPacketCount}");

        SyncEntry eventSend = Find(catalog, "EventExtensionsPatch.Postfix", SyncEntryKind.PacketSend);
        Equal(SyncEntryStatus.RegisteredDisabled, eventSend.Status);
        True(!catalog.Entries.Any(entry => entry.Kind == SyncEntryKind.PacketRegistration &&
            EntryText(entry).Contains("GetRegisteredPacketFingerprint", StringComparison.Ordinal)),
            "actual catalog contains fingerprint pseudo-registration");
    }

    private const string DisabledFixture = """
        [HarmonyPatch(typeof(Target), nameof(Target.Receive))]
        public static class DisabledPatch
        {
            [HarmonyPostfix]
            public static void Postfix()
            {
                return;
                PacketSender.Send(new DisabledPacket());
            }
        }
        public sealed class DisabledPacket : IPacket { }
        [HarmonyPatch(typeof(Target), nameof(Target.Receive))]
        public static class ConditionalPatch
        {
            [HarmonyPostfix]
            public static void Postfix(bool enabled)
            {
                if (enabled) return;
                PacketSender.Send(new ConditionalPacket());
            }
        }
        public sealed class ConditionalPacket : IPacket { }
        """;

    private const string ReflectionPacketFixture = """
        public interface IPacket { }
        public interface IPacketSkipsRegistration { }
        public interface IInheritedPacket : IPacket { }
        public sealed class ConcretePacket : IPacket { }
        public sealed class IndirectPacket : IInheritedPacket { }
        public abstract class AbstractPacket : IPacket { }
        public interface InterfacePacket : IPacket { }
        public sealed class SkippedPacket : IPacket, IPacketSkipsRegistration { }
        public static class PacketRegistry
        {
            public static void RegisterDefaults() { }
        }
        public static class PacketRegistrationHelper
        {
            public static void AutoRegisterPackets() { }
        }
        public static class ReflectionBootstrap
        {
            public static void Start()
            {
                PacketRegistry.RegisterDefaults();
                PacketRegistrationHelper.AutoRegisterPackets();
            }
        }
        """;

    private const string FingerprintFixture = """
        public interface IPacket { }
        public sealed class FingerprintPacket : IPacket { }
        public static class PacketRegistry
        {
            public static void RegisterDefaults() { }
            public static int GetRegisteredPacketFingerprint() => 0;
        }
        public static class PacketRegistrationHelper
        {
            public static void AutoRegisterPackets() { }
        }
        public static class FingerprintBootstrap
        {
            public static void Start()
            {
                PacketRegistry.RegisterDefaults();
                PacketRegistrationHelper.AutoRegisterPackets();
                _ = PacketRegistry.GetRegisteredPacketFingerprint();
            }
        }
        """;

    private static string Patch(string name)
    {
        return $$"""
            [HarmonyPatch(typeof(Target), nameof(Target.Receive))]
            public static class {{name}}
            {
                [HarmonyPostfix]
                public static void Postfix()
                {
                    PacketSender.Send(new {{name}}Packet());
                }
            }
            public sealed class {{name}}Packet : IPacket { }
            """;
    }

    private static SyncEntry Find(SyncCatalogScan catalog, string symbol, SyncEntryKind kind)
    {
        return catalog.Entries.Single(entry => entry.Kind == kind &&
            entry.FullyQualifiedSymbol.Contains(symbol, StringComparison.Ordinal));
    }

    private static void EqualStatus(
        SyncCatalogScan catalog, string symbol, SyncEntryStatus expected)
    {
        SyncEntry[] entries = catalog.Entries
            .Where(entry => EntryText(entry).Contains(symbol, StringComparison.Ordinal))
            .ToArray();
        True(entries.Length > 0, $"{symbol} has no catalog entries");
        True(entries.All(entry => entry.Status == expected),
            $"{symbol} expected all entries {expected}, actual " +
            string.Join(", ", entries.Select(entry => entry.Status)));
    }

    private static string EntryText(SyncEntry entry)
    {
        return string.Join("\n", entry.FullyQualifiedSymbol,
            entry.ResolvedTargetSignature, entry.Bootstrap);
    }

    private static string FindRepositoryRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "Shared")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "ONI_Together")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new InvalidOperationException("ONI Together repository root was not found");
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
}
