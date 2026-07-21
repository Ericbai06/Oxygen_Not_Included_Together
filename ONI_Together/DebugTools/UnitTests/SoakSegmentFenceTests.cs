#if DEBUG
using System.Collections.Generic;
using System.IO;
using ONI_Together.Misc.World;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.World;
using Shared.Interfaces.Networking;

namespace ONI_Together.DebugTools.UnitTests
{
	public static partial class SoakSegmentFenceTests
	{
		[UnitTest(name: "Soak failed keyframe requires authoritative hard sync", category: "Networking")]
		public static UnitTestResult FailedKeyframeRequiresHardSync()
		{
			return SoakStateHashProbe.RequiresAuthoritativeHardSync(keyframeApplied: false)
			       && !SoakStateHashProbe.RequiresAuthoritativeHardSync(keyframeApplied: true)
				? UnitTestResult.Pass("A partial keyframe cannot release the next simulation segment")
				: UnitTestResult.Fail("A partial keyframe could continue without full authoritative sync");
		}

		[UnitTest(name: "Soak application fence is exact and ordered", category: "Networking")]
		public static UnitTestResult ApplicationFenceIsExactAndOrdered()
		{
			var fence = RoundTrip(new SoakSegmentFencePacket
			{
				RunId = 7,
				SampleId = 4,
				CompletedTicks = 7_200,
				RepairSequenceCut = 73,
			});
			var ack = RoundTrip(new SoakSegmentFenceAckPacket
			{
				RunId = 7,
				SampleId = 4,
				CompletedTicks = 7_200,
				RepairSequenceCut = 73,
				KeyframeApplied = true,
			});
			if (fence.RunId != 7 || fence.SampleId != 4 || fence.CompletedTicks != 7_200
			    || fence.RepairSequenceCut != 73
			    || ack.RunId != 7 || ack.SampleId != 4 || ack.CompletedTicks != 7_200
			    || ack.RepairSequenceCut != 73
			    || !ack.KeyframeApplied
			    || fence is not IHostOnlyPacket || (object)ack is IHostOnlyPacket)
			{
				return UnitTestResult.Fail("Soak fence lost its marker or authority");
			}

			return UnitTestResult.Pass("Fence ACK identifies the exact ordered application boundary");
		}

		[UnitTest(name: "Soak fence waits for contiguous repair application", category: "Networking")]
		public static UnitTestResult FenceWaitsForContiguousRepairApplication()
		{
			WorldUpdatePacket.ResetRevisionState();
			try
			{
				WorldUpdatePacket.SetClientRepairBaseline(40);
				WorldUpdatePacket.ResolveRepairSequence(42);
				if (SoakStateHashProbe.CanAcknowledgeRepairFence(
					    WorldUpdatePacket.ClientResolvedRepairSequence, 42))
				{
					return UnitTestResult.Fail("Fence acknowledged across a missing repair sequence");
				}
				WorldUpdatePacket.ResolveRepairSequence(41);
				return WorldUpdatePacket.ClientResolvedRepairSequence == 42
				       && SoakStateHashProbe.CanAcknowledgeRepairFence(42, 42)
					? UnitTestResult.Pass("Fence waits for every out-of-order repair through its exact cut")
					: UnitTestResult.Fail("Contiguous repair resolution did not release the fence");
			}
			finally
			{
				WorldUpdatePacket.ResetRevisionState();
			}
		}

		[UnitTest(name: "World repair ACK is cumulative and bounded", category: "Networking")]
		public static UnitTestResult RepairAckIsCumulativeAndBounded()
		{
			var ack = RoundTrip(new WorldRepairAckPacket { AppliedThrough = 73 });
			var journal = new WorldUpdateRepairJournal(
				maxEntries: 1, maxUpdates: 2, replayIntervalSeconds: 1f);
			WorldUpdatePacket.ResetRevisionState();
			try
			{
				WorldUpdatePacket first = RepairPacket(1);
				WorldUpdatePacket second = RepairPacket(2);
				if (ack.AppliedThrough != 73
				    || !journal.TryRecordNext(first, new[] { 11UL, 12UL }, now: 0f)
				    || journal.TryRecordNext(second, new[] { 11UL }, now: 0f)
				    || second.RepairSequence != 0 || journal.PendingEntryCount != 1
				    || !journal.IsBackpressured)
					return UnitTestResult.Fail("Unacknowledged repair was evicted or consumed a sequence at capacity");
				if (!journal.AcceptAppliedAck(11, first.RepairSequence)
				    || journal.PendingEntryCount != 1
				    || journal.TryRecordNext(second, new[] { 11UL }, now: 0f)
				    || second.RepairSequence != 0
				    || !journal.AcceptAppliedAck(12, first.RepairSequence)
				    || journal.PendingEntryCount != 0
				    || !journal.TryRecordNext(second, new[] { 11UL }, now: 0f)
				    || second.RepairSequence != first.RepairSequence + 1)
					return UnitTestResult.Fail("Per-client cumulative ACK released another client's obligation");
				return UnitTestResult.Pass("Journal waits for every client's cumulative ACK without a sequence gap");
			}
			finally
			{
				journal.Reset();
				WorldUpdatePacket.ResetRevisionState();
			}
		}

