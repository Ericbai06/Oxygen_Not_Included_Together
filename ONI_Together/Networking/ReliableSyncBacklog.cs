using System;
using System.Collections.Generic;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;

namespace ONI_Together.Networking
{
	internal enum SyncBacklogResult
	{
		NotBuffered,
		Buffered,
		Overflow,
		Terminated
	}

	internal static class ReliableSyncBacklog
	{
		private const int MaxEntriesPerClient = DeferredReliableBatchPacket.MaxFrames;
		internal const int MaxBytesPerClient = 16 * 1024 * 1024;
		private const int EmptyJournalBytes = sizeof(int);
		private static readonly Dictionary<ulong, ClientBacklog> Clients = new();
		private static long _nextReplayId;

		private sealed class ClientBacklog
		{
			internal readonly Queue<byte[]> Packets = new();
			internal int Bytes = EmptyJournalBytes;
			internal bool Overflowed;
			internal bool Replaying;
			internal long ConnectionGeneration;
			internal ReadyReplayProof Proof;
			internal List<byte[][]> ReplayBatches;
			internal MultiplayerPlayer ReplayPlayer;
			internal Action<bool> Completion;
		}

		internal static void Begin(ulong clientId)
		{
			if (Clients.TryGetValue(clientId, out ClientBacklog current) && current.Replaying)
			{
				AbortReplay(clientId, current);
				ReadyManager.CancelPendingReadyCommit(clientId);
				return;
			}
			Clients[clientId] = new ClientBacklog();
		}

		internal static SyncBacklogResult TryBuffer(
			ulong clientId, IPacket packet, PacketSendMode sendMode)
		{
			if (!Clients.TryGetValue(clientId, out ClientBacklog backlog)
			    || (sendMode & PacketSendMode.Reliable) == 0)
				return SyncBacklogResult.NotBuffered;
			if (backlog.Overflowed)
				return SyncBacklogResult.Terminated;
			try
			{
				byte[] payload = PacketSender.SerializePacketForSending(packet);
				int cost = checked(sizeof(int) + payload.Length);
				if (PacketHandler.IsForbiddenReadyReplayFrame(payload)
				    || backlog.Packets.Count >= MaxEntriesPerClient
				    || cost > MaxBytesPerClient - backlog.Bytes
				    || cost > DeferredReliableBatchPacket.MaxFrameCost)
					return MarkOverflowed(backlog);
				backlog.Packets.Enqueue(payload);
				backlog.Bytes += cost;
				return SyncBacklogResult.Buffered;
			}
			catch (Exception ex)
			{
				DebugConsole.LogWarning(
					$"[SyncBacklog] Failed to snapshot {packet.GetType().Name}: {ex.Message}");
				return MarkOverflowed(backlog);
			}
		}

		internal static bool IsBuffering(ulong clientId)
			=> Clients.TryGetValue(clientId, out ClientBacklog backlog)
			   && !backlog.Overflowed;

		private static SyncBacklogResult MarkOverflowed(ClientBacklog backlog)
		{
			backlog.Overflowed = true;
			return SyncBacklogResult.Overflow;
		}

		internal static void BufferForDisconnectedClients(
			IPacket packet, PacketSendMode sendMode, Func<ulong, bool> isExcluded)
		{
			foreach (ulong clientId in new List<ulong>(Clients.Keys))
			{
				if (MultiplayerSession.ConnectedPlayers.ContainsKey(clientId)
				    || isExcluded != null && isExcluded(clientId))
					continue;
				if (TryBuffer(clientId, packet, sendMode) != SyncBacklogResult.Overflow)
					continue;
				DebugConsole.LogWarning(
					$"[SyncBacklog] Disconnected client {clientId} exceeded its delta journal");
				ReadyManager.PrepareFreshSnapshot(clientId);
			}
		}

		internal static bool Replay(
			MultiplayerPlayer player,
			ReadyReplayProof proof,
			Action<bool> completion)
		{
			if (player == null || player.Connection == null || proof.ReconnectToken == 0
			    || proof.SnapshotGeneration <= 0
			    || !Clients.TryGetValue(player.PlayerId, out ClientBacklog backlog)
			    || backlog.Overflowed || backlog.Replaying || completion == null)
				return false;
			backlog.Replaying = true;
			backlog.ReplayPlayer = player;
			backlog.ConnectionGeneration = player.ConnectionGeneration;
			backlog.Completion = completion;
			backlog.ReplayBatches = CreateBatches(backlog.Packets);
			proof.ReplayId = NextReplayId();
			proof.BatchCount = backlog.ReplayBatches.Count;
			backlog.Proof = proof;
			DebugConsole.Log(
				$"[SyncBacklog] Sending {backlog.Packets.Count} frame(s) in " +
				$"{backlog.Proof.BatchCount} batch(es), {backlog.Bytes - EmptyJournalBytes} B " +
				$"for {player.PlayerId} replay={backlog.Proof.ReplayId}");
			return SendReplay(player.PlayerId, backlog);
		}

