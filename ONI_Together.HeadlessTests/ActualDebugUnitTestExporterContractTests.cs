namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestExporterContractTests
{
    private const string Command = "actual-unit-export";

    internal static void ImplementationIsAvailable()
    {
        _ = ActualDebugUnitTestExporterLoader.Load();
    }

    internal static void ProgramRouteIsAvailable()
    {
        Equal(1, Program.Main([Command]));
    }

    internal static void InvalidCliInputsLeaveNoOutput()
    {
        IActualDebugUnitTestExporter exporter =
            ActualDebugUnitTestExporterLoader.Load();
        string root = ActualDebugUnitTestBatchFixture.RepositoryRoot();
        string gameLibs = RequireGameLibs();
        string coverage = Path.Combine(root, "sync-entry-coverage.json");
        string temporary = Path.Combine(Path.GetTempPath(),
            "oni-actual-unit-export-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Reject(exporter, [Command], Path.Combine(temporary, "missing"));
            Reject(exporter, [
                Command, "--game-libs", gameLibs,
                "--coverage", coverage,
                "--unknown", "value",
                "--output-dir", Path.Combine(temporary, "unknown")
            ], Path.Combine(temporary, "unknown"));
            Reject(exporter, [
                Command, "--game-libs", gameLibs,
                "--coverage", coverage,
                "--coverage", coverage,
                "--output-dir", Path.Combine(temporary, "duplicate")
            ], Path.Combine(temporary, "duplicate"));
            Reject(exporter, [
                Command, "--game-libs", gameLibs,
                "--coverage"
            ], Path.Combine(temporary, "missing-value"));

            var fixture = new InvalidFixture
            {
                Exporter = exporter,
                Temporary = temporary,
                GameLibs = gameLibs,
                Coverage = coverage
            };
            RejectMissingPaths(fixture);
            RejectExistingOutput(fixture);
            RejectOverlap(exporter, gameLibs, coverage);
            RejectMalformedCoverage(exporter, temporary, gameLibs);
            RejectBatchFailure(exporter, temporary, coverage);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static void RejectMissingPaths(InvalidFixture fixture)
    {
        string missingCoverageOutput =
            Path.Combine(fixture.Temporary, "missing-coverage-output");
        Reject(fixture.Exporter, [
            Command, "--game-libs", fixture.GameLibs,
            "--coverage", Path.Combine(fixture.Temporary, "missing.json"),
            "--output-dir", missingCoverageOutput
        ], missingCoverageOutput);

        string missingLibsOutput =
            Path.Combine(fixture.Temporary, "missing-libs-output");
        Reject(fixture.Exporter, [
            Command, "--game-libs",
            Path.Combine(fixture.Temporary, "missing-libs"),
            "--coverage", fixture.Coverage,
            "--output-dir", missingLibsOutput
        ], missingLibsOutput);
    }

    private static void RejectExistingOutput(InvalidFixture fixture)
    {
        string output = Path.Combine(fixture.Temporary, "existing-output");
        Directory.CreateDirectory(output);
        string sentinel = Path.Combine(output, "sentinel");
        File.WriteAllText(sentinel, "preserve");
        int exitCode = fixture.Exporter.RunCli([
            Command, "--game-libs", fixture.GameLibs,
            "--coverage", fixture.Coverage,
            "--output-dir", output
        ], TextWriter.Null, TextWriter.Null);
        Equal(1, exitCode);
        Equal("preserve", File.ReadAllText(sentinel));
        EqualSequence(["sentinel"], Directory.EnumerateFileSystemEntries(output)
            .Select(Path.GetFileName));
    }

    private static void RejectOverlap(
        IActualDebugUnitTestExporter exporter,
        string gameLibs,
        string coverage)
    {
        byte[] source = File.ReadAllBytes(coverage);
        int exitCode = exporter.RunCli([
            Command, "--game-libs", gameLibs,
            "--coverage", coverage,
            "--output-dir", coverage
        ], TextWriter.Null, TextWriter.Null);
        Equal(1, exitCode);
        True(source.SequenceEqual(File.ReadAllBytes(coverage)),
            "overlap failure changed source coverage bytes");
    }

    private static void RejectMalformedCoverage(
        IActualDebugUnitTestExporter exporter,
        string temporary,
        string gameLibs)
    {
        string malformed = Path.Combine(temporary, "malformed.json");
        string output = Path.Combine(temporary, "malformed-output");
        File.WriteAllText(malformed, "{}");
        Reject(exporter, [
            Command, "--game-libs", gameLibs,
            "--coverage", malformed,
            "--output-dir", output
        ], output);
    }

    private static void RejectBatchFailure(
        IActualDebugUnitTestExporter exporter,
        string temporary,
        string coverage)
    {
        string emptyGameLibs = Path.Combine(temporary, "empty-game-libs");
        string output = Path.Combine(temporary, "batch-failure-output");
        Directory.CreateDirectory(emptyGameLibs);
        Reject(exporter, [
            Command, "--game-libs", emptyGameLibs,
            "--coverage", coverage,
            "--output-dir", output
        ], output);
    }

    private static void Reject(
        IActualDebugUnitTestExporter exporter,
        IReadOnlyList<string> args,
        string output)
    {
        using var stderr = new StringWriter();
        Equal(1, exporter.RunCli(args, TextWriter.Null, stderr));
        True(stderr.ToString().Length > 0,
            "failed export omitted an error");
        True(!Directory.Exists(output) && !File.Exists(output),
            "failed export left an output path");
    }

    internal static string RequireGameLibs()
    {
        string? value = Environment.GetEnvironmentVariable("ONI_GAME_LIBS");
        if (string.IsNullOrWhiteSpace(value) || !Directory.Exists(value))
            throw new InvalidOperationException("ONI_GAME_LIBS is required");
        return Path.GetFullPath(value);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"expected {expected}, actual {actual}");
    }

    private static void EqualSequence<T>(
        IEnumerable<T> expected,
        IEnumerable<T?> actual)
    {
        if (!expected.Cast<T?>().SequenceEqual(actual))
            throw new InvalidOperationException("sequences differ");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record InvalidFixture
    {
        internal required IActualDebugUnitTestExporter Exporter { get; init; }
        internal required string Temporary { get; init; }
        internal required string GameLibs { get; init; }
        internal required string Coverage { get; init; }
    }
}
