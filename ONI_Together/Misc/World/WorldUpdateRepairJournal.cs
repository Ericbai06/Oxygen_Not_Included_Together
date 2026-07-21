using System;
using System.Collections;
using System.Collections.Generic;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.World;

namespace ONI_Together.Misc.World
{
	internal sealed class WorldUpdateRepairJournal
	{
		internal const int DefaultMaxEntries = WorldUpdateRepairObservability.MaxPendingPackets;
		internal const int DefaultMaxUpdates = WorldUpdateRepairObservability.MaxPendingUpdates;
		internal const float DefaultReplayIntervalSeconds = 1f;

		private sealed class Entry
		{
			internal WorldUpdatePacket Packet;
			internal readonly SortedSet<ulong> PendingClients = new();
		}

		private readonly object _gate = new();
		private readonly int _maxEntries;
		private readonly int _maxUpdates;
		private readonly float _replayIntervalSeconds;
		private readonly SortedDictionary<long, Entry> _entries = new();
		private readonly Dictionary<ulong, long> _appliedThrough = new();
		private readonly Dictionary<ulong, float> _lastReplayAt = new();
		private int _pendingUpdates;
		private long _retransmitCount;
		private ulong _replayCursorClient;
		private bool _backpressured;

		internal WorldUpdateRepairJournal(
			int maxEntries = DefaultMaxEntries,
			int maxUpdates = DefaultMaxUpdates,
			float replayIntervalSeconds = DefaultReplayIntervalSeconds)
		{
			if (maxEntries <= 0 || maxUpdates <= 0 || replayIntervalSeconds <= 0f
			    || float.IsNaN(replayIntervalSeconds) || float.IsInfinity(replayIntervalSeconds))
				throw new ArgumentOutOfRangeException();
			_maxEntries = maxEntries;
			_maxUpdates = maxUpdates;
			_replayIntervalSeconds = replayIntervalSeconds;
		}

		internal bool TryRecordNext(
			WorldUpdatePacket packet, IEnumerable<ulong> clientIds, float now)
		{
			if (packet == null || !packet.IsBackgroundRepair || packet.RepairSequence != 0)
				return false;
			var clients = new SortedSet<ulong>(clientIds ?? Array.Empty<ulong>());
			clients.Remove(0);
			lock (_gate)
			{
				if (clients.Count != 0 && !HasCapacityLocked(packet.Updates.Count))
				{
					_backpressured = true;
					return false;
				}
				packet.RepairSequence = WorldUpdatePacket.NextHostRepairDispatchSequence();
				if (clients.Count == 0)
					return true;
				var entry = new Entry { Packet = Clone(packet) };
				foreach (ulong clientId in clients)
				{
					if (_appliedThrough.TryGetValue(clientId, out long applied)
					    && applied >= packet.RepairSequence)
						continue;
					entry.PendingClients.Add(clientId);
					if (!_lastReplayAt.ContainsKey(clientId))
						_lastReplayAt.Add(clientId, now);
				}
				if (entry.PendingClients.Count != 0)
				{
					_entries.Add(packet.RepairSequence, entry);
					_pendingUpdates += packet.Updates.Count;
				}
				return true;
			}
		}

		internal bool AcceptAppliedAck(ulong clientId, long appliedThrough)
		{
			if (clientId == 0 || appliedThrough <= 0
			    || appliedThrough > WorldUpdatePacket.CurrentHostRepairDispatchSequence)
				return false;
			lock (_gate)
			{
				_appliedThrough.TryGetValue(clientId, out long previous);
				if (appliedThrough < previous)
					return false;
				_appliedThrough[clientId] = appliedThrough;
				var completed = new List<long>();
				foreach (var pair in _entries)
				{
					if (pair.Key <= appliedThrough)
						pair.Value.PendingClients.Remove(clientId);
					if (pair.Value.PendingClients.Count == 0)
						completed.Add(pair.Key);
				}
				foreach (long sequence in completed)
					RemoveLocked(sequence);
				_backpressured = !HasCapacityLocked(1);
				return true;
			}
		}

		internal int DropClient(ulong clientId)
		{
			if (clientId == 0)
				return 0;
			lock (_gate)
			{
				_appliedThrough.Remove(clientId);
				_lastReplayAt.Remove(clientId);
				var completed = new List<long>();
				foreach (var pair in _entries)
				{
					pair.Value.PendingClients.Remove(clientId);
					if (pair.Value.PendingClients.Count == 0)
						completed.Add(pair.Key);
				}
				foreach (long sequence in completed)
					RemoveLocked(sequence);
				_backpressured = !HasCapacityLocked(1);
				return completed.Count;
			}
		}

