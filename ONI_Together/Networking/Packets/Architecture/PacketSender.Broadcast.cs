using ONI_Together.DebugTools;
using ONI_Together.Misc;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.States;
using Shared.Interfaces.Networking;
using Shared.Profiling;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ONI_Together.Networking
{
	public static partial class PacketSender
	{
		/// <summary>
		/// Send a packet to a player by their SteamID.
		/// </summary>
		public static bool SendToPlayer(ulong steamID, IPacket packet, PacketSendMode sendType = PacketSendMode.ReliableImmediate)
		{
			using var _ = Profiler.Scope();

			// Prevent host from sending packets to itself (can cause loops and errors)
			if (MultiplayerSession.IsHost && steamID == MultiplayerSession.HostUserID)
			{
				DebugConsole.LogWarning($"[PacketSender] Host attempted to send packet {packet.GetType().Name} to itself - blocked");
				return false;
			}

			if (!MultiplayerSession.ConnectedPlayers.TryGetValue(steamID, out var player) || player.Connection == null)
			{
				DebugConsole.LogWarning($"[PacketSender] No connection found for SteamID {steamID}");
				return false;
			}
			bool? handledResult = TryHandleSyncBarrierTarget(steamID, packet, sendType);
			if (handledResult.HasValue)
				return handledResult.Value;

			return SendToConnection(player.Connection, packet, sendType);
		}

		private static bool CanBroadcastTo(MultiplayerPlayer player)
		{
			using var _ = Profiler.Scope();

			if (player == null || player.Connection == null)
			{
				return false;
			}

			if (!MultiplayerSession.IsHost || player.PlayerId == MultiplayerSession.HostUserID)
			{
				return true;
			}

			return player.ProtocolVerified && SyncBarrier.IsExactReady(player.readyState);
		}

		private static void SendOrBufferBroadcast(
			MultiplayerPlayer player,
			IPacket packet,
			PacketSendMode sendType,
			bool inViewport = true)
		{
			bool inBarrier = player != null
			                 && UsesSyncBarrierBroadcastRouteForTests(
				                 MultiplayerSession.IsHost,
				                 ReadyManager.IsClientInSyncBarrier(player.PlayerId));
			if (inBarrier)
			{
				if (IsSyncBarrierControl(packet))
				{
					if (player.Connection != null && inViewport)
						TrySendToConnection(player, packet, sendType);
					return;
				}

				TryHandleSyncBarrierTarget(player.PlayerId, packet, sendType);
				return;
			}

			if (CanBroadcastTo(player))
			{
				if (inViewport)
					TrySendToConnection(player, packet, sendType);
				return;
			}
		}

		internal static bool UsesSyncBarrierBroadcastRouteForTests(
			bool isHost, bool clientInBarrier)
			=> isHost && clientInBarrier;

		public static bool SendToHost(IPacket packet, PacketSendMode sendType = PacketSendMode.ReliableImmediate)
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.IsHost
			    && !PacketHandler.CanSendClientPacket(packet, GameClient.State))
				return false;
			if (!MultiplayerSession.HostUserID.IsValid())
			{
				if (GameClient.State != ClientState.LoadingWorld)
					DebugConsole.LogWarning("[PacketSender] Failed to send to host. Host is invalid.");
				return false;
			}
			return SendToPlayer(MultiplayerSession.HostUserID, packet, sendType);
		}

		// Throttle counter for per-connection send failures so a transport storm
		// does not flood the log. First 5 errors are logged in full, then 1/100 after.
		private static long _sendErrorCount;

		private static void LogSendFailure(IPacket packet, Exception ex)
		{
			long n = ++_sendErrorCount;
			if (n <= 5 || n % 100 == 0)
				DebugConsole.LogError(
					$"[PacketSender] Send failed (packet={packet.GetType().Name}, #{n}): {ex}");
		}

		private static void TrySendToConnection(MultiplayerPlayer player, IPacket packet, PacketSendMode sendType)
		{
			try
			{
				object connection = player.Connection;
				if (SendToConnection(connection, packet, sendType)
				    || (sendType & PacketSendMode.Reliable) == 0)
					return;

				DebugConsole.LogError(
					$"[PacketSender] Reliable broadcast of {packet.GetType().Name} to {player.PlayerId} failed; disconnecting to prevent desync.",
					false);
				if (MultiplayerSession.IsHost)
					NetworkConfig.TransportServer?.KickClient(player.PlayerId);
			}
			catch (Exception ex)
			{
				LogSendFailure(packet, ex);
			}
		}

		/// Original single-exclude overload
		public static void SendToAll(IPacket packet, ulong? exclude = null, PacketSendMode sendType = PacketSendMode.Reliable)
		{
			using var _ = Profiler.Scope();
			MultiplayerPlayer[] players = MultiplayerSession.ConnectedPlayers.Values.ToArray();

			// Only send this packet if its being observed by a someone
			if (packet is IViewportCullable vp && WorldStateSyncer.Instance != null)
			{
				int cell = vp.GetViewportCell();
				foreach (var player in players)
				{
					if (exclude.HasValue && player.PlayerId == exclude.Value) continue;
					bool inViewport = WorldStateSyncer.Instance.IsCellInPlayerViewport(player.PlayerId, cell);
					SendOrBufferBroadcast(player, packet, sendType, inViewport);
				}
				ReliableSyncBacklog.BufferForDisconnectedClients(
					packet, sendType, id => exclude.HasValue && id == exclude.Value);
				return;
			}

			foreach (var player in players)
			{
				if (exclude.HasValue && player.PlayerId == exclude.Value)
					continue;

				SendOrBufferBroadcast(player, packet, sendType);
			}
			ReliableSyncBacklog.BufferForDisconnectedClients(
				packet, sendType, id => exclude.HasValue && id == exclude.Value);
		}

		public static void SendToAllClients(IPacket packet, PacketSendMode sendType = PacketSendMode.Reliable)
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.IsHost)
			{
				DebugConsole.LogWarning("[PacketSender] Only the host can send to all clients. Tried sending: " + packet.GetType());
				return;
			}
			SendToAll(packet, MultiplayerSession.HostUserID, sendType);
		}

		public static void SendToAllExcluding(IPacket packet, HashSet<ulong> excludedIds, PacketSendMode sendType = PacketSendMode.Reliable)
		{
			using var _ = Profiler.Scope();
			MultiplayerPlayer[] players = MultiplayerSession.ConnectedPlayers.Values.ToArray();

			if (packet is IViewportCullable vp && WorldStateSyncer.Instance != null)
			{
				int cell = vp.GetViewportCell();
				foreach (var player in players)
				{
					if (excludedIds != null && excludedIds.Contains(player.PlayerId)) continue;
					bool inViewport = WorldStateSyncer.Instance.IsCellInPlayerViewport(player.PlayerId, cell);
					SendOrBufferBroadcast(player, packet, sendType, inViewport);
				}
				ReliableSyncBacklog.BufferForDisconnectedClients(
					packet, sendType, id => excludedIds != null && excludedIds.Contains(id));
				return;
			}

			foreach (var player in players)
			{
				if (excludedIds != null && excludedIds.Contains(player.PlayerId))
					continue;

				SendOrBufferBroadcast(player, packet, sendType);
			}
			ReliableSyncBacklog.BufferForDisconnectedClients(
				packet, sendType, id => excludedIds != null && excludedIds.Contains(id));
		}

		/// <summary>
		/// Sends a packet to all other players.
		/// Forces the packet origin to be on the host itself
		/// if sent from the host, it goes to all clients.
		/// otherwise it is wrapped in a HostBroadcastPacket and sent to the host for rebroadcasting.
		///
		/// </summary>
		/// <param name="packet"></param>
		public static void SendToAllOtherPeersFromHost(IPacket packet)
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.InSession)
			{
				DebugConsole.LogWarning("[PacketSender] Not in a multiplayer session, cannot send to other peers");
				return;
			}
			if (!CanSendPeerRuntime(MultiplayerSession.IsHost, GameClient.State))
				return;
			//DebugConsole.Log("[PacketSender] Sending packet to all other peers: " + packet.GetType().Name + " from host");

			if (packet is PlayerCursorPacket cursor
			    && !HostBroadcastPacket.TryFitUnreliableRelay(cursor))
				return;
			PacketSendMode sendMode = HostBroadcastPacket.GetRelaySendMode(packet);
			if (MultiplayerSession.IsHost)
				SendToAllClients(packet, sendMode);
			else
				SendClientRelay(packet, sendMode);
		}

		internal static HostBroadcastPacket CreateHostRelayForClient(IPacket packet, ulong localUserId)
			=> new HostBroadcastPacket(packet, localUserId);

		private static void SendClientRelay(IPacket packet, PacketSendMode sendMode)
		{
			bool sent = SendToHost(
				CreateHostRelayForClient(packet, MultiplayerSession.LocalUserID), sendMode);
			HandleClientRelaySendResult(
				sendMode, sent, () => NetworkConfig.TransportClient?.Disconnect());
		}

		internal static bool HandleClientRelaySendResult(
			PacketSendMode sendMode, bool sent, System.Action disconnect)
		{
			if (sent || (sendMode & PacketSendMode.Reliable) == 0)
				return sent;
			DebugConsole.LogError(
				"[PacketSender] Reliable client relay failed; disconnecting to prevent desync.",
				false);
			disconnect?.Invoke();
			return false;
		}

		/// <summary>
		/// Sends a packet to all other players.
		/// if sent from the host, it goes to all clients.
		/// otherwise it is wrapped in a HostBroadcastPacket and sent to the host for rebroadcasting.
		/// </summary>
		/// <param name="packet"></param>
		public static void SendToAllOtherPeers(IPacket packet)
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.InSession)
			{
				DebugConsole.LogWarning("[PacketSender] Not in a multiplayer session, cannot send to other peers");
				return;
			}
			if (!CanSendPeerRuntime(MultiplayerSession.IsHost, GameClient.State))
				return;
			//DebugConsole.Log("[PacketSender] Sending packet to all other peers: " + packet.GetType().Name);

			if (packet is PlayerCursorPacket cursor
			    && !HostBroadcastPacket.TryFitUnreliableRelay(cursor))
				return;
			PacketSendMode sendMode = HostBroadcastPacket.GetRelaySendMode(packet);
			if (MultiplayerSession.IsHost)
				SendToAllClients(packet, sendMode);
			else if (packet is IBulkablePacket && packet is not IClientRelayable)
				SendToHost(packet, sendMode);
			else
				SendClientRelay(packet, sendMode);
		}
	}
}
