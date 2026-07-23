using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ONI_Together.HeadlessTests;

internal static class SyncExecutionCoroutineOverloadContractTests
{
    private const string Owner =
        "ONI_Together.Patches.DLC.SpacedOut.ArtifactGameplaySync";
    private const string SpawnPacket =
        "ONI_Together.Networking.Packets.DLC.SpacedOut.ArtifactSpawnStatePacket";
    private const string PoiPacket =
        "ONI_Together.Networking.Packets.DLC.SpacedOut.ArtifactPoiOneTimeStatePacket";
    private const string SpawnId = "sync:50f044b144ea6f1c9faa02f4";
    private const string PoiId = "sync:65a302f1b0ec0f90346122f4";

    internal static void Validate()
    {
        ActualDebugUnitTestBatchInput input =
            ActualDebugUnitTestBatchFixture.Load();
        Equal(input.DllHash, Digest(input.Assembly.PeImage));
        Equal(input.PdbHash, Digest(input.Assembly.PdbImage));
        SyncEntry spawn = Entry(input, SpawnId, SpawnPacket);
        SyncEntry poi = Entry(input, PoiId, PoiPacket);
        using LoadedAssembly source = Read(input.Assembly);
        TypeDefinition spawnIterator =
            IteratorFor(source.Assembly, SpawnPacket);
        TypeDefinition poiIterator = IteratorFor(source.Assembly, PoiPacket);
        True(spawnIterator.FullName != poiIterator.FullName,
            "Retry overloads share one generated iterator type");

        SyncExecutionInstrumentedAssembly poiOnly = Instrument(input, poi);
        AssertOnly(poiOnly, poiIterator.FullName, PoiId,
            spawnIterator.FullName, SpawnId);
        SyncExecutionInstrumentedAssembly spawnOnly = Instrument(input, spawn);
        AssertOnly(spawnOnly, spawnIterator.FullName, SpawnId,
            poiIterator.FullName, PoiId);
        SyncExecutionInstrumentedAssembly both =
            SyncExecutionIlInstrumenter.Instrument(
                new SyncCatalogScan([spawn, poi], []), input.Assembly);
        AssertBound(both, spawnIterator.FullName, SpawnId, PoiId);
        AssertBound(both, poiIterator.FullName, PoiId, SpawnId);
        InvalidSignatureFailsClosed(input, poi);
    }

    private static SyncEntry Entry(
        ActualDebugUnitTestBatchInput input,
        string id,
        string parameter)
    {
        string signature =
            $"System.Collections.IEnumerator {Owner}.Retry({parameter})";
        SyncEntry entry = input.Catalog.Entries.Single(item =>
            item.Id == id && item.Kind == SyncEntryKind.Coroutine);
        Equal(signature, entry.ResolvedTargetSignature);
        return entry;
    }

    private static TypeDefinition IteratorFor(
        AssemblyDefinition assembly,
        string parameter)
    {
        MethodDefinition source = Types(assembly)
            .SelectMany(type => type.Methods)
            .Single(method => method.DeclaringType.FullName == Owner &&
                method.Name == "Retry" &&
                method.ReturnType.FullName == "System.Collections.IEnumerator" &&
                method.Parameters.Count == 1 &&
                method.Parameters[0].ParameterType.FullName == parameter);
        CustomAttribute binding = source.CustomAttributes.Single(attribute =>
            attribute.AttributeType.FullName ==
            "System.Runtime.CompilerServices.IteratorStateMachineAttribute");
        TypeReference iterator = binding.ConstructorArguments.Count == 1 &&
            binding.ConstructorArguments[0].Value is TypeReference value
                ? value
                : throw new InvalidOperationException(
                    $"Retry({parameter}) lacks exact iterator binding");
        return Types(assembly).Single(type =>
            type.FullName == iterator.FullName);
    }

    private static SyncExecutionInstrumentedAssembly Instrument(
        ActualDebugUnitTestBatchInput input,
        SyncEntry entry)
    {
        SyncExecutionInstrumentedAssembly result =
            SyncExecutionIlInstrumenter.Instrument(
                new SyncCatalogScan([entry], []), input.Assembly);
        Equal(input.DllHash, result.DllHash);
        Equal(input.PdbHash, result.PdbHash);
        return result;
    }

