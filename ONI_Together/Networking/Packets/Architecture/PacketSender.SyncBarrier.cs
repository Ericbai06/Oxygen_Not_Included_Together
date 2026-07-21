using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.Handshake;
using ONI_Together.Networking.Packets.World;
using Shared.Interfaces.Networking;

namespace ONI_Together.Networking
{
	public static partial class PacketSender
	{
		private static bool? TryHandleSyncBarrierTarget(
			ulong clientId,
			IPacket packet,
			PacketSendMode sendMode)
		{
			if (!MultiplayerSession.IsHost || IsSyncBarrierControl(packet))
				return null;

			SyncBacklogResult buffered = ReliableSyncBacklog.TryBuffer(
				clientId, packet, sendMode);
			if (buffered == SyncBacklogResult.NotBuffered)
				return null;
			if (buffered == SyncBacklogResult.Buffered)
				return true;
			if (buffered == SyncBacklogResult.Overflow)
			{
				DebugConsole.LogError(
					$"[SyncBacklog] Reliable delta limit exceeded for {clientId}; disconnecting to prevent desync.",
					false);
				ReadyManager.PrepareFreshSnapshot(clientId);
				NetworkConfig.TransportServer?.KickClient(clientId);
			}
			return false;
		}

		private static bool IsSyncBarrierControl(IPacket packet)
			=> packet is GameStateRequestPacket
				or LoadingAcceptedPacket
				or ReadyAcceptedPacket
				or WorldDataPacket
				or DeferredReliableBatchPacket
				or ReadyReplayCommitPacket
				or ReadyReplayAppliedPacket;

		internal static bool IsSyncBarrierControlForTests(IPacket packet)
			=> IsSyncBarrierControl(packet);
	}
}
