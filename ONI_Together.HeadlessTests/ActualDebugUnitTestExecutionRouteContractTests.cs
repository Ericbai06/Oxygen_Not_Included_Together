using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestExecutionRouteContractTests
{
    internal static void RoutesAndExporterUseExplicitModes()
    {
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
            typeof(ActualDebugUnitTestExecutionRouteContractTests)
                .Assembly.Location);
        MethodDefinition main = Method(assembly,
            "ONI_Together.HeadlessTests.Program", "Main");
        RequireRoute(main, ActualDebugUnitTestExecutionCommands.Preflight,
            "ONI_Together.HeadlessTests.ActualDebugUnitTestPreflight");
        RequireRoute(main, ActualDebugUnitTestExecutionCommands.BatchOnce,
            "ONI_Together.HeadlessTests.ActualDebugUnitTestBatchOnce");
        RequireRoute(main, ActualDebugUnitTestExecutionCommands.BatchMilestone,
            "ONI_Together.HeadlessTests.ActualDebugUnitTestBatchMilestone");

        TypeDefinition exporter = Type(assembly,
            "ONI_Together.HeadlessTests.ActualDebugUnitTestExporter");
        MethodReference[] calls = exporter.Methods.Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction => instruction.OpCode.Code is Code.Call or
                Code.Callvirt)
            .Select(instruction => instruction.Operand as MethodReference)
            .Where(reference => reference is not null)
            .Select(reference => reference!)
            .ToArray();
        True(calls.Any(call =>
                call.DeclaringType.FullName ==
                "ONI_Together.HeadlessTests." +
                "ActualDebugUnitTestBatchMilestoneLoader" &&
                call.Name == "Load"),
            "exporter does not load explicit milestone mode");
        True(calls.Any(call =>
                call.DeclaringType.FullName ==
                "ONI_Together.HeadlessTests." +
                "IActualDebugUnitTestBatchMilestone" &&
                call.Name == "Run"),
            "exporter does not execute explicit milestone mode");
        True(!calls.Any(call =>
                call.DeclaringType.FullName ==
                "ONI_Together.HeadlessTests.IActualDebugUnitTestBatchRunner" &&
                call.Name == "Run"),
            "exporter still invokes the raw batch runner");
    }

    private static void RequireRoute(
        MethodDefinition main,
        string command,
        string implementationType)
    {
        True(main.Body.Instructions.Any(instruction =>
                instruction.OpCode == OpCodes.Ldstr &&
                Equals(instruction.Operand, command)),
            $"Program route is missing: {command}");
        True(main.Body.Instructions.Any(instruction =>
                instruction.Operand is MethodReference call &&
                call.DeclaringType.FullName == implementationType &&
                call.Name == "RunCli"),
            $"Program route does not dispatch {command}");
    }

    private static MethodDefinition Method(
        AssemblyDefinition assembly,
        string typeName,
        string methodName) => Type(assembly, typeName).Methods
        .Single(method => method.Name == methodName);

    private static TypeDefinition Type(
        AssemblyDefinition assembly,
        string fullName) => assembly.MainModule.Types
        .SelectMany(DescendantsAndSelf)
        .Single(type => type.FullName == fullName);

    private static IEnumerable<TypeDefinition> DescendantsAndSelf(
        TypeDefinition type)
    {
        yield return type;
        foreach (TypeDefinition nested in
                 type.NestedTypes.SelectMany(DescendantsAndSelf))
            yield return nested;
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
