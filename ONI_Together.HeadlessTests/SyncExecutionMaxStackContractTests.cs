using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ONI_Together.HeadlessTests;

internal static class SyncExecutionMaxStackContractTests
{
    private const int OriginalMaxStack = 2;
    private const string EntryId = "sync:maxstack-contract";
    private const string MethodName =
        "System.Int32 StackBoundaryRuntime::Run()";

    internal static void Validate()
    {
        WriteOptionalRealAssemblyDiagnostic();
        SyncExecutionFixtureAssembly fixture = CompileFixture();
        Equal(OriginalMaxStack, ReadMaxStack(fixture));

        SyncExecutionInstrumentedAssembly instrumented =
            SyncExecutionIlInstrumenter.Instrument(Catalog(), fixture);
        int instrumentedMaxStack = ReadMaxStack(
            new SyncExecutionFixtureAssembly(
                instrumented.PeImage, instrumented.PdbImage));
        True(instrumentedMaxStack >= OriginalMaxStack + 2,
            $"probe maxstack remained {instrumentedMaxStack}; expected at least " +
            $"{OriginalMaxStack + 2}");
        InvokeAndRequireObservation(instrumented);
    }

    private static SyncExecutionFixtureAssembly CompileFixture()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            SourceText.From(Source, Encoding.UTF8),
            path: "MaxStackFixture.cs");
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ONI.MaxStackFixture",
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
        return ForceMaxStack(new SyncExecutionFixtureAssembly(
            pe.ToArray(), pdb.ToArray()));
    }

    private static SyncExecutionFixtureAssembly ForceMaxStack(
        SyncExecutionFixtureAssembly fixture)
    {
        using LoadedAssembly loaded = Read(fixture);
        Method(loaded.Assembly).Body.MaxStackSize = OriginalMaxStack;
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

    private static int ReadMaxStack(SyncExecutionFixtureAssembly fixture)
    {
        using LoadedAssembly loaded = Read(fixture);
        return Method(loaded.Assembly).Body.MaxStackSize;
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
            object? result = assembly.GetType("StackBoundaryRuntime", true)!
                .GetMethod("Run", BindingFlags.Public |
                    BindingFlags.Static)!
                .Invoke(null, null);
            Equal(30, result);
        }
        catch (TargetInvocationException error) when (
            error.InnerException is InvalidProgramException)
        {
            throw new InvalidOperationException(
                "maxstack instrumentation produced InvalidProgramException",
                error.InnerException);
        }
        True(observations.SequenceEqual([(EntryId, "hit")]),
            "maxstack callsite did not emit exactly one observer hit");
    }

    private static SyncCatalogScan Catalog() => new(
        [
            new SyncEntry(
                EntryId,
                SyncEntryKind.PacketSend,
                "StackBoundaryRuntime.Run()",
                "void PacketSender.Send(int, int)",
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

    private static void WriteOptionalRealAssemblyDiagnostic()
    {
        string? source = Environment.GetEnvironmentVariable(
            "ONI_REBUILT_SOURCE_DLL");
        string? instrumented = Environment.GetEnvironmentVariable(
            "ONI_CACHED_INSTRUMENTED_DLL");
        if (!File.Exists(source) || !File.Exists(instrumented))
            return;
        using AssemblyDefinition sourceAssembly =
            AssemblyDefinition.ReadAssembly(source);
        using AssemblyDefinition instrumentedAssembly =
            AssemblyDefinition.ReadAssembly(instrumented);
        foreach (string identity in DiagnosticMethods)
            WriteDiagnostic(sourceAssembly, instrumentedAssembly, identity);
    }

    private static void WriteDiagnostic(
        AssemblyDefinition source,
        AssemblyDefinition instrumented,
        string identity)
    {
        MethodDefinition? before = FindMethod(source, identity);
        MethodDefinition? after = FindMethod(instrumented, identity);
        if (before is null || after is null)
            return;
        Console.WriteLine(
            $"MAXSTACK_DIAGNOSTIC method={identity} " +
            $"source={before.Body.MaxStackSize} " +
            $"instrumented={after.Body.MaxStackSize}");
    }

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

    private static void Equal<T>(T expected, T? actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
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
        public static class PacketSender
        {
            public static int Sum { get; private set; }
            public static void Send(int left, int right) => Sum = left + right;
        }

        public static class StackBoundaryRuntime
        {
            public static int Run()
            {
                PacketSender.Send(10, 20);
                return PacketSender.Sum;
            }
        }
        """;
}
