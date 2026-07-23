using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ONI_Together.HeadlessTests;

internal static class CatalogSymbolHelpers
{
    private static readonly SymbolDisplayFormat SignatureFormat =
        SymbolDisplayFormat.CSharpErrorMessageFormat;

    public static string EnclosingMethod(SemanticModel model, SyntaxNode node)
    {
        return model.GetEnclosingSymbol(node.SpanStart) is IMethodSymbol method
            ? method.ToDisplayString(SignatureFormat)
            : $"{node.SyntaxTree.FilePath}::<global>";
    }

    public static IMethodSymbol? InvocationMethod(
        SemanticModel model,
        InvocationExpressionSyntax invocation)
    {
        SymbolInfo info = model.GetSymbolInfo(invocation);
        return info.Symbol as IMethodSymbol ??
            info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
    }

    public static string MethodSignature(IMethodSymbol method)
    {
        return method.ToDisplayString(SignatureFormat);
    }

    public static bool IsPacketType(ITypeSymbol? type)
    {
        return type is INamedTypeSymbol named &&
            (named.Name == "IPacket" ||
             named.AllInterfaces.Any(contract => contract.Name == "IPacket"));
    }

    public static bool ContainsPacketType(ITypeSymbol? type)
    {
        if (IsPacketType(type))
            return true;
        if (type is not INamedTypeSymbol named)
            return false;
        return named.GetMembers().Any(MemberContainsPacket) ||
            named.InstanceConstructors.SelectMany(constructor => constructor.Parameters)
                .Any(parameter => IsPacketType(parameter.Type));
    }

    private static bool MemberContainsPacket(ISymbol member)
    {
        return member switch
        {
            IPropertySymbol property => IsPacketType(property.Type),
            IFieldSymbol field => IsPacketType(field.Type),
            _ => false
        };
    }

    public static bool HasAttribute(ISymbol symbol, string attributeName)
    {
        return symbol.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.Name is string name &&
            (name == attributeName || name == $"{attributeName}Attribute"));
    }

    public static string TypeName(ITypeSymbol type)
    {
        return type.ToDisplayString(SignatureFormat);
    }
}
