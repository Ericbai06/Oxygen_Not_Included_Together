using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ONI_Together.HeadlessTests;

internal static class CatalogPacketRegistrationExtractor
{
    public static void Extract(
        CSharpCompilation compilation,
        SyncBuildVariant variant,
        IReadOnlyList<SyntaxTree> trees,
        ICollection<SyncEntryCandidate> candidates)
    {
        string? bootstrap = FindBootstrap(compilation);
        if (bootstrap is null)
            return;
        foreach (SyntaxTree tree in trees)
            ExtractTree(compilation.GetSemanticModel(tree), tree, variant,
                bootstrap, candidates);
    }

    private static void ExtractTree(
        SemanticModel model,
        SyntaxTree tree,
        SyncBuildVariant variant,
        string bootstrap,
        ICollection<SyncEntryCandidate> candidates)
    {
        SyncEntryStatus status = CatalogSourceClassification.FromPath(tree.FilePath);
        foreach (TypeDeclarationSyntax declaration in tree.GetRoot().DescendantNodes()
                     .OfType<TypeDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type ||
                !IsRegisteredPacket(type))
                continue;
            string packetType = CatalogSymbolHelpers.TypeName(type);
            candidates.Add(new SyncEntryCandidate(
                SyncEntryKind.PacketRegistration,
                packetType,
                $"{packetType} via reflection registration",
                bootstrap,
                variant,
                status,
                $"reflection:{packetType}"));
        }
    }

    private static string? FindBootstrap(CSharpCompilation compilation)
    {
        IMethodSymbol? defaults = FindMethod(compilation, "RegisterDefaults",
            "PacketRegistry");
        IMethodSymbol? automatic = FindMethod(compilation, "AutoRegisterPackets",
            "PacketRegistrationHelper");
        if (defaults is null || automatic is null)
            return null;
        return $"{CatalogSymbolHelpers.MethodSignature(defaults)} -> " +
            CatalogSymbolHelpers.MethodSignature(automatic);
    }

    private static IMethodSymbol? FindMethod(
        CSharpCompilation compilation,
        string methodName,
        string typeName)
    {
        return DeclaredMethods(compilation).Concat(InvokedMethods(compilation))
            .Where(method => Matches(method, methodName, typeName))
            .OrderBy(CatalogSymbolHelpers.MethodSignature, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static IEnumerable<IMethodSymbol> DeclaredMethods(
        CSharpCompilation compilation)
    {
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            foreach (MethodDeclarationSyntax declaration in tree.GetRoot()
                         .DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration) is IMethodSymbol method)
                    yield return method;
            }
        }
    }

    private static IEnumerable<IMethodSymbol> InvokedMethods(
        CSharpCompilation compilation)
    {
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            foreach (InvocationExpressionSyntax invocation in tree.GetRoot()
                         .DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (CatalogSymbolHelpers.InvocationMethod(model, invocation) is
                    IMethodSymbol method)
                    yield return method;
            }
        }
    }

    private static bool Matches(
        IMethodSymbol method,
        string methodName,
        string typeName)
    {
        return method.Name == methodName && method.ContainingType.Name == typeName;
    }

    private static bool IsRegisteredPacket(INamedTypeSymbol type)
    {
        return type.TypeKind == TypeKind.Class &&
            !type.IsAbstract &&
            type.AllInterfaces.Any(contract => contract.Name == "IPacket") &&
            type.AllInterfaces.All(contract => contract.Name !=
                "IPacketSkipsRegistration");
    }
}
