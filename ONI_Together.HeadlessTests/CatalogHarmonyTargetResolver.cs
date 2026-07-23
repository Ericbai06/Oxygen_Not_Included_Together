using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ONI_Together.HeadlessTests;

internal sealed record CatalogHarmonyTarget(
    string MethodName,
    string Signature,
    string Display)
{
    public static CatalogHarmonyTarget Empty { get; } = new("", "", "");
    public bool HasSignature => !string.IsNullOrWhiteSpace(Signature);
}

internal static class CatalogHarmonyTargetResolver
{
    public static CatalogHarmonyTarget ResolveAttributes(
        SemanticModel model,
        IEnumerable<AttributeSyntax> attributes)
    {
        var state = new AttributeTargetState();
        foreach (AttributeSyntax attribute in attributes)
            ApplyAttribute(model, attribute, state);
        return ResolveMember(state.TargetType, state.MethodName,
            state.MemberKind, state.ParameterTypes);
    }

    public static CatalogHarmonyTarget ResolveProvider(
        SemanticModel model,
        IMethodSymbol provider)
    {
        return ResolveProvider(model, provider, new HashSet<string>(StringComparer.Ordinal), 0);
    }

    public static CatalogHarmonyTarget ResolveExpression(
        SemanticModel model,
        ExpressionSyntax expression)
    {
        return ResolveExpression(model, expression,
            new HashSet<string>(StringComparer.Ordinal), 0);
    }

    private static void ApplyAttribute(
        SemanticModel model,
        AttributeSyntax attribute,
        AttributeTargetState state)
    {
        SeparatedSyntaxList<AttributeArgumentSyntax> arguments =
            attribute.ArgumentList?.Arguments ?? default;
        TypeOfExpressionSyntax? declaringType = arguments.FirstOrDefault()?.Expression
            as TypeOfExpressionSyntax;
        if (declaringType is not null &&
            model.GetTypeInfo(declaringType.Type).Type is INamedTypeSymbol targetType)
            state.TargetType = targetType;

        string? methodName = arguments.Select(argument =>
                model.GetConstantValue(argument.Expression))
            .Where(value => value.HasValue && value.Value is string)
            .Select(value => (string)value.Value!)
            .LastOrDefault();
        if (!string.IsNullOrWhiteSpace(methodName))
            state.MethodName = methodName;
        ApplyMemberKind(model, arguments, state);
        ApplyParameterTypes(model, arguments, declaringType, state);
    }

    private static void ApplyMemberKind(
        SemanticModel model,
        SeparatedSyntaxList<AttributeArgumentSyntax> arguments,
        AttributeTargetState state)
    {
        foreach (AttributeArgumentSyntax argument in arguments)
        {
            if (model.GetSymbolInfo(argument.Expression).Symbol is not IFieldSymbol field ||
                field.ContainingType.Name != "MethodType")
                continue;
            state.MemberKind = field.Name switch
            {
                "Getter" => HarmonyMemberKind.Getter,
                "Setter" => HarmonyMemberKind.Setter,
                "Constructor" => HarmonyMemberKind.Constructor,
                "StaticConstructor" => HarmonyMemberKind.StaticConstructor,
                _ => HarmonyMemberKind.Method
            };
        }
    }

    private static void ApplyParameterTypes(
        SemanticModel model,
        SeparatedSyntaxList<AttributeArgumentSyntax> arguments,
        TypeOfExpressionSyntax? declaringType,
        AttributeTargetState state)
    {
        TypeOfExpressionSyntax[] types = arguments
            .SelectMany(argument => argument.Expression.DescendantNodesAndSelf()
                .OfType<TypeOfExpressionSyntax>())
            .Where(expression => expression != declaringType)
            .ToArray();
        if (types.Length == 0)
            return;
        state.ParameterTypes = types.Select(expression =>
                model.GetTypeInfo(expression.Type).Type)
            .Where(type => type is not null)
            .Cast<ITypeSymbol>()
            .ToArray();
    }

