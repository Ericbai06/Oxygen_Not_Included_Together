using ONI_Together.DebugTools;
using ONI_Together.Misc.World;
using ONI_Together.Networking.Packets.Architecture;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World
{
	public partial class WorldDataPacket : IPacket, Shared.Interfaces.Networking.IHostOnlyPacket
	{
		internal const int ReliableFragmentPayloadBytes = 980;
		internal const int ReliableAckHistoryMessages = sizeof(ushort) * 8;
		internal const int MaxReliableFragmentsPerPacket = 6;
		internal const int ReliableFixedWireBytes = sizeof(int) * 2;
		internal const int MaxCompressedBytes =
			ReliableFragmentPayloadBytes * MaxReliableFragmentsPerPacket
			- ReliableFixedWireBytes;
		internal const int MaxDecompressedBytes = 64 * 1024 * 1024;
		internal const int MaxChunkCount = 16384;
		internal const int MaxChunkCellCount = 16 * 16;
		internal const int MaxTotalCellCount = 4 * 1024 * 1024;
		internal const int MaxLifecycleBaselineEntries = 1024 * 1024;
		internal const int MaxLifecycleEntriesPerPacket = 32;
		internal const int MaxInFlightReliableFragments =
			WorldDataSendWindow.MaxInFlightChunks * MaxReliableFragmentsPerPacket;
		private const int BytesPerCell = sizeof(ushort) + sizeof(float) + sizeof(float) + sizeof(byte) + sizeof(int);
		private const float MinimumObservationTimeoutSeconds = 10f;

		public static event Action<long> SnapshotApplied;
		public long SnapshotGeneration;
		public long HostSimTick;
		public bool IsFinalChunk;
		public int ChunkIndex;
		public int ChunkCount;
		public int GridChunkCount;
		public int LifecycleBaselineTotalEntries;
		public long WorldUpdateForegroundBaseline;
		public long WorldUpdateRevisionBaseline;
		public long WorldUpdateRepairSequenceBaseline;
		public List<ChunkData> Chunks = new List<ChunkData>();
		public List<NetworkIdentityRegistry.LifecycleRevisionSnapshotEntry> LifecycleBaseline = new();
		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			ValidateTransferMetadata();
			if (WorldUpdateForegroundBaseline < 0)
				throw new InvalidDataException("World update foreground baseline cannot be negative");
			if (WorldUpdateRevisionBaseline < 0)
				throw new InvalidDataException("World update revision baseline cannot be negative");
			if (WorldUpdateRepairSequenceBaseline < 0)
				throw new InvalidDataException("World repair sequence baseline cannot be negative");
			if (!IsFinalChunk && (WorldUpdateForegroundBaseline != 0 || WorldUpdateRevisionBaseline != 0
			    || WorldUpdateRepairSequenceBaseline != 0))
				throw new InvalidDataException("World baselines are only valid on the final world chunk");

			// First write all chunk data into a compressed memory stream
			using (var memoryStream = new MemoryStream())
			{
				using (var compressStream = new DeflateStream(memoryStream, CompressionLevel.Fastest, leaveOpen: true))
				{
					using (var compressWriter = new BinaryWriter(compressStream))
					{
						compressWriter.Write(SnapshotGeneration);
						compressWriter.Write(HostSimTick);
						compressWriter.Write(IsFinalChunk);
						compressWriter.Write(ChunkIndex);
						compressWriter.Write(ChunkCount);
						compressWriter.Write(GridChunkCount);
						compressWriter.Write(LifecycleBaselineTotalEntries);
						compressWriter.Write(Chunks.Count);
						foreach (var chunk in Chunks)
						{
							compressWriter.Write(chunk.TileX);
							compressWriter.Write(chunk.TileY);
							compressWriter.Write(chunk.Width);
							compressWriter.Write(chunk.Height);
							chunk.Serialize(compressWriter);
						}
						WorldLifecycleBaselineCodec.Write(
							compressWriter, LifecycleBaseline, MaxLifecycleEntriesPerPacket);
						if (IsFinalChunk)
						{
							compressWriter.Write(WorldUpdateForegroundBaseline);
							compressWriter.Write(WorldUpdateRevisionBaseline);
							compressWriter.Write(WorldUpdateRepairSequenceBaseline);
						}
					}
				}

				// Write the compressed data length followed by the compressed data
				byte[] buffer = memoryStream.ToArray();
				if (buffer.Length > MaxCompressedBytes)
					throw new InvalidDataException(
						$"World baseline part exceeds {MaxReliableFragmentsPerPacket} reliable fragments");
				writer.Write(buffer.Length);
				writer.Write(buffer);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			byte[] compressedData = ReadCompressedPayload(reader);
			using var memoryStream = new MemoryStream(Decompress(compressedData));
			using var decompressed = new BinaryReader(memoryStream);
			SnapshotGeneration = decompressed.ReadInt64();
			HostSimTick = decompressed.ReadInt64();
			IsFinalChunk = decompressed.ReadBoolean();
			ChunkIndex = decompressed.ReadInt32();
			ChunkCount = decompressed.ReadInt32();
			GridChunkCount = decompressed.ReadInt32();
			LifecycleBaselineTotalEntries = decompressed.ReadInt32();
			int count = decompressed.ReadInt32();
			if (count < 0 || count > 1)
				throw new InvalidDataException($"Invalid world data chunk count: {count}");
			Chunks = new List<ChunkData>(count);
			int totalCells = 0;
			for (int i = 0; i < count; i++)
				Chunks.Add(ReadChunk(decompressed, ref totalCells));
			LifecycleBaseline = WorldLifecycleBaselineCodec.Read(
				decompressed, MaxLifecycleEntriesPerPacket);
			WorldUpdateForegroundBaseline = IsFinalChunk ? decompressed.ReadInt64() : 0;
			if (WorldUpdateForegroundBaseline < 0)
				throw new InvalidDataException("World update foreground baseline cannot be negative");
			WorldUpdateRevisionBaseline = IsFinalChunk ? decompressed.ReadInt64() : 0;
			if (WorldUpdateRevisionBaseline < 0)
				throw new InvalidDataException("World update revision baseline cannot be negative");
			WorldUpdateRepairSequenceBaseline = IsFinalChunk ? decompressed.ReadInt64() : 0;
			if (WorldUpdateRepairSequenceBaseline < 0)
				throw new InvalidDataException("World repair sequence baseline cannot be negative");
			ValidateTransferMetadata();
			if (decompressed.BaseStream.Position != decompressed.BaseStream.Length)
				throw new InvalidDataException("World data payload contains trailing bytes");
		}

		private void ValidateTransferMetadata()
		{
			if (SnapshotGeneration <= 0 || HostSimTick < 0 || GridChunkCount <= 0
			    || GridChunkCount > MaxChunkCount
			    || LifecycleBaselineTotalEntries < 0
			    || LifecycleBaselineTotalEntries > MaxLifecycleBaselineEntries)
				throw new InvalidDataException("Invalid world baseline transfer metadata");
			int lifecyclePages = LifecyclePageCount(LifecycleBaselineTotalEntries);
			int expectedParts = Math.Max(GridChunkCount, lifecyclePages);
			if (ChunkCount != expectedParts || ChunkCount > MaxChunkCount
			    || ChunkIndex < 0 || ChunkIndex >= ChunkCount
			    || IsFinalChunk != (ChunkIndex == ChunkCount - 1)
			    || Chunks == null || LifecycleBaseline == null)
				throw new InvalidDataException("Invalid world baseline part ordering");
			int expectedChunks = ChunkIndex < GridChunkCount ? 1 : 0;
			int expectedLifecycleEntries = ExpectedLifecyclePageEntries(
				LifecycleBaselineTotalEntries, ChunkIndex);
			if (Chunks.Count != expectedChunks
			    || LifecycleBaseline.Count != expectedLifecycleEntries)
				throw new InvalidDataException("Invalid world baseline part shape");
		}

		internal static int LifecyclePageCount(int totalEntries)
			=> totalEntries <= 0
				? 0
				: (totalEntries + MaxLifecycleEntriesPerPacket - 1)
				  / MaxLifecycleEntriesPerPacket;

		private static int ExpectedLifecyclePageEntries(int totalEntries, int pageIndex)
		{
			int start = pageIndex * MaxLifecycleEntriesPerPacket;
			return start >= totalEntries
				? 0
				: Math.Min(MaxLifecycleEntriesPerPacket, totalEntries - start);
		}

		private static byte[] ReadCompressedPayload(BinaryReader reader)
		{
			int length = reader.ReadInt32();
			if (length <= 0 || length > MaxCompressedBytes)
				throw new InvalidDataException($"Invalid world data payload length: {length}");
			byte[] payload = reader.ReadBytes(length);
			if (payload.Length != length)
				throw new EndOfStreamException("World data payload is truncated");
			return payload;
		}

		private static ChunkData ReadChunk(BinaryReader reader, ref int totalCells)
		{
			var chunk = new ChunkData
			{
				TileX = reader.ReadInt32(), TileY = reader.ReadInt32(),
				Width = reader.ReadInt32(), Height = reader.ReadInt32()
			};
			long innerStart = reader.BaseStream.Position;
			int tileX = reader.ReadInt32();
			int tileY = reader.ReadInt32();
			int width = reader.ReadInt32();
			int height = reader.ReadInt32();
			int cellCount = reader.ReadInt32();
			if (tileX != chunk.TileX || tileY != chunk.TileY || width != chunk.Width || height != chunk.Height)
				throw new InvalidDataException("World data chunk headers do not match");
			ValidateChunkDimensions(width, height, cellCount);
			totalCells = checked(totalCells + cellCount);
			if (totalCells > MaxTotalCellCount)
				throw new InvalidDataException($"World data exceeds {MaxTotalCellCount} cells");
			if (reader.BaseStream.Length - reader.BaseStream.Position < (long)cellCount * BytesPerCell)
				throw new EndOfStreamException("World data chunk cells are truncated");
			reader.BaseStream.Position = innerStart;
			chunk.Deserialize(reader);
			return chunk;
		}

		private static void ValidateChunkDimensions(int width, int height, int cellCount)
		{
			long expected = (long)width * height;
			if (width <= 0 || height <= 0 || expected > MaxChunkCellCount || cellCount != expected)
				throw new InvalidDataException($"Invalid world data chunk dimensions: {width}x{height}, cells={cellCount}");
		}

		private static byte[] Decompress(byte[] compressedData)
		{
			using var input = new MemoryStream(compressedData);
			using var deflate = new DeflateStream(input, CompressionMode.Decompress);
			using var output = new MemoryStream();
			var buffer = new byte[8192];
			int total = 0;
			while (true)
			{
				int remaining = MaxDecompressedBytes - total;
				int read = deflate.Read(buffer, 0, remaining < buffer.Length ? remaining + 1 : buffer.Length);
				if (read == 0)
					return output.ToArray();
				total += read;
				if (total > MaxDecompressedBytes)
					throw new InvalidDataException($"World data expands beyond {MaxDecompressedBytes} bytes");
				output.Write(buffer, 0, read);
			}
		}

		private bool TrySubmitSnapshotChunks()
		{
			foreach (ChunkData chunk in Chunks)
			{
				if (!chunk.TryApplyAndCaptureTargets(out List<SnapshotGridCell> targets)
				    || !SnapshotGridObservation.TryAddTargets(SnapshotGeneration, targets))
					return false;
			}
			return true;
		}

		private bool TryCollectLifecyclePage()
		{
			int lifecyclePages = LifecyclePageCount(LifecycleBaselineTotalEntries);
			if (ChunkIndex >= lifecyclePages)
				return LifecycleBaseline.Count == 0;
			return _lifecycleCollector != null
			       && _lifecycleCollector.TryAppend(
				       LifecycleBaseline, ChunkIndex == lifecyclePages - 1);
		}

		private void CompleteObservedSnapshot()
		{
			if (!ReadyManager.IsCurrentClientSnapshot(SnapshotGeneration))
			{
				ResetSnapshotProgress();
				return;
			}
			if (!TryAcceptFinalLifecycleBaseline())
				return;
			if (!SessionStateReset.ResetPresentationForBaseline(HostSimTick, SnapshotGeneration))
			{
				RejectGridBaseline("World baseline presentation state could not be reset.");
				return;
			}
			WorldUpdatePacket.AdvanceClientSupersededRevision(WorldUpdateRevisionBaseline);
			WorldUpdatePacket.SetClientForegroundBaseline(WorldUpdateForegroundBaseline);
			WorldUpdatePacket.SetClientRepairBaseline(WorldUpdateRepairSequenceBaseline);
			long completedGeneration = SnapshotGeneration;
			ResetSnapshotProgress();
			DebugConsole.Log($"[WorldDataPacket] Snapshot {completedGeneration} is observable in Grid.");
			SnapshotApplied?.Invoke(completedGeneration);
		}

		private void RejectUnobservableSnapshot()
			=> RejectGridBaseline("World grid baseline did not become observable before the deadline.");

		private void RejectGridBaseline(string message)
		{
			long rejectedGeneration = SnapshotGeneration;
			ResetSnapshotProgress();
			DebugConsole.LogError($"[WorldDataPacket] {message}");
			GameClient.RejectWorldBaseline(rejectedGeneration, message);
		}

		private bool TryAcceptFinalLifecycleBaseline()
		{
			if (_lifecycleCollector == null || !_lifecycleCollector.IsComplete)
				return false;
			IReadOnlyList<NetworkIdentityRegistry.LifecycleRevisionSnapshotEntry> lifecycle =
				_lifecycleCollector.Entries;
			NetworkIdentityRegistry.LifecycleMembershipValidationResult membership =
				default;
			if (NetworkIdentityRegistry.TryReconcileLifecycleBaseline(
				    lifecycle, out membership))
			{
				PrioritizeStatePacket.ResetClientRevisionState();
				return true;
			}

			long rejectedGeneration = SnapshotGeneration;
			ResetSnapshotProgress();
			DebugConsole.LogError(
				$"[WorldDataPacket] Lifecycle membership rejected: " +
				$"baseline={membership.BaselineValid}, missing={membership.MissingLiveCount}, " +
				$"unexpected={membership.UnexpectedLiveCount}, tombstonedLive={membership.TombstonedLiveCount}, " +
				$"unassigned={membership.UnassignedLiveCount}");
			GameClient.RejectWorldBaseline(
				rejectedGeneration,
				"World identity baseline does not match the loaded save.");
			return false;
		}
	}
}
