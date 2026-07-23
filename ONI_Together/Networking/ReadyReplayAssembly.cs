using System;
using System.Collections.Generic;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;

namespace ONI_Together.Networking
{
	internal enum ReadyReplayAssemblyResult
	{
		Pending,
		Duplicate,
		Ready,
		Rejected,
		Terminal,
	}

	internal struct ReadyReplayAssemblyLimits
	{
		internal float StartedAt;
		internal float IdleSeconds;
		internal float AbsoluteSeconds;
		internal int MaxBufferedBytes;

		internal bool IsValid()
			=> IsFinite(StartedAt) && IsPositiveFinite(IdleSeconds)
			   && IsPositiveFinite(AbsoluteSeconds)
			   && MaxBufferedBytes > 0
			   && MaxBufferedBytes <= ReliableSyncBacklog.MaxBytesPerClient;

		private static bool IsPositiveFinite(float value)
			=> value > 0f && IsFinite(value);

		private static bool IsFinite(float value)
			=> !float.IsNaN(value) && !float.IsInfinity(value);
	}

	internal sealed class ReadyReplayApply
	{
		internal ReadyReplayProof Proof;
		internal IReadOnlyList<byte[][]> Batches = Array.Empty<byte[][]>();
	}

	internal sealed class ReadyReplayAssembly
	{
		private readonly ulong _expectedToken;
		private readonly long _expectedGeneration;
		private readonly ReadyReplayAssemblyLimits _limits;
		private readonly float _absoluteDeadline;
		private float _idleDeadline;
		private byte[][][] _batches = Array.Empty<byte[][]>();
		private ReadyReplayProof _proof;
		private ulong _replayId;
		private ulong _senderId;
		private long _connectionGeneration;
		private long _sessionEpoch;
		private int _batchCount;
		private int _receivedBatchCount;
		private int _bufferedFrameCount;
		private int _bufferedBytes;
		private bool _identityBound;
		private bool _commitReceived;
		private bool _applying;
		private bool _applied;
		private bool _failed;

		internal ReadyReplayAssembly(
			ulong reconnectToken, long snapshotGeneration, ReadyReplayAssemblyLimits limits)
		{
			if (reconnectToken == 0 || snapshotGeneration <= 0 || !limits.IsValid())
				throw new ArgumentOutOfRangeException();
			_expectedToken = reconnectToken;
			_expectedGeneration = snapshotGeneration;
			_limits = limits;
			_idleDeadline = limits.StartedAt + limits.IdleSeconds;
			_absoluteDeadline = limits.StartedAt + limits.AbsoluteSeconds;
		}

		internal ReadyReplayAssemblyResult AcceptBatch(
			ReadyReplayBatchHeader header,
			IReadOnlyList<byte[]> frames,
			(DispatchContext context, float now) arrival)
		{
			if (_failed)
				return ReadyReplayAssemblyResult.Terminal;
			if (!header.IsValid() || header.SnapshotGeneration != _expectedGeneration
			    || !TryMeasureBatch(frames, out int batchBytes))
				return ReadyReplayAssemblyResult.Rejected;
			if (IsTimedOut(arrival.now))
				return Fail();
			ReadyReplayAssemblyResult identity = MatchOrBind(
				header.ReplayId, header.BatchCount, arrival.context);
			if (identity != ReadyReplayAssemblyResult.Pending)
				return identity;
			byte[][] existing = _batches[header.BatchIndex];
			if (existing != null)
				return AcceptDuplicate(existing, frames, arrival.now);
			if (frames.Count > DeferredReliableBatchPacket.MaxFrames - _bufferedFrameCount
			    || batchBytes > _limits.MaxBufferedBytes - _bufferedBytes)
				return Fail();
			_batches[header.BatchIndex] = CloneBatch(frames);
			_bufferedFrameCount += frames.Count;
			_bufferedBytes += batchBytes;
			_receivedBatchCount++;
			RefreshLease(arrival.now);
			return IsReady ? ReadyReplayAssemblyResult.Ready : ReadyReplayAssemblyResult.Pending;
		}

		internal ReadyReplayAssemblyResult AcceptCommit(
			ReadyReplayProof proof, DispatchContext context, float now)
		{
			if (_failed)
				return ReadyReplayAssemblyResult.Terminal;
			if (!proof.IsValid() || proof.ReconnectToken != _expectedToken
			    || proof.SnapshotGeneration != _expectedGeneration)
				return ReadyReplayAssemblyResult.Rejected;
			if (IsTimedOut(now))
				return Fail();
			ReadyReplayAssemblyResult identity = MatchOrBind(
				proof.ReplayId, proof.BatchCount, context);
			if (identity != ReadyReplayAssemblyResult.Pending)
				return identity;
			if (_commitReceived)
			{
				if (!proof.Matches(_proof))
					return Fail();
				RefreshLease(now);
				return ReadyReplayAssemblyResult.Duplicate;
			}
			_proof = proof;
			_commitReceived = true;
			RefreshLease(now);
			return IsReady ? ReadyReplayAssemblyResult.Ready : ReadyReplayAssemblyResult.Pending;
		}

		internal bool TryBeginApply(out ReadyReplayApply apply)
		{
			apply = null;
			if (!IsReady || _applying || _applied || _failed)
				return false;
			_applying = true;
			apply = new ReadyReplayApply { Proof = _proof, Batches = _batches };
			return true;
		}

