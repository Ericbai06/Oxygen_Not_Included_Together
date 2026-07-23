using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;

namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestBatchFixture
{
    private const string AttributeName =
        "ONI_Together.DebugTools.UnitTestAttribute";

    internal static ActualDebugUnitTestBatchInput Load(
        string? gameLibsOverride = null)
    {
        IActualDebugUnitTestRuntimeClassifier classifier =
            ActualDebugUnitTestRuntimeClassifierLoader.Load();
        string root = RepositoryRoot();
        string gameLibs = Path.GetFullPath(ResolveGameLibs(gameLibsOverride));
        if (!Directory.Exists(gameLibs))
            throw new InvalidOperationException("ONI_GAME_LIBS is required");
        string project = Path.Combine(
            root, "ONI_Together", "ONI_Together.csproj");
        IReadOnlyList<SyncVariantInput> inputs = SyncMsBuildProjectLoader.Load(
            project, Variants(), new Dictionary<string, string> {
                ["GameLibsFolder"] = gameLibs
            });
        SyncCatalogScan catalog = SyncSurfaceScanner.ScanCatalogVariants(inputs);
        if (catalog.Errors.Count != 0)
            throw new InvalidOperationException(
                "current eight-variant catalog contains errors");
        string inventory = SyncInventoryJson.Serialize(catalog);
        using JsonDocument json = JsonDocument.Parse(inventory);
        string inventoryDigest = json.RootElement.GetProperty("digest")
            .GetString()!;
        SyncCoverageManifest coverage = SyncCoverageManifest.Parse(
            File.ReadAllText(Path.Combine(root, "sync-entry-coverage.json")));
        string coverageDigest = coverage.CoverageDigest ??
            throw new InvalidOperationException(
                "current coverage manifest has no content-derived digest");
        string output = Path.Combine(
            root, "ONI_Together", "bin", "Debug", "netstandard2.1");
        byte[] pe = File.ReadAllBytes(
            Path.Combine(output, "ONI_Together.dll"));
        byte[] pdb = File.ReadAllBytes(
            Path.Combine(output, "ONI_Together.pdb"));
        return new ActualDebugUnitTestBatchInput(
            "actual-debug-unit-batch", new SyncExecutionFixtureAssembly(pe, pdb),
            catalog, inventoryDigest, Digest(pe), Digest(pdb),
            Discover(pe, classifier, gameLibs), RuntimeAssemblies(gameLibs),
            CoverageDigest: coverageDigest,
            GameLibsDirectory: gameLibs);
    }

    private static string ResolveGameLibs(string? overridePath) =>
        overridePath ?? Environment.GetEnvironmentVariable("ONI_GAME_LIBS") ??
        Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile),
            "Library/Application Support/Steam/steamapps/common/" +
            "OxygenNotIncluded/OxygenNotIncluded.app/Contents/Resources/" +
            "Data/Managed");

    private static IReadOnlyList<ActualDebugRuntimeAssemblyInput>
        RuntimeAssemblies(string gameLibs) =>
    [
        RuntimeAssembly(gameLibs, "Assembly-CSharp.dll"),
        RuntimeAssembly(gameLibs, "Assembly-CSharp-firstpass.dll")
    ];

    private static ActualDebugRuntimeAssemblyInput RuntimeAssembly(
        string gameLibs,
        string fileName)
    {
        string path = Path.Combine(gameLibs, fileName);
        byte[] image = File.ReadAllBytes(path);
        using var stream = new MemoryStream(image, writable: false);
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(stream);
        return new ActualDebugRuntimeAssemblyInput(
            path, image, Digest(image), assembly.Name.FullName);
    }

    internal static IReadOnlyList<ActualDebugUnitTestDescriptor> Discover(
        byte[] peImage,
        IActualDebugUnitTestRuntimeClassifier classifier,
        string? gameLibsOverride = null)
    {
        using var resolver = new DefaultAssemblyResolver();
        foreach (string directory in AssemblySearchDirectories(
                     gameLibsOverride))
            if (Directory.Exists(directory))
                resolver.AddSearchDirectory(directory);
        using var stream = new MemoryStream(peImage, writable: false);
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
            stream, new ReaderParameters { AssemblyResolver = resolver });
        return AllTypes(assembly.MainModule)
            .SelectMany(type => type.Methods)
            .Where(method => UnitTestMetadata(method) is not null)
            .Select(method => Descriptor(method, classifier))
            .OrderBy(test => test.TestId, StringComparer.Ordinal)
            .ToArray();
    }

    private static ActualDebugUnitTestDescriptor Descriptor(
        MethodDefinition method,
        IActualDebugUnitTestRuntimeClassifier classifier)
    {
        CustomAttribute metadata = UnitTestMetadata(method)!;
        string? reason = UnsupportedReason(metadata);
        var input = new ActualDebugUnitTestRuntimeClassificationInput(
            MethodSymbol(method), reason, DirectCalls(method));
        return new ActualDebugUnitTestDescriptor(
            StableId(method), input.MethodSymbol, classifier.Classify(input),
            reason);
    }

    private static CustomAttribute? UnitTestMetadata(MethodDefinition method) =>
        method.CustomAttributes.SingleOrDefault(attribute =>
            attribute.AttributeType.FullName == AttributeName);

    private static string? UnsupportedReason(CustomAttribute attribute)
    {
        object? raw = attribute.ConstructorArguments.Count > 3
            ? attribute.ConstructorArguments[3].Value
            : attribute.Properties.FirstOrDefault(property =>
                property.Name == "HeadlessUnsupportedReason").Argument.Value;
        if (raw is null)
            return null;
        string reason = raw as string ??
            throw new FormatException(
                "headlessUnsupportedReason must be a string");
        if (string.IsNullOrWhiteSpace(reason))
            throw new FormatException(
                "headlessUnsupportedReason must be nonempty");
        return reason;
    }

    private static IReadOnlyList<ActualDebugUnitTestDirectCall> DirectCalls(
        MethodDefinition method)
    {
        if (!method.HasBody)
            return [];
        return method.Body.Instructions
            .Select(instruction => instruction.Operand as MethodReference)
            .Where(reference => reference is not null)
            .Select(reference => DirectCall(reference!))
            .Distinct()
            .OrderBy(call => call.MethodSymbol, StringComparer.Ordinal)
            .ToArray();
    }

    private static ActualDebugUnitTestDirectCall DirectCall(
        MethodReference reference)
    {
        MethodDefinition? resolved = Resolve(reference);
        MethodImplAttributes implementation =
            resolved?.ImplAttributes ?? 0;
        return new ActualDebugUnitTestDirectCall(
            MethodSymbol(reference),
            resolved?.Module.Assembly.Name.Name ??
                reference.DeclaringType.Scope?.Name ?? "",
            (implementation & MethodImplAttributes.InternalCall) != 0,
            (implementation & MethodImplAttributes.Runtime) != 0,
            (implementation & MethodImplAttributes.Native) != 0,
            resolved?.PInvokeInfo is not null);
    }

    private static MethodDefinition? Resolve(MethodReference reference)
    {
        try { return reference.Resolve(); }
        catch (AssemblyResolutionException) { return null; }
    }

    private static IEnumerable<string> AssemblySearchDirectories(
        string? gameLibsOverride)
    {
        string root = RepositoryRoot();
        string gameLibs = ResolveGameLibs(gameLibsOverride);
        yield return Path.Combine(
            root, "ONI_Together", "bin", "Debug", "netstandard2.1");
        yield return Path.Combine(
            root, "Shared", "bin", "Debug", "netstandard2.1");
        yield return gameLibs;
    }

    private static string StableId(MethodDefinition method)
    {
        string digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(MethodSymbol(method))))
            .ToLowerInvariant()[..24];
        return "headless:unit:" + digest;
    }

    private static string MethodSymbol(MethodReference method)
    {
        string parameters = string.Join(",", method.Parameters.Select(
            parameter => TypeName(parameter.ParameterType.FullName)));
        return $"{TypeName(method.DeclaringType.FullName)}.{method.Name}({parameters})";
    }

    private static string TypeName(string name) =>
        name.Replace('/', '+').Replace("::", ".");

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (TypeDefinition type in module.Types)
        {
            yield return type;
            foreach (TypeDefinition nested in AllNested(type))
                yield return nested;
        }
    }

    private static IEnumerable<TypeDefinition> AllNested(TypeDefinition parent)
    {
        foreach (TypeDefinition nested in parent.NestedTypes)
        {
            yield return nested;
            foreach (TypeDefinition child in AllNested(nested))
                yield return child;
        }
    }

    private static SyncBuildVariant[] Variants() =>
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

    private static SyncBuildVariant Variant(
        string configuration,
        string platform,
        params string[] symbols) => new(
            configuration, platform, symbols.ToHashSet(StringComparer.Ordinal));

    internal static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
             directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "Shared")) &&
                Directory.Exists(Path.Combine(directory.FullName, "ONI_Together")))
                return directory.FullName;
        throw new InvalidOperationException("repository root was not found");
    }

    private static string Digest(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
