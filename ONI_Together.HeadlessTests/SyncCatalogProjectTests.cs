using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class SyncCatalogProjectTests
{
    private static readonly SyncBuildVariant[] AllVariants =
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

    private const string VariantFixture = """
        using System;
        public interface IPacket { }
        public sealed class FixturePacket : IPacket { }
        public static class PacketRegistry
        {
            public static void Register<T>() { }
        }
        public static class PacketSender
        {
            public static void Send(IPacket packet) { }
        }
        #if DEBUG
        public static class DebugOnlySurface
        {
            public static void Register() => PacketRegistry.Register<FixturePacket>();
        }
        #endif
        #if OS_MAC
        public static class MacOnlySurface
        {
            public static void Register() => PacketRegistry.Register<FixturePacket>();
        }
        #endif
        #if OS_WINDOWS
        public static class WindowsOnlySurface
        {
            public static void Register() => PacketRegistry.Register<FixturePacket>();
        }
        #endif
        #if OS_LINUX
        public static class LinuxOnlySurface
        {
            public static void Register() => PacketRegistry.Register<FixturePacket>();
        }
        #endif
        #if OS_FREEBSD
        public static class FreeBsdOnlySurface
        {
            public static void Register() => PacketRegistry.Register<FixturePacket>();
        }
        #endif
        #if !DEBUG
        public static class ReleaseOnlySurface
        {
            public static void Send() => PacketSender.Send(new FixturePacket());
        }
        #endif
        """;

    private const string PacketChainFixture = """
        using System.IO;
        public interface IPacket
        {
            void Deserialize(BinaryReader reader);
            void OnDispatched();
        }
        public sealed class FixturePacket : IPacket
        {
            public FixturePacket() { }
            public void Deserialize(BinaryReader reader) { }
            public void OnDispatched() { }
        }
        public sealed class PacketEnvelope
        {
            public PacketEnvelope(IPacket packet) => Packet = packet;
            public IPacket Packet { get; }
        }
        public static class PacketRegistry
        {
            public static void Register<T>() where T : IPacket, new() { }
        }
        public static class PacketSender
        {
            public static void Send(IPacket packet) { }
        }
        public static class PacketRelay
        {
            public static void SendEnvelope(PacketEnvelope envelope) { }
        }
        public static class PacketWire
        {
            public static void Read(BinaryReader reader)
            {
                IPacket packet = new FixturePacket();
                packet.Deserialize(reader);
            }
            public static void Dispatch(PacketEnvelope envelope)
            {
                envelope.Packet.OnDispatched();
            }
        }
        public static class PacketBootstrap
        {
            public static void Register() => PacketRegistry.Register<FixturePacket>();
            public static void Send(IPacket packet) => PacketSender.Send(packet);
            public static void Relay(IPacket packet) =>
                PacketRelay.SendEnvelope(new PacketEnvelope(packet));
            public static void Read(BinaryReader reader) => PacketWire.Read(reader);
            public static void Dispatch(PacketEnvelope envelope) => PacketWire.Dispatch(envelope);
        }
        """;

    private const string InventoryFixture = """
        public interface IPacket { }
        public sealed class InventoryPacket : IPacket { }
        public static class PacketRegistry
        {
            public static void Register<T>() { }
        }
        public static class PacketSender
        {
            public static void Send(IPacket packet) { }
        }
        public static class InventoryBootstrap
        {
            public static void Register() => PacketRegistry.Register<InventoryPacket>();
            public static void Send(IPacket packet) => PacketSender.Send(packet);
        }
        """;

    private const string ConditionalProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
            <DefineConstants>$(DefineConstants);FIXTURE_BASE</DefineConstants>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="Shared.cs" />
            <Compile Include="DebugOnly.cs" Condition="'$(Configuration)' == 'Debug'" />
            <Compile Include="ReleaseOnly.cs" Condition="'$(Configuration)' == 'Release'" />
            <Compile Include="MacOnly.cs" Condition="'$(Platform)' == 'OS_MAC'" />
            <Compile Include="WindowsOnly.cs" Condition="'$(Platform)' == 'OS_WINDOWS'" />
            <Compile Include="LinuxOnly.cs" Condition="'$(Platform)' == 'OS_LINUX'" />
            <Compile Include="FreeBsdOnly.cs" Condition="'$(Platform)' == 'OS_FREEBSD'" />
            <Compile Include="Excluded.cs" />
            <Compile Remove="Excluded.cs" Condition="'$(Configuration)' == 'Debug' Or '$(Configuration)' == 'Release'" />
          </ItemGroup>
        </Project>
        """;

    public static void CatalogEvaluatesEightBuildVariants()
    {
        SyncCatalogScan catalog = ScanVariants(VariantFixture);
        string[] expected = AllVariants.Select(item => item.Key)
            .Order(StringComparer.Ordinal).ToArray();
        string[] actual = catalog.Entries.SelectMany(item => item.Variants)
            .Select(item => item.Key).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();

        EqualSequence(expected, actual);
        AssertSurfaceVariant(catalog, "DebugOnlySurface", "Debug/");
        AssertSurfaceVariant(catalog, "ReleaseOnlySurface", "Release/");
        AssertSurfaceVariant(catalog, "MacOnlySurface", "/OS_MAC");
        AssertSurfaceVariant(catalog, "WindowsOnlySurface", "/OS_WINDOWS");
        AssertSurfaceVariant(catalog, "LinuxOnlySurface", "/OS_LINUX");
        AssertSurfaceVariant(catalog, "FreeBsdOnlySurface", "/OS_FREEBSD");
    }

    public static void MsBuildLoaderEvaluatesCompileItemsAndReferences()
    {
        string root = Directory.CreateTempSubdirectory("oni-catalog-project-").FullName;
        try
        {
            string projectPath = WriteConditionalProject(root);
            IReadOnlyList<SyncVariantInput> inputs =
                SyncMsBuildProjectLoader.Load(projectPath, AllVariants);
            Equal(AllVariants.Length, inputs.Count);

            SyncVariantInput debugMac = inputs.Single(item => item.Variant.Key == "Debug/OS_MAC");
            SyncVariantInput releaseMac = inputs.Single(item => item.Variant.Key == "Release/OS_MAC");
            AssertSource(debugMac, root, "Shared.cs");
            AssertSource(debugMac, root, "DebugOnly.cs");
            AssertAbsent(debugMac, root, "ReleaseOnly.cs");
            AssertAbsent(debugMac, root, "Excluded.cs");
            AssertSource(releaseMac, root, "ReleaseOnly.cs");
            AssertAbsent(releaseMac, root, "DebugOnly.cs");
            AssertAbsent(releaseMac, root, "Excluded.cs");
            True(debugMac.Variant.Symbols.Contains("DEBUG"), "DEBUG symbol was not evaluated");
            True(debugMac.Variant.Symbols.Contains("FIXTURE_BASE"), "DefineConstants was not evaluated");
            True(!releaseMac.Variant.Symbols.Contains("DEBUG"), "Release retained DEBUG symbol");
            True(debugMac.Sources.Keys.All(Path.IsPathFullyQualified), "source path was not canonical");
            True(debugMac.MetadataReferences.Count > 0, "metadata references were not loaded");
            True(debugMac.MetadataReferences.All(Path.IsPathFullyQualified),
                "metadata reference path was not canonical");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void CatalogIncludesFullPacketTransportChain()
    {
        SyncCatalogScan catalog = ScanVariants(PacketChainFixture);
        SyncEntry[] packets = catalog.Entries
            .Where(entry => entry.FullyQualifiedSymbol.Contains("Packet", StringComparison.Ordinal) ||
                entry.Bootstrap.Contains("Packet", StringComparison.Ordinal))
            .ToArray();

        True(packets.Any(entry => entry.Kind == SyncEntryKind.PacketRegistration),
            "packet registration entry is missing");
        True(packets.Any(entry => entry.Kind == SyncEntryKind.PacketSend),
            "packet send entry is missing");
        True(packets.Any(entry => entry.Kind == SyncEntryKind.PacketRelay &&
            EntryText(entry).Contains("Relay", StringComparison.Ordinal)),
            "packet relay/envelope entry is missing");
        True(packets.Any(entry => entry.Kind == SyncEntryKind.PacketDeserialize &&
            entry.ResolvedTargetSignature.Contains("Deserialize", StringComparison.Ordinal)),
            "packet Deserialize entry is missing");
        True(packets.Any(entry => entry.Kind == SyncEntryKind.PacketDispatch &&
            EntryText(entry).Contains("OnDispatched", StringComparison.Ordinal)),
            "packet OnDispatched/dispatch entry is missing");
    }

    public static void InventorySerializationIsDeterministicAndComplete()
    {
        string shifted = "\n\n\n" + InventoryFixture;
        SyncVariantInput firstInput = Input(AllVariants[0], new Dictionary<string, string>
        {
            ["z.cs"] = InventoryFixture,
            ["a.cs"] = "public static class EmptyA { }"
        });
        SyncVariantInput secondInput = Input(AllVariants[0], new Dictionary<string, string>
        {
            ["a.cs"] = "public static class EmptyA { }",
            ["z.cs"] = shifted
        });

        string first = SyncInventoryJson.Serialize(
            SyncSurfaceScanner.ScanCatalogVariants(new[] { firstInput }));
        string second = SyncInventoryJson.Serialize(
            SyncSurfaceScanner.ScanCatalogVariants(new[] { secondInput }));
        Equal(first, second);

        using JsonDocument document = JsonDocument.Parse(first);
        JsonElement root = document.RootElement;
        True(root.TryGetProperty("schemaVersion", out _), "inventory schemaVersion is missing");
        True(root.TryGetProperty("digest", out JsonElement digest) &&
            digest.ValueKind == JsonValueKind.String && digest.GetString() is { Length: > 0 },
            "inventory digest is missing");
        JsonElement entries = root.GetProperty("entries");
        True(entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() > 0,
            "inventory entries are missing");
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            foreach (string field in new[]
            {
                "id", "kind", "fullyQualifiedSymbol", "resolvedTargetSignature",
                "bootstrap", "variants", "status"
            })
                True(entry.TryGetProperty(field, out _), $"inventory field {field} is missing");
        }
    }

    public static void CoverageValidatorFailsClosedForExecutionAndRuntimeLayers()
    {
        SyncCatalogScan catalog = ScanVariants(InventoryFixture);
        SyncEntry entry = catalog.Entries.First(item =>
            item.Status == SyncEntryStatus.Active &&
            !string.IsNullOrWhiteSpace(item.ResolvedTargetSignature));

        string noExecution = CoverageJson(entry, Array.Empty<string>(), null);
        ExpectCoverageError(catalog, noExecution, "manifest_missing_execution_test");

        string unityOnly = CoverageJson(entry, new[] { "headless:catalog" }, "prefab requires ONI");
        ExpectCoverageError(catalog, unityOnly, "unity_only_missing_runtime_test");

        string unknownScenario = CoverageJson(entry, new[] { "headless:catalog" }, null,
            scenario: "unknown-scenario");
        ExpectCoverageError(catalog, unknownScenario, "manifest_unknown_scenario_id");

        SyncBuildVariant variant = AllVariants[0];
        SyncEntry duplicateA = new("sync:duplicate", SyncEntryKind.PacketSend,
            "Duplicate.A", "Duplicate.A()", "fixture", new[] { variant }, SyncEntryStatus.Active);
        SyncEntry duplicateB = duplicateA with { FullyQualifiedSymbol = "Duplicate.B" };
        SyncCatalogScan duplicateCatalog = new(new[] { duplicateA, duplicateB }, Array.Empty<SurfaceError>());
        string duplicateManifest = CoverageJson(duplicateA, new[] { "headless:catalog" }, null);
        ExpectCoverageError(duplicateCatalog, duplicateManifest, "catalog_duplicate_id");
    }

    public static void RepeatedCallsitesHaveDistinctStableIds()
    {
        SyncCatalogScan first = ScanVariants(RepeatedCallsiteFixture);
        SyncCatalogScan shifted = ScanVariants("\n\n" + RepeatedCallsiteFixture);
        string[] firstIds = CallsiteIds(first);
        string[] shiftedIds = CallsiteIds(shifted);

        Equal(2, firstIds.Length);
        Equal(2, firstIds.Distinct(StringComparer.Ordinal).Count());
        EqualSequence(firstIds, shiftedIds);
    }

    private static readonly string RepeatedCallsiteFixture = """
        public interface IPacket { }
        public sealed class RepeatPacket : IPacket { }
        public static class PacketSender { public static void Send(IPacket packet) { } }
        public static class RepeatBootstrap
        {
            public static void Emit(IPacket packet)
            {
                PacketSender.Send(packet);
                PacketSender.Send(packet);
            }
        }
        """;

    private static SyncBuildVariant Variant(
        string configuration, string platform, params string[] symbols)
    {
        return new SyncBuildVariant(configuration, platform,
            new HashSet<string>(symbols, StringComparer.Ordinal));
    }

    private static SyncVariantInput Input(
        SyncBuildVariant variant, IReadOnlyDictionary<string, string> sources)
    {
        return new SyncVariantInput(variant, sources, Array.Empty<string>());
    }

    private static SyncCatalogScan ScanVariants(string source)
    {
        return SyncSurfaceScanner.ScanCatalogVariants(AllVariants.Select(variant =>
            Input(variant, new Dictionary<string, string> { ["Fixture.cs"] = source }))
            .ToArray());
    }

    private static void AssertSurfaceVariant(
        SyncCatalogScan catalog, string symbol, string expectedMarker)
    {
        SyncEntry entry = catalog.Entries.Single(item =>
            item.FullyQualifiedSymbol.Contains(symbol, StringComparison.Ordinal));
        True(entry.Variants.All(variant => variant.Key.Contains(expectedMarker,
            StringComparison.Ordinal)), $"{symbol} leaked outside {expectedMarker}");
        True(entry.Variants.Count > 0, $"{symbol} has no declared variant");
    }

    private static string WriteConditionalProject(string root)
    {
        string projectPath = Path.Combine(root, "ConditionalFixture.csproj");
        File.WriteAllText(projectPath, ConditionalProject);
        foreach (string file in new[]
        {
            "Shared.cs", "DebugOnly.cs", "ReleaseOnly.cs", "MacOnly.cs",
            "WindowsOnly.cs", "LinuxOnly.cs", "FreeBsdOnly.cs", "Excluded.cs"
        })
            File.WriteAllText(Path.Combine(root, file), $"public static class {Path.GetFileNameWithoutExtension(file)} {{ }}");
        return projectPath;
    }

    private static void AssertSource(SyncVariantInput input, string root, string file)
    {
        string path = Path.GetFullPath(Path.Combine(root, file));
        True(input.Sources.ContainsKey(path), $"evaluated Compile omitted {file}");
    }

    private static void AssertAbsent(SyncVariantInput input, string root, string file)
    {
        string path = Path.GetFullPath(Path.Combine(root, file));
        True(!input.Sources.ContainsKey(path), $"excluded Compile still scanned {file}");
    }

    private static string[] CallsiteIds(SyncCatalogScan catalog)
    {
        return catalog.Entries.Where(entry => entry.Kind == SyncEntryKind.PacketSend)
            .Where(entry => entry.FullyQualifiedSymbol.Contains("RepeatBootstrap.Emit", StringComparison.Ordinal))
            .Select(entry => entry.Id).Order(StringComparer.Ordinal).ToArray();
    }

    private static string CoverageJson(
        SyncEntry entry, IReadOnlyList<string> testIds, string? headlessReason,
        string scenario = "remote-dig")
    {
        var row = new Dictionary<string, object?>
        {
            ["id"] = entry.Id,
            ["domain"] = "catalog-fixture",
            ["testIds"] = testIds,
            ["negativeTestIds"] = Array.Empty<string>(),
            ["scenarioIds"] = new[] { scenario },
            ["variants"] = entry.Variants.Select(item => item.Key).ToArray(),
            ["status"] = entry.Status.ToString()
        };
        if (headlessReason is not null)
            row["headlessUnsupportedReason"] = headlessReason;
        return JsonSerializer.Serialize(new { entries = new[] { row } });
    }

    private static void ExpectCoverageError(
        SyncCatalogScan catalog, string manifestJson, string code)
    {
        SyncCoverageManifest manifest = SyncCoverageManifest.Parse(manifestJson);
        IReadOnlyList<SurfaceError> errors = SyncCoverageValidator.Validate(
            catalog, manifest,
            new HashSet<string>(["headless:catalog", "ingame:catalog", "real:catalog"]),
            new HashSet<string>(["remote-dig"]));
        True(errors.Any(error => error.Code == code),
            $"expected coverage error {code}; got {string.Join(",", errors.Select(error => error.Code))}");
    }

    private static string EntryText(SyncEntry entry)
    {
        return string.Join("|", entry.Id, entry.Kind, entry.FullyQualifiedSymbol,
            entry.ResolvedTargetSignature, entry.Bootstrap);
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