		internal bool CompleteApply(bool succeeded)
		{
			if (!_applying || _failed || _applied)
				return false;
			_applying = false;
			if (ShouldRollbackApply(succeeded))
			{
				Fail();
				return true;
			}
			_applied = true;
			return true;
		}

		internal static bool ShouldRollbackApply(bool succeeded) => !succeeded;

		internal bool IsTimedOut(float now)
			=> !IsFinite(now) || now >= _idleDeadline || now >= _absoluteDeadline;

		internal void Reset()
		{
			_batches = Array.Empty<byte[][]>();
			_proof = default;
			_replayId = 0;
			_senderId = 0;
			_connectionGeneration = 0;
			_sessionEpoch = 0;
			_batchCount = 0;
			_receivedBatchCount = 0;
			_bufferedFrameCount = 0;
			_bufferedBytes = 0;
			_identityBound = false;
			_commitReceived = false;
			_applying = false;
			_applied = false;
			_failed = false;
			_idleDeadline = _limits.StartedAt + _limits.IdleSeconds;
		}

		private ReadyReplayAssemblyResult MatchOrBind(
			ulong replayId, int batchCount, DispatchContext context)
		{
			if (!context.SenderIsHost || context.ConnectionGeneration <= 0)
				return ReadyReplayAssemblyResult.Rejected;
			if (!_identityBound)
			{
				BindIdentity(replayId, batchCount, context);
				return ReadyReplayAssemblyResult.Pending;
			}
			if (!MatchesContext(context) || replayId != _replayId)
				return ReadyReplayAssemblyResult.Rejected;
			return batchCount == _batchCount
				? ReadyReplayAssemblyResult.Pending
				: Fail();
		}

		private void BindIdentity(
			ulong replayId, int batchCount, DispatchContext context)
		{
			_replayId = replayId;
			_batchCount = batchCount;
			_senderId = context.SenderId;
			_connectionGeneration = context.ConnectionGeneration;
			_sessionEpoch = context.SessionEpoch;
			_batches = new byte[batchCount][][];
			_identityBound = true;
		}

		private bool MatchesContext(DispatchContext context)
			=> context.SenderIsHost && context.SenderId == _senderId
			   && context.ConnectionGeneration == _connectionGeneration
			   && context.SessionEpoch == _sessionEpoch;

		private ReadyReplayAssemblyResult AcceptDuplicate(
			IReadOnlyList<byte[]> stored, IReadOnlyList<byte[]> incoming, float now)
		{
			if (!FramesEqual(stored, incoming))
				return Fail();
			RefreshLease(now);
			return ReadyReplayAssemblyResult.Duplicate;
		}

		private static bool TryMeasureBatch(
			IReadOnlyList<byte[]> frames, out int bufferedBytes)
		{
			bufferedBytes = 0;
			if (frames == null || frames.Count <= 0
			    || frames.Count > DeferredReliableBatchPacket.MaxFrames)
				return false;
			int wireBytes = DeferredReliableBatchPacket.FixedWireBytes;
			try
			{
				foreach (byte[] frame in frames)
				{
					if (frame == null || frame.Length < sizeof(int)
					    || PacketHandler.IsForbiddenReadyReplayFrame(frame))
						return false;
					int cost = checked(sizeof(int) + frame.Length);
					wireBytes = checked(wireBytes + cost);
					bufferedBytes = checked(bufferedBytes + cost);
				}
				return wireBytes <= DeferredReliableBatchPacket.MaxWireBytes;
			}
			catch (OverflowException)
			{
				return false;
			}
		}

		private static byte[][] CloneBatch(IReadOnlyList<byte[]> frames)
		{
			var clone = new byte[frames.Count][];
			for (int index = 0; index < frames.Count; index++)
			{
				clone[index] = new byte[frames[index].Length];
				Buffer.BlockCopy(frames[index], 0, clone[index], 0, frames[index].Length);
			}
			return clone;
		}

		private static bool FramesEqual(
			IReadOnlyList<byte[]> left, IReadOnlyList<byte[]> right)
		{
			if (left == null || right == null || left.Count != right.Count)
				return false;
			for (int frameIndex = 0; frameIndex < left.Count; frameIndex++)
			{
				byte[] a = left[frameIndex];
				byte[] b = right[frameIndex];
				if (a == null || b == null || a.Length != b.Length)
					return false;
				for (int byteIndex = 0; byteIndex < a.Length; byteIndex++)
					if (a[byteIndex] != b[byteIndex])
						return false;
			}
			return true;
		}

		private ReadyReplayAssemblyResult Fail()
		{
			_failed = true;
			_applying = false;
			_batches = Array.Empty<byte[][]>();
			_bufferedFrameCount = 0;
			_bufferedBytes = 0;
			return ReadyReplayAssemblyResult.Terminal;
		}

		private void RefreshLease(float now)
			=> _idleDeadline = Math.Min(_absoluteDeadline, now + _limits.IdleSeconds);

		private bool IsReady
			=> _commitReceived && _receivedBatchCount == _batchCount;

		private static bool IsFinite(float value)
			=> !float.IsNaN(value) && !float.IsInfinity(value);

#if DEBUG
		internal static ReadyReplayAssembly CreateForTests(
			ulong token, long generation, ReadyReplayAssemblyLimits limits)
			=> new ReadyReplayAssembly(token, generation, limits);

		internal int BufferedBytesForTests => _bufferedBytes;
		internal int ReceivedBatchCountForTests => _receivedBatchCount;
		internal bool FailedForTests => _failed;
		internal bool AppliedForTests => _applied;
#endif
	}
}
