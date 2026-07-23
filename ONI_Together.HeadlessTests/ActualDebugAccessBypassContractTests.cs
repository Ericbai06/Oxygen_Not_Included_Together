using System.Reflection;
using Mono.Cecil;

namespace ONI_Together.HeadlessTests;

internal static class ActualDebugAccessBypassContractTests
{
    private const string AttributeName =
        "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute";
    private static readonly string[] Targets =
        ["Assembly-CSharp", "Assembly-CSharp-firstpass"];

    internal static void InstrumentedAssemblyDefinesExactAccessBypass()
    {
        ActualDebugUnitTestBatchInput input =
            ActualDebugUnitTestBatchFixture.Load();
        ActualDebugUnitTestEnvironmentContract
            .ValidateStaticUnsupportedClassification(input);
        ValidateRuntimeInputs(input);

        SyncExecutionInstrumentedAssembly instrumented =
            SyncExecutionIlInstrumenter.Instrument(
                new SyncCatalogScan([], []), input.Assembly);
        using var pe = new MemoryStream(
            instrumented.PeImage, writable: false);
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(pe);
        TypeDefinition attribute = assembly.MainModule.Types.SingleOrDefault(
            type => type.FullName == AttributeName) ??
            throw new InvalidOperationException(
                "instrumented assembly omitted IgnoresAccessChecksToAttribute");
        True(attribute.IsClass && attribute.IsSealed &&
            attribute.BaseType?.FullName == typeof(Attribute).FullName,
            "instrumented access-bypass attribute is incompatible");
        MethodDefinition constructor = attribute.Methods.SingleOrDefault(
            method => method.IsConstructor && method.IsPublic &&
                method.Parameters.Count == 1 &&
                method.Parameters[0].ParameterType.FullName ==
                typeof(string).FullName) ??
            throw new InvalidOperationException(
                "instrumented access-bypass attribute lacks .ctor(string)");
        string[] actualTargets = assembly.CustomAttributes
            .Where(item => item.AttributeType.FullName == AttributeName &&
                item.Constructor.FullName == constructor.FullName)
            .Select(item => item.ConstructorArguments.Count == 1
                ? item.ConstructorArguments[0].Value as string
                : null)
            .Where(target => target is not null)
            .Select(target => target!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        EqualSequence(Targets.Order(StringComparer.Ordinal), actualTargets,
            "instrumented assembly access-bypass targets are not exact");
    }

    internal static void ValidateBootstrap(
        ActualDebugUnitTestBatchInput input,
        ActualDebugAccessBypassBootstrapEvidence? bootstrap)
    {
        ValidateRuntimeInputs(input);
        ActualDebugAccessBypassBootstrapEvidence evidence = bootstrap ??
            throw new InvalidOperationException(
                "batch omitted access-bypass bootstrap evidence");
        EqualSequence(Targets.Order(StringComparer.Ordinal),
            evidence.AccessBypassTargets.Order(StringComparer.Ordinal),
            "access-bypass targets are not exact");
        SyncExecutionInstrumentedAssembly expected =
            SyncExecutionIlInstrumenter.Instrument(input.Catalog, input.Assembly);
        Equal(expected.DllHash, evidence.InstrumentedDllHash);
        Equal(expected.PdbHash, evidence.InstrumentedPdbHash);
        IReadOnlyList<ActualDebugRuntimeAssemblyInput> runtime =
            input.RuntimeAssemblies!;
        Equal(runtime.Count, evidence.RuntimeAssemblies.Count);
        foreach (ActualDebugRuntimeAssemblyInput item in runtime)
        {
            ActualDebugRuntimeAssemblyEvidence actual =
                evidence.RuntimeAssemblies.Single(candidate =>
                    candidate.AssemblyIdentity == item.AssemblyIdentity);
            Equal(item.Path, actual.Path);
            Equal(item.Sha256, actual.Sha256);
        }
        True(evidence.InstrumentedTypeCount > 0,
            "Assembly.GetTypes produced no instrumented types");
        Equal(0, evidence.CannotReduceAccessTypeLoadExceptionCount);
    }

    internal static void MutationContract(
        IActualDebugUnitTestBatchRunner runner,
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestBatchResult batch)
    {
        ActualDebugAccessBypassBootstrapEvidence evidence =
            batch.AccessBypassBootstrap!;
        ActualDebugRuntimeAssemblyEvidence first =
            evidence.RuntimeAssemblies[0];
        Reject(runner, input, batch with {
            AccessBypassBootstrap = evidence with {
                AccessBypassTargets = [Targets[0]]
            }
        }, "missing access-bypass target was accepted");
        Reject(runner, input, batch with {
            AccessBypassBootstrap = evidence with {
                AccessBypassTargets = [.. Targets, Targets[0]]
            }
        }, "duplicate access-bypass target was accepted");
        Reject(runner, input, batch with {
            AccessBypassBootstrap = evidence with {
                AccessBypassTargets = [Targets[0], "Assembly-CSharp-wrong"]
            }
        }, "wrong access-bypass target was accepted");
        Reject(runner, input, batch with {
            AccessBypassBootstrap = evidence with {
                InstrumentedDllHash = Changed(evidence.InstrumentedDllHash)
            }
        }, "instrumented DLL hash drift was accepted");
        Reject(runner, input, batch with {
            AccessBypassBootstrap = evidence with {
                InstrumentedPdbHash = Changed(evidence.InstrumentedPdbHash)
            }
        }, "instrumented PDB hash drift was accepted");
        Reject(runner, input, batch with {
            AccessBypassBootstrap = evidence with {
                RuntimeAssemblies = [first with {
                    Sha256 = Changed(first.Sha256)
                }, .. evidence.RuntimeAssemblies.Skip(1)]
            }
        }, "runtime assembly hash drift was accepted");
        Reject(runner, input, batch with {
            AccessBypassBootstrap = evidence with {
                RuntimeAssemblies = [first with {
                    Path = Path.Combine(
                        ActualDebugUnitTestBatchFixture.RepositoryRoot(),
                        "PublicisedAssembly", "Assembly-CSharp_public.dll")
                }, .. evidence.RuntimeAssemblies.Skip(1)]
            }
        }, "publicized runtime assembly substitution was accepted");
    }

    private static void ValidateRuntimeInputs(
        ActualDebugUnitTestBatchInput input)
    {
        True(input.PublicizedAssemblies is null,
            "batch input still supplies publicized assembly bytes");
        IReadOnlyList<ActualDebugRuntimeAssemblyInput> runtime =
            input.RuntimeAssemblies ??
            throw new InvalidOperationException(
                "batch input omitted normal runtime assemblies");
        Equal(2, runtime.Count);
        EqualSequence(Targets, runtime
            .Select(item => new AssemblyName(item.AssemblyIdentity).Name!)
            .Order(StringComparer.Ordinal),
            "runtime assembly identities are not exact");
        EqualSequence(["Assembly-CSharp-firstpass.dll", "Assembly-CSharp.dll"],
            runtime.Select(item => Path.GetFileName(item.Path))
                .Order(StringComparer.Ordinal),
            "runtime assembly paths are not normal GameLibs binaries");
        True(runtime.All(item =>
                !item.Path.Contains("_public", StringComparison.Ordinal) &&
                !item.Path.Contains(
                    "PublicisedAssembly", StringComparison.Ordinal)),
            "runtime input substituted publicized assembly bytes");
    }

    private static string Changed(string value) =>
        (value[0] == '0' ? "1" : "0") + value[1..];

    private static void Reject(
        IActualDebugUnitTestBatchRunner runner,
        ActualDebugUnitTestBatchInput input,
        ActualDebugUnitTestBatchResult result,
        string message)
    {
        try { runner.Validate(input, result); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"expected {expected}, actual {actual}");
    }

    private static void EqualSequence<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string message)
    {
        if (!expected.SequenceEqual(actual)) throw new InvalidOperationException(message);
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
