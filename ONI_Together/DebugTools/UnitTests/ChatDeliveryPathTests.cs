#if DEBUG
using System;
using System.Collections.Generic;
using System.Reflection;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Handshake;
using ONI_Together.Networking.Packets.Social;
using ONI_Together.Networking.States;
using ONI_Together.Networking.Transport;
using ONI_Together.Networking.Transport.Lan;
using ONI_Together.Networking.Transport.Steamworks;
using ONI_Together.UI;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class ChatDeliveryPathTests
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

		[UnitTest(name: "Target chat history enters the ready replay backlog", category: "Networking")]
		public static UnitTestResult TargetHistoryUsesBarrierBacklog()
		{
			TransportPacketSender originalSender = NetworkConfig.TransportPacketSender;
			bool originalHost = MultiplayerSession.IsHost;
			ulong originalHostId = MultiplayerSession.HostUserID;
			var originalPlayers = new Dictionary<ulong, MultiplayerPlayer>(
				MultiplayerSession.ConnectedPlayers);
			var sender = new RecordingSender();
			try
			{
				ArrangeBarrier(sender);
				if (!ReadyManager.BeginSyncBarrier(2))
					return UnitTestResult.Fail("Could not begin target sync barrier");
				ChatScreen.BufferHistoryForPlayer(2);
				if (ChatScreen.PendingHistoryRecipientCountForTests != 1
				    || ReliableSyncBacklog.CountForTests(2) != 0)
					return UnitTestResult.Fail("History was not held before the replay journal opened");
				if (!ReadyManager.BeginSnapshotEpoch(2, out long generation)
				    || !ReadyManager.SetPlayerReadyState(
					    MultiplayerSession.ConnectedPlayers[2], ClientReadyState.Loading, 99, generation)
				    || !ReadyManager.TryBeginWorldBaseline(2, generation))
					return UnitTestResult.Fail("Could not open the target replay journal");
				bool valid = ChatScreen.PendingHistoryRecipientCountForTests == 0
				             && ReliableSyncBacklog.CountForTests(2) == 1
				             && ReliableSyncBacklog.CountForTests(3) == 0
				             && CountPackets<ChatHistorySyncPacket>(sender.Packets) == 0;
				return valid
					? UnitTestResult.Pass("Target history is journalled once and never broadcast or sent early")
					: UnitTestResult.Fail("History bypassed, duplicated, or broadcast outside the target backlog");
			}
			finally
			{
				Restore(originalSender, originalHost, originalHostId, originalPlayers);
			}
		}

		[UnitTest(name: "Game-state request establishes barrier before chat history", category: "Networking", liveSafe: true)]
		public static UnitTestResult RequestOrdersBarrierBeforeHistory()
		{
			MethodInfo request = Method(typeof(GameStateRequestPacket), "HandleHostRequest");
			MethodInfo begin = Method(typeof(ReadyManager), "BeginSyncBarrier");
			MethodInfo buffer = Method(typeof(ChatScreen), "BufferHistoryForPlayer");
			MethodInfo flush = Method(typeof(ChatScreen), "FlushBufferedHistory");
			MethodInfo target = Method(typeof(PacketSender), nameof(PacketSender.SendToPlayer));
			MethodInfo broadcast = Method(typeof(PacketSender), nameof(PacketSender.SendToAllClients));
			int beginIndex = IndexOfCall(request, begin);
			int bufferIndex = IndexOfCall(request, buffer);
			bool valid = beginIndex >= 0 && bufferIndex > beginIndex
			             && Calls(flush, target) && !Calls(flush, broadcast);
			return valid
				? UnitTestResult.Pass("Request begins its barrier before scheduling target-only history")
				: UnitTestResult.Fail("History scheduling precedes the barrier or uses a broadcast path");
		}

		[UnitTest(name: "Raw transport joins never send chat history", category: "Networking", liveSafe: true)]
		public static UnitTestResult RawJoinDoesNotSendHistory()
		{
			MethodInfo steamJoin = Method(typeof(SteamLobby), "OnLobbyChatUpdate");
			MethodInfo serverJoin = Method(typeof(RiptideServer), nameof(RiptideServer.AddClientToList));
			MethodInfo clientJoin = Method(typeof(RiptideClient), nameof(RiptideClient.AddClientToList));
			MethodInfo buffer = Method(typeof(ChatScreen), "BufferHistoryForPlayer");
			MethodInfo flush = Method(typeof(ChatScreen), "FlushBufferedHistory");
			MethodInfo send = Method(typeof(PacketSender), nameof(PacketSender.SendToPlayer));
			foreach (MethodInfo join in new[] { steamJoin, serverJoin, clientJoin })
				if (join == null || Calls(join, buffer) || Calls(join, flush) || Calls(join, send))
					return UnitTestResult.Fail("A raw Steam/Riptide join directly sends chat history");
			return UnitTestResult.Pass("Only the verified game-state handshake can schedule history");
		}

		private static void ArrangeBarrier(RecordingSender sender)
		{
			PacketRegistry.TryRegister(typeof(ChatHistorySyncPacket));
			NetworkConfig.TransportPacketSender = sender;
			ReadyManager.ResetSessionState();
			PacketSender.ResetSessionState();
			ChatScreen.ResetSessionState();
			MultiplayerSession.ConnectedPlayers.Clear();
			MultiplayerSession.IsHost = true;
			MultiplayerSession.HostUserID = 1;
			AddClient(2, ClientReadyState.Unready);
			AddClient(3, ClientReadyState.Ready);
		}

		private static void AddClient(ulong playerId, ClientReadyState readyState)
		{
			var player = new MultiplayerPlayer(playerId);
			player.BeginConnection(new object());
			player.ProtocolVerified = true;
			player.readyState = readyState;
			MultiplayerSession.ConnectedPlayers.Add(playerId, player);
		}

		private static void Restore(
			TransportPacketSender sender,
			bool isHost,
			ulong hostId,
			Dictionary<ulong, MultiplayerPlayer> players)
		{
			ChatScreen.ResetSessionState();
			ReadyManager.ResetSessionState();
			PacketSender.ResetSessionState();
			MultiplayerSession.ConnectedPlayers.Clear();
			foreach (KeyValuePair<ulong, MultiplayerPlayer> pair in players)
				MultiplayerSession.ConnectedPlayers.Add(pair.Key, pair.Value);
			MultiplayerSession.IsHost = isHost;
			MultiplayerSession.HostUserID = hostId;
			NetworkConfig.TransportPacketSender = sender;
		}

		private static int CountPackets<T>(IReadOnlyList<IPacket> packets) where T : IPacket
		{
			int count = 0;
			for (int i = 0; i < packets.Count; i++)
				if (packets[i] is T) count++;
			return count;
		}

		private static MethodInfo Method(Type type, string name)
			=> type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic
			                              | BindingFlags.Static | BindingFlags.Instance);

		private static bool Calls(MethodInfo caller, MethodInfo callee)
			=> IndexOfCall(caller, callee) >= 0;

		private static int IndexOfCall(MethodInfo caller, MethodInfo callee)
		{
			byte[] il = caller?.GetMethodBody()?.GetILAsByteArray();
			if (il == null || callee == null) return -1;
			for (int index = 0; index <= il.Length - 5; index++)
			{
				if (il[index] != 0x28 && il[index] != 0x6F) continue;
				try
				{
					MethodBase target = caller.Module.ResolveMethod(BitConverter.ToInt32(il, index + 1));
					if (target == callee) return index;
				}
				catch (ArgumentException) { }
			}
			return -1;
		}
	}
}
#endif
