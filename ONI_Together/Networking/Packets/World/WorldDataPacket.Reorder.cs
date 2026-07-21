using System;
using System.Collections.Generic;
using System.IO;
using ONI_Together.DebugTools;
using ONI_Together.Misc.World;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World
{
	internal enum WorldDataReorderResult
	{
		Apply,
		Buffered,
		Duplicate,
		Rejected,
	}

	internal sealed class WorldDataReorderDecision
	{
		internal WorldDataReorderResult Result;
		internal IReadOnlyList<WorldDataPacket> ReadyPackets = Array.Empty<WorldDataPacket>();
		internal string Failure;
		internal bool Started;
	}

	internal sealed class WorldDataReorderBuffer
	{
		private const int MaxBufferedChunks = WorldDataSendWindow.MaxInFlightChunks - 1;
		private readonly Dictionary<int, WorldDataPacket> _pending = new();
		private long _generation;
		private int _chunkCount;
		private int _gridChunkCount;
		private int _lifecycleTotal;
		private long _hostSimTick;
		private ulong _senderId;
		private long _connectionGeneration;
		private long _sessionEpoch;
		private int _nextChunkIndex;
		private bool _failed;

		internal WorldDataReorderDecision Enqueue(
			WorldDataPacket packet, DispatchContext context)
		{
			if (packet == null || _failed)
				return Reject("World baseline reorder is unavailable after a prior failure.");
			bool started = _generation == 0;
			if (started)
				Bind(packet, context);
			if (!Matches(packet, context))
				return Reject(MismatchMessage(packet, context));
			if (packet.ChunkIndex < _nextChunkIndex)
				return Decision(WorldDataReorderResult.Duplicate, started);
			if (packet.ChunkIndex == _nextChunkIndex)
				return Ready(packet, started);
			if (_pending.ContainsKey(packet.ChunkIndex))
				return Decision(WorldDataReorderResult.Buffered, started);
			int distance = packet.ChunkIndex - _nextChunkIndex;
			if (distance >= WorldDataSendWindow.MaxInFlightChunks
			    || _pending.Count >= MaxBufferedChunks)
				return Reject(CapacityMessage(packet, distance));
			_pending.Add(packet.ChunkIndex, packet);
			return Decision(WorldDataReorderResult.Buffered, started);
		}

		private WorldDataReorderDecision Ready(WorldDataPacket packet, bool started)
		{
			var ready = new List<WorldDataPacket>(WorldDataSendWindow.MaxInFlightChunks)
			{
				packet
			};
			_nextChunkIndex++;
			while (_pending.Remove(_nextChunkIndex, out WorldDataPacket next))
			{
				ready.Add(next);
				_nextChunkIndex++;
			}
			return new WorldDataReorderDecision
			{
				Result = WorldDataReorderResult.Apply,
				ReadyPackets = ready,
				Started = started,
			};
		}

		private static WorldDataReorderDecision Decision(
			WorldDataReorderResult result, bool started)
			=> new WorldDataReorderDecision { Result = result, Started = started };

		private WorldDataReorderDecision Reject(string failure)
		{
			_failed = true;
			_pending.Clear();
			return new WorldDataReorderDecision
			{
				Result = WorldDataReorderResult.Rejected,
				Failure = failure,
			};
		}

		private void Bind(WorldDataPacket packet, DispatchContext context)
		{
			_generation = packet.SnapshotGeneration;
			_chunkCount = packet.ChunkCount;
			_gridChunkCount = packet.GridChunkCount;
			_lifecycleTotal = packet.LifecycleBaselineTotalEntries;
			_hostSimTick = packet.HostSimTick;
			_senderId = context.SenderId;
			_connectionGeneration = context.ConnectionGeneration;
			_sessionEpoch = context.SessionEpoch;
		}

		private bool Matches(WorldDataPacket packet, DispatchContext context)
			=> packet.SnapshotGeneration == _generation
			   && packet.ChunkCount == _chunkCount
			   && packet.GridChunkCount == _gridChunkCount
			   && packet.LifecycleBaselineTotalEntries == _lifecycleTotal
			   && packet.HostSimTick == _hostSimTick
			   && context.SenderId == _senderId
			   && context.ConnectionGeneration == _connectionGeneration
			   && context.SessionEpoch == _sessionEpoch;

		private string MismatchMessage(WorldDataPacket packet, DispatchContext context)
			=> "World baseline metadata/session mismatch: "
			   + $"expected[generation={_generation},count={_chunkCount},gridCount={_gridChunkCount},"
			   + $"lifecycleTotal={_lifecycleTotal},hostSimTick={_hostSimTick},sender={_senderId},"
			   + $"connectionGeneration={_connectionGeneration},sessionEpoch={_sessionEpoch}] "
			   + $"actual[generation={packet.SnapshotGeneration},count={packet.ChunkCount},"
			   + $"gridCount={packet.GridChunkCount},lifecycleTotal={packet.LifecycleBaselineTotalEntries},"
			   + $"hostSimTick={packet.HostSimTick},sender={context.SenderId},"
			   + $"connectionGeneration={context.ConnectionGeneration},sessionEpoch={context.SessionEpoch}] "
			   + $"expectedChunk={_nextChunkIndex},actualChunk={packet.ChunkIndex},pending={_pending.Count}.";

		private string CapacityMessage(WorldDataPacket packet, int distance)
			=> "World baseline reorder capacity exceeded: "
			   + $"generation={packet.SnapshotGeneration},expectedChunk={_nextChunkIndex},"
			   + $"actualChunk={packet.ChunkIndex},distance={distance},pending={_pending.Count},"
			   + $"capacity={MaxBufferedChunks},window={WorldDataSendWindow.MaxInFlightChunks}.";

		internal bool RequiresGenerationReset(long generation)
			=> _generation != 0 && _generation != generation;

		internal void Reset()
		{
			_pending.Clear();
			_generation = 0;
			_chunkCount = 0;
			_gridChunkCount = 0;
			_lifecycleTotal = 0;
			_hostSimTick = 0;
			_senderId = 0;
			_connectionGeneration = 0;
			_sessionEpoch = 0;
			_nextChunkIndex = 0;
			_failed = false;
		}

		internal int PendingCount => _pending.Count;
		internal bool Failed => _failed;
	}

	public partial class WorldDataPacket
	{
		private static readonly WorldDataReorderBuffer Reorder = new();
		private static int _appliedThroughChunk = -1;
		private static WorldDataLifecycleCollector _lifecycleCollector;

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();
			DispatchContext context = PacketHandler.CurrentContext;
			if (MultiplayerSession.IsHost
			    || !ReadyManager.IsCurrentClientSnapshot(SnapshotGeneration)
			    || !context.SenderIsHost)
				return;
			if (Reorder.RequiresGenerationReset(SnapshotGeneration))
				ResetSnapshotProgress();
			WorldDataReorderDecision decision = EnqueueValidated(this, context);
			if (decision.Result == WorldDataReorderResult.Rejected)
			{
				RejectGridBaseline(decision.Failure);
				return;
			}
			if (decision.Started)
				BeginOrderedApply();
			if (decision.Result == WorldDataReorderResult.Duplicate)
			{
				ResendCumulativeProgress();
				return;
			}
			foreach (WorldDataPacket packet in decision.ReadyPackets)
			{
				if (!packet.ApplyInOrder(out string failure))
				{
					packet.RejectGridBaseline(failure);
					return;
				}
			}
		}

		private static WorldDataReorderDecision EnqueueValidated(
			WorldDataPacket packet, DispatchContext context)
		{
			try
			{
				packet.ValidateTransferMetadata();
				return Reorder.Enqueue(packet, context);
			}
			catch (InvalidDataException exception)
			{
				return new WorldDataReorderDecision
				{
					Result = WorldDataReorderResult.Rejected,
					Failure = packet.InvalidMetadataMessage(context, exception.Message),
				};
			}
		}

		private bool ApplyInOrder(out string failure)
		{
			failure = null;
			if (ChunkIndex != _appliedThroughChunk + 1)
			{
				failure = $"World baseline ordered apply mismatch: expectedChunk={_appliedThroughChunk + 1},"
				          + $"actualChunk={ChunkIndex},generation={SnapshotGeneration}.";
				return false;
			}
			if (!TrySubmitSnapshotChunks() || !TryCollectLifecyclePage())
			{
				failure = $"World baseline state invalid: generation={SnapshotGeneration},"
				          + $"chunk={ChunkIndex},grid={Chunks.Count},lifecycle={LifecycleBaseline.Count}.";
				return false;
			}
			_appliedThroughChunk = ChunkIndex;
			if (!GameClient.RecordWorldBaselineProgress(SnapshotGeneration, ChunkIndex, ChunkCount)
			    || !SendProgressAck(ChunkIndex))
			{
				failure = $"World baseline progress commit failed: generation={SnapshotGeneration},"
				          + $"chunk={ChunkIndex},count={ChunkCount}.";
				return false;
			}
			DebugConsole.Log(
				$"[WorldDataPacket] Applied baseline part {ChunkIndex + 1}/{ChunkCount}: "
				+ $"grid={Chunks.Count}, lifecycle={LifecycleBaseline.Count}.");
			return !IsFinalChunk || TryBeginFinalObservation(out failure);
		}

		private bool TryBeginFinalObservation(out string failure)
		{
			failure = null;
			if (_lifecycleCollector != null && _lifecycleCollector.IsComplete
			    && SnapshotGridObservation.TryObserve(
				    SnapshotGeneration,
				    Math.Max(MinimumObservationTimeoutSeconds, Configuration.Instance.Client.TimeoutSeconds),
				    new SnapshotGridObservationCallbacks
				    {
					    Completed = CompleteObservedSnapshot,
					    TimedOut = RejectUnobservableSnapshot,
				    }))
				return true;
			failure = $"Complete world baseline observation could not start: generation={SnapshotGeneration},"
			          + $"chunk={ChunkIndex},lifecycleComplete={_lifecycleCollector?.IsComplete == true}.";
			return false;
		}

		private bool SendProgressAck(int appliedThrough)
			=> PacketSender.SendToHost(new WorldDataProgressAckPacket
			{
				ClientId = MultiplayerSession.LocalUserID,
				SnapshotGeneration = SnapshotGeneration,
				AppliedThroughChunkIndex = appliedThrough,
			}, PacketSendMode.ReliableImmediate);

		private void ResendCumulativeProgress()
		{
			if (_appliedThroughChunk >= 0 && !SendProgressAck(_appliedThroughChunk))
			{
				RejectGridBaseline(
					$"World baseline cumulative ACK failed: generation={SnapshotGeneration},"
					+ $"duplicateChunk={ChunkIndex},appliedThrough={_appliedThroughChunk}.");
			}
		}

		private void BeginOrderedApply()
		{
			SnapshotGridObservation.BeginCollection(SnapshotGeneration, MaxTotalCellCount);
			_appliedThroughChunk = -1;
			_lifecycleCollector = new WorldDataLifecycleCollector(
				LifecycleBaselineTotalEntries);
		}

		private string InvalidMetadataMessage(DispatchContext context, string reason)
			=> "World baseline metadata invalid: "
			   + $"reason={reason},generation={SnapshotGeneration},chunk={ChunkIndex},count={ChunkCount},"
			   + $"gridCount={GridChunkCount},lifecycleTotal={LifecycleBaselineTotalEntries},"
			   + $"hostSimTick={HostSimTick},sender={context.SenderId},"
			   + $"connectionGeneration={context.ConnectionGeneration},sessionEpoch={context.SessionEpoch}.";

		private static void ResetSnapshotProgress()
		{
			SnapshotGridObservation.Cancel();
			Reorder.Reset();
			_appliedThroughChunk = -1;
			_lifecycleCollector = null;
		}

		internal static void ResetSessionState()
			=> ResetSnapshotProgress();

#if DEBUG
		internal static WorldDataReorderDecision EnqueueForTests(
			WorldDataPacket packet, DispatchContext context)
		{
			if (packet != null && Reorder.RequiresGenerationReset(packet.SnapshotGeneration))
				Reorder.Reset();
			return packet == null
				? new WorldDataReorderDecision
				{
					Result = WorldDataReorderResult.Rejected,
					Failure = "World baseline packet is null.",
				}
				: EnqueueValidated(packet, context);
		}

		internal static int PendingReorderPacketsForTests => Reorder.PendingCount;
		internal static bool ReorderFailedForTests => Reorder.Failed;
		internal static void ResetReorderForTests() => Reorder.Reset();
#endif
	}
}
