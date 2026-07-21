#if DEBUG
using System;
using System.Collections;
using System.Reflection;
using ONI_Together.Networking.Packets.Architecture;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class BuildingSyncerTests
	{
		private const string PacketTypeName =
			"ONI_Together.Networking.Packets.World.BuildingStatePacket";
		private const string SyncerTypeName =
			"ONI_Together.Networking.Components.BuildingSyncer";

		[UnitTest(name: "Dead periodic building snapshot protocol is removed", category: "Sync")]
		public static UnitTestResult DeadSnapshotProtocolIsAbsent()
		{
			Assembly assembly = typeof(PacketRegistry).Assembly;
			Type packet = assembly.GetType(PacketTypeName, throwOnError: false);
			Type syncer = assembly.GetType(SyncerTypeName, throwOnError: false);
			bool registered = RegistryContains(PacketTypeName);
			return packet == null && syncer == null && !registered
				? UnitTestResult.Pass("Dead oversized building snapshot protocol is absent and unregistered")
				: UnitTestResult.Fail("BuildingStatePacket or BuildingSyncer still exists or is registered");
		}

		private static bool RegistryContains(string fullName)
		{
			FieldInfo field = typeof(PacketRegistry).GetField(
				"_PacketTypes", BindingFlags.Static | BindingFlags.NonPublic);
			if (field?.GetValue(null) is not IDictionary packets) return false;
			foreach (DictionaryEntry entry in packets)
				if (entry.Value is Type type && fullName == type.FullName)
					return true;
			return false;
		}
	}
}
#endif
