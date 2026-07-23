using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class SyncCatalogCliTests
{
    private const string FixtureProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
          </PropertyGroup>
          <ItemGroup><Compile Include="Fixture.cs" /></ItemGroup>
        </Project>
        """;

    private const string FixtureSource = """
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

    public static void CatalogCliWritesInventory()
    {
        string root = CreateFixture(out string project, out string gameLibs);
        try
        {
            string inventory = Path.Combine(root, "sync-entry-inventory.json");
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = SyncCatalogCli.Run(Arguments(project, inventory, gameLibs),
                stdout, stderr);

            Equal(0, exitCode, stderr.ToString());
            True(File.Exists(inventory), "catalog CLI did not write inventory");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(inventory));
            True(document.RootElement.TryGetProperty("digest", out _),
                "inventory digest is missing");
            True(document.RootElement.GetProperty("entries").GetArrayLength() > 0,
                "inventory entries are missing");
            string summary = stdout.ToString();
            True(summary.Contains("digest", StringComparison.OrdinalIgnoreCase),
                "CLI output does not contain digest summary");
            True(summary.Contains("entry", StringComparison.OrdinalIgnoreCase),
                "CLI output does not contain entry summary");
            True(summary.Contains("variant", StringComparison.OrdinalIgnoreCase),
                "CLI output does not contain variant summary");
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    public static void CatalogCliRejectsInvalidOptions()
    {
        string root = CreateFixture(out string project, out string gameLibs);
        try
        {
            string missingInventory = Path.Combine(root, "missing.json");
            ExpectFailure(
                new[] { "catalog", "--project", project, "--game-libs", gameLibs },
                missingInventory, "missing option");
            string unknownInventory = Path.Combine(root, "unknown.json");
            ExpectFailure(
                Arguments(project, unknownInventory, gameLibs, "--unknown"),
                unknownInventory, "unknown option");
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    public static void CatalogCliRejectsMissingPaths()
    {
        string root = CreateFixture(out string project, out string gameLibs);
        try
        {
            string missingProjectInventory = Path.Combine(root, "missing-project.json");
            ExpectFailure(
                Arguments(Path.Combine(root, "does-not-exist.csproj"),
                    missingProjectInventory, gameLibs),
                missingProjectInventory, "missing project");
            string missingLibsInventory = Path.Combine(root, "missing-libs.json");
            ExpectFailure(
                Arguments(project, missingLibsInventory,
                    Path.Combine(root, "does-not-exist-libs")),
                missingLibsInventory, "missing game libs");
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    public static void CatalogCliRejectsInvalidCoverage()
    {
        string root = CreateFixture(out string project, out string gameLibs);
        try
        {
            string inventory = Path.Combine(root, "coverage-invalid.json");
            string coverage = Path.Combine(root, "coverage.json");
            File.WriteAllText(coverage, "{\"entries\":[]}");
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = SyncCatalogCli.Run(
                Arguments(project, inventory, gameLibs, "--coverage", coverage),
                stdout, stderr);

            True(exitCode != 0, "invalid coverage unexpectedly succeeded");
            True(!File.Exists(inventory),
                "invalid coverage wrote an inventory before failing");
        }
        finally
        {
            DeleteFixture(root);
        }
    }

    private static string[] Arguments(string project, string inventory, string gameLibs,
        params string[] extra)
    {
        var args = new List<string>
        {
            "catalog", "--project", project, "--inventory", inventory,
            "--game-libs", gameLibs
        };
        args.AddRange(extra);
        return args.ToArray();
    }

    private static void ExpectFailure(
        IReadOnlyList<string> args, string inventory, string description)
    {
        int exitCode = SyncCatalogCli.Run(args, new StringWriter(), new StringWriter());
        True(exitCode != 0, $"{description} unexpectedly succeeded");
        True(!File.Exists(inventory), $"{description} wrote an inventory");
    }

    private static string CreateFixture(out string projectPath, out string gameLibs)
    {
        string root = Directory.CreateTempSubdirectory("oni-catalog-cli-").FullName;
        projectPath = Path.Combine(root, "Fixture.csproj");
        File.WriteAllText(projectPath, FixtureProject);
        File.WriteAllText(Path.Combine(root, "Fixture.cs"), FixtureSource);
        gameLibs = Path.Combine(root, "game-libs");
        Directory.CreateDirectory(gameLibs);
        return root;
    }

    private static void DeleteFixture(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Equal(int expected, int actual, string message)
    {
        if (expected != actual)
            throw new InvalidOperationException(
                $"{message}; expected {expected}, actual {actual}");
    }
}