		private static bool SendReplay(ulong clientId, ClientBacklog backlog)
		{
			for (int index = 0; index < backlog.ReplayBatches.Count; index++)
			{
				var header = new ReadyReplayBatchHeader
				{
					SnapshotGeneration = backlog.Proof.SnapshotGeneration,
					ReplayId = backlog.Proof.ReplayId,
					BatchIndex = index,
					BatchCount = backlog.Proof.BatchCount,
				};
				DeferredReliableBatchPacket packet = DeferredReliableBatchPacket.Create(
					header, backlog.ReplayBatches[index]);
				if (!PacketSender.SendToConnection(
					    backlog.ReplayPlayer.Connection, packet, PacketSendMode.Reliable))
					return FailReplay(clientId, backlog);
			}
			ReadyReplayCommitPacket commit = ReadyReplayCommitPacket.Create(backlog.Proof);
			if (PacketSender.SendToConnection(
				    backlog.ReplayPlayer.Connection, commit, PacketSendMode.ReliableImmediate))
				return true;
			return FailReplay(clientId, backlog);
		}

		internal static bool AcceptApplied(
			ulong clientId, long connectionGeneration, ReadyReplayProof proof)
		{
			if (!Clients.TryGetValue(clientId, out ClientBacklog backlog)
			    || !backlog.Replaying || backlog.ReplayPlayer?.Connection == null
			    || backlog.ConnectionGeneration != connectionGeneration
			    || backlog.ReplayPlayer.ConnectionGeneration != connectionGeneration
			    || !proof.Matches(backlog.Proof))
				return false;
			ReadyManager.RecordReliableReplayProgress(clientId);
			Clients.Remove(clientId);
			DebugConsole.Log(
				$"[SyncBacklog] Replay applied for {clientId} replay={backlog.Proof.ReplayId}");
			Action<bool> completion = backlog.Completion;
			backlog.Completion = null;
			completion?.Invoke(true);
			return true;
		}

		private static bool FailReplay(ulong clientId, ClientBacklog backlog)
		{
			if (!Clients.TryGetValue(clientId, out ClientBacklog current)
			    || !ReferenceEquals(current, backlog))
				return false;
			backlog.Replaying = false;
			Action<bool> completion = backlog.Completion;
			backlog.Completion = null;
			completion?.Invoke(false);
			return false;
		}

		private static List<byte[][]> CreateBatches(IEnumerable<byte[]> packets)
		{
			var batches = new List<byte[][]>();
			var current = new List<byte[]>();
			int wireBytes = DeferredReliableBatchPacket.FixedWireBytes;
			foreach (byte[] packet in packets)
			{
				int cost = sizeof(int) + packet.Length;
				if (wireBytes + cost > DeferredReliableBatchPacket.MaxWireBytes)
				{
					batches.Add(current.ToArray());
					current.Clear();
					wireBytes = DeferredReliableBatchPacket.FixedWireBytes;
				}
				current.Add(packet);
				wireBytes += cost;
			}
			if (current.Count > 0)
				batches.Add(current.ToArray());
			return batches;
		}

		private static ulong NextReplayId()
		{
			long replayId = System.Threading.Interlocked.Increment(ref _nextReplayId);
			if (replayId > 0)
				return (ulong)replayId;
			System.Threading.Interlocked.Exchange(ref _nextReplayId, 1);
			return 1UL;
		}

		internal static bool Transfer(ulong oldClientId, ulong newClientId)
		{
			if (!CanTransfer(oldClientId, newClientId))
				return false;
			if (oldClientId == newClientId)
				return true;
			ClientBacklog backlog = Clients[oldClientId];
			Clients.Remove(oldClientId);
			Clients.Add(newClientId, backlog);
			return true;
		}

		internal static bool CanTransfer(ulong oldClientId, ulong newClientId)
			=> Clients.TryGetValue(oldClientId, out ClientBacklog backlog)
			   && !backlog.Overflowed && !backlog.Replaying
			   && (oldClientId == newClientId || !Clients.ContainsKey(newClientId));

		internal static void Prune(Func<ulong, bool> keep)
		{
			foreach (ulong clientId in new List<ulong>(Clients.Keys))
			{
				if (!keep(clientId))
					Clients.Remove(clientId);
			}
		}

		internal static void Clear(ulong clientId)
		{
			if (Clients.TryGetValue(clientId, out ClientBacklog backlog) && backlog.Replaying)
				AbortReplay(clientId, backlog);
			else
				Clients.Remove(clientId);
			ReadyManager.CancelPendingReadyCommit(clientId);
		}

		private static void AbortReplay(ulong clientId, ClientBacklog backlog)
		{
			Clients.Remove(clientId);
			object connection = backlog.ReplayPlayer?.Connection;
			if (connection != null)
				PacketSender.DropConnection(connection);
			if (MultiplayerSession.IsHost)
				NetworkConfig.TransportServer?.KickClient(clientId);
			backlog.Completion = null;
		}

		internal static void ClearAll()
		{
			Clients.Clear();
			System.Threading.Interlocked.Exchange(ref _nextReplayId, 0);
			ReadyManager.CancelAllPendingReadyCommits();
		}

		internal static int CountForTests(ulong clientId)
			=> Clients.TryGetValue(clientId, out ClientBacklog backlog)
				? backlog.Packets.Count : 0;

		internal static bool IsCollecting(ulong clientId)
			=> Clients.TryGetValue(clientId, out ClientBacklog backlog)
			   && !backlog.Overflowed && !backlog.Replaying;

		internal static bool IsReplaying(ulong clientId)
			=> Clients.TryGetValue(clientId, out ClientBacklog backlog) && backlog.Replaying;

		internal static bool IsReplayingForTests(ulong clientId)
			=> IsReplaying(clientId);
	}
}
