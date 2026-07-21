#if DEBUG
using System;
using System.Collections.Generic;
using ONI_Together.Misc.World;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World;

namespace ONI_Together.DebugTools.UnitTests
{
	public static partial class WorldDataPacketTests
	{
		private const long Generation = 41;
		private const long HostSimTick = 1234;
		private static readonly DispatchContext HostContext = new(1, true, 7, 11);

		[UnitTest(name: "World baseline buffers final 208 until missing 207", category: "Networking")]
		public static UnitTestResult WorldBaselineDrainsObserved207Gap()
		{
			WorldDataPacket.ResetReorderForTests();
			try
			{
				const int totalChunks = 209;
				string failure = PrimeThrough(206, totalChunks, Generation, HostContext);
				if (failure != null)
					return UnitTestResult.Fail(failure);

				WorldDataPacket final208 = Packet(208, totalChunks, Generation, marker: 8);
				WorldDataReorderDecision future = WorldDataPacket.EnqueueForTests(final208, HostContext);
				if (WorldDataReorderResult.Buffered != future.Result
				    || 0 != future.ReadyPackets.Count
				    || 1 != WorldDataPacket.PendingReorderPacketsForTests)
				{
					return UnitTestResult.Fail(
						"Final chunk 208 became ready, advanced progress, or failed to buffer before 207");
				}

				WorldDataPacket chunk207 = Packet(207, totalChunks, Generation, marker: 7);
				WorldDataReorderDecision closing = WorldDataPacket.EnqueueForTests(chunk207, HostContext);
				if (WorldDataReorderResult.Apply != closing.Result
				    || !MatchesReady(new[] { chunk207, final208 }, closing.ReadyPackets)
				    || 0 != WorldDataPacket.PendingReorderPacketsForTests)
				{
					return UnitTestResult.Fail("Chunk 207 did not drain 207 and final 208 exactly once in order");
				}
				return UnitTestResult.Pass("Final 208 waits for 207, then both become ready in strict order");
			}
			finally { WorldDataPacket.ResetReorderForTests(); }
		}

		[UnitTest(name: "World baseline past and future duplicates are idempotent", category: "Networking")]
		public static UnitTestResult WorldBaselineDuplicatesKeepFirstPacket()
		{
			WorldDataPacket.ResetReorderForTests();
			try
			{
				const int totalChunks = 210;
				string failure = PrimeThrough(206, totalChunks, Generation, HostContext);
				if (failure != null)
					return UnitTestResult.Fail(failure);

				WorldDataPacket first208 = Packet(208, totalChunks, Generation, marker: 8);
				WorldDataPacket duplicate208 = Packet(208, totalChunks, Generation, marker: 88);
				WorldDataReorderDecision first = WorldDataPacket.EnqueueForTests(first208, HostContext);
				WorldDataReorderDecision duplicate = WorldDataPacket.EnqueueForTests(duplicate208, HostContext);
				if (WorldDataReorderResult.Buffered != first.Result
				    || WorldDataReorderResult.Buffered != duplicate.Result
				    || 0 != first.ReadyPackets.Count || 0 != duplicate.ReadyPackets.Count
				    || 1 != WorldDataPacket.PendingReorderPacketsForTests)
				{
					return UnitTestResult.Fail("A future duplicate applied, multiplied, or displaced pending chunk 208");
				}

				WorldDataPacket chunk207 = Packet(207, totalChunks, Generation, marker: 7);
				WorldDataReorderDecision closing = WorldDataPacket.EnqueueForTests(chunk207, HostContext);
				if (WorldDataReorderResult.Apply != closing.Result
				    || !MatchesReady(new[] { chunk207, first208 }, closing.ReadyPackets)
				    || !ReferenceEquals(first208, closing.ReadyPackets[1]))
				{
					return UnitTestResult.Fail("A future duplicate overwrote the first buffered packet");
				}

				WorldDataReorderDecision past = WorldDataPacket.EnqueueForTests(
					Packet(206, totalChunks, Generation, marker: 66), HostContext);
				WorldDataReorderDecision repeated = WorldDataPacket.EnqueueForTests(
					Packet(208, totalChunks, Generation, marker: 99), HostContext);
				if (WorldDataReorderResult.Duplicate != past.Result
				    || WorldDataReorderResult.Duplicate != repeated.Result
				    || 0 != past.ReadyPackets.Count || 0 != repeated.ReadyPackets.Count)
				{
					return UnitTestResult.Fail("A past or already-drained duplicate became ready again");
				}
				return UnitTestResult.Pass("Past and future duplicates are idempotent and retain the first packet");
			}
			finally { WorldDataPacket.ResetReorderForTests(); }
		}

		[UnitTest(name: "World baseline generation and fixed metadata mismatch fail closed", category: "Networking")]
		public static UnitTestResult WorldBaselineMetadataMismatchFailsClosed()
		{
			if (!RejectsMismatch(packet => packet.SnapshotGeneration++)
			    || !RejectsMismatch(packet => packet.ChunkCount++)
			    || !RejectsMismatch(packet => packet.GridChunkCount++)
			    || !RejectsMismatch(packet => packet.LifecycleBaselineTotalEntries++)
			    || !RejectsMismatch(packet => packet.HostSimTick++)
			    || !RejectsContextMismatch())
			{
				return UnitTestResult.Fail("Generation, fixed metadata, or dispatch context mismatch did not fail closed");
			}
			return UnitTestResult.Pass("Generation, fixed metadata, and dispatch context are immutable per transfer");
		}

