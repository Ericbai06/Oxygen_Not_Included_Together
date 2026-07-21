using System.Collections.Generic;
using System.Threading;
using ONI_Together.Misc.World;
using ONI_Together.Networking.Packets.Architecture;

namespace ONI_Together.Networking.Packets.World
{
	public partial class WorldUpdatePacket
	{
		internal enum ForegroundReorderResult
		{
			Accepted,
			Buffered,
			Ignored,
			CapacityExceeded,
			SessionMismatch,
			Failed,
		}

		internal const int MaxForegroundSequenceGap = 256;
		internal const int MaxPendingForegroundPackets = 128;
		internal const int MaxPendingForegroundUpdates = 65536;
		internal const int MaxPendingRepairPackets = 128;
		internal const int MaxPendingRepairUpdates = 65536;
		internal const int MaxResolvedRepairGaps = 16384;
		private static long _nextHostRevision;
		private static long _nextHostForegroundSequence;
		private static long _nextHostRepairDispatchSequence;
		private static long _clientSupersededRevision;
		private static long _clientForegroundSequence;
		private static long _clientResolvedRepairSequence;
		private static bool _clientForegroundInitialized;
		private static readonly SortedDictionary<long, WorldUpdatePacket> PendingForeground = new();
		private static int _pendingForegroundUpdates;
		private static float _foregroundGapStartedAt = float.PositiveInfinity;
		private static long _foregroundConnectionGeneration;
		private static long _foregroundSessionEpoch;
		private static bool _foregroundReorderFailed;
		private static readonly Dictionary<int, long> ClientCellRevisions = new();
		private static readonly SortedDictionary<long, WorldUpdatePacket> PendingRepairs = new();
		private static readonly SortedSet<long> ResolvedRepairGaps = new();
		private static int _pendingRepairUpdates;
		private static readonly object ClientStateLock = new();

		internal static long NextHostRevision()
		{
			long revision = Interlocked.Increment(ref _nextHostRevision);
			if (revision > 0)
				return revision;
			Interlocked.Exchange(ref _nextHostRevision, 1);
			return 1;
		}

		internal static long CurrentHostRevision => Interlocked.Read(ref _nextHostRevision);

		internal static long NextHostForegroundSequence()
		{
			long sequence = Interlocked.Increment(ref _nextHostForegroundSequence);
			if (sequence > 0)
				return sequence;
			Interlocked.Exchange(ref _nextHostForegroundSequence, 1);
			return 1;
		}

		internal static long CurrentHostForegroundSequence
			=> Interlocked.Read(ref _nextHostForegroundSequence);

		internal static long NextHostRepairDispatchSequence()
		{
			long sequence = Interlocked.Increment(ref _nextHostRepairDispatchSequence);
			if (sequence > 0)
				return sequence;
			Interlocked.Exchange(ref _nextHostRepairDispatchSequence, 1);
			return 1;
		}

		internal static long CurrentHostRepairDispatchSequence
			=> Interlocked.Read(ref _nextHostRepairDispatchSequence);

		internal static long ClientSupersededRevision
			=> Interlocked.Read(ref _clientSupersededRevision);

		internal static void AdvanceClientSupersededRevision(long revision)
		{
			if (revision < 0)
				throw new System.ArgumentOutOfRangeException(nameof(revision));
			long current = Interlocked.Read(ref _clientSupersededRevision);
			while (revision > current)
			{
				long observed = Interlocked.CompareExchange(
					ref _clientSupersededRevision, revision, current);
				if (observed == current)
					break;
				current = observed;
			}
			DropSupersededRepairs(revision);
		}

		private static void DropSupersededRepairs(long revision)
		{
			var repairSequences = new List<long>();
			lock (ClientStateLock)
			{
				var superseded = new List<long>();
				foreach (var entry in PendingRepairs)
				{
					if (entry.Key <= revision)
						superseded.Add(entry.Key);
				}
				foreach (long key in superseded)
				{
					repairSequences.Add(PendingRepairs[key].RepairSequence);
					_pendingRepairUpdates -= PendingRepairs[key].Updates.Count;
					PendingRepairs.Remove(key);
				}
			}
			foreach (long repairSequence in repairSequences)
				ResolveRepairSequence(repairSequence);
		}

