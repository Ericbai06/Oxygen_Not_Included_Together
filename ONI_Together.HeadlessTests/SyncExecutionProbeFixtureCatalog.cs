using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace ONI_Together.HeadlessTests;

internal static class SyncExecutionProbeFixtureCatalog
{
    private static readonly SyncBuildVariant Variant = new(
        "Debug", "OS_MAC", new HashSet<string>(["DEBUG", "OS_MAC"]));

    public static SyncCatalogScan Scan()
    {
        SyncCatalogScan scan = SyncCatalogSourceScanner.Scan(
            new Dictionary<string, string> { ["ExecutionProbeFixture.cs"] = Source },
            [Variant]);
        if (scan.Errors.Count > 0)
            throw new InvalidOperationException(string.Join("; ", scan.Errors));
        return scan;
    }

    public static SyncExecutionFixtureAssembly Compile()
    {
        return CompileSource(Source);
    }

    public static SyncExecutionFixtureAssembly CompileWithMismatchedPdb()
    {
        SyncExecutionFixtureAssembly original = CompileSource(Source);
        SyncExecutionFixtureAssembly shifted = CompileSource("\n" + Source);
        return new SyncExecutionFixtureAssembly(original.PeImage, shifted.PdbImage);
    }

    public static SyncExecutionFixtureAssembly CompileWithoutPdb()
    {
        SyncExecutionFixtureAssembly original = CompileSource(Source);
        return new SyncExecutionFixtureAssembly(original.PeImage, []);
    }

    private static SyncExecutionFixtureAssembly CompileSource(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            SourceText.From(source, Encoding.UTF8), path: "ExecutionProbeFixture.cs");
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ONI.ExecutionProbeFixture",
            [tree],
            SyncSurfaceScanner.PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        EmitResult result = compilation.Emit(pe, pdb, options: new EmitOptions(
            debugInformationFormat: DebugInformationFormat.PortablePdb));
        Diagnostic[] errors = result.Diagnostics.Where(item =>
            item.Severity == DiagnosticSeverity.Error).ToArray();
        if (!result.Success)
            throw new InvalidOperationException(string.Join("; ", errors.AsEnumerable()));
        return new SyncExecutionFixtureAssembly(pe.ToArray(), pdb.ToArray());
    }

    internal const string Source = """
        using System;
        using System.Collections;
        using System.IO;

        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
        public sealed class HarmonyPatchAttribute : Attribute
        {
            public HarmonyPatchAttribute(Type type, string method) { }
        }
        [AttributeUsage(AttributeTargets.Method)]
        public sealed class HarmonyPostfixAttribute : Attribute { }

        public interface IPacket
        {
            void Serialize(BinaryWriter writer);
            void Deserialize(BinaryReader reader);
            void OnDispatched();
        }
        public sealed class ProbePacket : IPacket
        {
            public static bool Deserialized { get; private set; }
            public static bool Dispatched { get; private set; }
            public void Serialize(BinaryWriter writer) { }
            public void Deserialize(BinaryReader reader) => Deserialized = true;
            public void OnDispatched() => Dispatched = true;
        }
        public static class PacketRegistrationHelper
        {
            public static void AutoRegisterPackets() { }
        }
        public static class PacketRegistry
        {
            public static bool Registered { get; private set; }
            public static void RegisterDefaults()
            {
                PacketRegistrationHelper.AutoRegisterPackets();
            }
            public static void Register<T>() where T : IPacket, new()
            {
                Registered = true;
            }
        }
        public static class PacketSender
        {
            public static int SendCount { get; private set; }
            public static void Send(IPacket packet) => SendCount++;
        }
        public static class PacketDispatcher
        {
            public static void Dispatch(IPacket packet)
            {
                packet.OnDispatched();
            }
        }
        public static class PacketRuntime
        {
            public static void RunBoth() => Run(sendFirst: true, sendSecond: true);
            public static void RunFirstOnly() => Run(sendFirst: true, sendSecond: false);
            public static void RunSecondOnly() => Run(sendFirst: false, sendSecond: true);
            private static void Run(bool sendFirst, bool sendSecond)
            {
                var packet = new ProbePacket();
                using var stream = new MemoryStream(new byte[] { 0 });
                using var reader = new BinaryReader(stream);
                PacketRegistry.Register<ProbePacket>();
                if (sendFirst)
                    PacketSender.Send(packet);
                if (sendSecond)
                    PacketSender.Send(packet);
                packet.Deserialize(reader);
                PacketDispatcher.Dispatch(packet);
            }
        }
        public static class DisabledPacketRuntime
        {
            public static void Register()
            {
                PacketRegistry.Register<ProbePacket>();
            }
            public static void Send(ProbePacket packet)
            {
                return;
                PacketSender.Send(packet);
            }
        }

        public sealed class Door
        {
            public static bool LastOpen { get; private set; }
            public bool Open { get; private set; }
            public void SetOpen(bool open)
            {
                Open = open;
                LastOpen = open;
            }
        }
        [HarmonyPatch(typeof(Door), nameof(Door.SetOpen))]
        public static class DoorPatch
        {
            public static int Invocations { get; private set; }
            [HarmonyPostfix]
            public static void Postfix() => Invocations++;
        }
        public static class HarmonyRuntime
        {
            public static void Run()
            {
                var door = new Door();
                door.SetOpen(true);
                DoorPatch.Postfix();
            }
        }

        public static class Game
        {
            public static int SubscribeCount { get; private set; }
            public static int TriggerCount { get; private set; }
            public static void Subscribe(string key, Action handler) => SubscribeCount++;
            public static void Trigger(string key, object? data = null) => TriggerCount++;
        }
        public static class EventRuntime
        {
            public static event Action? Changed;
            public static int Deliveries { get; private set; }
            public static void Attach()
            {
                Game.Subscribe("probe", Handle);
                Changed += Handle;
                Changed -= Handle;
            }
            public static void Publish()
            {
                Changed?.Invoke();
                Game.Trigger("probe", null);
            }
            private static void Handle() => Deliveries++;
        }

        public abstract class CoroutineOwner
        {
            protected IEnumerator StartCoroutine(IEnumerator routine) => routine;
        }
        public sealed class CoroutineRuntime : CoroutineOwner
        {
            public IEnumerator Start() => StartCoroutine(WaitForCompletion());
            private IEnumerator WaitForCompletion()
            {
                yield return null;
            }
        }

        public sealed class StateNode { }
        public sealed class StateMachineInstance
        {
            public void GoTo(StateNode state) { }
        }
        public static class StateRuntime
        {
            public static bool Transitioned { get; private set; }
            public static void Apply()
            {
                var smi = new StateMachineInstance();
                var state = new StateNode();
                smi.GoTo(state);
                Transitioned = true;
            }
        }
        """;
}
