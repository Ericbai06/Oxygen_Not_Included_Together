#if DEBUG
using System.Collections.Generic;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World;

namespace ONI_Together.DebugTools.UnitTests
{
	public static partial class WorldUpdatePacketTests
	{
		private static readonly DispatchContext ForegroundContext = new(7, true, 11, 13);

		[UnitTest(name: "World foreground buffers 160 until 123 through 159 arrive", category: "Networking")]
		public static UnitTestResult ForegroundReordersExactObservedGap()
		{
			WorldUpdatePacket.ResetRevisionState();
			try
			{
				WorldUpdatePacket.SetClientForegroundBaseline(122);
				var applied = new List<WorldUpdatePacket>();
				WorldUpdatePacket.ForegroundReorderResult first = Queue(160, 1f, applied);
				if (WorldUpdatePacket.ForegroundReorderResult.Buffered != first
				    || 122L != WorldUpdatePacket.CurrentClientForegroundSequence
				    || 1 != WorldUpdatePacket.PendingForegroundPacketCount || 0 != applied.Count)
					return UnitTestResult.Fail("Sequence 160 applied early or moved the contiguous cut");
				for (long sequence = 159; sequence >= 124; sequence--)
					if (WorldUpdatePacket.ForegroundReorderResult.Buffered != Queue(sequence, 2f, applied))
						return UnitTestResult.Fail($"Future sequence {sequence} was not buffered");
				if (0 != applied.Count || 37 != WorldUpdatePacket.PendingForegroundPacketCount)
					return UnitTestResult.Fail("A future mutation applied before sequence 123 arrived");
				if (WorldUpdatePacket.ForegroundReorderResult.Accepted != Queue(123, 3f, applied)
				    || !MatchesContiguousMutations(applied, 123, 160)
				    || 160L != WorldUpdatePacket.CurrentClientForegroundSequence
				    || 0 != WorldUpdatePacket.PendingForegroundPacketCount)
					return UnitTestResult.Fail("Closing the gap did not apply every mutation in strict order");
				return UnitTestResult.Pass("Sequence 160 waits, then mutations 123 through 160 apply exactly once in order");
			}
			finally { WorldUpdatePacket.ResetRevisionState(); }
		}

		[UnitTest(name: "World foreground duplicate and old packets are idempotent", category: "Networking")]
		public static UnitTestResult ForegroundDuplicatesAreIdempotent()
		{
			WorldUpdatePacket.ResetRevisionState();
			try
			{
				WorldUpdatePacket.SetClientForegroundBaseline(122);
				var applied = new List<WorldUpdatePacket>();
				if (WorldUpdatePacket.ForegroundReorderResult.Buffered != Queue(124, 1f, applied)
				    || WorldUpdatePacket.ForegroundReorderResult.Ignored != Queue(124, 2f, applied)
				    || 1 != WorldUpdatePacket.PendingForegroundPacketCount
				    || 1 != WorldUpdatePacket.PendingForegroundUpdateCount)
					return UnitTestResult.Fail("Duplicate future packet replaced or duplicated the buffered mutation");
				if (WorldUpdatePacket.ForegroundReorderResult.Accepted != Queue(123, 3f, applied)
				    || !MatchesContiguousMutations(applied, 123, 124))
					return UnitTestResult.Fail("Buffered mutation did not drain after its predecessor");
				if (WorldUpdatePacket.ForegroundReorderResult.Ignored != Queue(123, 4f, applied)
				    || WorldUpdatePacket.ForegroundReorderResult.Ignored != Queue(124, 5f, applied)
				    || 2 != applied.Count || 124L != WorldUpdatePacket.CurrentClientForegroundSequence)
					return UnitTestResult.Fail("Old or repeated packet reapplied a mutation");
				return UnitTestResult.Pass("Future duplicates and already-applied packets are idempotent");
			}
			finally { WorldUpdatePacket.ResetRevisionState(); }
		}

		[UnitTest(name: "World foreground baseline and session reset clear reorder state", category: "Networking")]
		public static UnitTestResult ForegroundResetClearsBufferedMutations()
		{
			WorldUpdatePacket.ResetRevisionState();
			try
			{
				var applied = new List<WorldUpdatePacket>();
				WorldUpdatePacket.SetClientForegroundBaseline(122);
				Queue(124, 1f, applied);
				WorldUpdatePacket.SetClientForegroundBaseline(200);
				if (0 != WorldUpdatePacket.PendingForegroundPacketCount
				    || 0 != WorldUpdatePacket.PendingForegroundUpdateCount
				    || WorldUpdatePacket.ForegroundReorderResult.Accepted != Queue(201, 2f, applied))
					return UnitTestResult.Fail("Accepted baseline retained or blocked an old buffered mutation");
				Queue(203, 3f, applied);
				WorldUpdatePacket.ResetRevisionState();
				WorldUpdatePacket.ForegroundReorderResult result = NewPacket(500)
					.QueueForegroundForTests(4f, new DispatchContext(8, true, 12, 14), out var newSession);
				if (0 != WorldUpdatePacket.PendingForegroundPacketCount
				    || WorldUpdatePacket.ForegroundReorderResult.Accepted != result
				    || !MatchesContiguousMutations(newSession, 500, 500)
				    || !MatchesContiguousMutations(applied, 201, 201))
					return UnitTestResult.Fail("Session reset retained old state or prevented a new baseline");
				return UnitTestResult.Pass("Baseline and session reset discard old buffers and permit a fresh baseline");
			}
			finally { WorldUpdatePacket.ResetRevisionState(); }
		}