		[UnitTest(name: "World repair journal replays ordered before a fence", category: "Networking")]
		public static UnitTestResult RepairJournalReplaysOrderedBeforeFence()
		{
			var journal = new WorldUpdateRepairJournal(4, 8, 1f);
			var replayed = new List<long>();
			var paced = new List<long>();
			WorldUpdatePacket.ResetRevisionState();
			try
			{
				WorldUpdatePacket first = RepairPacket(1);
				WorldUpdatePacket second = RepairPacket(2);
				if (!journal.TryRecordNext(first, new[] { 11UL }, now: 0f)
				    || !journal.TryRecordNext(second, new[] { 11UL }, now: 0f)
				    || !journal.ReplayPendingThrough(second.RepairSequence,
					    (client, packet) =>
					    {
						    replayed.Add(packet.RepairSequence);
						    return client == 11;
					    })
				    || replayed.Count != 2 || replayed[0] != 1 || replayed[1] != 2
				    || journal.RetransmitCount != 2)
					return UnitTestResult.Fail("Fence replay skipped, reordered, or failed to count repair retransmits");
				if (journal.ReplayOneDue(0.5f, (_, _) => true)
				    || !journal.ReplayOneDue(1.1f, (_, packet) =>
				    {
					    paced.Add(packet.RepairSequence);
					    return true;
				    })
				    || !journal.ReplayOneDue(2.2f, (_, packet) =>
				    {
					    paced.Add(packet.RepairSequence);
					    return true;
				    })
				    || paced.Count != 2 || paced[0] != 1 || paced[1] != 1)
					return UnitTestResult.Fail("Periodic replay skipped the client's oldest cumulative gap");
				for (int attempt = 0; attempt < 60; attempt++)
					if (journal.ReplayOneDue(2.3f, (_, _) => true))
						return UnitTestResult.Fail(
							"One client emitted more than one periodic replay inside its lease");
				if (!journal.AcceptAppliedAck(11, first.RepairSequence)
				    || !journal.ReplayOneDue(3.3f, (_, packet) =>
				    {
					    paced.Add(packet.RepairSequence);
					    return true;
				    })
				    || paced.Count != 3 || paced[2] != 2)
					return UnitTestResult.Fail("Cumulative ACK did not advance replay to the next gap");
				return UnitTestResult.Pass(
					"Each client retries its oldest cumulative gap under one global replay lease");
			}
			finally
			{
				journal.Reset();
				WorldUpdatePacket.ResetRevisionState();
			}
		}

		[UnitTest(name: "World repair journal respects client observation window", category: "Networking")]
		public static UnitTestResult RepairJournalRespectsClientObservationWindow()
		{
			const ulong clientId = 11;
			int observationWindow = WorldUpdateRepairObservability.MaxPendingPackets;
			var journal = new WorldUpdateRepairJournal();
			var recorded = new List<WorldUpdatePacket>();
			WorldUpdatePacket.ResetRevisionState();
			try
			{
				for (int cell = 0; cell < observationWindow; cell++)
				{
					WorldUpdatePacket packet = RepairPacket(cell);
					if (!journal.TryRecordNext(packet, new[] { clientId }, now: 0f))
						return UnitTestResult.Fail("Host window closed before the client observation limit");
					recorded.Add(packet);
				}

				WorldUpdatePacket overflow = RepairPacket(observationWindow);
				if (journal.TryRecordNext(overflow, new[] { clientId }, now: 0f)
				    || overflow.RepairSequence != 0
				    || journal.PendingEntryCount != observationWindow)
				{
					return UnitTestResult.Fail(
						"Host exceeded the client repair observation window before ACK");
				}

				if (!journal.AcceptAppliedAck(clientId, recorded[0].RepairSequence)
				    || !journal.TryRecordNext(overflow, new[] { clientId }, now: 0f)
				    || overflow.RepairSequence != recorded[^1].RepairSequence + 1)
				{
					return UnitTestResult.Fail("Cumulative ACK did not reopen exactly one window slot");
				}

				return UnitTestResult.Pass(
					"Host backpressure bounds unobserved repairs to the client window");
			}
			finally
			{
				journal.Reset();
				WorldUpdatePacket.ResetRevisionState();
			}
		}

