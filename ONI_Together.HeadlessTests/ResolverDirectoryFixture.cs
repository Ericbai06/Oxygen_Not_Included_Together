using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Mono.Cecil;

namespace ONI_Together.HeadlessTests;

internal sealed class ResolverDirectoryFixture : IDisposable
{
    internal const string DependencyAssemblyName =
        "Explicit.Resolver.Dependency";

    private readonly string root;
    private readonly SyncCatalogScan catalog;
    private readonly SyncExecutionFixtureAssembly assembly;
    private readonly string inventoryDigest;

    internal string CorrectGameLibsDirectory { get; }
    internal string WrongGameLibsDirectory { get; }
    internal string MissingGameLibsDirectory =>
        Path.Combine(root, "missing-game-libs");

    private ResolverDirectoryFixture(
        string root,
        FixtureData data)
    {
        this.root = root;
        catalog = data.Catalog;
        assembly = data.Assembly;
        inventoryDigest = data.InventoryDigest;
        CorrectGameLibsDirectory = Path.Combine(root, "correct-game-libs");
        WrongGameLibsDirectory = Path.Combine(root, "wrong-game-libs");
    }

    internal static ResolverDirectoryFixture Create()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "oni-explicit-resolver-" + Guid.NewGuid().ToString("N"));
        string correct = Path.Combine(root, "correct-game-libs");
        string wrong = Path.Combine(root, "wrong-game-libs");
        Directory.CreateDirectory(correct);
        Directory.CreateDirectory(wrong);
        string dependencyPath = Path.Combine(
            correct, DependencyAssemblyName + ".dll");
        File.WriteAllBytes(dependencyPath, Compile(
            DependencyAssemblyName, DependencySource, []).PeImage);
        SyncExecutionFixtureAssembly assembly = Compile(
            "Explicit.Resolver.Consumer", ConsumerSource,
            [MetadataReference.CreateFromFile(dependencyPath)]);
        SyncCatalogScan catalog = SyncCatalogSourceScanner.Scan(
            new Dictionary<string, string>
            {
                ["ResolverConsumer.cs"] = CatalogSource
            },
            [new SyncBuildVariant("Debug", "OS_MAC",
                new HashSet<string>(["DEBUG", "OS_MAC"]))]);
        if (catalog.Errors.Count != 0)
            throw new InvalidOperationException(string.Join("; ", catalog.Errors));
        using JsonDocument inventory = JsonDocument.Parse(
            SyncInventoryJson.Serialize(catalog));
        string digest = inventory.RootElement.GetProperty("digest").GetString()!;
        return new ResolverDirectoryFixture(
            root, new FixtureData(catalog, assembly, digest));
    }

    internal ActualDebugUnitTestInstrumentationCacheInput CacheInput(
        string gameLibsDirectory,
        string cacheName) => new()
    {
        Catalog = catalog,
        Assembly = assembly,
        InventoryDigest = inventoryDigest,
        CacheDirectory = Path.Combine(root, "cache", cacheName),
        GameLibsDirectory = gameLibsDirectory
    };

    internal void ValidateExternalReferenceIsIsolated()
    {
        using var stream = new MemoryStream(
            assembly.PeImage, writable: false);
        using AssemblyDefinition definition =
            AssemblyDefinition.ReadAssembly(stream);
        if (!definition.MainModule.AssemblyReferences.Any(reference =>
                reference.Name == DependencyAssemblyName))
            throw new InvalidOperationException(
                "resolver fixture lacks external assembly reference");
        if (!File.Exists(Path.Combine(
                CorrectGameLibsDirectory, DependencyAssemblyName + ".dll")) ||
            File.Exists(Path.Combine(
                WrongGameLibsDirectory, DependencyAssemblyName + ".dll")))
            throw new InvalidOperationException(
                "resolver fixture dependency isolation is invalid");
    }

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
    }

    private static SyncExecutionFixtureAssembly Compile(
        string assemblyName,
        string source,
        IReadOnlyList<MetadataReference> additionalReferences)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            SourceText.From(source, Encoding.UTF8), path: assemblyName + ".cs");
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName, [tree],
            SyncSurfaceScanner.PlatformReferences()
                .Concat(additionalReferences),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        EmitResult result = compilation.Emit(pe, pdb, options: new EmitOptions(
            debugInformationFormat: DebugInformationFormat.PortablePdb));
        if (!result.Success)
            throw new InvalidOperationException(string.Join("; ",
                result.Diagnostics.Where(diagnostic =>
                    diagnostic.Severity == DiagnosticSeverity.Error)));
        return new SyncExecutionFixtureAssembly(pe.ToArray(), pdb.ToArray());
    }

    private const string DependencySource = """
        namespace ExplicitResolver;
        public static class Dependency
        {
            public static void Touch() { }
        }
        """;

    private const string ConsumerSource = """
        using System.IO;
        using ExplicitResolver;

        public interface IPacket
        {
            void Serialize(BinaryWriter writer);
            void Deserialize(BinaryReader reader);
            void OnDispatched();
        }

        public sealed class ProbePacket : IPacket
        {
            public void Serialize(BinaryWriter writer) { }
            public void Deserialize(BinaryReader reader) { }
            public void OnDispatched() { }
        }

        public static class PacketSender
        {
            public static void Send(IPacket packet) { }
        }

        public static class PacketRuntime
        {
            public static void Run()
            {
                Dependency.Touch();
                PacketSender.Send(new ProbePacket());
            }
        }
        """;

    private const string CatalogSource = ConsumerSource + """

        namespace ExplicitResolver
        {
            public static class Dependency
            {
                public static void Touch() { }
            }
        }
        """;

    private sealed record FixtureData(
        SyncCatalogScan Catalog,
        SyncExecutionFixtureAssembly Assembly,
        string InventoryDigest);
}
