using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ONI_Together.HeadlessTests;

internal static class SyncCatalogSourceScanner
{
    public static SyncCatalogScan Scan(
        IReadOnlyDictionary<string, string> sources,
        IReadOnlyList<SyncBuildVariant> variants)
    {
        SyncVariantInput[] inputs = variants
            .Select(variant => new SyncVariantInput(
                variant, sources, Array.Empty<string>()))
            .ToArray();
        return Scan(inputs);
    }

    public static SyncCatalogScan Scan(IReadOnlyList<SyncVariantInput> inputs)
    {
        var candidates = new List<SyncEntryCandidate>();
        var errors = new List<SurfaceError>();
        foreach (SyncVariantInput input in inputs)
            ScanVariant(input, candidates, errors);

        SyncEntry[] entries = candidates
            .GroupBy(Identity)
            .Select(BuildEntry)
            .OrderBy(entry => entry.Kind)
            .ThenBy(entry => entry.FullyQualifiedSymbol, StringComparer.Ordinal)
            .ThenBy(entry => entry.ResolvedTargetSignature, StringComparer.Ordinal)
            .ThenBy(entry => entry.Bootstrap, StringComparer.Ordinal)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ToArray();
        return new SyncCatalogScan(entries, DeduplicateErrors(errors));
    }

    private static void ScanVariant(
        SyncVariantInput input,
        ICollection<SyncEntryCandidate> candidates,
        ICollection<SurfaceError> errors)
    {
        CSharpParseOptions parseOptions = CSharpParseOptions.Default
            .WithPreprocessorSymbols(input.Variant.Symbols);
        SyntaxTree[] trees = input.Sources
            .OrderBy(source => source.Key, StringComparer.Ordinal)
            .Select(source => CSharpSyntaxTree.ParseText(
                source.Value, parseOptions, source.Key))
            .ToArray();
        CSharpCompilation compilation = CSharpCompilation.Create(
            $"SyncCatalog-{input.Variant.Configuration}-{input.Variant.Platform}",
            trees,
            BuildReferences(input, errors),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        AddCompilationErrors(compilation, input.Variant, errors);
        CatalogPacketRegistrationExtractor.Extract(
            compilation, input.Variant, trees, candidates);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (SyntaxTree tree in trees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            var context = new CatalogExtractionContext(
                model, input.Variant, candidates, occurrences,
                CatalogSourceClassification.FromPath(tree.FilePath));
            CatalogHarmonyExtractor.Extract(context, tree);
            CatalogInvocationExtractor.Extract(context, tree);
        }
    }

    private static IEnumerable<MetadataReference> BuildReferences(
        SyncVariantInput input,
        ICollection<SurfaceError> errors)
    {
        var references = new Dictionary<string, MetadataReference>(
            StringComparer.OrdinalIgnoreCase);
        foreach (string path in input.MetadataReferences
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
            AddMetadataReference(path, input.Variant, references, errors);

        if (references.Count > 0)
            return references.Values;
        foreach (MetadataReference reference in SyncSurfaceScanner.PlatformReferences())
        {
            string? path = reference.Display;
            if (!string.IsNullOrWhiteSpace(path))
                references.TryAdd(Path.GetFileName(path), reference);
        }
        return references.Values;
    }

    private static void AddMetadataReference(
        string path,
        SyncBuildVariant variant,
        IDictionary<string, MetadataReference> references,
        ICollection<SurfaceError> errors)
    {
        string canonical = Path.GetFullPath(path);
        if (!File.Exists(canonical))
        {
            errors.Add(new SurfaceError(
                "catalog_metadata_reference_missing", $"{variant.Key}:{canonical}"));
            return;
        }
        try
        {
            references.TryAdd(
                Path.GetFileName(canonical),
                MetadataReference.CreateFromFile(canonical));
        }
        catch (Exception error) when (error is IOException or BadImageFormatException or
                                     UnauthorizedAccessException)
        {
            errors.Add(new SurfaceError(
                "catalog_metadata_reference_invalid",
                $"{variant.Key}:{canonical}:{error.Message}"));
        }
    }

    private static void AddCompilationErrors(
        CSharpCompilation compilation,
        SyncBuildVariant variant,
        ICollection<SurfaceError> errors)
    {
        foreach (Diagnostic diagnostic in compilation.GetDiagnostics()
                     .Where(item => item.Severity == DiagnosticSeverity.Error))
        {
            string subject = $"{variant.Key}:{diagnostic.Id}:{diagnostic.GetMessage()}";
            errors.Add(new SurfaceError("catalog_compilation_error", subject));
        }
    }

    private static string Identity(SyncEntryCandidate candidate)
    {
        return string.Join("\n",
            candidate.Kind,
            Normalize(candidate.FullyQualifiedSymbol),
            Normalize(candidate.ResolvedTargetSignature),
            Normalize(candidate.Callsite),
            candidate.Status);
    }

    private static SyncEntry BuildEntry(IGrouping<string, SyncEntryCandidate> group)
    {
        SyncEntryCandidate first = group.First();
        SyncBuildVariant[] variants = group
            .Select(candidate => candidate.Variant)
            .GroupBy(variant => variant.Key, StringComparer.Ordinal)
            .Select(variantGroup => variantGroup.First())
            .OrderBy(variant => variant.Key, StringComparer.Ordinal)
            .ToArray();
        return new SyncEntry(
            StableId(group.Key),
            first.Kind,
            Normalize(first.FullyQualifiedSymbol),
            Normalize(first.ResolvedTargetSignature),
            Normalize(first.Bootstrap),
            variants,
            first.Status);
    }

    private static string StableId(string identity)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        string prefix = Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
        return $"sync:{prefix}";
    }

    internal static string Normalize(string value)
    {
        return string.Join(" ", value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
    }

    private static SurfaceError[] DeduplicateErrors(IEnumerable<SurfaceError> errors)
    {
        return errors
            .Distinct()
            .OrderBy(error => error.Code, StringComparer.Ordinal)
            .ThenBy(error => error.Subject, StringComparer.Ordinal)
            .ToArray();
    }
}
