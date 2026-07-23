using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ONI_Together.HeadlessTests;

internal static class ActualDebugUnitTestResolverDirectoryContractTests
{
    internal static void ExplicitDirectoryFlowsThroughOnceAndCache()
    {
        using ResolverDirectoryFixture fixture =
            ResolverDirectoryFixture.Create();
        fixture.ValidateExternalReferenceIsIsolated();
        RequireOnceForwarding();
        RequireCacheForwarding();
        CacheUsesExplicitDirectory(fixture);
    }

    private static void RequireOnceForwarding()
    {
        MethodReference[] calls = Calls(
            typeof(ActualDebugUnitTestBatchOnce), "Run");
        True(calls.Any(call =>
                call.DeclaringType.FullName ==
                "ONI_Together.HeadlessTests.ActualDebugUnitTestBatchInput" &&
                call.Name == "get_GameLibsDirectory"),
            "batch-once does not read explicit GameLibsDirectory");
        True(calls.Any(call =>
                call.DeclaringType.FullName ==
                "ONI_Together.HeadlessTests." +
                "ActualDebugUnitTestInstrumentationCacheInput" &&
                call.Name == "set_GameLibsDirectory"),
            "batch-once does not forward GameLibsDirectory to cache input");
    }

    private static void RequireCacheForwarding()
    {
        MethodReference[] calls = Calls(
            typeof(ActualDebugUnitTestInstrumentationCache), "GetOrCreate");
        True(calls.Any(call =>
                call.DeclaringType.FullName ==
                "ONI_Together.HeadlessTests." +
                "ActualDebugUnitTestInstrumentationCacheInput" &&
                call.Name == "get_GameLibsDirectory"),
            "instrumentation cache does not read explicit GameLibsDirectory");
        True(calls.Any(call =>
                call.DeclaringType.FullName ==
                "ONI_Together.HeadlessTests.SyncExecutionIlInstrumenter" &&
                call.Name == "Instrument" &&
                call.Parameters.Count == 3),
            "instrumentation cache does not pass explicit resolver directories");
    }

    private static void CacheUsesExplicitDirectory(
        ResolverDirectoryFixture fixture)
    {
        string? original = Environment.GetEnvironmentVariable("ONI_GAME_LIBS");
        Environment.SetEnvironmentVariable(
            "ONI_GAME_LIBS", fixture.WrongGameLibsDirectory);
        try
        {
            IActualDebugUnitTestInstrumentationCache cache =
                ActualDebugUnitTestInstrumentationCacheLoader.Load();
            ActualDebugUnitTestInstrumentationCacheResult result =
                cache.GetOrCreate(fixture.CacheInput(
                    fixture.CorrectGameLibsDirectory, "correct"));
            Equal(1, result.InstrumentationCount);

            RejectDirectory(cache, fixture, fixture.MissingGameLibsDirectory,
                "missing");
            RejectDirectory(cache, fixture, fixture.WrongGameLibsDirectory,
                "wrong");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ONI_GAME_LIBS", original);
        }
    }

    private static void RejectDirectory(
        IActualDebugUnitTestInstrumentationCache cache,
        ResolverDirectoryFixture fixture,
        string directory,
        string cacheName)
    {
        try
        {
            _ = cache.GetOrCreate(fixture.CacheInput(directory, cacheName));
        }
        catch (Exception error) when (
            error.Message.Contains(
                ResolverDirectoryFixture.DependencyAssemblyName,
                StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException(
            $"resolver directory failure was not explicit: {directory}");
    }

    private static MethodReference[] Calls(Type type, string methodName)
    {
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
            type.Assembly.Location);
        TypeDefinition definition = assembly.MainModule.Types.Single(item =>
            item.FullName == type.FullName);
        return definition.Methods.Single(method => method.Name == methodName)
            .Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or
                Code.Callvirt)
            .Select(instruction => instruction.Operand as MethodReference)
            .Where(call => call is not null)
            .Select(call => call!)
            .ToArray();
    }

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
}
