using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ONI_Together.HeadlessTests;

internal static partial class SyncExecutionIlInstrumenter
{
    public const string ObserverTypeName = "__SyncExecutionProbe";

    public static SyncExecutionInstrumentedAssembly Instrument(
        SyncCatalogScan catalog,
        SyncExecutionFixtureAssembly fixture,
        string? gameLibsDirectory = null)
    {
        if (fixture.PdbImage.Length == 0)
            throw new FormatException("execution probe requires portable PDB identity");
        try
        {
            using var pe = new MemoryStream(fixture.PeImage, writable: false);
            using var pdb = new MemoryStream(fixture.PdbImage, writable: false);
            using DefaultAssemblyResolver resolver =
                CreateAssemblyResolver(gameLibsDirectory, fixture);
            var parameters = new ReaderParameters
            {
                AssemblyResolver = resolver,
                InMemory = true,
                ReadSymbols = true,
                SymbolReaderProvider = new PortablePdbReaderProvider(),
                SymbolStream = pdb,
            };
            using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(pe, parameters);
            ValidateAssemblyReferences(assembly, resolver);
            AddAccessBypass(assembly);
            MethodReference hit = AddObserver(assembly.MainModule);
            InstrumentEntries(assembly.MainModule, catalog, hit);
            return Write(assembly, Digest(fixture.PeImage), Digest(fixture.PdbImage));
        }
        catch (SymbolsNotMatchingException error)
        {
            throw new FormatException("portable PDB does not match fixture PE", error);
        }
        catch (BadImageFormatException error)
        {
            throw new FormatException("invalid execution fixture image", error);
        }
    }

    private static void AddAccessBypass(AssemblyDefinition assembly)
    {
        ModuleDefinition module = assembly.MainModule;
        const string attributeNamespace = "System.Runtime.CompilerServices";
        const string attributeName = "IgnoresAccessChecksToAttribute";
        if (module.Types.Any(type =>
                type.Namespace == attributeNamespace &&
                type.Name == attributeName))
            throw new FormatException(
                "fixture already defines IgnoresAccessChecksToAttribute");
        var type = new TypeDefinition(
            attributeNamespace, attributeName,
            TypeAttributes.Class | TypeAttributes.Sealed |
            TypeAttributes.NotPublic,
            module.ImportReference(typeof(Attribute)));
        AddAttributeUsage(module, type);
        MethodDefinition constructor = AddAccessBypassConstructor(module, type);
        module.Types.Add(type);
        foreach (string target in new[]
                 { "Assembly-CSharp", "Assembly-CSharp-firstpass" })
        {
            var attribute = new CustomAttribute(constructor);
            attribute.ConstructorArguments.Add(new CustomAttributeArgument(
                module.TypeSystem.String, target));
            assembly.CustomAttributes.Add(attribute);
        }
    }

    private static void AddAttributeUsage(
        ModuleDefinition module,
        TypeDefinition type)
    {
        var usage = new CustomAttribute(module.ImportReference(
            typeof(AttributeUsageAttribute).GetConstructor(
                new[] { typeof(AttributeTargets) })!));
        usage.ConstructorArguments.Add(new CustomAttributeArgument(
            module.ImportReference(typeof(AttributeTargets)),
            (int)AttributeTargets.Assembly));
        usage.Properties.Add(new CustomAttributeNamedArgument(
            nameof(AttributeUsageAttribute.AllowMultiple),
            new CustomAttributeArgument(module.TypeSystem.Boolean, true)));
        type.CustomAttributes.Add(usage);
    }