		[UnitTest(name: "World repair replay paces clients independently", category: "Networking")]
		public static UnitTestResult RepairReplayPacesClientsIndependently()
		{
			var journal = new WorldUpdateRepairJournal(4, 8, 1f);
			var replayed = new List<(ulong Client, long Sequence)>();
			WorldUpdatePacket.ResetRevisionState();
			try
			{
				WorldUpdatePacket first = RepairPacket(1);
				WorldUpdatePacket second = RepairPacket(2);
				if (!journal.TryRecordNext(first, new[] { 11UL, 12UL }, now: 0f)
				    || !journal.TryRecordNext(second, new[] { 11UL, 12UL }, now: 0f))
					return UnitTestResult.Fail("Could not arrange two-client repair obligations");
				bool Replay(ulong client, WorldUpdatePacket packet)
				{
					replayed.Add((client, packet.RepairSequence));
					return true;
				}
				if (!journal.ReplayOneDue(1.1f, Replay)
				    || !journal.ReplayOneDue(1.1f, Replay)
				    || journal.ReplayOneDue(1.1f, Replay)
				    || replayed.Count != 2
				    || replayed[0] != (11UL, first.RepairSequence)
				    || replayed[1] != (12UL, first.RepairSequence))
					return UnitTestResult.Fail("One client consumed another client's replay lease or skipped a gap");
				return UnitTestResult.Pass("Each client gets one oldest-gap replay per interval");
			}
			finally
			{
				journal.Reset();
				WorldUpdatePacket.ResetRevisionState();
			}
		}

		[UnitTest(name: "World repair journal respects client observation update limit", category: "Networking")]
		public static UnitTestResult RepairJournalRespectsClientObservationUpdateLimit()
		{
			const ulong clientId = 11;
			int updateLimit = WorldUpdateRepairObservability.MaxPendingUpdates;
			var journal = new WorldUpdateRepairJournal();
			WorldUpdatePacket.ResetRevisionState();
			try
			{
				WorldUpdatePacket full = RepairPacket(0);
				for (int cell = 1; cell < updateLimit; cell++)
					full.Updates.Add(new WorldUpdatePacket.CellUpdate { Cell = cell });
				if (!journal.TryRecordNext(full, new[] { clientId }, now: 0f))
					return UnitTestResult.Fail("Host update window closed before the client limit");

				WorldUpdatePacket overflow = RepairPacket(updateLimit);
				if (journal.TryRecordNext(overflow, new[] { clientId }, now: 0f)
				    || overflow.RepairSequence != 0)
				{
					return UnitTestResult.Fail(
						"Host exceeded the client repair update limit before ACK");
				}

				if (!journal.AcceptAppliedAck(clientId, full.RepairSequence)
				    || !journal.TryRecordNext(overflow, new[] { clientId }, now: 0f))
					return UnitTestResult.Fail("ACK did not reopen the repair update window");

				return UnitTestResult.Pass(
					"Host update backpressure matches the client observation limit");
			}
			finally
			{
				journal.Reset();
				WorldUpdatePacket.ResetRevisionState();
			}
		}

		[UnitTest(name: "World repair journal releases disconnected clients explicitly", category: "Networking")]
		public static UnitTestResult RepairJournalDropsDisconnectedClient()
		{
			var journal = new WorldUpdateRepairJournal(1, 2, 1f);
			WorldUpdatePacket.ResetRevisionState();
			try
			{
				WorldUpdatePacket first = RepairPacket(1);
				WorldUpdatePacket second = RepairPacket(2);
				if (!journal.TryRecordNext(first, new[] { 11UL, 12UL }, 0f)
				    || journal.TryRecordNext(second, new[] { 12UL }, 0f)
				    || journal.DropClient(11) != 0 || journal.PendingEntryCount != 1
				    || journal.DropClient(12) != 1 || journal.PendingEntryCount != 0
				    || !journal.TryRecordNext(second, new[] { 12UL }, 0f))
					return UnitTestResult.Fail("Disconnect pruning dropped shared state or failed to release capacity");
				return UnitTestResult.Pass("Disconnect lifecycle explicitly prunes only the departed client's obligations");
			}
			finally
			{
				journal.Reset();
				WorldUpdatePacket.ResetRevisionState();
			}
		}

		[UnitTest(name: "World repair replay cannot starve fresh dispatch", category: "Networking")]
		public static UnitTestResult RepairReplayPreservesFreshDispatchBudget()
		{
			return WorldUpdateBatcher.HasFreshRepairBudget(
				       foregroundDispatched: false, replayed: true)
			       && WorldUpdateBatcher.HasFreshRepairBudget(
				       foregroundDispatched: false, replayed: false)
			       && !WorldUpdateBatcher.HasFreshRepairBudget(
				       foregroundDispatched: true, replayed: false)
				? UnitTestResult.Pass("Each non-foreground frame retains one fresh repair slot after replay")
				: UnitTestResult.Fail("Repair replay consumed or foreground bypassed the fresh dispatch budget");
		}

	}
}
#endif
