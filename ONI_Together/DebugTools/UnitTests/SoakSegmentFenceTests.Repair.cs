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
		[UnitTest(name: "World repair ACK crosses one client apply frame", category: "Networking")]
		public static UnitTestResult RepairAckCrossesApplyFrame()
		{
			WorldUpdatePacket.ResetRevisionState();
			try
			{
				WorldUpdatePacket packet = RepairPacket(7);
				packet.Revision = 10;
				packet.RepairSequence = 1;
				if (!WorldUpdateRepairObservability.Track(packet, packet.Updates)
				    || WorldUpdatePacket.ClientResolvedRepairSequence != 0)
					return UnitTestResult.Fail("Repair resolved in the ModifyCell dispatch frame");
				WorldUpdateRepairObservability.CompleteApplyBarrierForTests();
				if (WorldUpdatePacket.ClientResolvedRepairSequence != 1)
					return UnitTestResult.Fail("Cross-frame apply barrier did not resolve the repair");
				if (!WorldUpdateRepairObservability.Track(packet, packet.Updates)
				    || WorldUpdateRepairObservability.PendingCount != 0)
					return UnitTestResult.Fail("Replay recreated an already resolved observation");

				packet = RepairPacket(8);
				packet.Revision = 12;
				packet.RepairSequence = 3;
				WorldUpdateRepairObservability.Track(packet, packet.Updates);
				WorldUpdateRepairObservability.CompleteApplyBarrierForTests();
				if (WorldUpdatePacket.ClientResolvedRepairSequence != 1)
					return UnitTestResult.Fail("Out-of-order repair advanced the cumulative ACK");
				packet = RepairPacket(9);
				packet.Revision = 13;
				packet.RepairSequence = 2;
				WorldUpdateRepairObservability.Track(packet, packet.Updates);
				WorldUpdateRepairObservability.CompleteApplyBarrierForTests();
				return WorldUpdatePacket.ClientResolvedRepairSequence == 3
					? UnitTestResult.Pass("ACK follows a client frame and remains cumulatively ordered")
					: UnitTestResult.Fail("Repair gap did not close after its apply barrier");
			}
			finally
			{
				WorldUpdatePacket.ResetRevisionState();
			}
		}

		[UnitTest(name: "World dispatch freeze is versioned and blocks all classes", category: "Networking")]
		public static UnitTestResult WorldDispatchFreezeIsVersioned()
		{
			WorldUpdateBatcher.ResetSessionState();
			bool leaseHeld = false;
			try
			{
				leaseHeld = WorldUpdateBatcher.TryBeginWorldDispatchForTests();
				if (!leaseHeld || WorldUpdateBatcher.TryFreezeWorldDispatch(out _, out _))
					return UnitTestResult.Fail("Freeze crossed an in-flight world send");
				WorldUpdateBatcher.CompleteWorldDispatchForTests();
				leaseHeld = false;
				if (!WorldUpdateBatcher.TryFreezeWorldDispatch(
				    out long repairCut, out long mutationVersion)
				    || repairCut != 0
				    || !WorldUpdateBatcher.IsFrozenCheckpointValid(mutationVersion))
					return UnitTestResult.Fail("Empty world dispatch could not freeze exactly");
				bool backgroundQueued = WorldUpdateBatcher.QueueForTests(
					new WorldUpdatePacket.CellUpdate
					{
						Cell = 8,
						ReplaceType = SimMessages.ReplaceType.Replace,
					}, backgroundRepair: true);
				if (backgroundQueued
				    || !WorldUpdateBatcher.IsFrozenCheckpointValid(mutationVersion))
					return UnitTestResult.Fail(
						"Periodic background repair invalidated a stable checkpoint");
				WorldUpdateBatcher.QueueForTests(new WorldUpdatePacket.CellUpdate
				{
					Cell = 9,
					ReplaceType = SimMessages.ReplaceType.Replace,
				});
				WorldUpdateBatcher.Flush();
				if (WorldUpdateBatcher.IsFrozenCheckpointValid(mutationVersion)
				    || WorldUpdateBatcher.TryTakePendingDispatch(
					    out _, out _, requireReadyClients: false))
					return UnitTestResult.Fail("Foreground mutation crossed the frozen checkpoint");
				WorldUpdateBatcher.ResumeRepairDispatch();
				return WorldUpdateBatcher.TryTakePendingDispatch(
					       out WorldUpdatePacket packet, out _, requireReadyClients: false)
				       && !packet.IsBackgroundRepair
					? UnitTestResult.Pass("Freeze blocks every world dispatch and exposes mutation invalidation")
					: UnitTestResult.Fail("Queued foreground did not resume after the checkpoint");
			}
			finally
			{
				if (leaseHeld)
					WorldUpdateBatcher.CompleteWorldDispatchForTests();
				WorldUpdateBatcher.ResetSessionState();
			}
		}

		[UnitTest(name: "World update keeps valid host numeric semantics exact", category: "Networking")]
		public static UnitTestResult KeepsHostNumericSemanticsExact()
		{
			var additive = new WorldUpdatePacket.CellUpdate
			{
				Mass = -0.5f, Temperature = 280f, ReplaceType = SimMessages.ReplaceType.None
			};
			if (!WorldUpdatePacket.TryGetApplyValues(additive, out float temperature, out float mass)
			    || temperature != 280f || mass != -0.5f)
				return UnitTestResult.Fail("A valid additive removal was changed or rejected");
			var vacuum = new WorldUpdatePacket.CellUpdate
			{
				Mass = 0f, Temperature = 123f, ReplaceType = SimMessages.ReplaceType.Replace
			};
			if (!WorldUpdatePacket.TryGetApplyValues(vacuum, out temperature, out mass)
			    || temperature != 123f || mass != 0f)
				return UnitTestResult.Fail("An exact vacuum temperature was rewritten");
			vacuum.Mass = -1f;
			return !WorldUpdatePacket.TryGetApplyValues(vacuum, out _, out _)
				? UnitTestResult.Pass("Finite host operation values remain exact and corrupt absolutes are rejected")
				: UnitTestResult.Fail("A negative absolute mass was accepted");
		}

		[UnitTest(name: "World repair staging reports capacity backpressure", category: "Networking")]
		public static UnitTestResult RepairStagingReportsCapacityBackpressure()
		{
			WorldUpdateBatcher.ResetSessionState();
			try
			{
				for (int cell = 0; cell < 65536; cell++)
				{
					if (!WorldUpdateBatcher.QueueForTests(new WorldUpdatePacket.CellUpdate
					    {
						    Cell = cell,
						    ReplaceType = SimMessages.ReplaceType.Replace,
					    }, backgroundRepair: true))
						return UnitTestResult.Fail("Repair staging rejected an entry below capacity");
				}
				bool rejected = !WorldUpdateBatcher.QueueForTests(
					new WorldUpdatePacket.CellUpdate
					{
						Cell = 65536,
						ReplaceType = SimMessages.ReplaceType.Replace,
					}, backgroundRepair: true);
				bool overwriteAccepted = WorldUpdateBatcher.QueueForTests(
					new WorldUpdatePacket.CellUpdate
					{
						Cell = 0,
						ElementIdx = 7,
						ReplaceType = SimMessages.ReplaceType.Replace,
					}, backgroundRepair: true);
				if (!rejected || !overwriteAccepted
				    || WorldUpdateBatcher.PendingCountForTests(true) != 65536
				    || WorldUpdateBatcher.Flush() <= 0)
					return UnitTestResult.Fail("Repair staging hid capacity loss or rejected a safe overwrite");
				int retained = WorldUpdateBatcher.PendingCountForTests(true);
				int dispatched = 0;
				while (WorldUpdateBatcher.TryTakePendingDispatch(
					       out _, out _, requireReadyClients: false))
					dispatched++;
				if (retained <= 0 || retained >= 65536 || dispatched != 256
				    || WorldUpdateBatcher.Flush() <= 0
				    || WorldUpdateBatcher.PendingCountForTests(true) >= retained)
					return UnitTestResult.Fail("Bounded dispatch capacity deadlocked a larger staged repair set");
				return UnitTestResult.Pass("Repair staging drains bounded prefixes without loss or eviction");
			}
			finally
			{
				WorldUpdateBatcher.ResetSessionState();
			}
		}

		[UnitTest(name: "Soak raw and observed hashes reject the first mismatch", category: "Networking")]
		public static UnitTestResult RawAndObservedHashesRequireExactEquality()
		{
			var raw = new SoakHashCheckpointPacket
			{
				RunId = 3,
				SampleId = 2,
				CompletedTicks = 3_600,
				Cycle = 4,
				CycleTime = 12f,
			};
			var observed = new SoakHashReportPacket
			{
				RunId = 3,
				SampleId = 2,
				CompletedTicks = 3_600,
				Cycle = 4,
				CycleTime = 12f,
			};
			if (!SoakStateHashProbe.RawAndObservedHashesMatch(raw, observed))
				return UnitTestResult.Fail("Equal raw and observed hashes were rejected");
			observed.Lifecycle.UnassignedLiveCount = 1;
			if (SoakStateHashProbe.RawAndObservedHashesMatch(raw, observed))
				return UnitTestResult.Fail("Raw comparison ignored unassigned lifecycle state");
			observed.Lifecycle.UnassignedLiveCount = 0;
			observed.CycleTime = 12.001f;
			if (!SoakStateHashProbe.RawAndObservedHashesMatch(raw, observed))
				return UnitTestResult.Fail("Raw comparison rejected sub-frame client clock skew");
			observed.CycleTime = 13f;
			if (SoakStateHashProbe.RawAndObservedHashesMatch(raw, observed))
				return UnitTestResult.Fail("Raw comparison ignored client clock drift");
			raw.CycleTime = 599.999f;
			observed.Cycle = 5;
			observed.CycleTime = 0.001f;
			if (!SoakStateHashProbe.RawAndObservedHashesMatch(raw, observed))
				return UnitTestResult.Fail("Raw comparison rejected a cycle-boundary clock skew");
			raw.CycleTime = 12f;
			observed.Cycle = 4;
			observed.CycleTime = 12f;
			observed.StorageMembershipHash[0] = 1;
			return !SoakStateHashProbe.RawAndObservedHashesMatch(raw, observed)
				? UnitTestResult.Pass("The first differing domain rejects the sample")
				: UnitTestResult.Fail("A raw/observed hash mismatch was accepted");
		}

		[UnitTest(name: "Soak segment markers reject stale and duplicate acknowledgements", category: "Networking")]
		public static UnitTestResult SegmentMarkersAreGenerationBound()
		{
			if (!SoakTickBarrier.IsNextSegment((0, 0), 7, 1)
			    || !SoakTickBarrier.IsNextSegment((7, 1), 7, 2)
			    || SoakTickBarrier.IsNextSegment((7, 1), 7, 1)
			    || SoakTickBarrier.IsNextSegment((7, 1), 6, 2)
			    || SoakTickBarrier.IsNextSegment((0, 0), 7, 2))
			{
				return UnitTestResult.Fail("Soak segment sequence accepted a stale or skipped marker");
			}

			var ready = RoundTrip(new SoakTickReadyAckPacket
			{
				RunId = 7,
				SampleId = 4,
				Ready = true,
			});
			var start = RoundTrip(new SoakTickStartPacket { RunId = 7, SampleId = 4 });
			if (ready.RunId != 7 || ready.SampleId != 4 || !ready.Ready
			    || start.RunId != 7 || start.SampleId != 4)
			{
				return UnitTestResult.Fail("Soak ready/start packets lost the segment marker");
			}

			return UnitTestResult.Pass("Soak segment ACKs are bound to one run and sample");
		}

		[UnitTest(name: "New soak run supersedes stale client state", category: "Networking")]
		public static UnitTestResult NewRunSupersedesDroppedCancelState()
		{
			if (!SoakStateHashProbe.ShouldSupersedeClientRun(7, 8, 1)
			    || SoakStateHashProbe.ShouldSupersedeClientRun(7, 8, 2)
			    || SoakStateHashProbe.ShouldSupersedeClientRun(7, 7, 1)
			    || SoakStateHashProbe.ShouldSupersedeClientRun(0, 8, 1))
				return UnitTestResult.Fail("Dropped cancel can poison the next run");
			return UnitTestResult.Pass(
				"Host sample one explicitly supersedes stale client run state");
		}

		[UnitTest(name: "Soak causal fences cover 37800 fixed simulation ticks", category: "Networking")]
		public static UnitTestResult SegmentScheduleAndCheckpointAreExact()
		{
			int segmentTicks = SoakStateHashProbe.SegmentTickCount;
			int segmentCount = SoakStateHashProbe.SegmentCount;
			int targetTicks = SoakStateHashProbe.TargetTickCount;
			if (segmentTicks != 1_800 || segmentCount != 21 || targetTicks != 37_800
			    || targetTicks < 600 * 60)
			{
				return UnitTestResult.Fail("Soak segment schedule does not cover the required game cycle");
			}

			var checkpoint = RoundTrip(new SoakHashCheckpointPacket
			{
				RunId = 7,
				SampleId = 4,
				CompletedTicks = 7_200,
				Cycle = 5,
				CycleTime = 12f,
			});
			return checkpoint.SampleId == 4 && checkpoint.CompletedTicks == 7_200
				? UnitTestResult.Pass("Every hash checkpoint carries its completed causal segment")
				: UnitTestResult.Fail("Soak checkpoint lost its causal tick marker");
		}

		private static T RoundTrip<T>(T source) where T : IPacket, new()
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
				source.Serialize(writer);
			stream.Position = 0;
			var copy = new T();
			using var reader = new BinaryReader(stream);
			copy.Deserialize(reader);
			return copy;
		}

		private static WorldUpdatePacket RepairPacket(int cell)
		{
			var packet = new WorldUpdatePacket { Revision = cell + 1 };
			packet.Updates.Add(new WorldUpdatePacket.CellUpdate
			{
				Cell = cell,
				ElementIdx = 1,
				Mass = 1f,
				Temperature = 290f,
				ReplaceType = SimMessages.ReplaceType.Replace,
			});
			return packet;
		}
	}
}
#endif
