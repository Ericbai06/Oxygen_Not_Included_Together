using System.Text.RegularExpressions;

namespace ONI_Together.HeadlessTests;

internal static class BuildAuthorityArchitectureContractTests
{
    private static readonly string[] RequiredSymbols = [
        "BuildOperationId",
        "BuildRequest",
        "BuildGeometry",
        "SinglePlacementGeometry",
        "UtilityPathGeometry",
        "BuildCommit",
        "BuildRejected",
        "ApplyResult",
        "HostBuildPolicy",
        "AuthoritativeBuildExecutor",
        "BuildCommitApplier",
        "BuildMutationContext",
        "BuildPublisher",
        "SinglePlacementPlanner",
        "UtilityPathPlanner",
        "BuildRequestPacket",
        "BuildCommitPacket",
        "BuildRejectedPacket"
    ];

    private static readonly string[] ForbiddenBoundaryTokens = [
        "BuildPath",
        "PlanScreen",
        "PriorityScreen",
        "BuildMenu",
        "ToolMenu",
        "ResourceRemainingDisplayScreen",
        "SandboxToolParameterMenu",
        "DebugHandler"
    ];

    private static readonly Regex Declaration = new(
        @"\b(?:class|record|struct|enum|interface)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static void Run()
    {
        IReadOnlyDictionary<string, string> sources = LoadProductionSources();
        RequiredBuildSymbolsAreDeclared(sources);
        BoundaryContainsNoUiOrToolReferences(sources);
        LegacyBuildWireSurfaceIsGone(sources);
        BuildWireSurfaceContainsExactlyThreePackets(sources);
        UtilityPlannerIsOutcomeDriven(sources);
        SuccessOnlyEdgeOracleRejectsFailedEndpoints();
    }

    internal static int RunCli(TextWriter output, TextWriter error)
    {
        try
        {
            Run();
            output.WriteLine("PASS build architecture contracts");
            return 0;
        }
        catch (Exception exception)
        {
            error.WriteLine($"FAIL build architecture contracts: {exception.Message}");
            return 1;
        }
    }

    private static void RequiredBuildSymbolsAreDeclared(
        IReadOnlyDictionary<string, string> sources)
    {
        string allSource = string.Join("\n", sources.Values);
        foreach (string symbol in RequiredSymbols)
        {
            bool declared = sources.Values.Any(source => Declaration.Matches(source)
                .Cast<Match>()
                .Any(match => string.Equals(match.Groups["name"].Value,
                    symbol, StringComparison.Ordinal)));
            True(declared, $"required build symbol is missing: {symbol}");
        }

        True(allSource.Contains(
            "namespace ONI_Together.Networking.Packets.Tools.Build",
            StringComparison.Ordinal), "build domain namespace is missing");
    }

    private static void BoundaryContainsNoUiOrToolReferences(
        IReadOnlyDictionary<string, string> sources)
    {
        foreach ((string path, string source) in BoundarySources(sources))
        {
            if (IsCapturePatch(path))
                continue;

            foreach (string token in ForbiddenBoundaryTokens)
                True(!source.Contains(token, StringComparison.Ordinal),
                    $"forbidden boundary reference {token} in {path}");

            True(!Regex.IsMatch(source,
                    @"\b[A-Za-z_][A-Za-z0-9_]*Tool(?:\.Instance)?\b"),
                $"Tool reference in build boundary {path}");
            True(!Regex.IsMatch(source, @"\b[A-Za-z_][A-Za-z0-9_]*Screen\b"),
                $"Screen reference in build boundary {path}");
        }
    }

    private static void LegacyBuildWireSurfaceIsGone(
        IReadOnlyDictionary<string, string> sources)
    {
        string[] legacy =
        [
            "BuildPacket",
            "UtilityBuildPacket",
            "BuildStatePacket",
            "UtilityBuildStatePacket",
            "BuildCompletePacket"
        ];
        foreach (string symbol in legacy)
            True(!DeclaredSymbols(sources).Contains(symbol),
                $"legacy build wire type remains: {symbol}");
    }

    private static void BuildWireSurfaceContainsExactlyThreePackets(
        IReadOnlyDictionary<string, string> sources)
    {
        string[] expected = ["BuildCommitPacket", "BuildRejectedPacket",
            "BuildRequestPacket"];
        string[] actual = DeclaredSymbols(sources)
            .Where(name => name.EndsWith("BuildPacket", StringComparison.Ordinal) ||
                name is "BuildRequestPacket" or "BuildCommitPacket" or
                "BuildRejectedPacket")
            .Order(StringComparer.Ordinal)
            .ToArray();
        EqualSequence(expected, actual, "build packet surface drifted");
    }

    private static void UtilityPlannerIsOutcomeDriven(IReadOnlyDictionary<string, string> sources)
    {
        KeyValuePair<string, string>[] planners = sources.Where(pair =>
            pair.Value.Contains("class UtilityPathPlanner", StringComparison.Ordinal)).ToArray();
        True(planners.Length == 1, "UtilityPathPlanner must have one source owner");
        string source = planners[0].Value;
        True(source.Contains("successfulCells", StringComparison.Ordinal), "UtilityPathPlanner does not consume observed successful outcomes");
        True(source.Contains("UtilityEdge", StringComparison.Ordinal), "UtilityPathPlanner does not emit explicit utility edges");
        True(!Regex.IsMatch(source, @"Apply(?:Path)?Connections?\s*\([^)]*Cells"),
            "utility topology still accepts the requested full path");
    }

    private static void SuccessOnlyEdgeOracleRejectsFailedEndpoints()
    {
        int[] requested = [10, 11, 12, 13];
        TestPlacement[] observed =
        [
            new(10, PlacementKind.CreatedQueued),
            new(11, PlacementKind.Failed),
            new(12, PlacementKind.CreatedCompleted),
            new(13, PlacementKind.ExistingCompatible)
        ];

        TestEdge[] edges = BuildObservedEdges(requested, observed).ToArray();
        Equal(1, edges.Length, "failed path endpoint produced a connection");
        Equal(new TestEdge(12, 13), edges[0],
            "edge was not derived from adjacent successful outcomes");
        True(edges.All(edge => observed.Any(item => item.Cell == edge.From &&
                item.Kind != PlacementKind.Failed) && observed.Any(item =>
                item.Cell == edge.To && item.Kind != PlacementKind.Failed)),
            "edge endpoint is not a successful placement");
    }

    private static IEnumerable<TestEdge> BuildObservedEdges(
        IReadOnlyList<int> requested,
        IReadOnlyList<TestPlacement> observed)
    {
        HashSet<int> successful = observed
            .Where(item => item.Kind != PlacementKind.Failed)
            .Select(item => item.Cell)
            .ToHashSet();
        for (int index = 1; index < requested.Count; index++)
        {
            int from = requested[index - 1];
            int to = requested[index];
            if (successful.Contains(from) && successful.Contains(to) &&
                Math.Abs(from - to) == 1)
                yield return new TestEdge(from, to);
        }
    }

    private static IReadOnlyDictionary<string, string> LoadProductionSources()
    {
        string root = FindRepositoryRoot();
        string production = Path.Combine(root, "ONI_Together");
        return Directory.EnumerateFiles(production, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            .ToDictionary(path => Path.GetRelativePath(root, path),
                File.ReadAllText, StringComparer.Ordinal);
    }

    private static IEnumerable<KeyValuePair<string, string>> BoundarySources(
        IReadOnlyDictionary<string, string> sources)
    {
        foreach ((string path, string source) in sources)
        {
            if (path.Contains("/Networking/Building/", StringComparison.Ordinal) ||
                path.Contains("\\Networking\\Building\\", StringComparison.Ordinal) ||
                path.Contains("/Networking/Packets/Tools/Build/",
                    StringComparison.Ordinal) ||
                path.Contains("\\Networking\\Packets\\Tools\\Build\\",
                    StringComparison.Ordinal) ||
                path.EndsWith("/Patches/ToolPatches/Build/BuildRuntimeAdapter.cs",
                    StringComparison.Ordinal) ||
                path.EndsWith("\\Patches\\ToolPatches\\Build\\BuildRuntimeAdapter.cs",
                    StringComparison.Ordinal) ||
                path.EndsWith("/Patches/ToolPatches/Build/HostBuildPolicyProvider.cs",
                    StringComparison.Ordinal) ||
                path.EndsWith("\\Patches\\ToolPatches\\Build\\HostBuildPolicyProvider.cs",
                    StringComparison.Ordinal))
                yield return new KeyValuePair<string, string>(path, source);
        }
    }

    private static bool IsCapturePatch(string path)
        => path.EndsWith("BuildToolPatch.cs", StringComparison.Ordinal) ||
            path.EndsWith("UtilityBuildToolPatch.cs", StringComparison.Ordinal);

    private static IReadOnlySet<string> DeclaredSymbols(
        IReadOnlyDictionary<string, string> sources)
        => sources.Values.SelectMany(source => Declaration.Matches(source)
            .Cast<Match>()
            .Select(match => match.Groups["name"].Value))
            .ToHashSet(StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ONI_Together.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("ONI_Together repository root not found");
    }

    private static void True(bool condition, string message) =>
        _ = condition ? 0 : throw new InvalidOperationException(message);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{message}; expected={expected}, actual={actual}");
    }

    private static void EqualSequence<T>(IReadOnlyList<T> expected,
        IReadOnlyList<T> actual, string message)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(
                $"{message}; expected=[{string.Join(',', expected)}], " +
                $"actual=[{string.Join(',', actual)}]");
    }

    private enum PlacementKind
    {
        CreatedQueued,
        CreatedCompleted,
        Replaced,
        ExistingCompatible,
        Failed
    }

    private readonly record struct TestPlacement(int Cell, PlacementKind Kind);
    private readonly record struct TestEdge(int From, int To);
}
