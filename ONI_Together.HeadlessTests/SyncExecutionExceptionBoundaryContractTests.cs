using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ONI_Together.HeadlessTests;

internal static class SyncExecutionExceptionBoundaryContractTests
{
    private const string EntryId = "sync:eh-boundary-contract";
    private const string MethodName = "System.Int32 BoundaryRuntime::Run()";

    internal static void Validate()
    {
        SyncExecutionFixtureAssembly fixture = CompileFixture();
        AssertOriginalTryStartsAtTargetCall(fixture);
        SyncExecutionInstrumentedAssembly instrumented =
            SyncExecutionIlInstrumenter.Instrument(Catalog(), fixture);
        InvokeAndRequireObservation(instrumented);
        AssertInstrumentedBoundaries(instrumented);
    }

    private static SyncExecutionFixtureAssembly CompileFixture()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            SourceText.From(Source, Encoding.UTF8),
            path: "EhBoundaryFixture.cs");
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ONI.EhBoundaryFixture",
            [tree],
            SyncSurfaceScanner.PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        EmitResult result = compilation.Emit(pe, pdb, options: new EmitOptions(
            debugInformationFormat: DebugInformationFormat.PortablePdb));
        if (!result.Success)
            throw new InvalidOperationException(string.Join("; ",
                result.Diagnostics.Where(item =>
                    item.Severity == DiagnosticSeverity.Error)));
        return MoveTryStartToTargetCall(
            new SyncExecutionFixtureAssembly(pe.ToArray(), pdb.ToArray()));
    }

    private static SyncExecutionFixtureAssembly MoveTryStartToTargetCall(
        SyncExecutionFixtureAssembly fixture)
    {
        using LoadedAssembly loaded = Read(fixture);
        MethodDefinition method = Method(loaded.Assembly);
        ExceptionHandler handler = method.Body.ExceptionHandlers.Single();
        handler.TryStart = TargetCall(method);
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

    private static void AssertOriginalTryStartsAtTargetCall(
        SyncExecutionFixtureAssembly fixture)
    {
        using LoadedAssembly loaded = Read(fixture);
        MethodDefinition method = Method(loaded.Assembly);
        ExceptionHandler handler = method.Body.ExceptionHandlers.Single();
        AssertLegalBoundaries(method, handler);
        True(ReferenceEquals(handler.TryStart, TargetCall(method)),
            "fixture target call is not the exact TryStart boundary");
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
            object? result = assembly.GetType("BoundaryRuntime", true)!
                .GetMethod("Run", BindingFlags.Public |
                    BindingFlags.Static)!
                .Invoke(null, null);
            True(Equals(result, 1), "instrumented boundary method changed result");
        }
        catch (TargetInvocationException error) when (
            error.InnerException is InvalidProgramException)
        {
            throw new InvalidOperationException(
                "EH-boundary instrumentation produced InvalidProgramException",
                error.InnerException);
        }
        True(observations.SequenceEqual([(EntryId, "hit")]),
            "EH-boundary target did not emit exactly one observer hit");
    }

    private static void AssertInstrumentedBoundaries(
        SyncExecutionInstrumentedAssembly instrumented)
    {
        using LoadedAssembly loaded = Read(new SyncExecutionFixtureAssembly(
            instrumented.PeImage, instrumented.PdbImage));
        MethodDefinition method = Method(loaded.Assembly);
        ExceptionHandler handler = method.Body.ExceptionHandlers.Single();
        AssertLegalBoundaries(method, handler);
        Instruction call = TargetCall(method);
        Instruction probe = call.Previous?.Previous?.Previous ??
            throw new InvalidOperationException(
                "execution probe was not inserted before EH target");
        True(probe.OpCode == OpCodes.Ldstr &&
                Equals(probe.Operand, EntryId),
            "inserted EH-boundary probe has wrong entry identity");
        True(ReferenceEquals(handler.TryStart, probe),
            "TryStart did not expand to inserted execution probe");
    }

    private static void AssertLegalBoundaries(
        MethodDefinition method,
        ExceptionHandler handler)
    {
        IReadOnlySet<Instruction> instructions =
            method.Body.Instructions.ToHashSet();
        Instruction?[] boundaries =
        [
            handler.TryStart,
            handler.TryEnd,
            handler.HandlerStart,
            handler.HandlerEnd,
            handler.FilterStart
        ];
        True(boundaries.Where(item => item is not null)
                .All(item => instructions.Contains(item!)),
            "exception handler contains a dangling instruction boundary");
        True(handler.TryStart is not null &&
                handler.TryEnd is not null &&
                handler.HandlerStart is not null &&
                handler.HandlerEnd is not null,
            "fixture lost a required try/catch boundary");
        True(handler.FilterStart is null,
            "catch-only fixture unexpectedly gained a filter boundary");
    }

    private static SyncCatalogScan Catalog() => new(
        [
            new SyncEntry(
                EntryId,
                SyncEntryKind.PacketSend,
                "BoundaryRuntime.Run()",
                "void PacketSender.Send()",
                "PacketSender.Send",
                [
                    new SyncBuildVariant(
                        "Debug",
                        "OS_MAC",
                        new HashSet<string>(["DEBUG", "OS_MAC"]))
                ],
                SyncEntryStatus.TestOnly)
        ],
        []);

    private static Instruction TargetCall(MethodDefinition method) =>
        method.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code is Code.Call or Code.Callvirt &&
            instruction.Operand is MethodReference target &&
            target.DeclaringType.Name == "PacketSender" &&
            target.Name == "Send");

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
        using System;

        public static class PacketSender
        {
            public static int SendCount { get; private set; }
            public static void Send() => SendCount++;
        }

        public static class BoundaryRuntime
        {
            public static int Run()
            {
                try
                {
                    PacketSender.Send();
                    return 1;
                }
                catch (InvalidOperationException)
                {
                    return -1;
                }
            }
        }
        """;
}
