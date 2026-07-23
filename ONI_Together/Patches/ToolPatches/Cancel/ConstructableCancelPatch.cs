using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Tools;
using Shared.Profiling;

namespace ONI_Together.Patches.ToolPatches.Cancel
{
	[HarmonyPatch(typeof(Constructable), "OnCancel")]
	public static class ConstructableCancelPatch
	{
		public static bool Prefix(Constructable __instance)
		{
			using var _ = Profiler.Scope();
			if (!ShouldHandle())
				return true;
			if (MultiplayerSession.IsHost)
				return true;
			if (!TryCreate(__instance, out BuildingActionPacket packet))
				return true;

			PacketSender.SendToAllOtherPeers(packet);
#if DEBUG
			packet.LogOriginalBlocked("sync:a711f467afd68f8ebbf45096");
#endif
			return false;
		}

		public static void Postfix(Constructable __instance)
		{
			if (!MultiplayerSession.IsHost || !ShouldHandle()
			    || !TryCreate(__instance, out BuildingActionPacket packet))
				return;
			PacketSender.SendToAllClients(packet, PacketSendMode.ReliableImmediate);
			packet.LogHostOutcome("sync:98de2b9d6b03e3804bf7deed");
		}

		private static bool ShouldHandle()
			=> MultiplayerSession.InSession && !BuildingActionPacket.ProcessingIncoming
			   && !DragToolPacket.ProcessingIncoming;

		private static bool TryCreate(
			Constructable target, out BuildingActionPacket packet)
		{
			packet = null;
			NetworkIdentity identity = target?.GetComponent<NetworkIdentity>();
			if (identity == null || identity.NetId == 0)
				return false;
			packet = BuildingActionPacket.CreateLocal(
				identity.NetId, BuildingActionKind.CancelConstruct);
			return packet.LifecycleRevision != 0;
		}
	}
}