		internal bool ReplayPendingThrough(
			long sequenceCut, Func<ulong, WorldUpdatePacket, bool> send)
		{
			if (sequenceCut < 0 || send == null)
				return false;
			List<(ulong ClientId, WorldUpdatePacket Packet)> plan = SnapshotThrough(sequenceCut);
			foreach (var item in plan)
			{
				if (!send(item.ClientId, item.Packet))
					return false;
				lock (_gate)
					_retransmitCount++;
			}
			return true;
		}

		internal bool ReplayOneDue(
			float now, Func<ulong, WorldUpdatePacket, bool> send)
		{
			if (send == null || float.IsNaN(now) || float.IsInfinity(now))
				return false;
			ulong clientId = 0;
			WorldUpdatePacket packet = null;
			lock (_gate)
			{
				if (!TrySelectDueLocked(now, afterCursor: true, out clientId, out packet))
					TrySelectDueLocked(now, afterCursor: false, out clientId, out packet);
			}
			if (packet == null || !send(clientId, packet))
				return false;
			lock (_gate)
				_retransmitCount++;
			return true;
		}

		internal void Reset()
		{
			lock (_gate)
			{
				_entries.Clear();
				_appliedThrough.Clear();
				_lastReplayAt.Clear();
				_pendingUpdates = 0;
				_retransmitCount = 0;
				_replayCursorClient = 0;
				_backpressured = false;
			}
		}

		internal int PendingEntryCount
		{
			get { lock (_gate) return _entries.Count; }
		}

		internal int PendingUpdateCount
		{
			get { lock (_gate) return _pendingUpdates; }
		}

		internal long RetransmitCount
		{
			get { lock (_gate) return _retransmitCount; }
		}

		internal bool IsBackpressured
		{
			get { lock (_gate) return _backpressured; }
		}

		private List<(ulong ClientId, WorldUpdatePacket Packet)> SnapshotThrough(long cut)
		{
			var plan = new List<(ulong, WorldUpdatePacket)>();
			lock (_gate)
			{
				foreach (var pair in _entries)
				{
					if (pair.Key > cut)
						break;
					foreach (ulong clientId in pair.Value.PendingClients)
						plan.Add((clientId, pair.Value.Packet));
				}
			}
			return plan;
		}

		private bool TrySelectDueLocked(
			float now, bool afterCursor,
			out ulong clientId, out WorldUpdatePacket packet)
		{
			foreach (ulong pendingClient in PendingClientsLocked())
			{
				bool isAfter = pendingClient > _replayCursorClient;
				_lastReplayAt.TryGetValue(pendingClient, out float lastReplayAt);
				if (isAfter != afterCursor
				    || now - lastReplayAt < _replayIntervalSeconds
				    || !TryGetOldestPendingLocked(pendingClient, out packet))
					continue;
				_lastReplayAt[pendingClient] = now;
				_replayCursorClient = pendingClient;
				clientId = pendingClient;
				return true;
			}
			clientId = 0;
			packet = null;
			return false;
		}

		private SortedSet<ulong> PendingClientsLocked()
		{
			var clients = new SortedSet<ulong>();
			foreach (Entry entry in _entries.Values)
				clients.UnionWith(entry.PendingClients);
			return clients;
		}

		private bool TryGetOldestPendingLocked(
			ulong clientId, out WorldUpdatePacket packet)
		{
			foreach (Entry entry in _entries.Values)
				if (entry.PendingClients.Contains(clientId))
				{
					packet = entry.Packet;
					return true;
				}
			packet = null;
			return false;
		}

		private bool HasCapacityLocked(int updateCount)
			=> updateCount >= 0 && _entries.Count < _maxEntries
			   && updateCount <= _maxUpdates - _pendingUpdates;

		private void RemoveLocked(long sequence)
		{
			_pendingUpdates -= _entries[sequence].Packet.Updates.Count;
			_entries.Remove(sequence);
		}

		private static WorldUpdatePacket Clone(WorldUpdatePacket source)
		{
			return new WorldUpdatePacket
			{
				Revision = source.Revision,
				Sequence = source.Sequence,
				ForegroundCut = source.ForegroundCut,
				RepairSequence = source.RepairSequence,
				Updates = new List<WorldUpdatePacket.CellUpdate>(source.Updates),
			};
		}
	}

	internal static class WorldUpdateRepairObservability
	{
		private sealed class Observation
		{
			internal int UpdateCount;
		}

		internal const int MaxPendingPackets = 128;
		internal const int MaxPendingUpdates = 65536;
		private static readonly object Gate = new();
		private static readonly SortedDictionary<long, Observation> Pending = new();
		private static int _pendingUpdates;
		private static bool _workScheduled;
		private static long _epoch;
		private static long _ackTarget;
		private static long _lastAckSent;

