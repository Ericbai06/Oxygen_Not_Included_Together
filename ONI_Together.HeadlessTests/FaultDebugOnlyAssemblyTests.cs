using System.Reflection.Metadata;

namespace ONI_Together.HeadlessTests;

internal static class FaultDebugOnlyAssemblyTests
{
    private const string FaultNamespace = "ONI_Together.DebugTools.";

    private static readonly string[] ExpectedCaseIds =
    [
        "duplicant.personality-missing", "duplicant.set-minion-before-controller",
        "duplicant.preview-flatulence", "duplicant.destroyed-add-component",
        "work.workable-unregistered", "work.target-missing",
        "work.original-dig-element-null", "work.revision-stale",
        "work.client-native-start", "building.selected-elements-null",
        "building.complete-before-queued", "building.finish-duplicate",
        "building.net-id-collision", "building.destroy-deferred",
        "inventory.storage-missing", "inventory.item-missing",
        "inventory.membership-wrong", "inventory.mass-zero",
        "inventory.delta-duplicate", "inventory.delta-out-of-order",
        "entity.state-before-identity", "entity.despawn-before-spawn",
        "entity.spawn-after-tombstone", "entity.prefab-null",
        "dlc.fingerprint-mismatch", "dlc.prefab-missing",
        "dlc.state-before-start-sm", "dlc.family-aquatic", "dlc.family-bionic",
        "dlc.family-frosty", "dlc.family-prehistoric", "dlc.family-spaced-out",
        "dlc.family-common", "reconnect.session-stale",
        "reconnect.connection-stale", "reconnect.snapshot-stale",
        "reconnect.batch-missing", "reconnect.batch-duplicate",
        "reconnect.ack-lost", "reconnect.disconnect-mid-apply"
    ];

    private static readonly string[] RequiredDebugTypes =
    [
        FaultNamespace + "FaultInjectionController",
        FaultNamespace + "FaultInjectionRegistry",
        FaultNamespace + "FaultInjectionHandlerRegistry",
        FaultNamespace + "FaultInjectionUnitySeams",
        FaultNamespace + "FaultInjectionDriverRegistry",
        FaultNamespace + "FaultProductionBindingRegistry",
        FaultNamespace + "FaultUnityBindingRegistry",
        "ONI_Together.Networking.ProductionFaultInputGates",
        "ONI_Together.Patches.Duplicant.DiggableStartWorkFaultPatch",
        "ONI_Together.Patches.World.WorkableStartWorkAuthorityPatch"
    ];

    private static readonly string[] ForbiddenReleaseTypeFragments =
    [
        "ONI_Together.DebugTools.Fault", "IFaultInputMutation",
        "ProductionFaultInputGates",
        "DiggableStartWorkFaultPatch", "WorkableStartWorkAuthorityPatch"
    ];

    public static int Run(string[] assemblyPaths)
    {
        (string debugPath, string releasePath) = AssemblyPaths(assemblyPaths);
        (string Name, Action Test)[] tests =
        [
            ("Release excludes fault injection surface", () => ReleaseExcludesFaultSurface(releasePath)),
            ("Debug retains exact 40-case fault surface", () => DebugRetainsFaultSurface(debugPath))
        ];
        int failures = 0;
        foreach ((string name, Action test) in tests)
        {
            try
            {
                test();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {error.Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }

    internal static void ReleaseAssemblyExcludesFaultSurface()
        => ReleaseExcludesFaultSurface(AssemblyPaths(Array.Empty<string>()).Release);

    internal static void DebugAssemblyRetainsFaultSurface()
        => DebugRetainsFaultSurface(AssemblyPaths(Array.Empty<string>()).Debug);

    private static void ReleaseExcludesFaultSurface(string assemblyPath)
    {
        using var image = new AssemblyImage(assemblyPath);
        string[] forbiddenTypes = image.TypeNames
            .Where(IsForbiddenReleaseType).Order(StringComparer.Ordinal).ToArray();
        string[] forbiddenStrings = image.UserStrings
            .Where(IsFaultCommand).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        string[] forbiddenCalls = image.Calls
            .Where(call => IsForbiddenReleaseType(call.Owner))
            .Select(call => call.Owner + "." + call.Name)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (forbiddenTypes.Length + forbiddenStrings.Length + forbiddenCalls.Length == 0)
            return;
        throw new InvalidOperationException(
            "types=[" + string.Join(", ", forbiddenTypes) + "]; commands=[" +
            string.Join(", ", forbiddenStrings) + "]; calls=[" +
            string.Join(", ", forbiddenCalls) + "]");
    }

    private static void DebugRetainsFaultSurface(string assemblyPath)
    {
        using var image = new AssemblyImage(assemblyPath);
        foreach (string requiredType in RequiredDebugTypes)
            True(image.TypeNames.Contains(requiredType, StringComparer.Ordinal),
                "Debug assembly is missing " + requiredType);

        MethodDefinitionHandle initializer = image.FindMethod(
            FaultNamespace + "FaultInjectionRegistry", ".cctor");
        string[] registered = image.UserStringsIn(initializer)
            .Where(value => ExpectedCaseIds.Contains(value, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        EqualSequence(ExpectedCaseIds.Order(StringComparer.Ordinal), registered,
            "Debug fault registry case IDs");
        Equal(40, image.CountCalls(initializer,
            FaultNamespace + "FaultInjectionRegistry", "C"), "Debug fault registry size");

        string[] commands = image.UserStringsInTypes(
                FaultNamespace + "FaultInjectionCommand",
                FaultNamespace + "FaultInjectionDriverRegistry")
            .Where(IsFaultCommand).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        EqualSequence(new[] { "fault-clean:", "fault-inject:" }, commands,
            "Debug fault command prefixes");
    }

    private static (string Debug, string Release) AssemblyPaths(string[] paths)
    {
        if (paths.Length != 0 && paths.Length != 2)
            throw new ArgumentException("Expected zero paths or explicit Debug and Release DLL paths");
        string root = FindRepositoryRoot();
        string output = Path.Combine(root, "ONI_Together", "bin");
        string debug = paths.Length == 2 ? paths[0] :
            Path.Combine(output, "Debug", "netstandard2.1", "ONI_Together.dll");
        string release = paths.Length == 2 ? paths[1] :
            Path.Combine(output, "Release", "netstandard2.1", "ONI_Together.dll");
        True(File.Exists(debug), "Debug assembly is missing: " + debug);
        True(File.Exists(release), "Release assembly is missing: " + release);
        return (debug, release);
    }

    private static bool IsForbiddenReleaseType(string value)
        => ForbiddenReleaseTypeFragments.Any(fragment =>
            value.Contains(fragment, StringComparison.Ordinal));

    private static bool IsFaultCommand(string value)
        => value.StartsWith("fault-inject:", StringComparison.Ordinal) ||
           value.StartsWith("fault-clean:", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "Shared")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "ONI_Together")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new InvalidOperationException("ONI_Together repository root was not found");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal(int expected, int actual, string label)
    {
        if (expected != actual)
            throw new InvalidOperationException($"{label}: expected {expected}, actual {actual}");
    }

    private static void EqualSequence(
        IEnumerable<string> expected, IEnumerable<string> actual, string label)
    {
        string[] expectedArray = expected.ToArray();
        string[] actualArray = actual.ToArray();
        if (!expectedArray.SequenceEqual(actualArray, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"{label}: expected [{string.Join(", ", expectedArray)}], " +
                $"actual [{string.Join(", ", actualArray)}]");
    }
}
