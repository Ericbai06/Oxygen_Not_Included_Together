using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ONI_Together.HeadlessTests;

internal static class SyncExecutionHarmonyEntryPdbContractTests
{
    private const string EntryId = "sync:ebb787e2b73d232bcd6206af";
    private const string MethodSymbol =
        "ONI_Together.Patches.DLC.Bionic." +
        "RemoteWorkerDockMakeWorkerPatch.Postfix(RemoteWorkerDock)";
    private const string MethodFullName =
        "System.Void ONI_Together.Patches.DLC.Bionic." +
        "RemoteWorkerDockMakeWorkerPatch::Postfix(RemoteWorkerDock)";

    internal static void Validate()
    {
        OrdinaryCallsiteWithoutSequencePointFailsClosed();
        ActualHarmonyEntryWithoutSequencePointIsInstrumented();
    }

    private static void ActualHarmonyEntryWithoutSequencePointIsInstrumented()
    {
        ActualDebugUnitTestBatchInput input =
            ActualDebugUnitTestBatchFixture.Load();
        Equal(input.DllHash, Digest(input.Assembly.PeImage));
        Equal(input.PdbHash, Digest(input.Assembly.PdbImage));
        SyncEntry entry = input.Catalog.Entries.Single(item =>
            item.Id == EntryId && item.Kind == SyncEntryKind.HarmonyPatch);
        Equal(MethodSymbol, entry.FullyQualifiedSymbol);
        AssertEntryInstructionHasNoVisibleSequencePoint(input.Assembly);

        SyncExecutionInstrumentedAssembly result =
            SyncExecutionIlInstrumenter.Instrument(
                new SyncCatalogScan([entry], []), input.Assembly);

        Equal(input.DllHash, result.DllHash);
        Equal(input.PdbHash, result.PdbHash);
        using LoadedAssembly loaded = Read(result);
        AssemblyDefinition assembly = loaded.Assembly;
        MethodDefinition method = Method(assembly, MethodFullName);
        Instruction[] instructions = method.Body.Instructions.Take(3).ToArray();
        True(instructions.Length == 3 &&
            instructions[0].OpCode == OpCodes.Ldstr &&
            Equals(instructions[0].Operand, EntryId) &&
            instructions[1].OpCode == OpCodes.Ldstr &&
            Equals(instructions[1].Operand, "hit") &&
            instructions[2].OpCode == OpCodes.Call &&
            instructions[2].Operand is MethodReference hit &&
            hit.DeclaringType.FullName ==
            SyncExecutionIlInstrumenter.ObserverTypeName &&
            hit.Name == "Hit",
            "Harmony method entry lacks exact execution probe");
    }

    private static void OrdinaryCallsiteWithoutSequencePointFailsClosed()
    {
        SyncExecutionFixtureAssembly fixture =
            WithoutSequencePoints(SyncExecutionProbeFixtureCatalog.Compile(),
                "System.Void PacketRuntime::Run(System.Boolean,System.Boolean)");
        SyncEntry entry = SyncExecutionProbeFixtureCatalog.Scan().Entries
            .First(item => item.Kind == SyncEntryKind.PacketSend &&
                item.FullyQualifiedSymbol.Contains(
                    "PacketRuntime.Run(", StringComparison.Ordinal));
        try
        {
            _ = SyncExecutionIlInstrumenter.Instrument(
                new SyncCatalogScan([entry], []), fixture);
        }
        catch (FormatException error) when (error.Message ==
            "missing PDB callsite for System.Void " +
            "PacketRuntime::Run(System.Boolean,System.Boolean)")
        {
            return;
        }
        throw new InvalidOperationException(
            "ordinary callsite without PDB coverage did not fail closed");
    }

    private static void AssertEntryInstructionHasNoVisibleSequencePoint(
        SyncExecutionFixtureAssembly fixture)
    {
        using LoadedAssembly loaded = Read(fixture);
        AssemblyDefinition assembly = loaded.Assembly;
        MethodDefinition method = Method(assembly, MethodFullName);
        Instruction first = method.Body.Instructions.First();
        True(!method.DebugInformation.SequencePoints.Any(point =>
                point.Offset <= first.Offset && !point.IsHidden),
            "real Harmony method entry unexpectedly has visible PDB coverage");
    }

    private static SyncExecutionFixtureAssembly WithoutSequencePoints(
        SyncExecutionFixtureAssembly fixture,
        string methodFullName)
    {
        using LoadedAssembly loaded = Read(fixture);
        AssemblyDefinition assembly = loaded.Assembly;
        Method(assembly, methodFullName).DebugInformation.SequencePoints.Clear();
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        assembly.Write(pe, new WriterParameters {
            WriteSymbols = true,
            SymbolWriterProvider = new PortablePdbWriterProvider(),
            SymbolStream = pdb
        });
        return new SyncExecutionFixtureAssembly(pe.ToArray(), pdb.ToArray());
    }

    private static LoadedAssembly Read(
        SyncExecutionFixtureAssembly fixture)
    {
        var pe = new MemoryStream(fixture.PeImage, writable: false);
        var pdb = new MemoryStream(fixture.PdbImage, writable: false);
        AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
            pe, new ReaderParameters {
            InMemory = true,
            ReadSymbols = true,
            SymbolReaderProvider = new PortablePdbReaderProvider(),
            SymbolStream = pdb
        });
        return new LoadedAssembly(pe, pdb, assembly);
    }

    private static LoadedAssembly Read(
        SyncExecutionInstrumentedAssembly fixture) =>
        Read(new SyncExecutionFixtureAssembly(
            fixture.PeImage, fixture.PdbImage));

    private static MethodDefinition Method(
        AssemblyDefinition assembly,
        string fullName) => assembly.MainModule.Types
        .SelectMany(DescendantsAndSelf)
        .SelectMany(type => type.Methods)
        .Single(method => method.FullName == fullName);

    private static IEnumerable<TypeDefinition> DescendantsAndSelf(
        TypeDefinition type)
    {
        yield return type;
        foreach (TypeDefinition nested in
                 type.NestedTypes.SelectMany(DescendantsAndSelf))
            yield return nested;
    }

    private static string Digest(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static void Equal<T>(T expected, T actual)
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
}
