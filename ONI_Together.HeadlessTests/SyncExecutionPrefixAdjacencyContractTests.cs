using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ONI_Together.HeadlessTests;

internal static class SyncExecutionPrefixAdjacencyContractTests
{
    private const string EntryId = "sync:prefix-adjacency-contract";

    internal static void Validate()
    {
        WriteOptionalRealAssemblyDiagnostic();
        SyncExecutionFixtureAssembly fixture = CompileFixture();
        AssertOriginalPrefixIsAdjacent(fixture);
        SyncExecutionInstrumentedAssembly instrumented =
            SyncExecutionIlInstrumenter.Instrument(Catalog(), fixture);
        AssertProbePrecedesPrefix(instrumented);
        InvokeAndRequireObservation(instrumented);
    }

    private static SyncExecutionFixtureAssembly CompileFixture()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            SourceText.From(Source, Encoding.UTF8),
            path: "PrefixAdjacencyFixture.cs");
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ONI.PrefixAdjacencyFixture",
            [tree],
            SyncSurfaceScanner.PlatformReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release));
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        EmitResult result = compilation.Emit(pe, pdb, options: new EmitOptions(
            debugInformationFormat: DebugInformationFormat.PortablePdb));
        if (!result.Success)
            throw new InvalidOperationException(string.Join("; ",
                result.Diagnostics.Where(item =>
                    item.Severity == DiagnosticSeverity.Error)));
        return new SyncExecutionFixtureAssembly(
            pe.ToArray(), pdb.ToArray());
    }

    private static void AssertOriginalPrefixIsAdjacent(
        SyncExecutionFixtureAssembly fixture)
    {
        using LoadedAssembly loaded = Read(fixture);
        Instruction call = TargetCall(InvokeMethod(loaded.Assembly));
        True(call.Previous?.OpCode == OpCodes.Constrained,
            "fixture constrained. prefix is not adjacent to callvirt");
    }

    private static void AssertProbePrecedesPrefix(
        SyncExecutionInstrumentedAssembly instrumented)
    {
        using LoadedAssembly loaded = Read(new SyncExecutionFixtureAssembly(
            instrumented.PeImage, instrumented.PdbImage));
        Instruction call = TargetCall(InvokeMethod(loaded.Assembly));
        Instruction prefix = call.Previous ??
            throw new InvalidOperationException(
                "instrumented callvirt lost constrained. prefix");
        True(prefix.OpCode.OpCodeType == OpCodeType.Prefix,
            "execution probe split constrained. from callvirt");
        Instruction chainStart = prefix;
        while (chainStart.Previous?.OpCode.OpCodeType == OpCodeType.Prefix)
            chainStart = chainStart.Previous;
        Instruction probeCall = InvokeMethod(loaded.Assembly).Body.Instructions
            .SingleOrDefault(instruction =>
                instruction.Offset < chainStart.Offset &&
                IsObserverCall(instruction)) ??
            throw new InvalidOperationException(
                "execution probe was not inserted before prefix chain");
        True(probeCall.Previous?.OpCode == OpCodes.Ldstr &&
                Equals(probeCall.Previous.Operand, "hit") &&
                probeCall.Previous.Previous?.OpCode == OpCodes.Ldstr &&
                Equals(probeCall.Previous.Previous.Operand, EntryId),
            "execution probe prefix witness is incomplete");
    }

    private static void InvokeAndRequireObservation(
        SyncExecutionInstrumentedAssembly instrumented)
    {
        Assembly assembly = Assembly.Load(
            instrumented.PeImage, instrumented.PdbImage);
        var observations = new List<(string Id, string Phase)>();
        Type observer = assembly.GetType(
            SyncExecutionIlInstrumenter.ObserverTypeName, true)!;
        observer.GetField("Observer", BindingFlags.Public |
                BindingFlags.Static)!
            .SetValue(null, new Action<string, string>((id, phase) =>
                observations.Add((id, phase))));
        try
        {
            object? result = assembly.GetType("PrefixRuntime", true)!
                .GetMethod("Run", BindingFlags.Public |
                    BindingFlags.Static)!
                .Invoke(null, null);
            Equal(7, result);
        }
        catch (TargetInvocationException error) when (
            error.InnerException is InvalidProgramException)
        {
            throw new InvalidOperationException(
                "prefix instrumentation produced InvalidProgramException",
                error.InnerException);
        }
        True(observations.SequenceEqual([(EntryId, "hit")]),
            "prefixed callsite did not emit exactly one observer hit");
    }

    private static SyncCatalogScan Catalog() => new(
        [
            new SyncEntry(
                EntryId,
                SyncEntryKind.PacketSend,
                "PrefixRuntime.Invoke<T>(T)",
                "int IProbe.GetValue()",
                "IProbe.GetValue",
                [
                    new SyncBuildVariant(
                        "Debug",
                        "OS_MAC",
                        new HashSet<string>(["DEBUG", "OS_MAC"]))
                ],
                SyncEntryStatus.TestOnly)
        ],
        []);

    private static MethodDefinition InvokeMethod(
        AssemblyDefinition assembly) =>
        assembly.MainModule.Types.SelectMany(DescendantsAndSelf)
            .Where(type => type.Name == "PrefixRuntime")
            .SelectMany(type => type.Methods)
            .Single(method => method.Name == "Invoke" &&
                method.HasGenericParameters);

    private static Instruction TargetCall(MethodDefinition method) =>
        method.Body.Instructions.Single(instruction =>
            instruction.OpCode == OpCodes.Callvirt &&
            instruction.Operand is MethodReference called &&
            called.Name == "GetValue");

    private static IEnumerable<TypeDefinition> DescendantsAndSelf(
        TypeDefinition type)
    {
        yield return type;
        foreach (TypeDefinition nested in
                 type.NestedTypes.SelectMany(DescendantsAndSelf))
            yield return nested;
    }

    private static LoadedAssembly Read(
        SyncExecutionFixtureAssembly fixture)
    {
        var pe = new MemoryStream(fixture.PeImage, writable: false);
        var pdb = new MemoryStream(fixture.PdbImage, writable: false);
        AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
            pe,
            new ReaderParameters
            {
                InMemory = true,
                ReadSymbols = true,
                SymbolReaderProvider = new PortablePdbReaderProvider(),
                SymbolStream = pdb
            });
        return new LoadedAssembly(pe, pdb, assembly);
    }

    private static void WriteOptionalRealAssemblyDiagnostic()
    {
        string? source = Environment.GetEnvironmentVariable(
            "ONI_REBUILT_SOURCE_DLL");
        string? instrumented = Environment.GetEnvironmentVariable(
            "ONI_CACHED_INSTRUMENTED_DLL");
        if (!File.Exists(source) || !File.Exists(instrumented))
            return;
        using AssemblyDefinition before =
            AssemblyDefinition.ReadAssembly(source);
        using AssemblyDefinition after =
            AssemblyDefinition.ReadAssembly(instrumented);
        foreach (string identity in DiagnosticMethods)
            WriteSeparatedPrefixes(before, after, identity);
    }

    private static void WriteSeparatedPrefixes(
        AssemblyDefinition source,
        AssemblyDefinition instrumented,
        string identity)
    {
        MethodDefinition? before = FindMethod(source, identity);
        MethodDefinition? after = FindMethod(instrumented, identity);
        if (before is null || after is null)
            return;
        foreach (Instruction probe in after.Body.Instructions.Where(
                     IsObserverCall))
        {
            Instruction? prefix =
                probe.Previous?.Previous?.Previous;
            Instruction? target = probe.Next;
            if (prefix?.OpCode.OpCodeType != OpCodeType.Prefix ||
                target is null)
                continue;
            Console.WriteLine(
                $"PREFIX_DIAGNOSTIC method={identity} " +
                $"prefix={prefix.OpCode.Name} target={target.OpCode.Name}");
        }
    }

    private static bool IsObserverCall(Instruction instruction) =>
        instruction.OpCode == OpCodes.Call &&
        instruction.Operand is MethodReference called &&
        called.DeclaringType.FullName ==
        SyncExecutionIlInstrumenter.ObserverTypeName &&
        called.Name == "Hit";

    private static MethodDefinition? FindMethod(
        AssemblyDefinition assembly,
        string identity)
    {
        int separator = identity.LastIndexOf('.');
        string typeName = identity[..separator];
        string methodName = identity[(separator + 1)..];
        return assembly.MainModule.Types.SelectMany(DescendantsAndSelf)
            .Where(type => type.FullName.EndsWith(
                typeName, StringComparison.Ordinal))
            .SelectMany(type => type.Methods)
            .FirstOrDefault(method => method.Name == methodName &&
                method.HasBody);
    }

    private static void Equal<T>(T expected, object? actual)
    {
        if (!Equals(expected, actual))
            throw new InvalidOperationException(
                $"expected {expected}, actual {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class LoadedAssembly(
        MemoryStream pe,
        MemoryStream pdb,
        AssemblyDefinition assembly) : IDisposable
    {
        internal AssemblyDefinition Assembly { get; } = assembly;

        public void Dispose()
        {
            Assembly.Dispose();
            pdb.Dispose();
            pe.Dispose();
        }
    }

    private static readonly string[] DiagnosticMethods =
    [
        "PacketSender.SendToConnectionLocked",
        "PacketSender.SendToAll",
        "PacketHandler.ProcessIncoming",
        "WorldUpdateRepairObservability.TrySendAck"
    ];

    private const string Source = """
        public interface IProbe
        {
            int GetValue();
        }

        public readonly struct ProbeValue : IProbe
        {
            public int GetValue() => 7;
        }

        public static class PrefixRuntime
        {
            public static int Run() => Invoke(new ProbeValue());

            public static int Invoke<T>(T value) where T : IProbe
                => value.GetValue();
        }
        """;
}