		[UnitTest(name: "World baseline reset and generation switch clear pending chunks", category: "Networking")]
		public static UnitTestResult WorldBaselineResetAndGenerationSwitchClearPending()
		{
			WorldDataPacket.ResetReorderForTests();
			try
			{
				const int totalChunks = 4;
				WorldDataPacket.EnqueueForTests(Packet(0, totalChunks, Generation), HostContext);
				WorldDataReorderDecision pending = WorldDataPacket.EnqueueForTests(
					Packet(2, totalChunks, Generation), HostContext);
				if (WorldDataReorderResult.Buffered != pending.Result
				    || 1 != WorldDataPacket.PendingReorderPacketsForTests)
					return UnitTestResult.Fail("Could not arrange a pending chunk before generation switch");
				WorldDataPacket switchedPacket = Packet(0, totalChunks, Generation + 1);
				WorldDataReorderDecision switched = WorldDataPacket.EnqueueForTests(
					switchedPacket, HostContext);
				if (WorldDataReorderResult.Apply != switched.Result
				    || !MatchesReady(new[] { switchedPacket }, switched.ReadyPackets)
				    || 0 != WorldDataPacket.PendingReorderPacketsForTests
				    || WorldDataPacket.ReorderFailedForTests)
				{
					return UnitTestResult.Fail("Generation switch retained or poisoned the prior pending chunk");
				}

				pending = WorldDataPacket.EnqueueForTests(
					Packet(2, totalChunks, Generation + 1), HostContext);
				if (WorldDataReorderResult.Buffered != pending.Result
				    || 1 != WorldDataPacket.PendingReorderPacketsForTests)
					return UnitTestResult.Fail("Could not arrange a pending chunk before explicit reset");
				WorldDataPacket.ResetReorderForTests();
				WorldDataPacket fresh = Packet(0, totalChunks, Generation + 2);
				WorldDataReorderDecision reset = WorldDataPacket.EnqueueForTests(fresh, HostContext);
				if (WorldDataReorderResult.Apply != reset.Result
				    || !MatchesReady(new[] { fresh }, reset.ReadyPackets)
				    || 0 != WorldDataPacket.PendingReorderPacketsForTests
				    || WorldDataPacket.ReorderFailedForTests)
				{
					return UnitTestResult.Fail("Explicit reset retained pending or failed state");
				}
				return UnitTestResult.Pass("Generation switch and explicit reset discard pending chunks");
			}
			finally { WorldDataPacket.ResetReorderForTests(); }
		}

		[UnitTest(name: "World baseline reorder capacity fails closed without eviction", category: "Networking")]
		public static UnitTestResult WorldBaselineReorderCapacityFailsClosed()
		{
			WorldDataPacket.ResetReorderForTests();
			try
			{
				const int totalChunks = 210;
				string failure = PrimeThrough(206, totalChunks, Generation, HostContext);
				if (failure != null)
					return UnitTestResult.Fail(failure);
				WorldDataReorderDecision buffered = WorldDataPacket.EnqueueForTests(
					Packet(208, totalChunks, Generation), HostContext);
				WorldDataReorderDecision overflow = WorldDataPacket.EnqueueForTests(
					Packet(209, totalChunks, Generation), HostContext);
				WorldDataReorderDecision afterFailure = WorldDataPacket.EnqueueForTests(
					Packet(207, totalChunks, Generation), HostContext);
				if (WorldDataReorderResult.Buffered != buffered.Result
				    || WorldDataReorderResult.Rejected != overflow.Result
				    || WorldDataReorderResult.Rejected != afterFailure.Result
				    || 0 != buffered.ReadyPackets.Count || 0 != overflow.ReadyPackets.Count
				    || 0 != afterFailure.ReadyPackets.Count
				    || !WorldDataPacket.ReorderFailedForTests
				    || 0 != WorldDataPacket.PendingReorderPacketsForTests)
				{
					return UnitTestResult.Fail("Out-of-window chunk was applied, evicted 208, or failed to terminate the transfer");
				}
				return UnitTestResult.Pass("Out-of-window 209 terminates without applying or replacing buffered 208");
			}
			finally { WorldDataPacket.ResetReorderForTests(); }
		}

