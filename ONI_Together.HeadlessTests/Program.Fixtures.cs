namespace ONI_Together.HeadlessTests;

internal static partial class Program
{
    private const string PacketFixture = """
        using System;
        using System.IO;

        public interface IPacket
        {
            void Serialize(BinaryWriter writer);
            void Deserialize(BinaryReader reader);
            void OnDispatched();
        }
        public interface IIndirectPacket : IPacket { }
        public interface IPacketSkipsRegistration { }
        public interface IHostOnlyPacket { }
        public interface IClientRelayable { }

        public sealed class DirectPacket : IPacket
        {
            public void Serialize(BinaryWriter writer) { }
            public void Deserialize(BinaryReader reader) { }
            public void OnDispatched() { }
        }
        public sealed class IndirectPacket : IIndirectPacket
        {
            public void Serialize(BinaryWriter writer) { }
            public void Deserialize(BinaryReader reader) { }
            public void OnDispatched() { }
        }
        public interface InterfacePacket : IPacket { }
        public abstract class AbstractPacket : IPacket
        {
            public abstract void Serialize(BinaryWriter writer);
            public abstract void Deserialize(BinaryReader reader);
            public abstract void OnDispatched();
        }
        public sealed class SkippedPacket : IPacket, IPacketSkipsRegistration
        {
            public void Serialize(BinaryWriter writer) { }
            public void Deserialize(BinaryReader reader) { }
            public void OnDispatched() { }
        }
        """;

    private const string BrokenPacketFixture = """
        using System.IO;
        public interface IPacket
        {
            void Serialize(BinaryWriter writer);
            void Deserialize(BinaryReader reader);
            void OnDispatched();
        }
        public interface IHostOnlyPacket { }
        public interface IClientRelayable { }

        public sealed class ConflictPacket : IPacket, IHostOnlyPacket, IClientRelayable
        {
            public void Serialize(BinaryWriter writer) { }
            public void Deserialize(BinaryReader reader) { }
            public void OnDispatched() { }
        }
        public sealed class MissingMemberPacket : IPacket
        {
            public void Serialize(BinaryWriter writer) { }
            public void OnDispatched() { }
        }
        public sealed class PrivateConstructorPacket : IPacket
        {
            private PrivateConstructorPacket() { }
            public void Serialize(BinaryWriter writer) { }
            public void Deserialize(BinaryReader reader) { }
            public void OnDispatched() { }
        }
        """;

    private const string HarmonyFixture = """
        using System;
        public sealed class HarmonyPatch : Attribute { }

        [HarmonyPatch]
        public static class PrefixPatch
        {
            public static void Prefix() { }
        }
        [HarmonyPatch]
        public static class TargetPatch
        {
            public static void TargetMethod() { }
        }
        [HarmonyPatch]
        public static class MissingEntrypointPatch
        {
            public static void Helper() { }
        }
        """;
}