		internal static void ResetRevisionState()
		{
			Interlocked.Exchange(ref _nextHostRevision, 0);
			Interlocked.Exchange(ref _nextHostForegroundSequence, 0);
			Interlocked.Exchange(ref _nextHostRepairDispatchSequence, 0);
			Interlocked.Exchange(ref _clientSupersededRevision, 0);
			lock (ClientStateLock)
			{
				_clientForegroundSequence = 0;
				_clientResolvedRepairSequence = 0;
				_clientForegroundInitialized = false;
				ResetForegroundReorderLocked();
				ClientCellRevisions.Clear();
				PendingRepairs.Clear();
				ResolvedRepairGaps.Clear();
				_pendingRepairUpdates = 0;
			}
			WorldUpdateRepairObservability.Reset();
		}

		internal static ForegroundSequenceResult AcceptForegroundSequence(long sequence)
		{
			if (sequence <= 0)
				return ForegroundSequenceResult.Gap;
			lock (ClientStateLock)
				return AcceptForegroundSequenceLocked(sequence);
		}

		private static ForegroundSequenceResult AcceptForegroundSequenceLocked(long sequence)
		{
			if (!_clientForegroundInitialized)
			{
				_clientForegroundInitialized = true;
				_clientForegroundSequence = sequence;
				return ForegroundSequenceResult.Accepted;
			}
			if (sequence <= _clientForegroundSequence)
				return ForegroundSequenceResult.Superseded;
			if (sequence != _clientForegroundSequence + 1)
				return ForegroundSequenceResult.Gap;
			_clientForegroundSequence = sequence;
			return ForegroundSequenceResult.Accepted;
		}

		internal static bool TryAcceptForegroundSequence(long sequence)
			=> AcceptForegroundSequence(sequence) == ForegroundSequenceResult.Accepted;

		internal static long CurrentClientForegroundSequence
		{
			get { lock (ClientStateLock) return _clientForegroundSequence; }
		}

		internal static void SetClientForegroundBaseline(long foregroundCut)
		{
			if (foregroundCut < 0)
				throw new System.ArgumentOutOfRangeException(nameof(foregroundCut));
			lock (ClientStateLock)
			{
				_clientForegroundSequence = foregroundCut;
				_clientForegroundInitialized = true;
				ResetForegroundReorderLocked();
				PendingRepairs.Clear();
				_pendingRepairUpdates = 0;
			}
		}

		internal static void SetClientRepairBaseline(long repairSequenceCut)
		{
			if (repairSequenceCut < 0)
				throw new System.ArgumentOutOfRangeException(nameof(repairSequenceCut));
			lock (ClientStateLock)
			{
				_clientResolvedRepairSequence = repairSequenceCut;
				ResolvedRepairGaps.RemoveWhere(sequence => sequence <= repairSequenceCut);
				while (ResolvedRepairGaps.Remove(_clientResolvedRepairSequence + 1))
					_clientResolvedRepairSequence++;
			}
			WorldUpdateRepairObservability.SetBaseline(repairSequenceCut);
		}

		internal static long ClientResolvedRepairSequence
		{
			get { lock (ClientStateLock) return _clientResolvedRepairSequence; }
		}

		internal static bool ResolveRepairSequence(long repairSequence)
		{
			long previous;
			long appliedThrough;
			bool accepted;
			lock (ClientStateLock)
			{
				previous = _clientResolvedRepairSequence;
				accepted = ResolveRepairSequenceLocked(repairSequence);
				appliedThrough = _clientResolvedRepairSequence;
			}
			if (appliedThrough > previous)
				WorldUpdateRepairObservability.NotifyResolved(appliedThrough);
			return accepted;
		}

