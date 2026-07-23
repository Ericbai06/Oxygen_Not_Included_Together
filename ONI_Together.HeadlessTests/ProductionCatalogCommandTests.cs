using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class ProductionCatalogCommandTests
{
    private const string GameLibsFolder =
        "/Users/eric/Library/Application Support/Steam/steamapps/common/" +
        "OxygenNotIncluded/OxygenNotIncluded.app/Contents/Resources/Data/Managed";

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

    private const string CommandFixture = """
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
        public static class PacketRegistry
        {
            public static void Register<T>() where T : IPacket, new() { }
        }
        public static class PacketSender
        {
            public static void Send(IPacket packet) { }
        }
        public static class FixtureBootstrap
        {
            public static void Register() => PacketRegistry.Register<FixturePacket>();
            public static void Send(IPacket packet) => PacketSender.Send(packet);
        }
        """;

    private const string MinimalProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="Fixture.cs" />
          </ItemGroup>
        </Project>
        """;

    private const string HostOsProps = """
        <Project>
          <PropertyGroup>
            <DefineConstants>$(DefineConstants);OS_MAC;HOST_DIRECTORY_PROPS</DefineConstants>
          </PropertyGroup>
        </Project>
        """;

    public static void LoaderIsolatesRequestedOsSymbols()
    {
        string parent = Directory.CreateTempSubdirectory("oni-catalog-os-props-").FullName;
        string root = Path.Combine(parent, "child");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(parent, "Directory.Build.props"), HostOsProps);
            string projectPath = WriteProject(root, MinimalProject, CommandFixture);
            IReadOnlyList<SyncVariantInput> inputs = SyncMsBuildProjectLoader.Load(
                projectPath, AllVariants);

            Equal(AllVariants.Length, inputs.Count);
            foreach (SyncVariantInput input in inputs)
            {
                string[] osSymbols = input.Variant.Symbols
                    .Where(symbol => symbol.StartsWith("OS_", StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                EqualSequence(new[] { input.Variant.Platform }, osSymbols);
                bool isDebug = input.Variant.Configuration == "Debug";
                Equal(isDebug, input.Variant.Symbols.Contains("DEBUG"));
            }
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    public static void CatalogCommandWritesDeterministicInventoryAndValidatesCoverage()
    {
        string root = Directory.CreateTempSubdirectory("oni-catalog-command-").FullName;
        try
        {
            string projectPath = WriteProject(root, MinimalProject, CommandFixture);
            string firstInventory = Path.Combine(root, "inventory-a.json");
            SyncCatalogCommandResult first = SyncCatalogCommand.Run(
                Options(projectPath, firstInventory));

            True(first.Catalog.Entries.Count > 0, "command catalog has no entries");
            Equal(0, first.Catalog.Errors.Count);
            True(File.Exists(firstInventory), "command did not write inventory");
            string firstJson = File.ReadAllText(firstInventory);
            string coveragePath = Path.Combine(root, "coverage.json");
            File.WriteAllText(coveragePath, CoverageJson(first.Catalog,
                "headless:catalog-command", first.InventoryDigest));

            string secondInventory = Path.Combine(root, "inventory-b.json");
            SyncCatalogCommandResult second = SyncCatalogCommand.Run(
                Options(projectPath, secondInventory, coveragePath));
            Equal(firstJson, File.ReadAllText(secondInventory));
            Equal(first.InventoryDigest, second.InventoryDigest);
            Equal(0, second.CoverageErrors.Count);

            string invalidCoveragePath = Path.Combine(root, "coverage-invalid.json");
            File.WriteAllText(invalidCoveragePath, CoverageJson(first.Catalog,
                "unknown:test-id", first.InventoryDigest));
            SyncCatalogCommandResult invalid = SyncCatalogCommand.Run(
                Options(projectPath, Path.Combine(root, "inventory-invalid.json"),
                    invalidCoveragePath));
            True(invalid.CoverageErrors.Any(error =>
                error.Code == "manifest_unknown_test_id"),
                "coverage validation did not fail closed for unknown test ID");

            using JsonDocument document = JsonDocument.Parse(firstJson);
            True(document.RootElement.TryGetProperty("digest", out JsonElement digest) &&
                digest.GetString() == first.InventoryDigest,
                "inventory digest does not match command result");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void CatalogCommandRejectsCoverageInventoryDigestMismatch()
    {
        string root = Directory.CreateTempSubdirectory(
            "oni-catalog-inventory-digest-").FullName;
        try
        {
            string projectPath = WriteProject(root, MinimalProject, CommandFixture);
            SyncCatalogCommandResult baseline = SyncCatalogCommand.Run(Options(
                projectPath, Path.Combine(root, "inventory.json")));
            string coveragePath = Path.Combine(root, "coverage-mismatch.json");
            File.WriteAllText(coveragePath, CoverageJson(
                baseline.Catalog, "headless:catalog-command", new string('9', 64)));

            SyncCatalogCommandResult result = SyncCatalogCommand.Run(Options(
                projectPath, Path.Combine(root, "inventory-mismatch.json"), coveragePath));

            True(result.CoverageErrors.Any(error =>
                error.Code == "coverage_inventory_digest_mismatch"),
                "coverage inventory digest drift was accepted");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void CatalogCommandRejectsMalformedAndUnresolvedProjects()
    {
        string malformedRoot = Directory.CreateTempSubdirectory("oni-catalog-malformed-").FullName;
        string unresolvedRoot = Directory.CreateTempSubdirectory("oni-catalog-unresolved-").FullName;
        try
        {
            string malformedProject = Path.Combine(malformedRoot, "Malformed.csproj");
            File.WriteAllText(malformedProject, "<Project><PropertyGroup>");
            ExpectThrows(() => SyncCatalogCommand.Run(
                Options(malformedProject, Path.Combine(malformedRoot, "inventory.json"))),
                "malformed project was accepted");

            string unresolvedProject = Path.Combine(unresolvedRoot, "Unresolved.csproj");
            File.WriteAllText(unresolvedProject, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="does-not-exist.cs" /></ItemGroup>
                </Project>
                """);
            ExpectThrows(() => SyncCatalogCommand.Run(
                Options(unresolvedProject, Path.Combine(unresolvedRoot, "inventory.json"))),
                "unresolved Compile item was accepted");
        }
        finally
        {
            Directory.Delete(malformedRoot, recursive: true);
            Directory.Delete(unresolvedRoot, recursive: true);
        }
    }

    public static void ActualOniProjectCatalogAudit()
    {
        string repositoryRoot = FindRepositoryRoot();
        string projectPath = Path.Combine(repositoryRoot, "ONI_Together", "ONI_Together.csproj");
        True(File.Exists(projectPath), $"ONI project was not found: {projectPath}");
        True(Directory.Exists(GameLibsFolder),
            $"GameLibsFolder is unavailable: {GameLibsFolder}");
        True(File.Exists(Path.Combine(GameLibsFolder, "Assembly-CSharp.dll")),
            "GameLibsFolder does not contain Assembly-CSharp.dll");

        IReadOnlyList<SyncVariantInput> inputs = SyncMsBuildProjectLoader.Load(
            projectPath, AllVariants, new Dictionary<string, string>
            {
                ["GameLibsFolder"] = GameLibsFolder
            });
        Equal(AllVariants.Length, inputs.Count);
        foreach (SyncVariantInput input in inputs)
        {
            True(input.MetadataReferences.Count > 0,
                $"{input.Variant.Key} has no metadata references");
            True(input.MetadataReferences.All(File.Exists),
                $"{input.Variant.Key} contains missing metadata references");
        }

        SyncCatalogScan catalog = SyncSurfaceScanner.ScanCatalogVariants(inputs);
        True(catalog.Entries.Count > 0, "actual ONI catalog has no entries");
        SyncEntryKind[] missingKinds = Enum.GetValues<SyncEntryKind>()
            .Except(catalog.Entries.Select(entry => entry.Kind))
            .ToArray();
        True(missingKinds.Length == 0,
            $"actual ONI catalog is missing kinds: {string.Join(",", missingKinds)}");
        string[] expectedVariants = AllVariants.Select(variant => variant.Key)
            .Order(StringComparer.Ordinal).ToArray();
        string[] actualVariants = catalog.Entries.SelectMany(entry => entry.Variants)
            .Select(variant => variant.Key).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        EqualSequence(expectedVariants, actualVariants);
        True(!catalog.Errors.Any(error => error.Code is "catalog_compilation_error" or
            "catalog_metadata_reference_missing" or "catalog_metadata_reference_invalid"),
            "actual ONI catalog has compilation or metadata reference errors");
        True(!catalog.Entries.Any(entry =>
            entry.Status == SyncEntryStatus.Active &&
            (entry.Kind is SyncEntryKind.PacketRegistration or SyncEntryKind.PacketSend or
                SyncEntryKind.PacketRelay or SyncEntryKind.PacketDeserialize or
                SyncEntryKind.PacketDispatch or SyncEntryKind.HarmonyPatch) &&
            string.IsNullOrWhiteSpace(entry.ResolvedTargetSignature)),
            "actual ONI catalog has unresolved active packet/Harmony targets");
    }

    private static SyncCatalogCommandOptions Options(
        string projectPath, string inventoryPath, string? coveragePath = null)
    {
        return new SyncCatalogCommandOptions
        {
            ProjectPath = projectPath,
            Variants = AllVariants,
            InventoryPath = inventoryPath,
            CoveragePath = coveragePath,
            GlobalProperties = new Dictionary<string, string>(),
            KnownTestIds = new HashSet<string>(["headless:catalog-command"]),
            KnownScenarioIds = new HashSet<string>(["catalog-fixture"])
        };
    }

    private static string CoverageJson(
        SyncCatalogScan catalog, string testId, string inventoryDigest)
    {
        var entries = catalog.Entries.Select(entry => new
        {
            id = entry.Id,
            domain = "catalog-fixture",
            testIds = new[] { testId },
            negativeTestIds = Array.Empty<string>(),
            scenarioIds = new[] { "catalog-fixture" },
            variants = entry.Variants.Select(variant => variant.Key)
                .Order(StringComparer.Ordinal).ToArray(),
            status = entry.Status.ToString()
        });
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            inventoryDigest,
            entries,
        });
    }

    private static string WriteProject(string root, string project, string source)
    {
        string projectPath = Path.Combine(root, "Fixture.csproj");
        File.WriteAllText(projectPath, project);
        File.WriteAllText(Path.Combine(root, "Fixture.cs"), source);
        return projectPath;
    }

    private static SyncBuildVariant Variant(
        string configuration, string platform, params string[] symbols)
    {
        return new SyncBuildVariant(configuration, platform,
            new HashSet<string>(symbols, StringComparer.Ordinal));
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
        throw new InvalidOperationException("ONI_Together repository root was not found");
    }

    private static void ExpectThrows(Action action, string message)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            return;
        }
        throw new InvalidOperationException(message);
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
