using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestSkipMetadataContractTests
{
    private const string UnitTestAttribute =
        "ONI_Together.DebugTools.UnitTestAttribute";
    private const string UnitTestResult =
        "ONI_Together.DebugTools.UnitTestResult";

    internal static void Validate()
    {
        ActualDebugUnitTestBatchInput input =
            ActualDebugUnitTestBatchFixture.Load();
        using var stream = new MemoryStream(
            input.Assembly.PeImage, writable: false);
        using AssemblyDefinition assembly =
            AssemblyDefinition.ReadAssembly(stream);
        IReadOnlyDictionary<string, ActualDebugUnitTestDescriptor> descriptors =
            input.ExpectedTests.ToDictionary(
                descriptor => descriptor.MethodSymbol, StringComparer.Ordinal);
        var memo = new Dictionary<string, bool>(StringComparer.Ordinal);
        MethodDefinition[] skipCapable = Types(assembly)
            .SelectMany(type => type.Methods)
            .Where(IsUnitTest)
            .Where(method => ReachesSkip(
                method, assembly.MainModule, memo, new HashSet<string>(
                    StringComparer.Ordinal)))
            .OrderBy(MethodSymbol, StringComparer.Ordinal)
            .ToArray();
        var missing = new List<ActualDebugUnitTestDescriptor>();
        foreach (MethodDefinition method in skipCapable)
        {
            ActualDebugUnitTestDescriptor descriptor =
                descriptors[MethodSymbol(method)];
            Console.WriteLine("SKIP_CAPABLE " +
                $"test={descriptor.TestId} method={descriptor.MethodSymbol} " +
                $"reason={descriptor.HeadlessUnsupportedReason ?? "<missing>"}");
            if (string.IsNullOrWhiteSpace(
                    descriptor.HeadlessUnsupportedReason))
            {
                missing.Add(descriptor);
                Console.WriteLine("SKIP_METADATA_MISSING " +
                    $"test={descriptor.TestId} method={descriptor.MethodSymbol}");
            }
        }
        if (missing.Count != 0)
            throw new InvalidOperationException(
                $"Skip-capable UnitTests missing explicit metadata: " +
                string.Join("; ", missing.Select(item =>
                    $"{item.TestId}|{item.MethodSymbol}")));
        Console.WriteLine(
            $"SKIP_METADATA_PASS count={skipCapable.Length}");
        ActualDebugUnitTestEnvironmentContract
            .ValidateStaticUnsupportedClassification(input);
    }

    private static bool ReachesSkip(
        MethodDefinition method,
        ModuleDefinition module,
        IDictionary<string, bool> memo,
        ISet<string> visiting)
    {
        if (memo.TryGetValue(method.FullName, out bool cached))
            return cached;
        if (!method.HasBody || !visiting.Add(method.FullName))
            return false;
        bool result = method.Body.Instructions
            .Select(instruction => instruction.Operand as MethodReference)
            .Where(reference => reference is not null)
            .Any(reference => IsSkip(reference!) ||
                ResolveLocal(reference!, module) is MethodDefinition target &&
                ReachesSkip(target, module, memo, visiting));
        visiting.Remove(method.FullName);
        memo[method.FullName] = result;
        return result;
    }

    private static bool IsSkip(MethodReference method) =>
        method.DeclaringType.FullName == UnitTestResult &&
        method.Name == "Skip" &&
        method.Parameters.Count == 1 &&
        method.Parameters[0].ParameterType.FullName == "System.String";

    private static MethodDefinition? ResolveLocal(
        MethodReference reference,
        ModuleDefinition module)
    {
        try
        {
            MethodDefinition? method = reference.Resolve();
            return method?.Module == module ? method : null;
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    private static bool IsUnitTest(MethodDefinition method) =>
        method.CustomAttributes.Any(attribute =>
            attribute.AttributeType.FullName == UnitTestAttribute);

    private static string MethodSymbol(MethodReference method)
    {
        string parameters = string.Join(",", method.Parameters.Select(
            parameter => TypeName(parameter.ParameterType.FullName)));
        return $"{TypeName(method.DeclaringType.FullName)}." +
            $"{method.Name}({parameters})";
    }

    private static string TypeName(string name) =>
        name.Replace('/', '+').Replace("::", ".");

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