		internal static bool Track(
			WorldUpdatePacket packet, IEnumerable<WorldUpdatePacket.CellUpdate> updates)
		{
			if (packet == null || !packet.IsBackgroundRepair || packet.RepairSequence <= 0)
				return false;
			var accepted = new List<WorldUpdatePacket.CellUpdate>(
				updates ?? Array.Empty<WorldUpdatePacket.CellUpdate>());
			if (accepted.Count == 0)
				return false;
			lock (Gate)
			{
				if (packet.RepairSequence <= WorldUpdatePacket.ClientResolvedRepairSequence)
					return true;
				if (Pending.ContainsKey(packet.RepairSequence))
					return true;
				if (Pending.Count >= MaxPendingPackets
				         || accepted.Count > MaxPendingUpdates - _pendingUpdates)
					return false;
				Pending.Add(packet.RepairSequence, new Observation
				{
					UpdateCount = accepted.Count,
				});
				_pendingUpdates += accepted.Count;
			}
			EnsureWorkScheduled();
			return true;
		}

		internal static int CompleteApplyBarrierForTests()
		{
			List<long> complete;
			lock (Gate)
				complete = TakePendingLocked();
			ResolveCompleted(complete);
			return complete.Count;
		}

		internal static void NotifyResolved(long appliedThrough)
		{
			lock (Gate)
			{
				var completed = new List<long>();
				foreach (var pair in Pending)
				{
					if (pair.Key > appliedThrough)
						break;
					completed.Add(pair.Key);
				}
				foreach (long sequence in completed)
				{
					_pendingUpdates -= Pending[sequence].UpdateCount;
					Pending.Remove(sequence);
				}
				_ackTarget = Math.Max(_ackTarget, appliedThrough);
			}
			EnsureWorkScheduled();
		}

		internal static void SetBaseline(long appliedThrough)
		{
			lock (Gate)
			{
				Pending.Clear();
				_pendingUpdates = 0;
				_epoch++;
				_workScheduled = false;
				_ackTarget = appliedThrough;
				_lastAckSent = 0;
			}
			EnsureWorkScheduled();
		}

		internal static void Reset()
		{
			lock (Gate)
			{
				Pending.Clear();
				_pendingUpdates = 0;
				_epoch++;
				_workScheduled = false;
				_ackTarget = 0;
				_lastAckSent = 0;
			}
		}

		internal static int PendingCount
		{
			get { lock (Gate) return Pending.Count; }
		}

		internal static long AckTarget
		{
			get { lock (Gate) return _ackTarget; }
		}

		internal static long LastAckSent
		{
			get { lock (Gate) return _lastAckSent; }
		}

		private static void EnsureWorkScheduled()
		{
			Game game = Game.Instance;
			if (game == null)
				return;
			long epoch;
			lock (Gate)
			{
				if (_workScheduled)
					return;
				_workScheduled = true;
				epoch = _epoch;
			}
			game.StartCoroutine(RunNextUnityFrame(epoch));
		}

		private static IEnumerator RunNextUnityFrame(long epoch)
		{
			yield return null;
			RunScheduled(epoch);
		}

		internal static IEnumerator RunNextUnityFrameForTests(long epoch)
			=> RunNextUnityFrame(epoch);

		internal static long EpochForTests
		{
			get { lock (Gate) return _epoch; }
		}

		private static void RunScheduled(long epoch)
		{
			List<long> complete;
			lock (Gate)
			{
				if (epoch != _epoch)
					return;
				_workScheduled = false;
				complete = TakePendingLocked();
			}
			ResolveCompleted(complete);
			TrySendAck();
			bool hasPending;
			lock (Gate)
				hasPending = Pending.Count != 0
				             || MultiplayerSession.IsClient && _ackTarget > _lastAckSent;
			if (hasPending)
				EnsureWorkScheduled();
		}

		private static List<long> TakePendingLocked()
		{
			var complete = new List<long>(Pending.Keys);
			foreach (long sequence in complete)
				_pendingUpdates -= Pending[sequence].UpdateCount;
			Pending.Clear();
			return complete;
		}

		private static void ResolveCompleted(IEnumerable<long> complete)
		{
			// The ACK proves ModifyCell crossed a client frame; the paused hash fence
			// separately proves whether the resulting Grid state matches the host.
			foreach (long sequence in complete)
				WorldUpdatePacket.ResolveRepairSequence(sequence);
		}

		private static void TrySendAck()
		{
			long target;
			lock (Gate)
				target = _ackTarget;
			if (!MultiplayerSession.IsClient || target <= 0)
				return;
			lock (Gate)
			{
				if (target <= _lastAckSent)
					return;
			}
			bool sent = PacketSender.SendToHost(
				    new WorldRepairAckPacket { AppliedThrough = target },
				    PacketSendMode.ReliableImmediate);
			if (ShouldLogAck(target))
				DebugConsole.Log(
					$"[WorldRepairAck][SEND] target={target};sent={(sent ? 1 : 0)}");
			if (!sent)
				return;
			lock (Gate)
				_lastAckSent = Math.Max(_lastAckSent, target);
		}

		private static bool ShouldLogAck(long sequence)
			=> sequence == 1 || sequence % 32 == 0;

	}
}