		private static bool ResolveRepairSequenceLocked(long repairSequence)
		{
			if (repairSequence <= 0)
				return false;
			if (repairSequence <= _clientResolvedRepairSequence)
				return true;
			if (repairSequence == _clientResolvedRepairSequence + 1)
			{
				_clientResolvedRepairSequence = repairSequence;
				while (ResolvedRepairGaps.Remove(_clientResolvedRepairSequence + 1))
					_clientResolvedRepairSequence++;
				return true;
			}
			if (ResolvedRepairGaps.Count >= MaxResolvedRepairGaps)
				return false;
			ResolvedRepairGaps.Add(repairSequence);
			return true;
		}

		internal static bool ShouldDeferRepair(long foregroundCut)
		{
			if (foregroundCut < 0)
				return false;
			lock (ClientStateLock)
				return foregroundCut > 0 && (!_clientForegroundInitialized
				       || foregroundCut > _clientForegroundSequence);
		}

		internal static bool TryAcceptCellRevision(
			int cell, long revision, bool backgroundRepair)
		{
			if (cell < 0 || revision <= 0)
				return false;
			lock (ClientStateLock)
			{
				ClientCellRevisions.TryGetValue(cell, out long previous);
				if (backgroundRepair && revision < previous)
					return false;
				if (revision > previous)
					ClientCellRevisions[cell] = revision;
				return true;
			}
		}

		internal static long GetClientCellRevision(int cell)
		{
			lock (ClientStateLock)
				return ClientCellRevisions.TryGetValue(cell, out long revision) ? revision : 0;
		}

		internal static bool DeferRepair(WorldUpdatePacket packet)
		{
			if (packet == null || !packet.IsBackgroundRepair || packet.Revision <= 0
			    || packet.RepairSequence <= 0
			    || packet.Updates.Count > MaxPendingRepairUpdates)
				return false;
			lock (ClientStateLock)
			{
				if (PendingRepairs.ContainsKey(packet.Revision))
					return true;
				if (PendingRepairs.Count >= MaxPendingRepairPackets
				    || _pendingRepairUpdates > MaxPendingRepairUpdates - packet.Updates.Count)
					return false;
				PendingRepairs.Add(packet.Revision, packet);
				_pendingRepairUpdates += packet.Updates.Count;
				return true;
			}
		}

		internal static int PendingRepairPacketCount
		{
			get { lock (ClientStateLock) return PendingRepairs.Count; }
		}

		internal static int PendingRepairUpdateCount
		{
			get { lock (ClientStateLock) return _pendingRepairUpdates; }
		}

		private ForegroundReorderResult QueueForeground(
			float receivedAt, DispatchContext context, out WorldUpdatePacket ready)
		{
			ready = null;
			lock (ClientStateLock)
			{
				if (_foregroundReorderFailed)
					return ForegroundReorderResult.Failed;
				if (!BindForegroundSessionLocked(context))
					return ForegroundReorderResult.SessionMismatch;
				ForegroundSequenceResult result = AcceptForegroundSequenceLocked(Sequence);
				if (result == ForegroundSequenceResult.Accepted)
				{
					if (PendingForeground.Count != 0)
						_foregroundGapStartedAt = receivedAt;
					ready = this;
					return ForegroundReorderResult.Accepted;
				}
				if (result == ForegroundSequenceResult.Superseded
				    || PendingForeground.ContainsKey(Sequence))
					return ForegroundReorderResult.Ignored;
				if (Sequence - _clientForegroundSequence > MaxForegroundSequenceGap
				    || PendingForeground.Count >= MaxPendingForegroundPackets
				    || _pendingForegroundUpdates > MaxPendingForegroundUpdates - Updates.Count)
					return ForegroundReorderResult.CapacityExceeded;
				PendingForeground.Add(Sequence, this);
				_pendingForegroundUpdates += Updates.Count;
				if (PendingForeground.Count == 1)
					_foregroundGapStartedAt = receivedAt;
				return ForegroundReorderResult.Buffered;
			}
		}

