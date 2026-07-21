using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using ONI_Together.Misc.World;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World;

namespace ONI_Together.DebugTools.UnitTests
{
	public static partial class WorldUpdatePacketTests
	{
		[UnitTest(name: "World repair dispatch freezes at an exact sequence cut", category: "Networking")]
		public static UnitTestResult RepairDispatchFreezesAtExactCut()
		{
			WorldUpdateBatcher.ResetSessionState();
			try
			{
				if (!WorldUpdateBatcher.TryFreezeRepairDispatch(out long cut) || cut != 0
				    || !WorldUpdateBatcher.RepairDispatchPausedForTests)
					return UnitTestResult.Fail("Empty repair stream could not freeze at sequence zero");
				bool queuedWhileFrozen = WorldUpdateBatcher.QueueForTests(
					new WorldUpdatePacket.CellUpdate
				{
					Cell = 11,
					ReplaceType = SimMessages.ReplaceType.Replace,
				}, backgroundRepair: true);
				WorldUpdateBatcher.Flush();
				if (queuedWhileFrozen || WorldUpdateBatcher.TryTakePendingDispatch(
					    out _, out _, requireReadyClients: false))
					return UnitTestResult.Fail("A post-cut repair crossed the frozen hash boundary");
				WorldUpdateBatcher.ResumeRepairDispatch();
				if (!WorldUpdateBatcher.QueueForTests(new WorldUpdatePacket.CellUpdate
				    {
					    Cell = 11,
					    ReplaceType = SimMessages.ReplaceType.Replace,
				    }, backgroundRepair: true))
					return UnitTestResult.Fail("Repair did not become retryable after the checkpoint");
				WorldUpdateBatcher.Flush();
				if (!WorldUpdateBatcher.TryTakePendingDispatch(
					    out WorldUpdatePacket repair, out _, requireReadyClients: false)
				    || repair.RepairSequence != 1)
					return UnitTestResult.Fail("Resumed repair did not receive the next contiguous sequence");
				return UnitTestResult.Pass("Repairs after the cut wait until raw hashing finishes");
			}
			finally
			{
				WorldUpdateBatcher.ResetSessionState();
			}
		}

		[UnitTest(name: "World scan compares every hashed cell field exactly", category: "Networking")]
		public static UnitTestResult DetectsEveryHashedCellField()
		{
			var baseline = new WorldUpdatePacket.CellUpdate
			{
				Cell = 5, ElementIdx = 1, Mass = 1f, Temperature = 290f,
				DiseaseIdx = 2, DiseaseCount = 3
			};
			var changed = baseline;
			changed.Mass += 0.005f;
			if (!WorldStateSyncer.CellStateChanged(baseline, changed))
				return UnitTestResult.Fail("A 5g mass change was ignored");
			changed = baseline;
			changed.Temperature += 0.01f;
			if (!WorldStateSyncer.CellStateChanged(baseline, changed))
				return UnitTestResult.Fail("A temperature-only change was ignored");
			changed = baseline;
			changed.DiseaseIdx++;
			if (!WorldStateSyncer.CellStateChanged(baseline, changed))
				return UnitTestResult.Fail("A disease-index-only change was ignored");
			changed = baseline;
			changed.DiseaseCount++;
			if (!WorldStateSyncer.CellStateChanged(baseline, changed))
				return UnitTestResult.Fail("A disease-count-only change was ignored");
			if (WorldStateSyncer.CellStateChanged(baseline, baseline))
				return UnitTestResult.Fail("Identical cell state was reported as changed");

			return UnitTestResult.Pass("World scan uses the full exact grid hash domain");
		}

		[UnitTest(name: "World baseline covers partial edge chunks", category: "Networking")]
		public static UnitTestResult BaselineCoversPartialEdgeChunks()
		{
			if (WorldDataRequestPacket.ChunkCountForDimension(0) != 0
				|| WorldDataRequestPacket.ChunkCountForDimension(16) != 1
				|| WorldDataRequestPacket.ChunkCountForDimension(32) != 2
				|| WorldDataRequestPacket.ChunkCountForDimension(33) != 3)
			{
				return UnitTestResult.Fail("World baseline omitted a partial edge chunk");
			}

			if (WorldStateSyncer.BackgroundChunkCount(33, 33) != 4)
				return UnitTestResult.Fail("Background scan omitted partial edge chunks");

			return UnitTestResult.Pass("Baseline and background scans include partial edge cells");
		}

