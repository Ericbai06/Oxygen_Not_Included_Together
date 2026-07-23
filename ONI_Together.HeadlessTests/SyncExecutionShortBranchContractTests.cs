using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ONI_Together.HeadlessTests;

internal static class SyncExecutionShortBranchContractTests
{
    private const string MethodName =
        "System.Int32 ShortBranchRuntime::Run(System.Boolean)";
    private const int OriginalDisplacement = 120;

    internal static void Validate()
    {
        SyncExecutionFixtureAssembly fixture = CompileFixture();
        AssertOriginalShortBranch(fixture);

        SyncExecutionInstrumentedAssembly instrumented;
        try
        {
            instrumented = SyncExecutionIlInstrumenter.Instrument(
                Catalog(), fixture);
        }
        catch (Exception error) when (
            error is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "short branch overflowed while writing instrumented IL",
                error);
        }

        AssertInstrumentedBranch(new SyncExecutionFixtureAssembly(
            instrumented.PeImage, instrumented.PdbImage));
        InvokeAndRequireObservations(instrumented);
    }

    private static SyncExecutionFixtureAssembly CompileFixture()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            SourceText.From(Source, Encoding.UTF8),
            path: "ShortBranchFixture.cs");
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ONI.ShortBranchFixture",
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
        return ForceShortBranch(new SyncExecutionFixtureAssembly(
            pe.ToArray(), pdb.ToArray()));
    }

    private static SyncExecutionFixtureAssembly ForceShortBranch(
        SyncExecutionFixtureAssembly fixture)
    {
        using LoadedAssembly loaded = Read(fixture);
        MethodDefinition method = Method(loaded.Assembly);
        Instruction branch = ConditionalBranch(method);
        Instruction target = (Instruction)branch.Operand;
        int displacement =
            target.Offset - (branch.Offset + branch.GetSize());
        int padding = OriginalDisplacement - displacement;
        True(padding >= 0,
            "fixture conditional branch already exceeds target displacement");
        ILProcessor il = method.Body.GetILProcessor();
        for (int index = 0; index < padding; index++)
            il.InsertBefore(target, il.Create(OpCodes.Nop));
        branch.OpCode = branch.OpCode.Code switch
        {
            Code.Brtrue => OpCodes.Brtrue_S,
            Code.Brfalse => OpCodes.Brfalse_S,
            Code.Brtrue_S => OpCodes.Brtrue_S,
            Code.Brfalse_S => OpCodes.Brfalse_S,
            _ => throw new InvalidOperationException(
                "fixture lacks a shortenable conditional branch")
        };
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        loaded.Assembly.Write(pe, new WriterParameters
        {
            WriteSymbols = true,
            SymbolWriterProvider = new PortablePdbWriterProvider(),
            SymbolStream = pdb
        });
        return new SyncExecutionFixtureAssembly(pe.ToArray(), pdb.ToArray());
    }

    private static void AssertOriginalShortBranch(
        SyncExecutionFixtureAssembly fixture)
    {
        using LoadedAssembly loaded = Read(fixture);
        MethodDefinition method = Method(loaded.Assembly);
        Instruction branch = ConditionalBranch(method);
        True(branch.OpCode.OperandType == OperandType.ShortInlineBrTarget,
            "fixture conditional branch is not short form");
        Instruction target = (Instruction)branch.Operand;
        int displacement =
            target.Offset - (branch.Offset + branch.GetSize());
        Equal(OriginalDisplacement, displacement);
        Equal(8, SendCallsBetween(method, branch, target));
    }

    private static void AssertInstrumentedBranch(
        SyncExecutionFixtureAssembly fixture)
    {
        using LoadedAssembly loaded = Read(fixture);
        MethodDefinition method = Method(loaded.Assembly);
        Instruction branch = ConditionalBranch(method);
        Instruction target = (Instruction)branch.Operand;
        if (branch.OpCode.OperandType == OperandType.ShortInlineBrTarget)
        {
            int displacement =
                target.Offset - (branch.Offset + branch.GetSize());
            True(displacement is >= sbyte.MinValue and <= sbyte.MaxValue,
                $"short branch displacement overflowed: {displacement}");
        }
        Equal(8, SendCallsBetween(method, branch, target));
    }

    private static Instruction ConditionalBranch(
        MethodDefinition method)
    {
        Instruction? branch = method.Body.Instructions.FirstOrDefault(instruction =>
            instruction.OpCode.FlowControl == FlowControl.Cond_Branch &&
            instruction.Operand is Instruction);
        return branch ?? throw new InvalidOperationException(
            "short conditional branch disappeared after probe insertion");
    }

    private static int SendCallsBetween(
        MethodDefinition method,
        Instruction branch,
        Instruction target) =>
        method.Body.Instructions.Count(instruction =>
            instruction.Offset > branch.Offset &&
            instruction.Offset < target.Offset &&
            instruction.Operand is MethodReference called &&
            called.DeclaringType.Name == "PacketSender" &&
            called.Name.StartsWith("Send", StringComparison.Ordinal));

    private static void InvokeAndRequireObservations(
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
            MethodInfo run = assembly.GetType(
                    "ShortBranchRuntime", true)!
                .GetMethod("Run", BindingFlags.Public |
                    BindingFlags.Static)!;
            Equal(0, run.Invoke(null, [true]));
            Equal(8, run.Invoke(null, [false]));
        }
        catch (TargetInvocationException error) when (
            error.InnerException is InvalidProgramException)
        {
            throw new InvalidOperationException(
                "short branch instrumentation produced InvalidProgramException",
                error.InnerException);
        }
        Equal(8, observations.Count);
    }

    private static SyncCatalogScan Catalog() => new(
        Enumerable.Range(0, 8).Select(index =>
            new SyncEntry(
                $"sync:short-branch-{index}",
                SyncEntryKind.PacketSend,
                "ShortBranchRuntime.Run(bool)",
                $"void PacketSender.Send{index}()",
                $"PacketSender.Send{index}",
                [
                    new SyncBuildVariant(
                        "Debug",
                        "OS_MAC",
                        new HashSet<string>(["DEBUG", "OS_MAC"]))
                ],
                SyncEntryStatus.TestOnly)).ToArray(),
        []);

    private static MethodDefinition Method(AssemblyDefinition assembly) =>
        assembly.MainModule.Types.SelectMany(DescendantsAndSelf)
            .SelectMany(type => type.Methods)
            .Single(method => method.FullName == MethodName);

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

    private const string Source = """
        public static class PacketSender
        {
            public static int Count { get; private set; }
            public static void Send0() => Count++;
            public static void Send1() => Count++;
            public static void Send2() => Count++;
            public static void Send3() => Count++;
            public static void Send4() => Count++;
            public static void Send5() => Count++;
            public static void Send6() => Count++;
            public static void Send7() => Count++;
        }

        public static class ShortBranchRuntime
        {
            public static int Run(bool skip)
            {
                if (!skip)
                {
                    PacketSender.Send0();
                    PacketSender.Send1();
                    PacketSender.Send2();
                    PacketSender.Send3();
                    PacketSender.Send4();
                    PacketSender.Send5();
                    PacketSender.Send6();
                    PacketSender.Send7();
                }
                return PacketSender.Count;
            }
        }
        """;
}
