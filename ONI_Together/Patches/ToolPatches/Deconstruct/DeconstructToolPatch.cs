using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Tools;
using ONI_Together.Networking.Packets.Tools.Deconstruct;
using Shared.Profiling;

namespace ONI_Together.Patches.ToolPatches.Deconstruct
{
	[HarmonyPatch(typeof(DeconstructTool), nameof(DeconstructTool.OnDragTool))]
	public static class DeconstructToolPatch
	{
		internal static bool ProcessingLocalDrag { get; private set; }

		public static bool Prefix(int cell, int distFromOrigin)
		{
			using var _ = Profiler.Scope();
			if (!MultiplayerSession.InSession || DragToolPacket.ProcessingIncoming)
				return true;

			if (MultiplayerSession.IsClient)
			{
				DeconstructPacket packet = DeconstructPacket.CreateLocal(cell, distFromOrigin);
				PacketSender.SendToAllOtherPeers(packet);
#if DEBUG
				packet.LogOriginalBlocked("sync:cf4cb1fd5a23fdf78e8e377f");
#endif
				return false;
			}

			ProcessingLocalDrag = true;
			return true;
		}

		public static void Postfix(int cell, int distFromOrigin)
		{
			if (!ProcessingLocalDrag)
				return;
			ProcessingLocalDrag = false;
			DeconstructPacket packet = DeconstructPacket.CreateLocal(cell, distFromOrigin);
			PacketSender.SendToAllClients(packet, PacketSendMode.ReliableImmediate);
			packet.LogHostOutcome("sync:5f6c9ca4e746a7fe30f0eba4");
		}

		public static System.Exception Finalizer(System.Exception __exception)
		{
			ProcessingLocalDrag = false;
			return __exception;
		}
	}
}
