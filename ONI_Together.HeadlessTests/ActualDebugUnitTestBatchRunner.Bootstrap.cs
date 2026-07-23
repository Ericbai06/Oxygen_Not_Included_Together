using System.Reflection;

namespace ONI_Together.HeadlessTests;

internal sealed partial class ActualDebugUnitTestBatchRunner
{
    private const string BootstrapMethod =
        "ONI_Together.Networking.Packets.Architecture." +
        "PacketRegistry.RegisterDefaults()";

    private static ActualDebugUnitTestBootstrapEvidence Bootstrap(
        Assembly assembly)
    {
        Type registry = assembly.GetType(
            "ONI_Together.Networking.Packets.Architecture.PacketRegistry",
            throwOnError: true)!;
        FieldInfo packets = registry.GetField(
            "_PacketTypes", BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "PacketRegistry packet dictionary is missing");
        int before = CollectionCount(packets.GetValue(null));
        if (before != 0)
            throw new InvalidOperationException(
                "fresh loaded PacketRegistry was not empty");
        MethodInfo registerDefaults = registry.GetMethod(
            "RegisterDefaults", BindingFlags.Static | BindingFlags.Public,
            binder: null, types: Type.EmptyTypes, modifiers: null) ??
            throw new InvalidOperationException(
                "PacketRegistry.RegisterDefaults() is missing");
        registerDefaults.Invoke(null, null);
        int after = CollectionCount(packets.GetValue(null));
        if (after <= 0)
            throw new InvalidOperationException(
                "PacketRegistry.RegisterDefaults() registered no packets");
        return new ActualDebugUnitTestBootstrapEvidence(
            BootstrapMethod, 1, before, after, true);
    }

    private static int CollectionCount(object? collection)
    {
        if (collection is null)
            throw new InvalidOperationException(
                "PacketRegistry packet dictionary is null");
        PropertyInfo count = collection.GetType().GetProperty(
            "Count", BindingFlags.Instance | BindingFlags.Public) ??
            throw new InvalidOperationException(
                "PacketRegistry packet dictionary has no Count");
        return (int)count.GetValue(collection)!;
    }

    private static void ValidateBootstrap(
        ActualDebugUnitTestBootstrapEvidence? bootstrap)
    {
        if (bootstrap is null ||
            bootstrap.MethodSymbol != BootstrapMethod ||
            bootstrap.InvocationCount != 1 ||
            bootstrap.RegisteredPacketCountBefore != 0 ||
            bootstrap.RegisteredPacketCountAfter <= 0 ||
            !bootstrap.RegistryWasInitiallyEmpty)
            throw new InvalidOperationException(
                "PacketRegistry bootstrap evidence drift");
    }
}
