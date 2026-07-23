using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Tools;
using Shared.Profiling;

namespace ONI_Together.Patches.ToolPatches.Deconstruct
{
	[HarmonyPatch(typeof(Deconstructable), nameof(Deconstructable.CancelDeconstruction))]
	public static class DeconstructableCancelPatch
	{
		public static bool Prefix(Deconstructable __instance)
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
			packet.LogOriginalBlocked("sync:408a825c6e7bfddc820bc98e");
#endif
			return false;
		}

		public static void Postfix(Deconstructable __instance)
		{
			if (!MultiplayerSession.IsHost || !ShouldHandle()
			    || !TryCreate(__instance, out BuildingActionPacket packet))
				return;
			PacketSender.SendToAllClients(packet, PacketSendMode.ReliableImmediate);
			packet.LogHostOutcome("sync:1bdce19ebb7dcde23da4128b");
		}

		private static bool ShouldHandle()
			=> MultiplayerSession.InSession && !BuildingActionPacket.ProcessingIncoming
			   && !DragToolPacket.ProcessingIncoming;

		private static bool TryCreate(
			Deconstructable target, out BuildingActionPacket packet)
		{
			packet = null;
			NetworkIdentity identity = target?.GetComponent<NetworkIdentity>();
			if (identity == null || identity.NetId == 0)
				return false;
			packet = BuildingActionPacket.CreateLocal(
				identity.NetId, BuildingActionKind.CancelDeconstruct);
			return packet.LifecycleRevision != 0;
		}
	}
}
