using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Tools;
using Shared.Profiling;

namespace ONI_Together.Patches.ToolPatches.Deconstruct
{
	[HarmonyPatch(typeof(Deconstructable), nameof(Deconstructable.QueueDeconstruction),
		new System.Type[] { typeof(bool) })]
	public static class DeconstructableQueuePatch
	{
		public static bool Prefix(Deconstructable __instance, bool userTriggered)
		{
			using var _ = Profiler.Scope();
			if (!ShouldHandle(userTriggered))
				return true;
			if (MultiplayerSession.IsHost)
				return true;
			if (!TryCreate(__instance, out BuildingActionPacket packet))
				return true;

			PacketSender.SendToAllOtherPeers(packet);
#if DEBUG
			packet.LogOriginalBlocked("sync:30317da11221bee00590d5a1");
#endif
			return false;
		}

		public static void Postfix(Deconstructable __instance, bool userTriggered)
		{
			if (!MultiplayerSession.IsHost || !ShouldHandle(userTriggered)
			    || !TryCreate(__instance, out BuildingActionPacket packet))
				return;
			PacketSender.SendToAllClients(packet, PacketSendMode.ReliableImmediate);
			packet.LogHostOutcome("sync:e089a2b828bfd6d5d84b1f3b");
		}

		private static bool ShouldHandle(bool userTriggered)
			=> MultiplayerSession.InSession && userTriggered
			   && !BuildingActionPacket.ProcessingIncoming
			   && !DragToolPacket.ProcessingIncoming
			   && !DeconstructToolPatch.ProcessingLocalDrag;

		private static bool TryCreate(
			Deconstructable target, out BuildingActionPacket packet)
		{
			packet = null;
			NetworkIdentity identity = target?.GetComponent<NetworkIdentity>();
			if (identity == null || identity.NetId == 0)
				return false;
			packet = BuildingActionPacket.CreateLocal(
				identity.NetId, BuildingActionKind.QueueDeconstruct);
			return packet.LifecycleRevision != 0;
		}
	}
}
