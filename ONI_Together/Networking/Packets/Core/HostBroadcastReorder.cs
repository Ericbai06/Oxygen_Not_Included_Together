using System;
using System.Collections.Generic;

namespace ONI_Together.Networking.Packets.Core
{
	internal enum HostBroadcastAcceptResult
	{
		Ignored,
		Buffered,
		Dispatched,
		Failed,
	}

	internal sealed class SequencedRelay<T>
	{
		internal ulong SenderId { get; set; }
		internal long Generation { get; set; }
		internal HostBroadcastPacket.RelayDomain Domain { get; set; }
		internal ulong Sequence { get; set; }
		internal int Bytes { get; set; }
		internal T Value { get; set; }
	}

#if DEBUG
	internal struct HostBroadcastReorderSnapshot
	{
		internal ulong NextExpected { get; set; }
		internal int PendingCount { get; set; }
		internal int PendingBytes { get; set; }
		internal ulong LatestSequence { get; set; }
		internal bool Failed { get; set; }
		internal bool KickIssued { get; set; }
	}
#endif

	internal sealed class HostBroadcastReorder<T>
	{
		internal const ulong MaxGap = 256;
		internal const int MaxPending = 128;
		internal const int MaxPendingBytes = 4 * 1024 * 1024;

		private enum PreparedAction
		{
			Ignored,
			Buffered,
			DrainMustExecute,
			DrainLatestState,
			Kick,
			Failed,
		}

		private sealed class ConnectionState
		{
			internal readonly SortedDictionary<ulong, SequencedRelay<T>> Pending = [];
			internal ulong NextExpected = 1;
			internal int PendingBytes;
			internal float NoProgressSince = -1f;
			internal bool MustExecuteDispatching;
			internal ulong LatestSequence;
			internal SequencedRelay<T> LatestPending;
			internal bool HasLatestPending;
			internal bool LatestStateDispatching;
			internal bool Failed;
			internal bool KickIssued;
		}

		private readonly object _gate = new();
		private readonly Dictionary<(ulong Sender, long Generation), ConnectionState> _states = [];
		private readonly Func<T, bool> _dispatch;
		private readonly Action<ulong> _kick;

		internal HostBroadcastReorder(Func<T, bool> dispatch, Action<ulong> kick)
		{
			_dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
			_kick = kick ?? throw new ArgumentNullException(nameof(kick));
		}

		internal HostBroadcastAcceptResult Accept(SequencedRelay<T> relay, float now)
		{
			if (relay == null || relay.SenderId == 0 || relay.Generation <= 0)
				return HostBroadcastAcceptResult.Ignored;

			PreparedAction action;
			var key = (relay.SenderId, relay.Generation);
			lock (_gate)
				action = PrepareLocked(relay, now);

			if (action == PreparedAction.Kick)
			{
				_kick(relay.SenderId);
				return HostBroadcastAcceptResult.Failed;
			}
			if (action == PreparedAction.DrainMustExecute)
				return DrainMustExecute(key, relay, now);
			if (action == PreparedAction.DrainLatestState)
				return DrainLatestState(key, relay);
			return action == PreparedAction.Buffered
				? HostBroadcastAcceptResult.Buffered
				: action == PreparedAction.Failed
					? HostBroadcastAcceptResult.Failed
					: HostBroadcastAcceptResult.Ignored;
		}

		internal void CheckTimeouts(float now, float timeout)
		{
			var kickSenders = new List<ulong>();
			float minimumTimeout = Math.Max(1f, timeout);
			lock (_gate)
			{
				foreach (var entry in _states)
				{
					ConnectionState state = entry.Value;
					if (state.Failed || state.Pending.Count == 0
					    || state.NoProgressSince < 0f
					    || now - state.NoProgressSince < minimumTimeout)
						continue;
					if (FailLocked(state) == PreparedAction.Kick)
						kickSenders.Add(entry.Key.Sender);
				}
			}
			foreach (ulong senderId in kickSenders)
				_kick(senderId);
		}

		internal void DropConnectionState(ulong senderId, long generation)
		{
			lock (_gate)
				_states.Remove((senderId, generation));
		}

		internal void Reset()
		{
			lock (_gate)
				_states.Clear();
		}

#if DEBUG
		internal bool TryGetSnapshot(
			ulong senderId, long generation, out HostBroadcastReorderSnapshot snapshot)
		{
			lock (_gate)
			{
				if (!_states.TryGetValue((senderId, generation), out ConnectionState state))
				{
					snapshot = default;
					return false;
				}
				snapshot = new HostBroadcastReorderSnapshot
				{
					NextExpected = state.NextExpected,
					PendingCount = state.Pending.Count,
					PendingBytes = state.PendingBytes,
					LatestSequence = state.LatestSequence,
					Failed = state.Failed,
					KickIssued = state.KickIssued,
				};
				return true;
			}
		}
#endif