    private static MethodDefinition AddAccessBypassConstructor(
        ModuleDefinition module,
        TypeDefinition type)
    {
        var constructor = new MethodDefinition(
            ".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig |
            MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        constructor.Parameters.Add(new ParameterDefinition(
            "assemblyName", ParameterAttributes.None, module.TypeSystem.String));
        MethodReference baseConstructor = module.ImportReference(
            typeof(Attribute).GetConstructor(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic,
                binder: null, Type.EmptyTypes, modifiers: null)!);
        ILProcessor il = constructor.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, baseConstructor));
        il.Append(il.Create(OpCodes.Ret));
        type.Methods.Add(constructor);
        return constructor;
    }

    private static MethodReference AddObserver(ModuleDefinition module)
    {
        TypeReference actionType = module.ImportReference(typeof(Action<string, string>));
        var type = new TypeDefinition(
            "", ObserverTypeName,
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
            module.TypeSystem.Object);
        var field = new FieldDefinition("Observer",
            FieldAttributes.Public | FieldAttributes.Static, actionType);
        type.Fields.Add(field);
        var hit = new MethodDefinition("Hit",
            MethodAttributes.Public | MethodAttributes.Static,
            module.TypeSystem.Void);
        hit.Parameters.Add(new ParameterDefinition("entryId",
            ParameterAttributes.None, module.TypeSystem.String));
        hit.Parameters.Add(new ParameterDefinition("phase",
            ParameterAttributes.None, module.TypeSystem.String));
        type.Methods.Add(hit);
        module.Types.Add(type);
        EmitObserverBody(module, hit, field);
        return hit;
    }

    private static void EmitObserverBody(
        ModuleDefinition module,
        MethodDefinition hit,
        FieldReference observer)
    {
        MethodReference invoke = module.ImportReference(
            typeof(Action<string, string>).GetMethod("Invoke")!);
        ILProcessor il = hit.Body.GetILProcessor();
        Instruction done = il.Create(OpCodes.Ret);
        il.Append(il.Create(OpCodes.Ldsfld, observer));
        il.Append(il.Create(OpCodes.Brfalse_S, done));
        il.Append(il.Create(OpCodes.Ldsfld, observer));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldarg_1));
        il.Append(il.Create(OpCodes.Callvirt, invoke));
        il.Append(done);
    }

    private static void InstrumentEntries(
        ModuleDefinition module,
        SyncCatalogScan catalog,
        MethodReference hit)
    {
        MethodDefinition[] methods = AllTypes(module)
            .SelectMany(type => type.Methods)
            .Where(method => method.HasBody)
            .ToArray();
        foreach (IGrouping<string, SyncEntry> group in catalog.Entries
                     .Where(IsCallsiteEntry)
                     .GroupBy(OwnerKey, StringComparer.Ordinal))
            InstrumentCallsites(methods, group.ToArray(), hit);
        foreach (SyncEntry entry in catalog.Entries.Where(item =>
                     item.Kind == SyncEntryKind.HarmonyPatch))
            InstrumentHarmony(methods, entry, hit);
        foreach (SyncEntry entry in catalog.Entries.Where(item =>
                     item.Kind == SyncEntryKind.Coroutine))
            InstrumentCoroutineTerminal(module, entry, hit);
    }

    private static void InstrumentCallsites(
        IEnumerable<MethodDefinition> methods,
        IReadOnlyList<SyncEntry> entries,
        MethodReference hit)
    {
        MethodDefinition? method = methods.FirstOrDefault(candidate =>
            OwnerMatches(candidate, entries[0].FullyQualifiedSymbol));
        if (method is null)
            return;
        Instruction[] calls = method.Body.Instructions.Where(instruction =>
                CalledMethod(instruction) is not null)
            .ToArray();
        foreach (IGrouping<string, SyncEntry> group in entries.GroupBy(
                     EntryShape, StringComparer.Ordinal))
        {
            SyncEntry template = group.First();
            Instruction[] matches = calls.Where(call =>
                    MatchesCall(template, CalledMethod(call)!))
                .OrderBy(call => call.Offset)
                .ToArray();
            if (group.Count() == 1 && matches.Length == 1)
            {
                InsertHit(method, matches[0], hit, template.Id, Phase(template));
                continue;
            }
            for (int index = 0; index < matches.Length; index++)
            {
                string id = StableCallsiteId(template, index + 1);
                if (group.Any(entry => entry.Id == id))
                    InsertHit(method, matches[index], hit, id, Phase(template));
            }
        }
    }

    private static void InstrumentHarmony(
        IEnumerable<MethodDefinition> methods,
        SyncEntry entry,
        MethodReference hit)
    {
        MethodDefinition? method = methods.FirstOrDefault(candidate =>
            OwnerMatches(candidate, entry.FullyQualifiedSymbol));
        if (method?.Body.Instructions.FirstOrDefault() is Instruction first)
            InsertGeneratedHit(method, first, hit, entry.Id, "hit");
    }

    private static void InstrumentCoroutineTerminal(
        ModuleDefinition module,
        SyncEntry entry,
        MethodReference hit)
    {
        TypeDefinition? iterator = FindIterator(module, entry);
        MethodDefinition? moveNext = iterator?.Methods.FirstOrDefault(method =>
            method.Name == "MoveNext" && method.HasBody);
        if (moveNext is not null)
        {
            foreach (Instruction ret in moveNext.Body.Instructions
                         .Where(item => item.OpCode == OpCodes.Ret).ToArray())
                InsertConditionalTerminal(moveNext, ret, hit, entry.Id);
        }
        MethodDefinition? dispose = iterator?.Methods.FirstOrDefault(method =>
            method.Name.EndsWith("Dispose", StringComparison.Ordinal) && method.HasBody);
        if (dispose is not null)
        {
            foreach (Instruction ret in dispose.Body.Instructions
                         .Where(item => item.OpCode == OpCodes.Ret).ToArray())
                InsertGeneratedHit(dispose, ret, hit, entry.Id, "cancel");
        }
    }

    private static TypeDefinition FindIterator(
        ModuleDefinition module,
        SyncEntry entry)
    {
        MethodDefinition[] sources = AllTypes(module)
            .SelectMany(type => type.Methods)
            .Where(method => SourceSignature(method) ==
                entry.ResolvedTargetSignature).ToArray();
        CustomAttribute[] bindings = sources.Length == 1
            ? sources[0].CustomAttributes.Where(attribute =>
                attribute.AttributeType.FullName ==
                "System.Runtime.CompilerServices.IteratorStateMachineAttribute")
                .ToArray()
            : [];
        TypeReference? iterator = bindings.Length == 1 &&
            bindings[0].ConstructorArguments.Count == 1
            ? bindings[0].ConstructorArguments[0].Value as TypeReference
            : null;
        TypeDefinition[] matches = iterator is null ? [] : AllTypes(module)
            .Where(type => type.FullName == iterator.FullName).ToArray();
        return matches.Length == 1 ? matches[0] : throw new FormatException(
            $"cannot resolve coroutine target {entry.ResolvedTargetSignature}");
    }

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
            _ => null,
        };
        if (alias is not null) return alias;
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

    private static void InsertGeneratedHit(
        MethodDefinition method,
        Instruction before,
        MethodReference hit,
        string entryId,
        string phase)
    {
        ILProcessor il = method.Body.GetILProcessor();
        il.InsertBefore(before, il.Create(OpCodes.Ldstr, entryId));
        il.InsertBefore(before, il.Create(OpCodes.Ldstr, phase));
        il.InsertBefore(before, il.Create(OpCodes.Call, hit));
    }

    private static void InsertConditionalTerminal(
        MethodDefinition method,
        Instruction ret,
        MethodReference hit,
        string entryId)
    {
        ILProcessor il = method.Body.GetILProcessor();
        il.InsertBefore(ret, il.Create(OpCodes.Dup));
        il.InsertBefore(ret, il.Create(OpCodes.Brtrue_S, ret));
        il.InsertBefore(ret, il.Create(OpCodes.Ldstr, entryId));
        il.InsertBefore(ret, il.Create(OpCodes.Ldstr, "complete"));
        il.InsertBefore(ret, il.Create(OpCodes.Call, hit));
    }

    private static void InsertHit(
        MethodDefinition method,
        Instruction before,
        MethodReference hit,
        string entryId,
        string phase)
    {
        RequireSequencePoint(method, before);
        ILProcessor il = method.Body.GetILProcessor();
        Instruction insertionTarget = PrefixChainStart(before);
        Instruction first = il.Create(OpCodes.Ldstr, entryId);
        for (Instruction target = before;; target = target.Previous!)
        {
            RedirectTargets(method, target, first);
            if (ReferenceEquals(target, insertionTarget))
                break;
        }
        il.InsertBefore(insertionTarget, first);
        il.InsertBefore(insertionTarget, il.Create(OpCodes.Ldstr, phase));
        il.InsertBefore(insertionTarget, il.Create(OpCodes.Call, hit));
    }

    private static Instruction PrefixChainStart(Instruction call)
    {
        Instruction first = call;
        while (first.Previous?.OpCode.OpCodeType == OpCodeType.Prefix)
            first = first.Previous;
        return first;
    }

    private static void RedirectTargets(
        MethodDefinition method,
        Instruction oldTarget,
        Instruction newTarget)
    {
        foreach (Instruction instruction in method.Body.Instructions)
        {
            if (ReferenceEquals(instruction.Operand, oldTarget))
                instruction.Operand = newTarget;
            if (instruction.Operand is Instruction[] targets)
            {
                for (int index = 0; index < targets.Length; index++)
                    if (ReferenceEquals(targets[index], oldTarget))
                        targets[index] = newTarget;
            }
        }
        foreach (ExceptionHandler handler in method.Body.ExceptionHandlers)
        {
            if (ReferenceEquals(handler.TryStart, oldTarget))
                handler.TryStart = newTarget;
            if (ReferenceEquals(handler.HandlerStart, oldTarget))
                handler.HandlerStart = newTarget;
            if (ReferenceEquals(handler.FilterStart, oldTarget))
                handler.FilterStart = newTarget;
        }
    }

    private static void RequireSequencePoint(
        MethodDefinition method,
        Instruction instruction)
    {
        bool covered = method.DebugInformation.SequencePoints.Any(point =>
            point.Offset <= instruction.Offset && !point.IsHidden);
        if (!covered)
            throw new FormatException($"missing PDB callsite for {method.FullName}");
    }

    private static MethodReference? CalledMethod(Instruction instruction)
    {
        return instruction.OpCode.Code is Code.Call or Code.Callvirt
            ? instruction.Operand as MethodReference
            : null;
    }

    private static bool MatchesCall(SyncEntry entry, MethodReference called)
    {
        if (entry.Bootstrap.Contains("+=", StringComparison.Ordinal))
            return called.Name.StartsWith("add_", StringComparison.Ordinal) ||
                called.Name == "Combine";
        if (entry.Bootstrap.Contains("-=", StringComparison.Ordinal))
            return called.Name.StartsWith("remove_", StringComparison.Ordinal) ||
                called.Name == "Remove";
        return entry.Bootstrap.Contains(called.Name, StringComparison.Ordinal);
    }

    private static bool OwnerMatches(MethodDefinition method, string symbol)
        => SyncExecutionOwnerSymbolMatcher.Matches(
            symbol, method.DeclaringType.Name, method.Name);

    private static bool IsCallsiteEntry(SyncEntry entry)
    {
        return entry.Kind != SyncEntryKind.HarmonyPatch &&
            !IsDispatchImplementation(entry) &&
            entry.FullyQualifiedSymbol.Contains('(');
    }

    private static bool IsDispatchImplementation(SyncEntry entry)
    {
        return entry.Kind == SyncEntryKind.PacketDispatch &&
            entry.Bootstrap.Contains(".OnDispatched(", StringComparison.Ordinal);
    }

    private static string OwnerKey(SyncEntry entry) => entry.FullyQualifiedSymbol;

    private static string EntryShape(SyncEntry entry)
    {
        return string.Join("\n", entry.Kind, entry.FullyQualifiedSymbol,
            entry.ResolvedTargetSignature, entry.Bootstrap, entry.Status);
    }

    private static string StableCallsiteId(SyncEntry entry, int occurrence)
    {
        string identity = string.Join("\n", entry.Kind, entry.FullyQualifiedSymbol,
            entry.ResolvedTargetSignature,
            $"{entry.Bootstrap}#call:{occurrence:D4}", entry.Status);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"sync:{Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private static string Phase(SyncEntry entry)
    {
        return entry.Kind == SyncEntryKind.Coroutine ? "start" : "hit";
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        return module.Types.SelectMany(DescendantsAndSelf);
    }

    private static IEnumerable<TypeDefinition> DescendantsAndSelf(TypeDefinition type)
    {
        yield return type;
        foreach (TypeDefinition nested in type.NestedTypes.SelectMany(DescendantsAndSelf))
            yield return nested;
    }

    private static string Digest(byte[] value)
    {
        return Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    }

    private static SyncExecutionInstrumentedAssembly Write(
        AssemblyDefinition assembly,
        string dllHash,
        string pdbHash)
    {
        ExpandShortBranches(assembly);
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        assembly.Write(pe, new WriterParameters
        {
            WriteSymbols = true,
            SymbolWriterProvider = new PortablePdbWriterProvider(),
            SymbolStream = pdb,
        });
        return new SyncExecutionInstrumentedAssembly(
            pe.ToArray(), pdb.ToArray(), dllHash, pdbHash);
    }
}
