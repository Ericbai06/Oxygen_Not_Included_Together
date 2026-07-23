using Mono.Cecil;

namespace ONI_Together.HeadlessTests;

internal static partial class SyncExecutionIlInstrumenter
{
    private static DefaultAssemblyResolver CreateAssemblyResolver(
        string? gameLibsDirectory,
        SyncExecutionFixtureAssembly fixture)
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (string directory in ResolverDirectories(
                     gameLibsDirectory, fixture))
            resolver.AddSearchDirectory(directory);
        return resolver;
    }

    private static IEnumerable<string> ResolverDirectories(
        string? gameLibsDirectory,
        SyncExecutionFixtureAssembly fixture)
    {
        string? root = FindRepositoryRoot();
        if (root is not null)
        {
            yield return Path.Combine(root, "ONI_Together", "bin", "Debug",
                "netstandard2.1");
            yield return Path.Combine(root, "Shared", "bin", "Debug",
                "netstandard2.1");
        }
        if (!string.IsNullOrWhiteSpace(gameLibsDirectory))
            yield return RequireDirectory(gameLibsDirectory, fixture);
    }

    private static string RequireDirectory(
        string directory,
        SyncExecutionFixtureAssembly fixture)
    {
        string fullPath = Path.GetFullPath(directory);
        if (Directory.Exists(fullPath))
            return fullPath;
        using var stream = new MemoryStream(fixture.PeImage, writable: false);
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(stream);
        string references = string.Join(", ",
            assembly.MainModule.AssemblyReferences.Select(item => item.Name));
        throw new DirectoryNotFoundException(
            $"resolver directory does not exist: {fullPath}; " +
            $"assembly references: {references}");
    }

    private static void ValidateAssemblyReferences(
        AssemblyDefinition assembly,
        IAssemblyResolver resolver)
    {
        foreach (AssemblyNameReference reference in
                 assembly.MainModule.AssemblyReferences)
            _ = resolver.Resolve(reference);
    }

    private static string? FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
             directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "Shared")) &&
                Directory.Exists(Path.Combine(directory.FullName, "ONI_Together")))
                return directory.FullName;
        return null;
    }
}
