using System.Diagnostics;
using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class SyncMsBuildProjectLoader
{
    private static readonly HashSet<string> ReservedProperties =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Configuration",
            "Platform",
            "DoNotBuildAsMod"
        };

    public static IReadOnlyList<SyncVariantInput> Load(
        string projectPath,
        IReadOnlyList<SyncBuildVariant> variants)
    {
        return Load(projectPath, variants,
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    public static IReadOnlyList<SyncVariantInput> Load(
        string projectPath,
        IReadOnlyList<SyncBuildVariant> variants,
        IReadOnlyDictionary<string, string> globalProperties)
    {
        string canonicalProject = Path.GetFullPath(projectPath);
        if (!File.Exists(canonicalProject))
            throw new FileNotFoundException("MSBuild project does not exist", canonicalProject);
        ValidateInputs(variants, globalProperties);

        var inputs = new List<SyncVariantInput>(variants.Count);
        bool restored = false;
        foreach (SyncBuildVariant variant in variants)
        {
            try
            {
                inputs.Add(LoadVariant(canonicalProject, variant, globalProperties));
            }
            catch (MsBuildFailure error) when (error.AssetsMissing && !restored)
            {
                RestoreOnce(canonicalProject, variant, globalProperties);
                restored = true;
                inputs.Add(LoadVariant(canonicalProject, variant, globalProperties));
            }
        }
        return inputs;
    }

    private static SyncVariantInput LoadVariant(
        string projectPath,
        SyncBuildVariant requestedVariant,
        IReadOnlyDictionary<string, string> globalProperties)
    {
        string output = RunMsBuild(projectPath, requestedVariant, globalProperties);
        using JsonDocument document = JsonDocument.Parse(JsonPayload(output));
        JsonElement root = document.RootElement;
        JsonElement properties = RequiredObject(root, "Properties");
        JsonElement items = RequiredObject(root, "Items");
        string constants = RequiredString(properties, "DefineConstants");
        SyncBuildVariant evaluatedVariant = requestedVariant with
        {
            Symbols = EvaluatedSymbols(requestedVariant, constants)
        };
        IReadOnlyDictionary<string, string> sources = ReadSources(
            RequiredArray(items, "Compile"));
        IReadOnlyList<string> references = ReadReferences(
            RequiredArray(items, "ReferencePath"));
        return new SyncVariantInput(evaluatedVariant, sources, references);
    }

    private static string RunMsBuild(
        string projectPath,
        SyncBuildVariant variant,
        IReadOnlyDictionary<string, string> globalProperties)
    {
        var startInfo = CreateProcessStartInfo();
        foreach (string argument in Arguments(projectPath, variant, globalProperties))
            startInfo.ArgumentList.Add(argument);

        ProcessResult result = RunProcess(startInfo, "dotnet msbuild");
        if (result.ExitCode != 0)
            throw new MsBuildFailure(variant.Key, result);
        return result.Output;
    }

    private static void RestoreOnce(
        string projectPath,
        SyncBuildVariant variant,
        IReadOnlyDictionary<string, string> globalProperties)
    {
        var startInfo = CreateProcessStartInfo();
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--nologo");
        foreach (string property in PropertyArguments(variant, globalProperties))
            startInfo.ArgumentList.Add(property);
        ProcessResult result = RunProcess(startInfo, "dotnet restore");
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"dotnet restore failed for {variant.Key}: {result.Error}\n{result.Output}");
    }

    private static IEnumerable<string> Arguments(
        string projectPath,
        SyncBuildVariant variant,
        IReadOnlyDictionary<string, string> globalProperties)
    {
        yield return "msbuild";
        yield return projectPath;
        yield return "-nologo";
        yield return "-target:ResolveReferences";
        yield return "-getProperty:DefineConstants";
        yield return "-getItem:Compile";
        yield return "-getItem:ReferencePath";
        foreach (string property in PropertyArguments(variant, globalProperties))
            yield return property;
    }

    private static IEnumerable<string> PropertyArguments(
        SyncBuildVariant variant,
        IReadOnlyDictionary<string, string> globalProperties)
    {
        yield return "-p:DoNotBuildAsMod=true";
        yield return $"-p:Configuration={variant.Configuration}";
        yield return $"-p:Platform={variant.Platform}";
        foreach ((string name, string value) in globalProperties
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
            yield return $"-p:{name}={value}";
    }

    private static ProcessStartInfo CreateProcessStartInfo()
    {
        return new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
    }

    private static ProcessResult RunProcess(
        ProcessStartInfo startInfo,
        string operation)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"failed to start {operation}");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new ProcessResult(
            process.ExitCode,
            standardOutput.GetAwaiter().GetResult(),
            standardError.GetAwaiter().GetResult());
    }

    private static string JsonPayload(string output)
    {
        int start = output.IndexOf('{');
        int end = output.LastIndexOf('}');
        if (start < 0 || end < start)
            throw new FormatException("MSBuild query did not emit a JSON object");
        return output[start..(end + 1)];
    }

    private static IReadOnlySet<string> EvaluatedSymbols(
        SyncBuildVariant requestedVariant,
        string constants)
    {
        HashSet<string> symbols = requestedVariant.Symbols
            .Concat(constants.Split(';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(symbol => !symbol.StartsWith("OS_", StringComparison.Ordinal) &&
                !string.Equals(symbol, "DEBUG", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        symbols.Add(requestedVariant.Platform);
        if (string.Equals(requestedVariant.Configuration, "Debug",
                StringComparison.Ordinal))
            symbols.Add("DEBUG");
        return symbols;
    }

    private static void ValidateInputs(
        IReadOnlyList<SyncBuildVariant> variants,
        IReadOnlyDictionary<string, string> globalProperties)
    {
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(globalProperties);
        if (variants.Count == 0)
            throw new ArgumentException("at least one build variant is required", nameof(variants));
        if (variants.Any(variant => string.IsNullOrWhiteSpace(variant.Configuration) ||
                                   string.IsNullOrWhiteSpace(variant.Platform)))
            throw new ArgumentException("build variants require configuration and platform");
        if (variants.Select(variant => variant.Key).Distinct(StringComparer.Ordinal).Count() !=
            variants.Count)
            throw new ArgumentException("build variant keys must be unique", nameof(variants));
        string? reserved = globalProperties.Keys.FirstOrDefault(ReservedProperties.Contains);
        if (reserved is not null)
            throw new ArgumentException(
                $"global property {reserved} is controlled by the catalog loader",
                nameof(globalProperties));
        foreach ((string name, string value) in globalProperties)
            ValidateGlobalProperty(name, value);
    }

    private static void ValidateGlobalProperty(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            !name.All(character => char.IsLetterOrDigit(character) ||
                character is '_' or '.'))
            throw new ArgumentException($"invalid MSBuild global property name {name}");
        if (value.Contains(';') || value.Contains('\r') || value.Contains('\n'))
            throw new ArgumentException(
                $"MSBuild global property {name} contains a property separator");
    }

    private static IReadOnlyDictionary<string, string> ReadSources(JsonElement items)
    {
        var sources = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement item in items.EnumerateArray())
        {
            string path = CanonicalExistingPath(RequiredString(item, "FullPath"));
            if (!sources.TryAdd(path, File.ReadAllText(path)))
                throw new FormatException($"MSBuild returned duplicate Compile item {path}");
        }
        return sources;
    }

    private static IReadOnlyList<string> ReadReferences(JsonElement items)
    {
        return items.EnumerateArray()
            .Select(item => CanonicalExistingPath(RequiredString(item, "FullPath")))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string CanonicalExistingPath(string path)
    {
        string canonical = Path.GetFullPath(path);
        if (!File.Exists(canonical))
            throw new FileNotFoundException("MSBuild item does not exist", canonical);
        return canonical;
    }

    private static JsonElement RequiredObject(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object)
            throw new FormatException($"MSBuild JSON requires object {property}");
        return value;
    }

    private static JsonElement RequiredArray(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
            throw new FormatException($"MSBuild JSON requires array {property}");
        return value;
    }

    private static string RequiredString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String || value.GetString() is not string text)
            throw new FormatException($"MSBuild JSON requires string {property}");
        return text;
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed class MsBuildFailure : InvalidOperationException
    {
        public MsBuildFailure(string variant, ProcessResult result)
            : base($"dotnet msbuild failed for {variant}: {result.Error}\n{result.Output}")
        {
            AssetsMissing = result.Error.Contains("NETSDK1004", StringComparison.Ordinal) ||
                result.Output.Contains("NETSDK1004", StringComparison.Ordinal);
        }

        public bool AssetsMissing { get; }
    }
}
