#if DEBUG
using System.Collections.Generic;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.States;
using ONI_Together.Networking.Transport;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class BroadcastSyncBarrierTests
	{
		private sealed class RecordingSender : TransportPacketSender
		{
			internal readonly List<IPacket> Packets = new();

			public override bool SendPacket(
				object connection,
				SerializedPacket packet,
				PacketSendMode sendMode = PacketSendMode.ReliableImmediate)
			{
				Packets.Add(packet.Packet);
				return true;
			}
		}

		[UnitTest(
			name: "Broadcast prioritizes an active sync barrier over stale Ready state",
			category: "Networking")]
		public static UnitTestResult BroadcastUsesActiveBarrierBeforeReadyState()
		{
			TransportPacketSender originalSender = NetworkConfig.TransportPacketSender;
			bool originalHost = MultiplayerSession.IsHost;
			ulong originalHostId = MultiplayerSession.HostUserID;
			var originalPlayers = new Dictionary<ulong, MultiplayerPlayer>(
				MultiplayerSession.ConnectedPlayers);
			var sender = new RecordingSender();
			try
			{
				Arrange(sender, out MultiplayerPlayer barrierClient);
				PacketSender.SendToAllClients(
					new ReliableReadyReplayTests.ReplayProbePacket { Value = 1 },
					PacketSendMode.Reliable);
				if (ReliableSyncBacklog.CountForTests(2) != 1 || sender.Packets.Count != 1)
					return UnitTestResult.Fail("Stale Ready bypassed the active barrier or Ready client was not sent");

				barrierClient.readyState = ClientReadyState.Unready;
				PacketSender.SendToAllClients(new LoadingAcceptedPacket
				{
					ReconnectToken = 7,
					SnapshotGeneration = 3
				}, PacketSendMode.ReliableImmediate);
				return ReliableSyncBacklog.CountForTests(2) == 1 && sender.Packets.Count == 3
					? UnitTestResult.Pass("Barrier gameplay buffered, control bypassed, and non-barrier Ready sent")
					: UnitTestResult.Fail("Barrier control did not bypass the backlog");
			}
			finally
			{
				ReadyManager.ResetSessionState();
				PacketSender.ResetSessionState();
				MultiplayerSession.ConnectedPlayers.Clear();
				foreach (var pair in originalPlayers)
					MultiplayerSession.ConnectedPlayers.Add(pair.Key, pair.Value);
				MultiplayerSession.IsHost = originalHost;
				MultiplayerSession.HostUserID = originalHostId;
				NetworkConfig.TransportPacketSender = originalSender;
			}
		}

		private static void Arrange(
			TransportPacketSender sender,
			out MultiplayerPlayer barrierClient)
		{
			PacketRegistry.TryRegister(typeof(ReliableReadyReplayTests.ReplayProbePacket));
			NetworkConfig.TransportPacketSender = sender;
			ReadyManager.ResetSessionState();
			PacketSender.ResetSessionState();
			MultiplayerSession.ConnectedPlayers.Clear();
			MultiplayerSession.IsHost = true;
			MultiplayerSession.HostUserID = 1;

			barrierClient = AddReadyClient(2);
			AddReadyClient(3);
			ReadyManager.BeginSyncBarrier(2);
			ReliableSyncBacklog.Begin(2);
			barrierClient.readyState = ClientReadyState.Ready;
		}

		private static MultiplayerPlayer AddReadyClient(ulong clientId)
		{
			var client = new MultiplayerPlayer(clientId);
			client.BeginConnection(new object());
			client.ProtocolVerified = true;
			client.readyState = ClientReadyState.Ready;
			MultiplayerSession.ConnectedPlayers.Add(clientId, client);
			return client;
		}
	}
}
#endif