    private static CatalogHarmonyTarget ResolveProvider(
        SemanticModel callerModel,
        IMethodSymbol provider,
        ISet<string> visited,
        int depth)
    {
        string providerSignature = CatalogSymbolHelpers.MethodSignature(provider);
        if (depth > 4 || !visited.Add(providerSignature))
            return ProviderTarget(provider);
        var targets = new List<CatalogHarmonyTarget>();
        foreach (SyntaxReference reference in provider.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not MethodDeclarationSyntax declaration)
                continue;
            SemanticModel model = callerModel.Compilation.GetSemanticModel(declaration.SyntaxTree);
            foreach (InvocationExpressionSyntax invocation in declaration.DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                CatalogHarmonyTarget target = ResolveInvocation(
                    model, invocation, visited, depth + 1);
                if (target.HasSignature)
                    targets.Add(target);
            }
        }
        visited.Remove(providerSignature);
        return CombineTargets(targets, provider);
    }

    private static CatalogHarmonyTarget ResolveExpression(
        SemanticModel model,
        ExpressionSyntax expression,
        ISet<string> visited,
        int depth)
    {
        if (depth > 4)
            return CatalogHarmonyTarget.Empty;
        if (expression is InvocationExpressionSyntax invocation)
            return ResolveInvocation(model, invocation, visited, depth + 1);
        ISymbol? symbol = model.GetSymbolInfo(expression).Symbol;
        SyntaxReference? declaration = symbol?.DeclaringSyntaxReferences.FirstOrDefault();
        if (declaration?.GetSyntax() is VariableDeclaratorSyntax variable &&
            variable.Initializer?.Value is ExpressionSyntax initializer)
        {
            SemanticModel declarationModel = model.Compilation.GetSemanticModel(
                initializer.SyntaxTree);
            return ResolveExpression(declarationModel, initializer, visited, depth + 1);
        }
        return CatalogHarmonyTarget.Empty;
    }

    private static CatalogHarmonyTarget ResolveInvocation(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        ISet<string> visited,
        int depth)
    {
        IMethodSymbol? method = CatalogSymbolHelpers.InvocationMethod(model, invocation);
        if (method is null)
            return CatalogHarmonyTarget.Empty;
        if (method.ContainingType.Name == "AccessTools")
            return ResolveAccessTools(model, invocation, method.Name);
        if (method.ContainingType.Name == "Type" &&
            method.ContainingType.ContainingNamespace.ToDisplayString() == "System")
            return ResolveSystemType(model, invocation, method.Name);
        if (method.DeclaringSyntaxReferences.Length > 0)
            return ResolveProvider(model, method, visited, depth + 1);
        return CatalogHarmonyTarget.Empty;
    }

    private static CatalogHarmonyTarget ResolveAccessTools(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        string resolverName)
    {
        SeparatedSyntaxList<ArgumentSyntax> arguments = invocation.ArgumentList.Arguments;
        TypeOfExpressionSyntax? typeExpression = arguments.FirstOrDefault()?.Expression
            as TypeOfExpressionSyntax;
        INamedTypeSymbol? type = typeExpression is null
            ? null
            : model.GetTypeInfo(typeExpression.Type).Type as INamedTypeSymbol;
        string name = ConstantString(model, arguments.Select(argument => argument.Expression));
        if (type is null && TryParseQualifiedMethod(name, model.Compilation,
                out INamedTypeSymbol? parsedType, out string parsedName))
        {
            type = parsedType;
            name = parsedName;
        }
        HarmonyMemberKind kind = resolverName switch
        {
            "PropertyGetter" => HarmonyMemberKind.Getter,
            "PropertySetter" => HarmonyMemberKind.Setter,
            "Constructor" or "DeclaredConstructor" => HarmonyMemberKind.Constructor,
            _ => HarmonyMemberKind.Method
        };
        return ResolveMember(type, name, kind, Array.Empty<ITypeSymbol>());
    }

    private static CatalogHarmonyTarget ResolveSystemType(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        string resolverName)
    {
        ExpressionSyntax? receiver = (invocation.Expression as MemberAccessExpressionSyntax)
            ?.Expression;
        INamedTypeSymbol? type = receiver is TypeOfExpressionSyntax typeOf
            ? model.GetTypeInfo(typeOf.Type).Type as INamedTypeSymbol
            : null;
        string name = ConstantString(model,
            invocation.ArgumentList.Arguments.Select(argument => argument.Expression));
        HarmonyMemberKind kind = resolverName switch
        {
            "GetProperty" => HarmonyMemberKind.Getter,
            "GetConstructor" => HarmonyMemberKind.Constructor,
            _ => HarmonyMemberKind.Method
        };
        return ResolveMember(type, name, kind, Array.Empty<ITypeSymbol>());
    }

    private static CatalogHarmonyTarget ResolveMember(
        INamedTypeSymbol? type,
        string methodName,
        HarmonyMemberKind kind,
        IReadOnlyList<ITypeSymbol> parameterTypes)
    {
        if (type is null || (string.IsNullOrWhiteSpace(methodName) &&
                             kind is not HarmonyMemberKind.Constructor and
                                 not HarmonyMemberKind.StaticConstructor))
            return CatalogHarmonyTarget.Empty;
        IEnumerable<IMethodSymbol> candidates = CandidateMembers(type, methodName, kind);
        if (parameterTypes.Count > 0)
            candidates = candidates.Where(method => ParametersMatch(method, parameterTypes));
        string[] signatures = candidates.Select(CatalogSymbolHelpers.MethodSignature)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string display = $"{CatalogSymbolHelpers.TypeName(type)}.{methodName}";
        return signatures.Length == 0
            ? new CatalogHarmonyTarget(methodName, "", display)
            : new CatalogHarmonyTarget(methodName, string.Join(" | ", signatures), display);
    }

    private static IEnumerable<IMethodSymbol> CandidateMembers(
        INamedTypeSymbol type,
        string name,
        HarmonyMemberKind kind)
    {
        if (kind == HarmonyMemberKind.Constructor)
            return type.InstanceConstructors;
        if (kind == HarmonyMemberKind.StaticConstructor)
            return type.StaticConstructors;
        IEnumerable<ISymbol> members = TypeHierarchy(type)
            .SelectMany(item => item.GetMembers(name));
        if (kind is HarmonyMemberKind.Getter or HarmonyMemberKind.Setter)
        {
            return members.OfType<IPropertySymbol>()
                .Select(property => kind == HarmonyMemberKind.Getter
                    ? property.GetMethod
                    : property.SetMethod)
                .Where(method => method is not null)
                .Cast<IMethodSymbol>();
        }
        return members.OfType<IMethodSymbol>();
    }

    private static IEnumerable<INamedTypeSymbol> TypeHierarchy(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null;
             current = current.BaseType)
            yield return current;
    }

    private static bool ParametersMatch(
        IMethodSymbol method,
        IReadOnlyList<ITypeSymbol> parameterTypes)
    {
        return method.Parameters.Length == parameterTypes.Count &&
            method.Parameters.Select(parameter => parameter.Type)
                .Zip(parameterTypes)
                .All(pair => SymbolEqualityComparer.Default.Equals(pair.First, pair.Second));
    }

    private static string ConstantString(
        SemanticModel model,
        IEnumerable<ExpressionSyntax> expressions)
    {
        return expressions.Select(expression => model.GetConstantValue(expression))
            .Where(value => value.HasValue && value.Value is string)
            .Select(value => (string)value.Value!)
            .FirstOrDefault() ?? "";
    }

    private static bool TryParseQualifiedMethod(
        string value,
        Compilation compilation,
        out INamedTypeSymbol? type,
        out string method)
    {
        type = null;
        method = "";
        int colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1)
            return false;
        string typePart = value[..colon].Split(',')[0].Trim();
        type = compilation.GetTypeByMetadataName(typePart);
        method = value[(colon + 1)..].Trim();
        return type is not null && method.Length > 0;
    }

    private static CatalogHarmonyTarget CombineTargets(
        IEnumerable<CatalogHarmonyTarget> targets,
        IMethodSymbol provider)
    {
        CatalogHarmonyTarget[] resolved = targets.Where(target => target.HasSignature)
            .GroupBy(target => target.Signature, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (resolved.Length == 0)
            return ProviderTarget(provider);
        return new CatalogHarmonyTarget(
            resolved.Select(target => target.MethodName).FirstOrDefault() ?? provider.Name,
            string.Join(" | ", resolved.Select(target => target.Signature)
                .Order(StringComparer.Ordinal)),
            string.Join(" | ", resolved.Select(target => target.Display)
                .Order(StringComparer.Ordinal)));
    }

    private static CatalogHarmonyTarget ProviderTarget(IMethodSymbol provider)
    {
        string signature = CatalogSymbolHelpers.MethodSignature(provider);
        return new CatalogHarmonyTarget(provider.Name,
            $"provider:{signature}", $"provider:{signature}");
    }

    private enum HarmonyMemberKind
    {
        Method,
        Getter,
        Setter,
        Constructor,
        StaticConstructor
    }

    private sealed class AttributeTargetState
    {
        public INamedTypeSymbol? TargetType { get; set; }
        public string MethodName { get; set; } = "";
        public HarmonyMemberKind MemberKind { get; set; }
        public IReadOnlyList<ITypeSymbol> ParameterTypes { get; set; } =
            Array.Empty<ITypeSymbol>();
    }
}