		private static WorldUpdatePacket TakeNextPendingForeground(float progressedAt)
		{
			lock (ClientStateLock)
			{
				long next = _clientForegroundSequence + 1;
				if (!PendingForeground.TryGetValue(next, out WorldUpdatePacket packet))
					return null;
				PendingForeground.Remove(next);
				_pendingForegroundUpdates -= packet.Updates.Count;
				_foregroundGapStartedAt = PendingForeground.Count == 0
					? float.PositiveInfinity : progressedAt;
				return AcceptForegroundSequenceLocked(next) == ForegroundSequenceResult.Accepted
					? packet : null;
			}
		}

		private static bool BindForegroundSessionLocked(DispatchContext context)
		{
			if (_foregroundConnectionGeneration == 0 && _foregroundSessionEpoch == 0)
			{
				_foregroundConnectionGeneration = context.ConnectionGeneration;
				_foregroundSessionEpoch = context.SessionEpoch;
				return true;
			}
			return _foregroundConnectionGeneration == context.ConnectionGeneration
			       && _foregroundSessionEpoch == context.SessionEpoch;
		}

		internal static void CheckForegroundReorderTimeout(float now)
		{
			float timeout = System.Math.Max(1f, Configuration.Instance.Client.TimeoutSeconds);
			CheckForegroundReorderTimeoutCore(now, timeout);
		}

		private static void CheckForegroundReorderTimeoutCore(float now, float timeout)
		{
			lock (ClientStateLock)
			{
				if (_foregroundReorderFailed || PendingForeground.Count == 0
				    || now - _foregroundGapStartedAt < timeout)
					return;
			}
			FailForegroundReorder(
				$"missing sequence after {CurrentClientForegroundSequence} for {timeout:0}s");
		}

		private static void FailForegroundReorder(string reason)
		{
			lock (ClientStateLock)
			{
				if (_foregroundReorderFailed)
					return;
				_foregroundReorderFailed = true;
				ClearPendingForegroundLocked();
			}
			ONI_Together.DebugTools.DebugConsole.LogError(
				$"[WorldUpdatePacket] Foreground reorder failed: {reason}; disconnecting.", false);
			NetworkConfig.TransportClient?.Disconnect();
		}

		private static void ResetForegroundReorderLocked()
		{
			ClearPendingForegroundLocked();
			_foregroundConnectionGeneration = 0;
			_foregroundSessionEpoch = 0;
			_foregroundReorderFailed = false;
		}

		private static void ClearPendingForegroundLocked()
		{
			PendingForeground.Clear();
			_pendingForegroundUpdates = 0;
			_foregroundGapStartedAt = float.PositiveInfinity;
		}

		internal static int PendingForegroundPacketCount
		{
			get { lock (ClientStateLock) return PendingForeground.Count; }
		}

		internal static int PendingForegroundUpdateCount
		{
			get { lock (ClientStateLock) return _pendingForegroundUpdates; }
		}

#if DEBUG
		internal ForegroundReorderResult QueueForegroundForTests(
			float receivedAt, DispatchContext context, out List<WorldUpdatePacket> readyPackets)
		{
			readyPackets = new List<WorldUpdatePacket>();
			ForegroundReorderResult result = QueueForeground(
				receivedAt, context, out WorldUpdatePacket ready);
			if (result is ForegroundReorderResult.CapacityExceeded
			    or ForegroundReorderResult.SessionMismatch
			    or ForegroundReorderResult.Failed)
			{
				FailForegroundReorder(result.ToString());
				return result;
			}
			while (ready != null)
			{
				readyPackets.Add(ready);
				ready = TakeNextPendingForeground(receivedAt);
			}
			return result;
		}

		internal static bool ForegroundReorderFailedForTests
		{
			get { lock (ClientStateLock) return _foregroundReorderFailed; }
		}

		internal static void CheckForegroundReorderTimeoutForTests(float now, float timeout)
		{
			if (timeout <= 0f || float.IsNaN(timeout) || float.IsInfinity(timeout))
				throw new System.ArgumentOutOfRangeException(nameof(timeout));
			CheckForegroundReorderTimeoutCore(now, timeout);
		}
#endif

	}
}