		[UnitTest(name: "World foreground pending capacity fails closed", category: "Networking")]
		public static UnitTestResult ForegroundPendingCapacityFailsClosed()
		{
			WorldUpdatePacket.ResetRevisionState();
			try
			{
				const long baseline = 1000;
				WorldUpdatePacket.SetClientForegroundBaseline(baseline);
				var applied = new List<WorldUpdatePacket>();
				for (int index = 0; index < WorldUpdatePacket.MaxPendingForegroundPackets; index++)
				{
					long sequence = baseline + index + 2;
					if (WorldUpdatePacket.ForegroundReorderResult.Buffered != Queue(sequence, index, applied))
						return UnitTestResult.Fail($"Pending capacity stopped early at {index}");
				}
				long overflow = baseline + WorldUpdatePacket.MaxPendingForegroundPackets + 2;
				WorldUpdatePacket.ForegroundReorderResult result = Queue(overflow, 200f, applied);
				if (WorldUpdatePacket.ForegroundReorderResult.CapacityExceeded != result
				    || !WorldUpdatePacket.ForegroundReorderFailedForTests || 0 != applied.Count
				    || baseline != WorldUpdatePacket.CurrentClientForegroundSequence
				    || 0 != WorldUpdatePacket.PendingForegroundPacketCount)
					return UnitTestResult.Fail("Pending overflow silently dropped or applied a mutation without failing");
				return UnitTestResult.Pass("Pending overflow becomes an explicit terminal failure without applying mutations");
			}
			finally { WorldUpdatePacket.ResetRevisionState(); }
		}

		[UnitTest(name: "World foreground gap and wait bounds fail closed", category: "Networking")]
		public static UnitTestResult ForegroundGapAndWaitBoundsFailClosed()
		{
			WorldUpdatePacket.ResetRevisionState();
			try
			{
				const long baseline = 1000;
				WorldUpdatePacket.SetClientForegroundBaseline(baseline);
				var applied = new List<WorldUpdatePacket>();
				long outsideWindow = baseline + WorldUpdatePacket.MaxForegroundSequenceGap + 1;
				if (WorldUpdatePacket.ForegroundReorderResult.CapacityExceeded
				    != Queue(outsideWindow, 1f, applied)
				    || !WorldUpdatePacket.ForegroundReorderFailedForTests || 0 != applied.Count)
					return UnitTestResult.Fail("Out-of-window mutation did not fail closed");
				WorldUpdatePacket.ResetRevisionState();
				WorldUpdatePacket.SetClientForegroundBaseline(10);
				float startedAt = 10f;
				const float timeout = 30f;
				Queue(13, startedAt, applied);
				Queue(11, startedAt + timeout / 2f, applied);
				WorldUpdatePacket.CheckForegroundReorderTimeoutForTests(startedAt + timeout, timeout);
				if (WorldUpdatePacket.ForegroundReorderFailedForTests)
					return UnitTestResult.Fail("Contiguous progress did not renew the bounded gap wait");
				WorldUpdatePacket.CheckForegroundReorderTimeoutForTests(
					startedAt + timeout * 1.5f, timeout);
				if (!WorldUpdatePacket.ForegroundReorderFailedForTests
				    || 11L != WorldUpdatePacket.CurrentClientForegroundSequence
				    || !MatchesContiguousMutations(applied, 11, 11))
					return UnitTestResult.Fail("Unresolved gap timed out without explicit terminal failure");
				return UnitTestResult.Pass("Sequence window and no-progress wait bounds fail closed");
			}
			finally { WorldUpdatePacket.ResetRevisionState(); }
		}

		private static WorldUpdatePacket.ForegroundReorderResult Queue(
			long sequence, float receivedAt, List<WorldUpdatePacket> applied)
		{
			WorldUpdatePacket.ForegroundReorderResult result = NewPacket(sequence)
				.QueueForegroundForTests(receivedAt, ForegroundContext, out var ready);
			applied.AddRange(ready);
			return result;
		}

		private static WorldUpdatePacket NewPacket(long sequence)
		{
			var packet = new WorldUpdatePacket { Revision = sequence, Sequence = sequence };
			packet.Updates.Add(new WorldUpdatePacket.CellUpdate { Cell = checked((int)sequence) });
			return packet;
		}

		private static bool MatchesContiguousMutations(
			IReadOnlyList<WorldUpdatePacket> packets, long first, long last)
		{
			long expectedCount = last - first + 1;
			if (expectedCount != packets.Count)
				return false;
			for (long expected = first; expected <= last; expected++)
			{
				WorldUpdatePacket actual = packets[checked((int)(expected - first))];
				if (expected != actual.Sequence || 1 != actual.Updates.Count
				    || expected != actual.Updates[0].Cell)
					return false;
			}
			return true;
		}
	}
}
#endif
