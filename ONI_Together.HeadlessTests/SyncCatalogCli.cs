namespace ONI_Together.HeadlessTests;

internal static class SyncCatalogCli
{
    private static readonly SyncBuildVariant[] CatalogVariants =
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

    public static int Run(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        try
        {
            ParsedOptions options = Parse(args);
            ValidatePaths(options);
            return Execute(options, stdout, stderr);
        }
        catch (Exception error)
        {
            stderr.WriteLine($"catalog failed: {error.Message}");
            return 1;
        }
    }

    private static int Execute(
        ParsedOptions options,
        TextWriter stdout,
        TextWriter stderr)
    {
        string destination = Path.GetFullPath(options.InventoryPath);
        string directory = Path.GetDirectoryName(destination)!;
        string staging = Path.Combine(directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.staging");

        try
        {
            SyncCatalogCommandResult result = SyncCatalogCommand.Run(
                new SyncCatalogCommandOptions
                {
                    ProjectPath = options.ProjectPath,
                    Variants = CatalogVariants,
                    InventoryPath = staging,
                    CoveragePath = options.CoveragePath,
                    GlobalProperties = new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["GameLibsFolder"] = options.GameLibsFolder
                    },
                    KnownTestIds = EmptyIds,
                    KnownScenarioIds = EmptyIds
                });

            if (result.CoverageErrors.Count > 0)
            {
                foreach (SurfaceError error in result.CoverageErrors)
                    stderr.WriteLine($"coverage error: {error.Code}: {error.Subject}");
                return 1;
            }

            File.Move(staging, destination, overwrite: true);
            int variantCount = result.Catalog.Entries
                .SelectMany(entry => entry.Variants)
                .Select(variant => variant.Key)
                .Distinct(StringComparer.Ordinal)
                .Count();
            stdout.WriteLine($"digest={result.InventoryDigest}");
            stdout.WriteLine($"entryCount={result.Catalog.Entries.Count}");
            stdout.WriteLine($"variants={variantCount}");
            return 0;
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }

    private static readonly IReadOnlySet<string> EmptyIds =
        new HashSet<string>(StringComparer.Ordinal);

    private static ParsedOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !string.Equals(args[0], "catalog",
                StringComparison.Ordinal))
            throw new ArgumentException("first argument must be catalog");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var options = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 1; index < args.Count; index++)
        {
            string? option = args[index];
            if (!AllowedOptions.Contains(option))
                throw new ArgumentException($"unknown option: {option}");
            if (!options.Add(option))
                throw new ArgumentException($"option specified more than once: {option}");
            if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]) ||
                args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"option requires a value: {option}");
            values.Add(option, args[index]);
        }

        return new ParsedOptions(
            Required(values, "--project"),
            Required(values, "--inventory"),
            Required(values, "--game-libs"),
            values.TryGetValue("--coverage", out string? coverage)
                ? coverage
                : null);
    }

    private static void ValidatePaths(ParsedOptions options)
    {
        if (!File.Exists(options.ProjectPath))
            throw new FileNotFoundException(
                $"project file does not exist: {options.ProjectPath}");
        if (!Directory.Exists(options.GameLibsFolder))
            throw new DirectoryNotFoundException(
                $"game-libs directory does not exist: {options.GameLibsFolder}");
        if (options.CoveragePath is not null && !File.Exists(options.CoveragePath))
            throw new FileNotFoundException(
                $"coverage file does not exist: {options.CoveragePath}");

        string? inventoryDirectory = Path.GetDirectoryName(
            Path.GetFullPath(options.InventoryPath));
        if (string.IsNullOrWhiteSpace(inventoryDirectory) ||
            !Directory.Exists(inventoryDirectory))
            throw new DirectoryNotFoundException(
                $"inventory directory does not exist: {inventoryDirectory}");
    }

    private static string Required(
        IReadOnlyDictionary<string, string> values,
        string option)
    {
        if (!values.TryGetValue(option, out string? value) ||
            string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"missing option: {option}");
        return value;
    }

    private static SyncBuildVariant Variant(
        string configuration,
        string platform,
        params string[] symbols)
    {
        return new SyncBuildVariant(configuration, platform,
            new HashSet<string>(symbols, StringComparer.Ordinal));
    }

    private static readonly IReadOnlySet<string> AllowedOptions =
        new HashSet<string>(new[]
        {
            "--project", "--inventory", "--game-libs", "--coverage"
        }, StringComparer.Ordinal);

    private sealed record ParsedOptions(
        string ProjectPath,
        string InventoryPath,
        string GameLibsFolder,
        string? CoveragePath);
}
