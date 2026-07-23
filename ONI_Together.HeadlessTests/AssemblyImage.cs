using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ONI_Together.HeadlessTests;

internal sealed class AssemblyImage : IDisposable
{
    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue = BuildOpCodes();
    private readonly FileStream stream;
    private readonly PEReader pe;
    private readonly MetadataReader metadata;
    private readonly IReadOnlyDictionary<MethodDefinitionHandle, string> methodOwners;

    internal AssemblyImage(string path)
    {
        stream = File.OpenRead(path);
        pe = new PEReader(stream);
        metadata = pe.GetMetadataReader();
        methodOwners = BuildMethodOwners();
    }

    internal IEnumerable<string> TypeNames => metadata.TypeDefinitions.Select(TypeName);

    internal IEnumerable<string> UserStrings => metadata.MethodDefinitions
        .SelectMany(UserStringsIn);

    internal IEnumerable<MethodCall> Calls => metadata.MethodDefinitions.SelectMany(CallsIn);

    internal MethodDefinitionHandle FindMethod(string owner, string name)
    {
        MethodDefinitionHandle[] matches = metadata.MethodDefinitions
            .Where(handle => methodOwners[handle] == owner && MethodName(handle) == name).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Expected one {owner}.{name}, found {matches.Length}");
        return matches[0];
    }

    internal IEnumerable<string> UserStringsInTypes(params string[] owners)
        => metadata.MethodDefinitions
            .Where(handle => owners.Contains(methodOwners[handle], StringComparer.Ordinal))
            .SelectMany(UserStringsIn);

    internal IEnumerable<string> UserStringsIn(MethodDefinitionHandle method)
        => Instructions(method)
            .Where(instruction => instruction.OpCode == OpCodes.Ldstr)
            .Select(instruction => metadata.GetUserString(
                MetadataTokens.UserStringHandle(instruction.Token & 0x00ffffff)));

    internal int CountCalls(MethodDefinitionHandle method, string owner, string name)
        => CallsIn(method).Count(call => call.Owner == owner && call.Name == name);

    public void Dispose()
    {
        pe.Dispose();
        stream.Dispose();
    }

    private IEnumerable<MethodCall> CallsIn(MethodDefinitionHandle method)
        => Instructions(method)
            .Where(instruction => instruction.OpCode.OperandType == OperandType.InlineMethod)
            .Select(instruction => ResolveCall(instruction.Token));

    private MethodCall ResolveCall(int token)
    {
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        return handle.Kind switch
        {
            HandleKind.MethodDefinition => DefinitionCall((MethodDefinitionHandle)handle),
            HandleKind.MemberReference => ReferenceCall((MemberReferenceHandle)handle),
            HandleKind.MethodSpecification => ResolveCall(MetadataTokens.GetToken(
                metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method)),
            _ => new MethodCall("<unknown>", handle.Kind.ToString())
        };
    }

    private MethodCall DefinitionCall(MethodDefinitionHandle handle)
        => new(methodOwners[handle], MethodName(handle));

    private MethodCall ReferenceCall(MemberReferenceHandle handle)
    {
        MemberReference reference = metadata.GetMemberReference(handle);
        return new MethodCall(ParentTypeName(reference.Parent), metadata.GetString(reference.Name));
    }

    private string ParentTypeName(EntityHandle handle)
        => handle.Kind switch
        {
            HandleKind.TypeDefinition => TypeName((TypeDefinitionHandle)handle),
            HandleKind.TypeReference => TypeName((TypeReferenceHandle)handle),
            _ => handle.Kind.ToString()
        };

    private IEnumerable<IlInstruction> Instructions(MethodDefinitionHandle handle)
    {
        MethodDefinition method = metadata.GetMethodDefinition(handle);
        if (method.RelativeVirtualAddress == 0) yield break;
        BlobReader reader = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
        while (reader.RemainingBytes > 0)
        {
            OpCode code = ReadOpCode(ref reader);
            int token = ReadOperand(ref reader, code.OperandType);
            yield return new IlInstruction(code, token);
        }
    }

    private IReadOnlyDictionary<MethodDefinitionHandle, string> BuildMethodOwners()
    {
        var owners = new Dictionary<MethodDefinitionHandle, string>();
        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
            foreach (MethodDefinitionHandle methodHandle in
                     metadata.GetTypeDefinition(typeHandle).GetMethods())
                owners.Add(methodHandle, TypeName(typeHandle));
        return owners;
    }

    private string MethodName(MethodDefinitionHandle handle)
        => metadata.GetString(metadata.GetMethodDefinition(handle).Name);

    private string TypeName(TypeDefinitionHandle handle)
    {
        TypeDefinition type = metadata.GetTypeDefinition(handle);
        return QualifiedName(metadata.GetString(type.Namespace), metadata.GetString(type.Name));
    }

    private string TypeName(TypeReferenceHandle handle)
    {
        TypeReference type = metadata.GetTypeReference(handle);
        return QualifiedName(metadata.GetString(type.Namespace), metadata.GetString(type.Name));
    }

    private static string QualifiedName(string nameSpace, string name)
        => string.IsNullOrEmpty(nameSpace) ? name : nameSpace + "." + name;

    private static OpCode ReadOpCode(ref BlobReader reader)
    {
        byte first = reader.ReadByte();
        ushort value = first == 0xfe ? (ushort)(0xfe00 | reader.ReadByte()) : first;
        return OpCodesByValue[value];
    }

    private static int ReadOperand(ref BlobReader reader, OperandType type)
    {
        if (type == OperandType.InlineSwitch)
        {
            int branchCount = reader.ReadInt32();
            reader.Offset += branchCount * 4;
            return 0;
        }
        int token = 0;
        int size = type switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or
                OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            _ => 4
        };
        if (size == 4) token = reader.ReadInt32();
        else if (size > 0) reader.Offset += size;
        return token;
    }

    private static IReadOnlyDictionary<ushort, OpCode> BuildOpCodes()
        => typeof(OpCodes).GetFields()
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(code => unchecked((ushort)code.Value));
}

internal readonly record struct MethodCall(string Owner, string Name);
internal readonly record struct IlInstruction(OpCode OpCode, int Token);
