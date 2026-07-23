using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ONI_Together.HeadlessTests;

internal static class CatalogHarmonyExtractor
{
    private static readonly HashSet<string> CallbackNames =
    [
        "Prefix",
        "Postfix",
        "Transpiler",
        "Finalizer",
        "Prepare",
        "Cleanup"
    ];

    private static readonly HashSet<string> StateMethods =
    [
        "StartSM",
        "GoTo",
        "IsInsideState",
        "Get",
        "Set",
        "Delta",
        "Trigger"
    ];

    public static void Extract(
        CatalogExtractionContext context,
        SyntaxTree tree)
    {
        foreach (TypeDeclarationSyntax type in tree.GetRoot().DescendantNodes()
                     .OfType<TypeDeclarationSyntax>())
            ExtractType(context, type);
    }

    private static void ExtractType(
        CatalogExtractionContext context,
        TypeDeclarationSyntax type)
    {
        AttributeSyntax[] typePatches = PatchAttributes(context.Model, type.AttributeLists);
        MethodDeclarationSyntax[] methods = type.Members.OfType<MethodDeclarationSyntax>()
            .ToArray();
        if (typePatches.Length == 0 && !methods.Any(method =>
                PatchAttributes(context.Model, method.AttributeLists).Length > 0))
            return;

        IMethodSymbol? provider = methods
            .Select(method => context.Model.GetDeclaredSymbol(method))
            .OfType<IMethodSymbol>()
            .FirstOrDefault(IsTargetProvider);
        CatalogHarmonyTarget providerTarget = provider is null
            ? CatalogHarmonyTarget.Empty
            : CatalogHarmonyTargetResolver.ResolveProvider(context.Model, provider);

        foreach (MethodDeclarationSyntax declaration in methods)
            AddPatchMethod(context, declaration, typePatches, providerTarget);
    }

    private static void AddPatchMethod(
        CatalogExtractionContext context,
        MethodDeclarationSyntax declaration,
        IReadOnlyList<AttributeSyntax> typePatches,
        CatalogHarmonyTarget providerTarget)
    {
        if (context.Model.GetDeclaredSymbol(declaration) is not IMethodSymbol method ||
            !IsPatchCallback(method))
            return;
        AttributeSyntax[] methodPatches = PatchAttributes(
            context.Model, declaration.AttributeLists);
        CatalogHarmonyTarget direct = CatalogHarmonyTargetResolver.ResolveAttributes(
            context.Model, typePatches.Concat(methodPatches));
        CatalogHarmonyTarget target = direct.HasSignature ? direct : providerTarget;
        SyncEntryStatus status = CatalogSourceClassification.ForNode(context, declaration);
        string bootstrap = BuildBootstrap(typePatches, methodPatches, providerTarget, method);
        context.Candidates.Add(new SyncEntryCandidate(
            SyncEntryKind.HarmonyPatch,
            CatalogSymbolHelpers.MethodSignature(method),
            target.Signature,
            bootstrap,
            context.Variant,
            status));
        AddStateMachineTarget(target, method, bootstrap, status, context);
    }

    private static AttributeSyntax[] PatchAttributes(
        SemanticModel model,
        SyntaxList<AttributeListSyntax> lists)
    {
        return lists.SelectMany(list => list.Attributes)
            .Where(attribute => IsHarmonyAttribute(model, attribute, "HarmonyPatch"))
            .ToArray();
    }

    private static bool IsPatchCallback(IMethodSymbol method)
    {
        if (IsTargetProvider(method))
            return false;
        if (CallbackNames.Contains(method.Name))
            return true;
        return CallbackNames.Any(name =>
            CatalogSymbolHelpers.HasAttribute(method, $"Harmony{name}"));
    }

    private static bool IsTargetProvider(IMethodSymbol method)
    {
        return method.Name is "TargetMethod" or "TargetMethods" ||
            CatalogSymbolHelpers.HasAttribute(method, "HarmonyTargetMethod") ||
            CatalogSymbolHelpers.HasAttribute(method, "HarmonyTargetMethods");
    }

    private static bool IsHarmonyAttribute(
        SemanticModel model,
        AttributeSyntax attribute,
        string expected)
    {
        IMethodSymbol? constructor = model.GetSymbolInfo(attribute).Symbol as IMethodSymbol;
        string name = constructor?.ContainingType.Name ?? attribute.Name.ToString();
        return name == expected || name == $"{expected}Attribute" ||
            name.EndsWith($".{expected}", StringComparison.Ordinal) ||
            name.EndsWith($".{expected}Attribute", StringComparison.Ordinal);
    }

    private static string BuildBootstrap(
        IReadOnlyList<AttributeSyntax> typePatches,
        IReadOnlyList<AttributeSyntax> methodPatches,
        CatalogHarmonyTarget providerTarget,
        IMethodSymbol method)
    {
        string attributes = string.Join(" + ", typePatches.Concat(methodPatches)
            .Select(attribute => SyncCatalogSourceScanner.Normalize(attribute.ToString())));
        if (string.IsNullOrWhiteSpace(attributes) && providerTarget.HasSignature)
            attributes = providerTarget.Display;
        return $"{attributes} -> {method.Name}";
    }

    private static void AddStateMachineTarget(
        CatalogHarmonyTarget target,
        IMethodSymbol patchMethod,
        string bootstrap,
        SyncEntryStatus status,
        CatalogExtractionContext context)
    {
        if (!StateMethods.Contains(target.MethodName))
            return;
        context.Candidates.Add(new SyncEntryCandidate(
            SyncEntryKind.StateMachine,
            CatalogSymbolHelpers.MethodSignature(patchMethod),
            target.Signature,
            bootstrap,
            context.Variant,
            status));
    }
}