    private static void AssertOnly(
        SyncExecutionInstrumentedAssembly result,
        string expectedIterator,
        string expectedId,
        string otherIterator,
        string otherId)
    {
        AssertBound(result, expectedIterator, expectedId, otherId);
        using LoadedAssembly assembly = Read(result);
        TypeDefinition other = Types(assembly.Assembly).Single(type =>
            type.FullName == otherIterator);
        Equal(0, ProbeCount(other, expectedId, "complete"));
        Equal(0, ProbeCount(other, expectedId, "cancel"));
    }

    private static void AssertBound(
        SyncExecutionInstrumentedAssembly result,
        string iteratorName,
        string expectedId,
        string rejectedId)
    {
        using LoadedAssembly assembly = Read(result);
        TypeDefinition iterator = Types(assembly.Assembly).Single(type =>
            type.FullName == iteratorName);
        True(ProbeCount(iterator, expectedId, "complete") > 0,
            $"{iteratorName} lacks exact complete probe");
        True(ProbeCount(iterator, expectedId, "cancel") > 0,
            $"{iteratorName} lacks exact cancel probe");
        Equal(0, ProbeCount(iterator, rejectedId, "complete"));
        Equal(0, ProbeCount(iterator, rejectedId, "cancel"));
    }

    private static int ProbeCount(
        TypeDefinition iterator,
        string id,
        string phase) => iterator.Methods
        .Where(method => method.Name == "MoveNext" ||
            method.Name.EndsWith("Dispose", StringComparison.Ordinal))
        .SelectMany(method => method.Body.Instructions)
        .Where(instruction => instruction.OpCode == OpCodes.Ldstr &&
            Equals(instruction.Operand, id) &&
            instruction.Next?.OpCode == OpCodes.Ldstr &&
            Equals(instruction.Next.Operand, phase) &&
            instruction.Next.Next?.OpCode == OpCodes.Call)
        .Count();

    private static void InvalidSignatureFailsClosed(
        ActualDebugUnitTestBatchInput input,
        SyncEntry basis)
    {
        SyncEntry invalid = basis with {
            ResolvedTargetSignature =
                $"System.Collections.IEnumerator {Owner}.Retry(Missing.Packet)"
        };
        try
        {
            _ = SyncExecutionIlInstrumenter.Instrument(
                new SyncCatalogScan([invalid], []), input.Assembly);
        }
        catch (FormatException error) when (error.Message ==
            $"cannot resolve coroutine target {invalid.ResolvedTargetSignature}")
        {
            return;
        }
        throw new InvalidOperationException(
            "invalid coroutine source signature did not fail closed");
    }

    private static IEnumerable<TypeDefinition> Types(
        AssemblyDefinition assembly) =>
        assembly.MainModule.Types.SelectMany(DescendantsAndSelf);

    private static IEnumerable<TypeDefinition> DescendantsAndSelf(
        TypeDefinition type)
    {
        yield return type;
        foreach (TypeDefinition nested in
                 type.NestedTypes.SelectMany(DescendantsAndSelf))
            yield return nested;
    }

    private static LoadedAssembly Read(
        SyncExecutionFixtureAssembly fixture) =>
        new(fixture.PeImage, fixture.PdbImage);

    private static LoadedAssembly Read(
        SyncExecutionInstrumentedAssembly fixture) =>
        new(fixture.PeImage, fixture.PdbImage);

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

    private sealed class LoadedAssembly : IDisposable
    {
        private readonly MemoryStream pe;
        private readonly MemoryStream pdb;
        internal AssemblyDefinition Assembly { get; }

        internal LoadedAssembly(byte[] peImage, byte[] pdbImage)
        {
            pe = new MemoryStream(peImage, writable: false);
            pdb = new MemoryStream(pdbImage, writable: false);
            Assembly = AssemblyDefinition.ReadAssembly(pe, new ReaderParameters {
                InMemory = true,
                ReadSymbols = true,
                SymbolReaderProvider = new PortablePdbReaderProvider(),
                SymbolStream = pdb
            });
        }

        public void Dispose()
        {
            Assembly.Dispose();
            pdb.Dispose();
            pe.Dispose();
        }
    }
}
