using System.Reflection;
using System.Runtime.Loader;

namespace ONI_Together.HeadlessTests;

internal sealed partial class ActualDebugUnitTestBatchRunner
{
    private static readonly string[] AccessBypassTargets =
        ["Assembly-CSharp", "Assembly-CSharp-firstpass"];
    private static readonly object RuntimeAssemblyLock = new();
    private static readonly Dictionary<string, LoadedRuntimeAssembly>
        LoadedRuntimeAssemblies = new(StringComparer.Ordinal);

    private static IReadOnlyList<ActualDebugRuntimeAssemblyEvidence>
        BootstrapRuntimeAssemblies(ActualDebugUnitTestBatchInput input)
    {
        IReadOnlyList<ActualDebugRuntimeAssemblyInput> assemblies =
            ValidateRuntimeInputs(input);
        lock (RuntimeAssemblyLock)
        {
            var evidence = new List<ActualDebugRuntimeAssemblyEvidence>(2);
            foreach (ActualDebugRuntimeAssemblyInput item in assemblies)
            {
                LoadRuntimeAssembly(item);
                evidence.Add(new ActualDebugRuntimeAssemblyEvidence(
                    item.Path, item.Sha256, item.AssemblyIdentity));
            }
            return evidence;
        }
    }

    private static void LoadRuntimeAssembly(ActualDebugRuntimeAssemblyInput input)
    {
        if (LoadedRuntimeAssemblies.TryGetValue(
                input.AssemblyIdentity, out LoadedRuntimeAssembly? loaded))
        {
            if (loaded.Path != input.Path || loaded.Sha256 != input.Sha256 ||
                !AppDomain.CurrentDomain.GetAssemblies().Contains(loaded.Assembly))
                throw new InvalidOperationException(
                    "runtime assembly identity was already bound differently");
            return;
        }
        AssemblyName expected = new(input.AssemblyIdentity);
        Assembly? preloaded = AppDomain.CurrentDomain.GetAssemblies()
            .SingleOrDefault(assembly =>
                AssemblyName.ReferenceMatchesDefinition(
                    expected, assembly.GetName()));
        if (preloaded is not null)
            throw new InvalidOperationException(
                $"runtime identity was preloaded outside bootstrap: {expected.Name}");
        using var stream = new MemoryStream(input.PeImage, writable: false);
        Assembly assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
        if (assembly.FullName != input.AssemblyIdentity)
            throw new InvalidOperationException(
                $"runtime assembly identity drift: {input.Path}");
        LoadedRuntimeAssemblies.Add(input.AssemblyIdentity,
            new LoadedRuntimeAssembly(input.Path, input.Sha256, assembly));
    }

    private static ActualDebugAccessBypassBootstrapEvidence
        CompleteAccessBypassBootstrap(
            SyncExecutionInstrumentedAssembly instrumented,
            Assembly assembly,
            IReadOnlyList<ActualDebugRuntimeAssemblyEvidence> runtimeAssemblies)
    {
        try
        {
            int typeCount = assembly.GetTypes().Length;
            return new ActualDebugAccessBypassBootstrapEvidence(
                AccessBypassTargets, instrumented.DllHash,
                instrumented.PdbHash, runtimeAssemblies, typeCount, 0);
        }
        catch (ReflectionTypeLoadException error)
        {
            int cannotReduce = error.LoaderExceptions.Count(exception =>
                exception is TypeLoadException &&
                exception.Message.Contains(
                    "cannot reduce access", StringComparison.OrdinalIgnoreCase));
            throw new InvalidOperationException(
                $"instrumented type load failed; cannotReduceAccess={cannotReduce}",
                error);
        }
    }