		private PreparedAction PrepareLocked(SequencedRelay<T> relay, float now)
		{
			ConnectionState state = GetStateLocked(relay.SenderId, relay.Generation);
			if (state.Failed)
				return PreparedAction.Failed;
			if (relay.Sequence == 0 || relay.Bytes < 0)
				return FailLocked(state);
			return relay.Domain switch
			{
				HostBroadcastPacket.RelayDomain.MustExecute
					=> PrepareMustExecuteLocked(state, relay, now),
				HostBroadcastPacket.RelayDomain.LatestState
					=> PrepareLatestStateLocked(state, relay),
				_ => FailLocked(state),
			};
		}

		private ConnectionState GetStateLocked(ulong senderId, long generation)
		{
			var key = (senderId, generation);
			if (!_states.TryGetValue(key, out ConnectionState state))
			{
				state = new ConnectionState();
				_states.Add(key, state);
			}
			return state;
		}

		private PreparedAction PrepareMustExecuteLocked(
			ConnectionState state, SequencedRelay<T> relay, float now)
		{
			if (relay.Sequence < state.NextExpected || state.Pending.ContainsKey(relay.Sequence))
				return PreparedAction.Ignored;
			ulong gap = relay.Sequence - state.NextExpected;
			if (gap > MaxGap)
				return FailLocked(state);
			if (gap == 0 && !state.MustExecuteDispatching)
			{
				state.MustExecuteDispatching = true;
				state.NextExpected++;
				state.NoProgressSince = -1f;
				return PreparedAction.DrainMustExecute;
			}
			if (state.Pending.Count >= MaxPending
			    || relay.Bytes > MaxPendingBytes - state.PendingBytes)
				return FailLocked(state);

			state.Pending.Add(relay.Sequence, relay);
			state.PendingBytes += relay.Bytes;
			if (relay.Sequence > state.NextExpected && state.NoProgressSince < 0f)
				state.NoProgressSince = now;
			return PreparedAction.Buffered;
		}

		private static PreparedAction PrepareLatestStateLocked(
			ConnectionState state, SequencedRelay<T> relay)
		{
			if (relay.Sequence <= state.LatestSequence)
				return PreparedAction.Ignored;
			state.LatestSequence = relay.Sequence;
			if (state.LatestStateDispatching)
			{
				state.LatestPending = relay;
				state.HasLatestPending = true;
				return PreparedAction.Buffered;
			}
			state.LatestStateDispatching = true;
			return PreparedAction.DrainLatestState;
		}

		private HostBroadcastAcceptResult DrainMustExecute(
			(ulong Sender, long Generation) key,
			SequencedRelay<T> relay,
			float now)
		{
			while (true)
			{
				if (!TryDispatch(relay.Value))
				{
					FailAndKick(key);
					return HostBroadcastAcceptResult.Failed;
				}
				if (!TryTakeMustExecute(key, now, out relay))
					return HostBroadcastAcceptResult.Dispatched;
			}
		}

		private bool TryTakeMustExecute(
			(ulong Sender, long Generation) key,
			float now,
			out SequencedRelay<T> relay)
		{
			lock (_gate)
			{
				if (!_states.TryGetValue(key, out ConnectionState state) || state.Failed)
				{
					relay = null;
					return false;
				}
				if (!state.Pending.Remove(state.NextExpected, out relay))
				{
					state.MustExecuteDispatching = false;
					if (state.Pending.Count > 0 && state.NoProgressSince < 0f)
						state.NoProgressSince = now;
					return false;
				}
				state.PendingBytes -= relay.Bytes;
				state.NextExpected++;
				state.NoProgressSince = -1f;
				return true;
			}
		}

		private HostBroadcastAcceptResult DrainLatestState(
			(ulong Sender, long Generation) key, SequencedRelay<T> relay)
		{
			while (true)
			{
				if (!TryDispatch(relay.Value))
				{
					FailAndKick(key);
					return HostBroadcastAcceptResult.Failed;
				}
				lock (_gate)
				{
					if (!_states.TryGetValue(key, out ConnectionState state) || state.Failed)
						return HostBroadcastAcceptResult.Dispatched;
					if (!state.HasLatestPending)
					{
						state.LatestStateDispatching = false;
						return HostBroadcastAcceptResult.Dispatched;
					}
					relay = state.LatestPending;
					state.LatestPending = null;
					state.HasLatestPending = false;
				}
			}
		}

		private bool TryDispatch(T value)
		{
			try
			{
				return _dispatch(value);
			}
			catch
			{
				return false;
			}
		}

		private void FailAndKick((ulong Sender, long Generation) key)
		{
			PreparedAction action;
			lock (_gate)
				action = _states.TryGetValue(key, out ConnectionState state)
					? FailLocked(state)
					: PreparedAction.Ignored;
			if (action == PreparedAction.Kick)
				_kick(key.Sender);
		}

		private static PreparedAction FailLocked(ConnectionState state)
		{
			state.Failed = true;
			state.Pending.Clear();
			state.PendingBytes = 0;
			state.NoProgressSince = -1f;
			state.MustExecuteDispatching = false;
			state.LatestPending = null;
			state.HasLatestPending = false;
			state.LatestStateDispatching = false;
			if (state.KickIssued)
				return PreparedAction.Failed;
			state.KickIssued = true;
			return PreparedAction.Kick;
		}
	}
}
