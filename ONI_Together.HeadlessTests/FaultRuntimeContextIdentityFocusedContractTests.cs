using System.Reflection;
using System.Runtime.Loader;

namespace ONI_Together.HeadlessTests;

internal static class FaultRuntimeContextIdentityFocusedContractTests
{
    private const string TestId =
        "headless:unit:5a10172f9d2fd94229c5a612";
    private const string TestType =
        "ONI_Together.DebugTools.UnitTests." +
        "FaultUnityRuntimeDriverContractTests";
    private const string TestMethod =
        "RuntimeStagesShareSetupTargetContext";

    internal static void Validate()
    {
        string gameLibs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library/Application Support/Steam/steamapps/common/" +
            "OxygenNotIncluded/OxygenNotIncluded.app/Contents/Resources/" +
            "Data/Managed");
        ActualDebugUnitTestBatchInput input =
            ActualDebugUnitTestBatchFixture.Load(gameLibs);
        ActualDebugUnitTestDescriptor descriptor = input.ExpectedTests.Single(
            item => item.TestId == TestId);
        True(descriptor.MethodSymbol.EndsWith(
                TestType + "." + TestMethod + "()",
                StringComparison.Ordinal),
            "focused descriptor identity drift");
        SyncExecutionInstrumentedAssembly instrumented =
            SyncExecutionIlInstrumenter.Instrument(
                input.Catalog, input.Assembly, gameLibs);
        using var resolver = new FocusedResolver(gameLibs);
        LoadRuntimeAssemblies(input);
        Assembly assembly = Assembly.Load(
            instrumented.PeImage, instrumented.PdbImage);
        object result = assembly.GetType(TestType, true)!
            .GetMethod(TestMethod, BindingFlags.Public |
                BindingFlags.Static)!
            .Invoke(null, null)!;
        string state = result.GetType().GetProperty("State")!
            .GetValue(result)!.ToString()!;
        string? message = result.GetType().GetProperty("Message")!
            .GetValue(result) as string;
        if (state != "Passed")
            throw new InvalidOperationException(
                $"{TestId} {state}: {message}");
    }

    private static void LoadRuntimeAssemblies(
        ActualDebugUnitTestBatchInput input)
    {
        foreach (ActualDebugRuntimeAssemblyInput runtime in
                 input.RuntimeAssemblies ?? [])
        {
            AssemblyName expected = new(runtime.AssemblyIdentity);
            if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
                    AssemblyName.ReferenceMatchesDefinition(
                        expected, assembly.GetName())))
                continue;
            using var stream = new MemoryStream(
                runtime.PeImage, writable: false);
            AssemblyLoadContext.Default.LoadFromStream(stream);
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FocusedResolver : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string> paths;

        internal FocusedResolver(string gameLibs)
        {
            string root = ActualDebugUnitTestBatchFixture.RepositoryRoot();
            paths = Index([
                gameLibs,
                Path.Combine(root, "ONI_Together", "bin", "Debug",
                    "netstandard2.1"),
                Path.Combine(root, "Shared", "bin", "Debug",
                    "netstandard2.1")
            ]);
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }

        public void Dispose() =>
            AppDomain.CurrentDomain.AssemblyResolve -= Resolve;

        private Assembly? Resolve(
            object? sender,
            ResolveEventArgs arguments)
        {
            string? name = new AssemblyName(arguments.Name).Name;
            return name is not null && paths.TryGetValue(
                name, out string? path)
                ? Assembly.LoadFrom(path)
                : null;
        }

        private static IReadOnlyDictionary<string, string> Index(
            IEnumerable<string> directories)
        {
            var result = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string path in directories.Where(Directory.Exists)
                         .SelectMany(directory => Directory.EnumerateFiles(
                             directory, "*.dll",
                             SearchOption.TopDirectoryOnly)))
                try
                {
                    string? name = AssemblyName.GetAssemblyName(path).Name;
                    if (name is not null)
                        result.TryAdd(name, path);
                }
                catch (BadImageFormatException)
                {
                }
            return result;
        }
    }
}