    private static IReadOnlyList<ActualDebugRuntimeAssemblyInput>
        ValidateRuntimeInputs(ActualDebugUnitTestBatchInput input)
    {
        if (input.PublicizedAssemblies is not null)
            throw new InvalidOperationException(
                "batch still supplies publicized runtime assemblies");
        IReadOnlyList<ActualDebugRuntimeAssemblyInput>? inputs =
            input.RuntimeAssemblies;
        if (inputs is null || inputs.Count != 2)
            throw new InvalidOperationException(
                "batch requires exactly two normal runtime assemblies");
        if (inputs.GroupBy(item => item.Path, StringComparer.Ordinal)
                .Any(group => group.Count() != 1) ||
            inputs.GroupBy(item => item.AssemblyIdentity, StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
            throw new InvalidOperationException(
                "batch runtime assemblies contain duplicates");
        foreach (ActualDebugRuntimeAssemblyInput item in inputs)
            ValidateRuntimeInput(item);
        string[] names = inputs.Select(item =>
                new AssemblyName(item.AssemblyIdentity).Name!)
            .Order(StringComparer.Ordinal).ToArray();
        if (!names.SequenceEqual(AccessBypassTargets))
            throw new InvalidOperationException(
                "batch runtime assembly identities are not exact");
        return inputs;
    }

    private static void ValidateRuntimeInput(ActualDebugRuntimeAssemblyInput input)
    {
        string simpleName = new AssemblyName(input.AssemblyIdentity).Name!;
        string expectedFile = simpleName + ".dll";
        string gameLibs = Environment.GetEnvironmentVariable("ONI_GAME_LIBS") ??
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile),
                "Library/Application Support/Steam/steamapps/common/" +
                "OxygenNotIncluded/OxygenNotIncluded.app/Contents/Resources/" +
                "Data/Managed");
        string expectedPath = Path.GetFullPath(
            Path.Combine(gameLibs, expectedFile));
        if (!Path.IsPathFullyQualified(input.Path) ||
            Path.GetFullPath(input.Path) != expectedPath ||
            input.Path.Contains("_public", StringComparison.Ordinal) ||
            input.Path.Contains("PublicisedAssembly", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "runtime assembly path is not a normal GameLibs binary");
        if (Digest(input.PeImage) != input.Sha256)
            throw new InvalidOperationException(
                $"runtime assembly hash drift: {input.Path}");
        using var stream = new MemoryStream(input.PeImage, writable: false);
        using Mono.Cecil.AssemblyDefinition assembly =
            Mono.Cecil.AssemblyDefinition.ReadAssembly(stream);
        if (assembly.Name.FullName != input.AssemblyIdentity)
            throw new InvalidOperationException(
                $"runtime input identity drift: {input.Path}");
    }

    private static void ValidateAccessBypassBootstrap(
        ActualDebugUnitTestBatchInput input,
        ActualDebugAccessBypassBootstrapEvidence? bootstrap)
    {
        IReadOnlyList<ActualDebugRuntimeAssemblyInput> runtime =
            ValidateRuntimeInputs(input);
        ActualDebugAccessBypassBootstrapEvidence evidence = bootstrap ??
            throw new InvalidOperationException(
                "batch omitted access-bypass bootstrap evidence");
        if (!evidence.AccessBypassTargets.SequenceEqual(AccessBypassTargets) ||
            evidence.AccessBypassTargets.Distinct(StringComparer.Ordinal).Count()
                != AccessBypassTargets.Length ||
            evidence.InstrumentedTypeCount <= 0 ||
            evidence.CannotReduceAccessTypeLoadExceptionCount != 0)
            throw new InvalidOperationException(
                "access-bypass bootstrap shape drift");
        if (evidence.InstrumentedDllHash != input.DllHash ||
            evidence.InstrumentedPdbHash != input.PdbHash ||
            evidence.RuntimeAssemblies.Count != runtime.Count)
            throw new InvalidOperationException(
                "access-bypass bootstrap identity drift");
        foreach (ActualDebugRuntimeAssemblyInput item in runtime)
        {
            ActualDebugRuntimeAssemblyEvidence? actual =
                evidence.RuntimeAssemblies.SingleOrDefault(candidate =>
                    candidate.AssemblyIdentity == item.AssemblyIdentity);
            if (actual is null || actual.Path != item.Path ||
                actual.Sha256 != item.Sha256 ||
                actual.AssemblyIdentity != item.AssemblyIdentity)
                throw new InvalidOperationException(
                    $"runtime bootstrap evidence drift: {item.Path}");
        }
    }

    private static void ValidateInput(ActualDebugUnitTestBatchInput input)
    {
        if (Digest(input.Assembly.PeImage) != input.DllHash ||
            Digest(input.Assembly.PdbImage) != input.PdbHash)
            throw new InvalidOperationException("batch binary input hash drift");
        if (!HasCanonicalCoverageDigest(input.CoverageDigest))
            throw new InvalidOperationException(
                "batch input has invalid coverageDigest");
        if (input.ExpectedTests.GroupBy(
                test => test.TestId, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
            throw new InvalidOperationException(
                "batch expected tests contain duplicates");
        ValidateRuntimeInputs(input);
    }

    private static bool HasCanonicalCoverageDigest(string? value)
    {
        if (value is null || value.Length != 71 ||
            !value.StartsWith("sha256:", StringComparison.Ordinal))
            return false;
        for (int index = 7; index < value.Length; index++)
            if (value[index] is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f'))
                return false;
        return true;
    }

    private static string Digest(byte[] value) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(value))
            .ToLowerInvariant();

    private sealed record LoadedRuntimeAssembly(
        string Path,
        string Sha256,
        Assembly Assembly);
}
