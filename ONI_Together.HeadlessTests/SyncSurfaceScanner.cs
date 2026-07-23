using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ONI_Together.HeadlessTests;

internal sealed record PacketSurface(string Name, string Direction);
internal sealed record HarmonyPatchSurface(string Name, IReadOnlyList<string> Entrypoints);
internal sealed record SurfaceError(string Code, string Subject);
internal sealed record SyncSurfaceScan(
    IReadOnlyList<PacketSurface> Packets,
    IReadOnlyList<HarmonyPatchSurface> HarmonyPatches,
    IReadOnlyList<SurfaceError> Errors);

internal static class SyncSurfaceScanner
{
    private static readonly HashSet<string> PatchEntrypoints =
    [
        "Prefix",
        "Postfix",
        "Transpiler",
        "Finalizer",
        "TargetMethod",
        "TargetMethods",
        "Prepare",
        "Cleanup"
    ];

    private static readonly HashSet<string> ExcludedDirectories =
    [
        ".git",
        ".omo",
        ".codex_tmp",
        ".codex-tmp",
        "bin",
        "obj",
        "dist",
        "mod",
        "workshop",
        "ONI_Together.HeadlessTests"
    ];

    public static SyncSurfaceScan ScanDirectory(string root)
    {
        var sources = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsExcluded(root, path))
            .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllText);
        return ScanSources(sources);
    }

    public static SyncSurfaceScan ScanSources(IReadOnlyDictionary<string, string> sources)
    {
        CSharpParseOptions parseOptions = CSharpParseOptions.Default
            .WithPreprocessorSymbols("DEBUG", "OS_MAC");
        SyntaxTree[] trees = sources
            .Select(source => CSharpSyntaxTree.ParseText(
                source.Value, parseOptions, source.Key))
            .ToArray();
        CSharpCompilation compilation = CSharpCompilation.Create(
            "SyncSurfaceAudit",
            trees,
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        INamedTypeSymbol[] types = trees
            .SelectMany(tree => DeclaredTypes(compilation.GetSemanticModel(tree), tree))
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .ToArray();

        var errors = new List<SurfaceError>();
        var packets = types
            .Where(IsRegisteredPacket)
            .Select(type => InspectPacket(type, errors))
            .OrderBy(packet => packet.Name, StringComparer.Ordinal)
            .ToArray();
        var patches = types
            .Where(IsHarmonyPatch)
            .Select(type => InspectPatch(type, errors))
            .OrderBy(patch => patch.Name, StringComparer.Ordinal)
            .ToArray();

        return new SyncSurfaceScan(packets, patches, errors);
    }

    public static SyncCatalogScan ScanCatalogSources(
        IReadOnlyDictionary<string, string> sources,
        IReadOnlyList<SyncBuildVariant> variants)
    {
        return SyncCatalogSourceScanner.Scan(sources, variants);
    }

    public static SyncCatalogScan ScanCatalogVariants(
        IReadOnlyList<SyncVariantInput> inputs)
    {
        return SyncCatalogSourceScanner.Scan(inputs);
    }

    public static bool RegistrationHelperExcludesAbstract(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        return tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Any(access => access.Name.Identifier.ValueText == "IsAbstract" &&
                access.Parent is PrefixUnaryExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.LogicalNotExpression
                });
    }

    private static IEnumerable<INamedTypeSymbol> DeclaredTypes(
        SemanticModel model,
        SyntaxTree tree)
    {
        foreach (BaseTypeDeclarationSyntax declaration in tree.GetRoot()
                     .DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(declaration) is INamedTypeSymbol symbol)
                yield return symbol;
        }
    }

    private static bool IsRegisteredPacket(INamedTypeSymbol type)
    {
        return type.TypeKind == TypeKind.Class &&
            !type.IsAbstract &&
            type.AllInterfaces.Any(item => item.Name == "IPacket") &&
            type.AllInterfaces.All(item => item.Name != "IPacketSkipsRegistration");
    }

    private static PacketSurface InspectPacket(
        INamedTypeSymbol type,
        ICollection<SurfaceError> errors)
    {
        INamedTypeSymbol packetContract = type.AllInterfaces.First(item => item.Name == "IPacket");
        foreach (IMethodSymbol member in packetContract.GetMembers().OfType<IMethodSymbol>())
        {
            if (type.FindImplementationForInterfaceMember(member) is null)
                errors.Add(new SurfaceError("missing_packet_member", type.Name));
        }

        bool constructible = type.InstanceConstructors.Any(constructor =>
            constructor.Parameters.Length == 0 &&
            constructor.DeclaredAccessibility == Accessibility.Public);
        if (!constructible)
            errors.Add(new SurfaceError("packet_not_constructible", type.Name));

        bool hostOnly = type.AllInterfaces.Any(item => item.Name == "IHostOnlyPacket");
        bool clientRelay = type.AllInterfaces.Any(item => item.Name == "IClientRelayable");
        if (hostOnly && clientRelay)
            errors.Add(new SurfaceError("conflicting_direction_markers", type.Name));

        string direction = (hostOnly, clientRelay) switch
        {
            (true, false) => "host_to_clients",
            (false, true) => "client_relay",
            (false, false) => "bidirectional_direct",
            _ => "conflicting"
        };
        return new PacketSurface(type.Name, direction);
    }

    private static bool IsHarmonyPatch(INamedTypeSymbol type)
    {
        return type.TypeKind == TypeKind.Class && type.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax())
            .OfType<ClassDeclarationSyntax>()
            .SelectMany(declaration => declaration.AttributeLists)
            .SelectMany(list => list.Attributes)
            .Any(attribute =>
            {
                string name = attribute.Name.ToString();
                return name.EndsWith("HarmonyPatch", StringComparison.Ordinal) ||
                    name.EndsWith("HarmonyPatchAttribute", StringComparison.Ordinal);
            });
    }

    private static HarmonyPatchSurface InspectPatch(
        INamedTypeSymbol type,
        ICollection<SurfaceError> errors)
    {
        string[] entrypoints = PatchMethods(type)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (entrypoints.Length == 0)
            errors.Add(new SurfaceError("harmony_patch_missing_entrypoint", type.Name));
        return new HarmonyPatchSurface(type.Name, entrypoints);
    }

    private static IEnumerable<string> PatchMethods(INamedTypeSymbol type)
    {
        foreach (IMethodSymbol method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (PatchEntrypoints.Contains(method.Name))
                yield return method.Name;
            foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is not MethodDeclarationSyntax declaration)
                    continue;
                foreach (AttributeSyntax attribute in declaration.AttributeLists
                             .SelectMany(list => list.Attributes))
                {
                    string name = attribute.Name.ToString();
                    foreach (string entrypoint in PatchEntrypoints)
                    {
                        if (name.EndsWith($"Harmony{entrypoint}", StringComparison.Ordinal) ||
                            name.EndsWith($"Harmony{entrypoint}Attribute", StringComparison.Ordinal))
                            yield return entrypoint;
                    }
                }
            }
        }
        foreach (INamedTypeSymbol nested in type.GetTypeMembers())
        {
            foreach (string entrypoint in PatchMethods(nested))
                yield return entrypoint;
        }
    }

    internal static IEnumerable<MetadataReference> PlatformReferences()
    {
        string assemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "";
        return assemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path));
    }

    private static bool IsExcluded(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar)
            .Any(ExcludedDirectories.Contains);
    }
}
