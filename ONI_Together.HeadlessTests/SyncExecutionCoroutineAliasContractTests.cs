using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ONI_Together.HeadlessTests;

internal static class SyncExecutionCoroutineAliasContractTests
{
    private static readonly IReadOnlyDictionary<string, string> SourceAliases =
        new Dictionary<string, string>(StringComparer.Ordinal) {
            ["System.Int64"] = "long",
            ["System.Boolean"] = "bool",
            ["System.Single"] = "float"
        };

    internal static void Validate()
    {
        ActualDebugUnitTestBatchInput input =
            ActualDebugUnitTestBatchFixture.Load();
        Equal(input.DllHash, Digest(input.Assembly.PeImage));
        Equal(input.PdbHash, Digest(input.Assembly.PdbImage));
        SyncEntry[] entries = input.Catalog.Entries
            .Where(entry => entry.Kind == SyncEntryKind.Coroutine)
            .OrderBy(entry => entry.Id, StringComparer.Ordinal)
            .ToArray();
        True(entries.Length > 0, "actual Debug catalog has no coroutine entries");

        using LoadedAssembly source = new(input.Assembly);
        IReadOnlyDictionary<string, string> iteratorByEntry =
            BindAll(source.Assembly, entries);
        RequireAlias(source.Assembly, entries,
            "sync:a74a7c40ad4d8e7f8dfdfb8d", "long", "System.Int64");
        RequireAlias(source.Assembly, entries,
            "sync:3d266802faf278ecdf7c2785", "bool", "System.Boolean");
        RequireAlias(source.Assembly, entries,
            "sync:51f20b0f74033d8db6617c65", "float", "System.Single");

        SyncEntry longEntry = entries.Single(entry =>
            entry.Id == "sync:a74a7c40ad4d8e7f8dfdfb8d");
        SyncExecutionInstrumentedAssembly longOnly =
            SyncExecutionIlInstrumenter.Instrument(
                new SyncCatalogScan([longEntry], []), input.Assembly);
        AssertProbe(longOnly, iteratorByEntry[longEntry.Id], longEntry.Id);

        SyncExecutionInstrumentedAssembly all =
            SyncExecutionIlInstrumenter.Instrument(
                new SyncCatalogScan(entries, []), input.Assembly);
        foreach (SyncEntry entry in entries)
            AssertProbe(all, iteratorByEntry[entry.Id], entry.Id);
    }

    private static IReadOnlyDictionary<string, string> BindAll(
        AssemblyDefinition assembly,
        IEnumerable<SyncEntry> entries)
    {
        MethodDefinition[] methods = Types(assembly)
            .SelectMany(type => type.Methods).ToArray();
        return entries.ToDictionary(entry => entry.Id, entry =>
        {
            MethodDefinition source = methods.Single(method =>
                SourceSignature(method) == entry.ResolvedTargetSignature);
            CustomAttribute binding = source.CustomAttributes.Single(attribute =>
                attribute.AttributeType.FullName ==
                "System.Runtime.CompilerServices.IteratorStateMachineAttribute");
            TypeReference iterator = binding.ConstructorArguments.Count == 1 &&
                binding.ConstructorArguments[0].Value is TypeReference value
                    ? value
                    : throw new InvalidOperationException(
                        $"coroutine binding lacks iterator type: {entry.Id}");
            True(Types(assembly).Count(type =>
                    type.FullName == iterator.FullName) == 1,
                $"coroutine iterator is not exact: {entry.Id}");
            return iterator.FullName;
        }, StringComparer.Ordinal);
    }

    private static void RequireAlias(
        AssemblyDefinition assembly,
        IReadOnlyList<SyncEntry> entries,
        string id,
        string alias,
        string cecilType)
    {
        SyncEntry entry = entries.Single(item => item.Id == id);
        True(entry.ResolvedTargetSignature.Contains(
                $"({alias}", StringComparison.Ordinal),
            $"{id} lost source alias {alias}");
        MethodDefinition source = Types(assembly)
            .SelectMany(type => type.Methods)
            .Single(method => SourceSignature(method) ==
                entry.ResolvedTargetSignature);
        True(source.Parameters.Any(parameter =>
                parameter.ParameterType.FullName == cecilType),
            $"{alias} did not normalize from {cecilType}");
    }

    private static string SourceSignature(MethodDefinition method) =>
        $"{TypeName(method.ReturnType)} {TypeName(method.DeclaringType)}." +
        $"{method.Name}({string.Join(", ", method.Parameters.Select(parameter =>
            TypeName(parameter.ParameterType)))})";

    private static string TypeName(TypeReference type)
    {
        if (SourceAliases.TryGetValue(type.FullName, out string? alias))
            return alias;
        if (type is GenericInstanceType instance)
            return $"{TrimArity(instance.ElementType.FullName)}<" +
                $"{string.Join(", ", instance.GenericArguments.Select(TypeName))}>";
        string name = type.FullName.Replace('/', '.');
        return type.HasGenericParameters
            ? $"{TrimArity(name)}<{string.Join(", ",
                type.GenericParameters.Select(parameter => parameter.Name))}>"
            : name;
    }

    private static string TrimArity(string name)
    {
        int tick = name.LastIndexOf('`');
        return (tick < 0 ? name : name[..tick]).Replace('/', '.');
    }

    private static void AssertProbe(
        SyncExecutionInstrumentedAssembly result,
        string iteratorName,
        string entryId)
    {
        using LoadedAssembly loaded = new(result);
        TypeDefinition iterator = Types(loaded.Assembly).Single(type =>
            type.FullName == iteratorName);
        True(ProbeCount(iterator, entryId, "complete") > 0,
            $"{entryId} lacks exact MoveNext complete probe");
        True(ProbeCount(iterator, entryId, "cancel") > 0,
            $"{entryId} lacks exact Dispose cancel probe");
    }

    private static int ProbeCount(
        TypeDefinition iterator,
        string id,
        string phase) => iterator.Methods
        .Where(method => method.Name == "MoveNext" ||
            method.Name.EndsWith("Dispose", StringComparison.Ordinal))
        .SelectMany(method => method.Body.Instructions)
        .Count(instruction => instruction.OpCode == OpCodes.Ldstr &&
            Equals(instruction.Operand, id) &&
            instruction.Next?.OpCode == OpCodes.Ldstr &&
            Equals(instruction.Next.Operand, phase) &&
            instruction.Next.Next?.OpCode == OpCodes.Call);

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

        internal LoadedAssembly(SyncExecutionFixtureAssembly fixture)
            : this(fixture.PeImage, fixture.PdbImage) { }

        internal LoadedAssembly(SyncExecutionInstrumentedAssembly fixture)
            : this(fixture.PeImage, fixture.PdbImage) { }

        private LoadedAssembly(byte[] peImage, byte[] pdbImage)
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
