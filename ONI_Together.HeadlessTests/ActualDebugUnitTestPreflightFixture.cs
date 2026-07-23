using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ONI_Together.HeadlessTests;

internal sealed record PreflightFixture(
    ActualDebugUnitTestPreflightInput Input,
    IReadOnlyList<ActualDebugUnitTestPreflightIssue> ExpectedIssues);

internal static class ActualDebugUnitTestPreflightFixture
{
    private const string UnresolvedId = "sync:fffffffffffffffffffffff1";

    internal static PreflightFixture CreateInvalid()
    {
        SyncCatalogScan sourceCatalog = SyncExecutionProbeFixtureCatalog.Scan();
        SyncEntry packet = sourceCatalog.Entries.First(entry =>
            entry.Kind == SyncEntryKind.PacketSend &&
            entry.FullyQualifiedSymbol.Contains(
                "PacketRuntime.Run(", StringComparison.Ordinal));
        SyncEntry coroutine = sourceCatalog.Entries.Single(entry =>
            entry.Kind == SyncEntryKind.Coroutine);
        SyncEntry unresolved = coroutine with
        {
            Id = UnresolvedId,
            ResolvedTargetSignature =
                "System.Collections.IEnumerator " +
                "CoroutineRuntime.MissingCoroutine()"
        };
        var catalog = new SyncCatalogScan(
            [packet, coroutine, unresolved], []);
        SyncExecutionFixtureAssembly assembly =
            CreateInvalidAssembly(SyncExecutionProbeFixtureCatalog.Compile());
        string inventoryDigest = InventoryDigest(catalog);
        var input = new ActualDebugUnitTestPreflightInput
        {
            Catalog = catalog,
            Assembly = assembly,
            InventoryDigest = inventoryDigest,
            DllHash = Digest(assembly.PeImage),
            PdbHash = Digest(assembly.PdbImage)
        };
        ActualDebugUnitTestPreflightIssue[] issues =
        [
            Issue(packet, "missing_pdb_callsite",
                "missing PDB callsite"),
            Issue(coroutine, "ambiguous_coroutine_target",
                "coroutine target resolves to multiple methods"),
            Issue(unresolved, "unresolved_coroutine_target",
                "coroutine target resolves to zero methods")
        ];
        return new PreflightFixture(input, issues.OrderBy(issue => issue.EntryId,
                StringComparer.Ordinal)
            .ThenBy(issue => issue.Kind)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal)
            .ThenBy(issue => issue.Symbol, StringComparer.Ordinal)
            .ThenBy(issue => issue.Message, StringComparer.Ordinal)
            .ToArray());
    }

    internal static ActualDebugUnitTestPreflightInput CreateValid()
    {
        SyncCatalogScan catalog = SyncExecutionProbeFixtureCatalog.Scan();
        SyncExecutionFixtureAssembly assembly =
            SyncExecutionProbeFixtureCatalog.Compile();
        return new ActualDebugUnitTestPreflightInput
        {
            Catalog = catalog,
            Assembly = assembly,
            InventoryDigest = InventoryDigest(catalog),
            DllHash = Digest(assembly.PeImage),
            PdbHash = Digest(assembly.PdbImage)
        };
    }

    internal static string InventoryDigest(SyncCatalogScan catalog)
    {
        using JsonDocument json = JsonDocument.Parse(
            SyncInventoryJson.Serialize(catalog));
        return json.RootElement.GetProperty("digest").GetString()!;
    }

    internal static string Digest(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static ActualDebugUnitTestPreflightIssue Issue(
        SyncEntry entry,
        string code,
        string message) => new()
    {
        EntryId = entry.Id,
        Kind = entry.Kind,
        Code = code,
        Symbol = entry.FullyQualifiedSymbol,
        Message = message
    };

    private static SyncExecutionFixtureAssembly CreateInvalidAssembly(
        SyncExecutionFixtureAssembly fixture)
    {
        using var pe = new MemoryStream(fixture.PeImage, writable: false);
        using var pdb = new MemoryStream(fixture.PdbImage, writable: false);
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
            pe, new ReaderParameters
            {
                ReadSymbols = true,
                SymbolReaderProvider = new PortablePdbReaderProvider(),
                SymbolStream = pdb
            });
        MethodDefinition packet = Types(assembly).SelectMany(type => type.Methods)
            .Single(method => method.FullName ==
                "System.Void PacketRuntime::Run(System.Boolean,System.Boolean)");
        packet.DebugInformation.SequencePoints.Clear();
        TypeDefinition owner = Types(assembly).Single(type =>
            type.FullName == "CoroutineRuntime");
        MethodDefinition source = owner.Methods.Single(method =>
            method.Name == "WaitForCompletion");
        var duplicate = new MethodDefinition(
            source.Name, source.Attributes, source.ReturnType);
        duplicate.Body = new MethodBody(duplicate);
        duplicate.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        duplicate.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        owner.Methods.Add(duplicate);
        using var outputPe = new MemoryStream();
        using var outputPdb = new MemoryStream();
        assembly.Write(outputPe, new WriterParameters
        {
            WriteSymbols = true,
            SymbolWriterProvider = new PortablePdbWriterProvider(),
            SymbolStream = outputPdb
        });
        return new SyncExecutionFixtureAssembly(
            outputPe.ToArray(), outputPdb.ToArray());
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
}
