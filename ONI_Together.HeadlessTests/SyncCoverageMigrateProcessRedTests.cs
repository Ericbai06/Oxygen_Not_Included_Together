using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class SyncCoverageMigrateProcessRedTests
{
    public static void RealProcessDispatchesCoverageMigrateCli()
    {
        string root = FindRepositoryRoot();
        string gameLibs = Environment.GetEnvironmentVariable("ONI_GAME_LIBS") ??
            throw new InvalidOperationException("ONI_GAME_LIBS is required");
        string temporary = Path.Combine(Path.GetTempPath(),
            "oni-coverage-migrate-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string stale = Path.Combine(temporary, "stale.json");
            string first = Path.Combine(temporary, "first.json");
            string second = Path.Combine(temporary, "second.json");
            File.WriteAllText(stale, StaleCoverageJson());
            string staleHash = FileDigest(stale);
            string project = Path.Combine(
                root, "ONI_Together", "ONI_Together.csproj");
            string currentDigest = CurrentInventoryDigest(project, gameLibs);

            ProcessResult initial = RunProcess(
                ValidArguments(project, gameLibs, stale, first));
            Equal(0, initial.ExitCode,
                "coverage-migrate child process returned failure");
            True(File.Exists(first),
                "coverage-migrate command was not dispatched: output is missing");
            Equal(staleHash, FileDigest(stale));
            SyncCoverageManifest firstManifest = SyncCoverageManifest.Parse(
                File.ReadAllText(first));
            Equal(currentDigest, firstManifest.InventoryDigest);
            True(initial.Stdout.Contains("removedOrphans=", StringComparison.Ordinal),
                "coverage-migrate stdout omitted removedOrphans");
            True(initial.Stdout.Contains("remainingUnmapped=", StringComparison.Ordinal),
                "coverage-migrate stdout omitted remainingUnmapped");

            ProcessResult repeated = RunProcess(
                ValidArguments(project, gameLibs, stale, second));
            Equal(0, repeated.ExitCode,
                "second coverage-migrate child process returned failure");
            Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
            Equal(staleHash, FileDigest(stale));

            ProcessResult unknown = RunProcess(
                ["coverage-migrate", "--unknown", "value"]);
            True(unknown.ExitCode != 0,
                "unknown coverage-migrate option returned success");
            ProcessResult missing = RunProcess([
                "coverage-migrate", "--project", project,
                "--game-libs", gameLibs
            ]);
            True(missing.ExitCode != 0,
                "missing coverage-migrate paths returned success");
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static ProcessResult RunProcess(IReadOnlyList<string> arguments)
    {
        string dotnet = Environment.ProcessPath ??
            throw new InvalidOperationException("dotnet process path is unavailable");
        var start = new ProcessStartInfo
        {
            FileName = dotnet,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException("coverage-migrate process did not start");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                "coverage-migrate process exceeded 120 seconds");
        }
        Task.WaitAll(stdout, stderr);
        return new ProcessResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    private static string CurrentInventoryDigest(
        string project,
        string gameLibs)
    {
        IReadOnlyList<SyncVariantInput> inputs = SyncMsBuildProjectLoader.Load(
            project, Variants(), new Dictionary<string, string>
            {
                ["GameLibsFolder"] = gameLibs
            });
        SyncCatalogScan catalog = SyncSurfaceScanner.ScanCatalogVariants(inputs);
        True(catalog.Errors.Count == 0,
            "current eight-variant catalog contains errors");
        using JsonDocument inventory = JsonDocument.Parse(
            SyncInventoryJson.Serialize(catalog));
        return inventory.RootElement.GetProperty("digest").GetString()!;
    }

    private static SyncBuildVariant[] Variants() =>
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

    private static SyncBuildVariant Variant(
        string configuration,
        string platform,
        params string[] symbols) => new(
            configuration, platform,
            symbols.ToHashSet(StringComparer.Ordinal));

    private static string[] ValidArguments(
        string project,
        string gameLibs,
        string stale,
        string output) =>
    [
        "coverage-migrate",
        "--project", project,
        "--game-libs", gameLibs,
        "--coverage", stale,
        "--output", output
    ];

    private static string StaleCoverageJson() =>
        JsonSerializer.Serialize(new
        {
            inventoryDigest = new string('0', 64),
            entries = new[]
            {
                new
                {
                    id = "sync:000000000000000000000000",
                    domain = "orphan",
                    testIds = Array.Empty<string>(),
                    negativeTestIds = Array.Empty<string>(),
                    scenarioIds = Array.Empty<string>(),
                    variants = new[] { "Debug/OS_MAC" },
                    status = SyncEntryStatus.Active.ToString()
                }
            }
        });

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
             directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "Shared")) &&
                Directory.Exists(Path.Combine(directory.FullName, "ONI_Together")))
                return directory.FullName;
        throw new InvalidOperationException("repository root was not found");
    }

    private static string FileDigest(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();

    private static void Equal(byte[] expected, byte[] actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(
                "coverage-migrate outputs are not byte-identical");
    }

    private static void Equal<T>(
        T expected,
        T actual,
        string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(message ??
                $"expected {expected}, actual {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record ProcessResult(
        int ExitCode,
        string Stdout,
        string Stderr);
}
