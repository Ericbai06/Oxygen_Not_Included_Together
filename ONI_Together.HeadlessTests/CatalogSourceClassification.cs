using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ONI_Together.HeadlessTests;

internal static class CatalogSourceClassification
{
    public static SyncEntryStatus FromPath(string path)
    {
        string[] segments = path.Replace('\\', '/').Split(
            '/', StringSplitOptions.RemoveEmptyEntries);
        if (ContainsPair(segments, "DebugTools", "UnitTests") ||
            segments.Contains("Tests", StringComparer.OrdinalIgnoreCase))
            return SyncEntryStatus.TestOnly;
        if (ContainsPair(segments, "UI", "lib"))
            return SyncEntryStatus.Vendor;
        return SyncEntryStatus.Active;
    }

    public static SyncEntryStatus ForNode(
        CatalogExtractionContext context,
        SyntaxNode node)
    {
        if (context.SourceStatus != SyncEntryStatus.Active)
            return context.SourceStatus;
        return BeginsWithReturn(node)
            ? SyncEntryStatus.RegisteredDisabled
            : SyncEntryStatus.Active;
    }

    private static bool BeginsWithReturn(SyntaxNode node)
    {
        SyntaxNode? owner = node.AncestorsAndSelf().FirstOrDefault(candidate =>
            candidate is BaseMethodDeclarationSyntax or
                LocalFunctionStatementSyntax or AccessorDeclarationSyntax);
        return FirstStatement(owner) is ReturnStatementSyntax;
    }

    private static StatementSyntax? FirstStatement(SyntaxNode? owner)
    {
        return owner switch
        {
            BaseMethodDeclarationSyntax method => method.Body?.Statements.FirstOrDefault(),
            LocalFunctionStatementSyntax local => local.Body?.Statements.FirstOrDefault(),
            AccessorDeclarationSyntax accessor => accessor.Body?.Statements.FirstOrDefault(),
            _ => null
        };
    }

    private static bool ContainsPair(
        IReadOnlyList<string> segments,
        string first,
        string second)
    {
        for (int index = 0; index + 1 < segments.Count; index++)
        {
            if (string.Equals(segments[index], first, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(segments[index + 1], second,
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