		[UnitTest(name: "World baseline binds completion to snapshot generation", category: "Networking")]
		public static UnitTestResult BaselineBindsSnapshotGeneration()
		{
			var packet = new WorldDataPacket
			{
				SnapshotGeneration = 17,
				IsFinalChunk = true,
				ChunkIndex = 2,
				ChunkCount = 3,
				GridChunkCount = 3,
				Chunks = new List<ChunkData> { CreateSingleCellChunk() },
			};
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
				packet.Serialize(writer);
			stream.Position = 0;
			var copy = new WorldDataPacket();
			using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
				copy.Deserialize(reader);

			if (copy.SnapshotGeneration != 17 || !copy.IsFinalChunk
				|| copy.ChunkIndex != 2 || copy.ChunkCount != 3)
				return UnitTestResult.Fail("World baseline completion lost its snapshot generation");
			if (WorldDataRequestPacket.IsValidSnapshotGeneration(17, false))
				return UnitTestResult.Fail("World baseline request accepted a stale snapshot generation");

			return UnitTestResult.Pass("World baseline completion and requests are generation-bound");
		}

		[UnitTest(name: "World baseline final chunk carries lifecycle journal", category: "Networking")]
		public static UnitTestResult FinalChunkCarriesLifecycleJournal()
		{
			var packet = new WorldDataPacket
			{
				SnapshotGeneration = 18,
				WorldUpdateForegroundBaseline = 23,
				WorldUpdateRevisionBaseline = 29,
				WorldUpdateRepairSequenceBaseline = 31,
				IsFinalChunk = true,
				ChunkIndex = 0,
				ChunkCount = 1,
				GridChunkCount = 1,
				LifecycleBaselineTotalEntries = 2,
				Chunks = new List<ChunkData> { CreateSingleCellChunk() },
				LifecycleBaseline = new List<NetworkIdentityRegistry.LifecycleRevisionSnapshotEntry>
				{
					new(-101, 20, false, new SpawnPrefabPacket
					{
						NetId = -101,
						Revision = 20,
						Hash = 101,
						Position = new UnityEngine.Vector3(1f, 2f, 0f),
						WorldId = 0,
						IsActive = true,
					}),
					new(-102, 21, true),
				}
			};
			WorldDataPacket copy = Roundtrip(packet);
			if (copy.WorldUpdateForegroundBaseline != 23
			    || copy.WorldUpdateRevisionBaseline != 29
			    || copy.WorldUpdateRepairSequenceBaseline != 31
			    || copy.LifecycleBaseline.Count != 2
			    || copy.LifecycleBaseline[0].NetId != -101
			    || copy.LifecycleBaseline[0].Revision != 20
			    || copy.LifecycleBaseline[0].Tombstoned
			    || copy.LifecycleBaseline[0].Descriptor?.NetId != -101
			    || copy.LifecycleBaseline[0].Descriptor.Hash != 101
			    || copy.LifecycleBaseline[1].NetId != -102
			    || copy.LifecycleBaseline[1].Revision != 21
			    || !copy.LifecycleBaseline[1].Tombstoned)
				return UnitTestResult.Fail("Final world baseline lost lifecycle journal state");

			return UnitTestResult.Pass("Lifecycle journal page roundtrips with its exact total");
		}

		[UnitTest(name: "World baseline rejects oversized lifecycle journal", category: "Networking")]
		public static UnitTestResult RejectsOversizedLifecycleJournal()
		{
			using MemoryStream stream = CreateLifecyclePayload(WorldDataPacket.MaxLifecycleBaselineEntries + 1);
			try
			{
				using var reader = new BinaryReader(stream);
				new WorldDataPacket().Deserialize(reader);
				return UnitTestResult.Fail("Oversized lifecycle journal was accepted");
			}
			catch (InvalidDataException)
			{
				return UnitTestResult.Pass("Oversized lifecycle journal is rejected before allocation");
			}
		}

		private static WorldDataPacket Roundtrip(WorldDataPacket packet)
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
				packet.Serialize(writer);
			stream.Position = 0;
			var copy = new WorldDataPacket();
			using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
				copy.Deserialize(reader);
			return copy;
		}

		private static MemoryStream CreateLifecyclePayload(int lifecycleCount)
		{
			using var payload = new MemoryStream();
			using (var deflate = new DeflateStream(payload, CompressionLevel.Fastest, true))
			using (var writer = new BinaryWriter(deflate))
			{
				writer.Write(18L);
				writer.Write(0L);
				writer.Write(true);
				writer.Write(0);
				writer.Write(1);
				writer.Write(1);
				writer.Write(lifecycleCount);
				writer.Write(0);
				writer.Write(0);
				writer.Write(0L);
				writer.Write(0L);
				writer.Write(0L);
			}
			var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
			{
				writer.Write(checked((int)payload.Length));
				writer.Write(payload.ToArray());
			}
			stream.Position = 0;
			return stream;
		}

		private static ChunkData CreateSingleCellChunk()
			=> new()
			{
				TileX = 0, TileY = 0, Width = 1, Height = 1,
				Tiles = new ushort[] { 1 }, Temperatures = new float[] { 290f },
				Masses = new float[] { 1f }, DiseaseIdx = new byte[] { 0 },
				DiseaseCount = new int[] { 0 },
			};
	}
}