		[UnitTest(name: "World baseline ACK is cumulative monotonic and bounded", category: "Networking")]
		public static UnitTestResult WorldBaselineAckHighWaterIsCumulativeAndBounded()
		{
			var sent = new List<int>();
			var window = new WorldDataSendWindow(5);
			if (!window.TrySendAvailable(index => { sent.Add(index); return true; })
			    || !Matches(new[] { 0, 1 }, sent)
			    || 1 != window.HighestSentChunk || -1 != window.HighestAppliedChunk)
			{
				return UnitTestResult.Fail("Initial host window did not send exactly chunks 0 and 1");
			}

			if (window.AcceptProgress(2)
			    || !window.AcceptProgress(1)
			    || !window.AcceptProgress(1)
			    || !window.AcceptProgress(0)
			    || 1 != window.HighestAppliedChunk)
			{
				return UnitTestResult.Fail("ACK crossed sent/window bounds or duplicate/late cumulative ACK regressed progress");
			}
			if (!window.TrySendAvailable(index => { sent.Add(index); return true; })
			    || !Matches(new[] { 0, 1, 2, 3 }, sent)
			    || !window.AcceptProgress(1)
			    || !window.TrySendAvailable(index => { sent.Add(index); return true; })
			    || !Matches(new[] { 0, 1, 2, 3 }, sent))
			{
				return UnitTestResult.Fail("Duplicate ACK reopened or advanced a full send window");
			}

			if (!window.AcceptProgress(3)
			    || !window.TrySendAvailable(index => { sent.Add(index); return true; })
			    || !Matches(new[] { 0, 1, 2, 3, 4 }, sent)
			    || window.AcceptProgress(5)
			    || !window.AcceptProgress(4)
			    || !window.IsComplete || 4 != window.HighestAppliedChunk)
			{
				return UnitTestResult.Fail("Cumulative high-water did not advance monotonically or reject total overflow");
			}

			var oneChunk = new WorldDataSendWindow(1);
			return oneChunk.TrySendAvailable(_ => true) && !oneChunk.AcceptProgress(1)
				? UnitTestResult.Pass("ACK accepts duplicate/late cumulative values and rejects sent/window/total overflow")
				: UnitTestResult.Fail("One-chunk transfer accepted an ACK beyond its total");
		}

		private static string PrimeThrough(
			int appliedThrough, int totalChunks, long generation, DispatchContext context)
		{
			for (int index = 0; index <= appliedThrough; index++)
			{
				WorldDataPacket packet = Packet(index, totalChunks, generation);
				WorldDataReorderDecision actual = WorldDataPacket.EnqueueForTests(packet, context);
				if (WorldDataReorderResult.Apply != actual.Result
				    || !MatchesReady(new[] { packet }, actual.ReadyPackets))
				{
					return $"Could not establish expected next chunk {appliedThrough + 1}; failed at {index}";
				}
			}
			return null;
		}

		private static bool RejectsMismatch(Action<WorldDataPacket> mutate)
		{
			var buffer = new WorldDataReorderBuffer();
			WorldDataReorderDecision first = buffer.Enqueue(Packet(0, 3, Generation), HostContext);
			WorldDataPacket mismatch = Packet(1, 3, Generation);
			mutate(mismatch);
			WorldDataReorderDecision actual = buffer.Enqueue(mismatch, HostContext);
			return WorldDataReorderResult.Apply == first.Result
			       && WorldDataReorderResult.Rejected == actual.Result && buffer.Failed;
		}

		private static bool RejectsContextMismatch()
		{
			var buffer = new WorldDataReorderBuffer();
			WorldDataReorderDecision first = buffer.Enqueue(Packet(0, 3, Generation), HostContext);
			WorldDataReorderDecision actual = buffer.Enqueue(
				Packet(1, 3, Generation), new DispatchContext(1, true, 8, 11));
			return WorldDataReorderResult.Apply == first.Result
			       && WorldDataReorderResult.Rejected == actual.Result && buffer.Failed;
		}

		private static WorldDataPacket Packet(
			int index, int totalChunks, long generation, ushort marker = 1)
			=> new()
			{
				SnapshotGeneration = generation,
				HostSimTick = HostSimTick,
				IsFinalChunk = index == totalChunks - 1,
				ChunkIndex = index,
				ChunkCount = totalChunks,
				GridChunkCount = totalChunks,
				LifecycleBaselineTotalEntries = 0,
				Chunks = new List<ChunkData> { Chunk(marker) },
				LifecycleBaseline = new List<NetworkIdentityRegistry.LifecycleRevisionSnapshotEntry>(),
			};

		private static ChunkData Chunk(ushort marker)
			=> new()
			{
				TileX = 0, TileY = 0, Width = 1, Height = 1,
				Tiles = new[] { marker }, Temperatures = new[] { 290f },
				Masses = new[] { 1f }, DiseaseIdx = new byte[] { 0 },
				DiseaseCount = new[] { 0 },
			};

		private static bool MatchesReady(
			IReadOnlyList<WorldDataPacket> expected, IReadOnlyList<WorldDataPacket> actual)
		{
			if (expected.Count != actual.Count)
				return false;
			for (int index = 0; index < expected.Count; index++)
				if (!ReferenceEquals(expected[index], actual[index]))
					return false;
			return true;
		}

		private static bool Matches(IReadOnlyList<int> expected, IReadOnlyList<int> actual)
		{
			if (expected.Count != actual.Count)
				return false;
			for (int index = 0; index < expected.Count; index++)
				if (expected[index] != actual[index])
					return false;
			return true;
		}
	}
}
#endif
