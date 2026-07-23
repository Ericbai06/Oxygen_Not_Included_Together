using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ONI_Together.HeadlessTests;

internal sealed class ActualDebugUnitTestPreflight :
    IActualDebugUnitTestPreflight
{
    private readonly Func<string, ActualDebugUnitTestPreflightInput> _inputLoader;

    internal ActualDebugUnitTestPreflight()
        : this(path => FromBatch(ActualDebugUnitTestBatchFixture.Load(path)))
    {
    }

    internal ActualDebugUnitTestPreflight(
        Func<string, ActualDebugUnitTestPreflightInput> inputLoader)
    {
        _inputLoader = inputLoader ??
            throw new ArgumentNullException(nameof(inputLoader));
    }

    public ActualDebugUnitTestPreflightResult Analyze(
        ActualDebugUnitTestPreflightInput input)
    {
        Validate(input);
        using var pe = new MemoryStream(input.Assembly.PeImage, writable: false);
        using var pdb = new MemoryStream(input.Assembly.PdbImage, writable: false);
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
            pe, new ReaderParameters
            {
                ReadSymbols = true,
                SymbolReaderProvider = new PortablePdbReaderProvider(),
                SymbolStream = pdb
            });
        MethodDefinition[] methods = Types(assembly.MainModule)
            .SelectMany(type => type.Methods)
            .Where(method => method.HasBody)
            .ToArray();
        var issues = new List<ActualDebugUnitTestPreflightIssue>();
        foreach (SyncEntry entry in input.Catalog.Entries)
        {
            if (entry.Kind == SyncEntryKind.Coroutine)
                AnalyzeCoroutine(entry, methods, issues);
            else if (IsCallsiteEntry(entry))
                AnalyzeCallsite(entry, methods, issues);
        }
        return new ActualDebugUnitTestPreflightResult
        {
            SchemaVersion = 1,
            AnalyzedEntryCount = input.Catalog.Entries.Count,
            DllHash = input.DllHash,
            PdbHash = input.PdbHash,
            InventoryDigest = input.InventoryDigest,
            Issues = issues.OrderBy(issue => issue.EntryId,
                    StringComparer.Ordinal)
                .ThenBy(issue => issue.Kind)
                .ThenBy(issue => issue.Code, StringComparer.Ordinal)
                .ThenBy(issue => issue.Symbol, StringComparer.Ordinal)
                .ThenBy(issue => issue.Message, StringComparer.Ordinal)
                .ToArray()
        };
    }

    public int RunCli(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr)
    {
        try
        {
            if (args.Count != 3 ||
                args[0] != ActualDebugUnitTestExecutionCommands.Preflight ||
                args[1] != "--game-libs" ||
                string.IsNullOrWhiteSpace(args[2]))
                throw new ArgumentException(
                    "usage: actual-unit-preflight --game-libs <path>");
            ActualDebugUnitTestPreflightResult result =
                Analyze(_inputLoader(args[2]));
            stdout.WriteLine(ActualDebugUnitTestPreflightJson.Serialize(result));
            return result.Issues.Count == 0 ? 0 : 1;
        }
        catch (Exception error)
        {
            stderr.WriteLine($"actual unit preflight failed: {error.Message}");
            return 1;
        }
    }

    private static ActualDebugUnitTestPreflightInput FromBatch(
        ActualDebugUnitTestBatchInput batch) => new()
    {
        Catalog = batch.Catalog,
        Assembly = batch.Assembly,
        InventoryDigest = batch.InventoryDigest,
        DllHash = batch.DllHash,
        PdbHash = batch.PdbHash
    };

    private static void AnalyzeCallsite(
        SyncEntry entry,
        IEnumerable<MethodDefinition> methods,
        ICollection<ActualDebugUnitTestPreflightIssue> issues)
    {
        MethodDefinition? owner = methods.FirstOrDefault(method =>
            OwnerMatches(method, entry.FullyQualifiedSymbol));
        if (owner is null)
            return;
        Instruction[] calls = owner.Body.Instructions.Where(instruction =>
                CalledMethod(instruction) is MethodReference called &&
                MatchesCall(entry, called))
            .ToArray();
        if (calls.Any(call => !HasSequencePoint(owner, call)))
            issues.Add(Issue(entry, "missing_pdb_callsite",
                "missing PDB callsite"));
    }

    private static void AnalyzeCoroutine(
        SyncEntry entry,
        IReadOnlyList<MethodDefinition> methods,
        ICollection<ActualDebugUnitTestPreflightIssue> issues)
    {
        int count = methods.Count(method =>
            SourceSignature(method) == entry.ResolvedTargetSignature);
        if (count == 1)
            return;
        issues.Add(count == 0
            ? Issue(entry, "unresolved_coroutine_target",
                "coroutine target resolves to zero methods")
            : Issue(entry, "ambiguous_coroutine_target",
                "coroutine target resolves to multiple methods"));
    }

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

    private static void Validate(ActualDebugUnitTestPreflightInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Catalog.Errors.Count != 0)
            throw new InvalidOperationException("catalog contains scan errors");
        if (Digest(input.Assembly.PeImage) != input.DllHash ||
            Digest(input.Assembly.PdbImage) != input.PdbHash)
            throw new InvalidOperationException("preflight binary hash drift");
        using System.Text.Json.JsonDocument inventory =
            System.Text.Json.JsonDocument.Parse(
            SyncInventoryJson.Serialize(input.Catalog));
        if (inventory.RootElement.GetProperty("digest").GetString() !=
            input.InventoryDigest)
            throw new InvalidOperationException(
                "preflight inventory digest drift");
    }

    private static bool HasSequencePoint(
        MethodDefinition method,
        Instruction instruction) =>
        method.DebugInformation.SequencePoints.Any(point =>
            point.Offset <= instruction.Offset && !point.IsHidden);

    private static MethodReference? CalledMethod(Instruction instruction) =>
        instruction.OpCode.Code is Code.Call or Code.Callvirt
            ? instruction.Operand as MethodReference
            : null;

    private static bool MatchesCall(
        SyncEntry entry,
        MethodReference called)
    {
        if (entry.Bootstrap.Contains("+=", StringComparison.Ordinal))
            return called.Name.StartsWith("add_", StringComparison.Ordinal) ||
                called.Name == "Combine";
        if (entry.Bootstrap.Contains("-=", StringComparison.Ordinal))
            return called.Name.StartsWith("remove_", StringComparison.Ordinal) ||
                called.Name == "Remove";
        return entry.Bootstrap.Contains(called.Name, StringComparison.Ordinal);
    }

    private static bool OwnerMatches(
        MethodDefinition method,
        string symbol) => SyncExecutionOwnerSymbolMatcher.Matches(
            symbol, method.DeclaringType.Name, method.Name);

    private static bool IsCallsiteEntry(SyncEntry entry) =>
        entry.Kind != SyncEntryKind.HarmonyPatch &&
        !(entry.Kind == SyncEntryKind.PacketDispatch &&
          entry.Bootstrap.Contains(".OnDispatched(", StringComparison.Ordinal)) &&
        entry.FullyQualifiedSymbol.Contains('(');

    private static string SourceSignature(MethodDefinition method) =>
        $"{TypeName(method.ReturnType)} {TypeName(method.DeclaringType)}." +
        $"{method.Name}({string.Join(", ", method.Parameters.Select(parameter =>
            TypeName(parameter.ParameterType)))})";

    private static string TypeName(TypeReference type)
    {
        string? alias = type.FullName switch
        {
            "System.Int64" => "long",
            "System.Boolean" => "bool",
            "System.Single" => "float",
            _ => null
        };
        if (alias is not null)
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

    private static IEnumerable<TypeDefinition> Types(ModuleDefinition module) =>
        module.Types.SelectMany(DescendantsAndSelf);

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
}
