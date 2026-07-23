namespace ONI_Together.HeadlessTests;

internal sealed class ActualDebugUnitTestRuntimeClassifier :
    IActualDebugUnitTestRuntimeClassifier
{
    private const string GameAssembly = "Assembly-CSharp";
    private const string GameFirstpassAssembly = "Assembly-CSharp-firstpass";

    public IReadOnlyList<string> Classify(
        ActualDebugUnitTestRuntimeClassificationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.HeadlessUnsupportedReason is not null)
        {
            if (string.IsNullOrWhiteSpace(input.HeadlessUnsupportedReason))
                throw new FormatException(
                    "headlessUnsupportedReason must be nonempty");
            return [input.HeadlessUnsupportedReason];
        }

        return input.DirectCalls
            .Where(IsDirectNativeTerminal)
            .Select(call => Evidence(input.MethodSymbol, call))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsDirectNativeTerminal(
        ActualDebugUnitTestDirectCall call) =>
        IsUnityAssembly(call.AssemblyName) ||
        (IsGameAssembly(call.AssemblyName) &&
         (call.IsInternalCall || call.IsRuntime || call.IsNative ||
          call.IsPInvoke));

    private static bool IsGameAssembly(string assembly) =>
        assembly == GameAssembly ||
        assembly == GameFirstpassAssembly;

    private static bool IsUnityAssembly(string assembly) =>
        assembly.StartsWith("UnityEngine", StringComparison.Ordinal);

    private static string Evidence(
        string method,
        ActualDebugUnitTestDirectCall call) =>
        $"direct-native|kind={Kind(call)}|assembly={call.AssemblyName}|" +
        $"terminal={call.MethodSymbol}|method={method}";

    private static string Kind(ActualDebugUnitTestDirectCall call)
    {
        if (call.IsPInvoke)
            return "pinvoke";
        if (call.IsInternalCall)
            return "internal-call";
        if (call.IsNative)
            return "native";
        return "runtime";
    }
}
