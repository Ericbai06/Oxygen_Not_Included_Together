using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.States;
using ONI_Together.Networking.Transport.Lan;
using ONI_Together.Networking.Packets.World;
using System.IO;

namespace ONI_Together.DebugTools.UnitTests
{
	public static partial class SyncBarrierTests
	{
		[UnitTest(name: "Sync barrier prunes disconnected clients", category: "Sync")]
		public static UnitTestResult PrunesDisconnectedClients()
		{
			var barrier = new SyncBarrier();
			barrier.Add(1, false);
			barrier.Add(2, true);
			barrier.Prune(id => id == 2);

			if (barrier.PendingCount != 1 || !barrier.IsActive)
				return UnitTestResult.Fail("Disconnected pending client was not pruned");
			barrier.Prune(id => false);
			if (barrier.IsActive || !barrier.ShouldUnpauseAfterCompletion)
				return UnitTestResult.Fail("Barrier did not complete after every pending client disconnected");

			return UnitTestResult.Pass("Disconnected pending clients are pruned and can complete the barrier");
		}

		[UnitTest(name: "Sync barrier expires only after snapshot progress stops", category: "Sync")]
		public static UnitTestResult SnapshotDeadlineIsAnIdleLease()
		{
			var started = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
			var barrier = new SyncBarrier();
			barrier.Add(7, false, started);
			barrier.MarkTransferStarted(7, 1);
			barrier.TryAcceptLoading(7, 77, 1);
			barrier.TryBeginWorldBaseline(7, 1, connectionGeneration: 1);
			var progress = System.DateTime.UtcNow.AddMinutes(4);
			if (!barrier.RecordWorldBaselineProgress(7, progress)
			    || barrier.RecordWorldBaselineProgress(7, progress)
			    || barrier.RecordWorldBaselineProgress(7, progress.AddSeconds(-1)))
				return UnitTestResult.Fail("Only forward replay progress may renew the host lease");
			var expired = new System.Collections.Generic.List<ulong>();

			bool early = barrier.Prune(
				id => true,
				System.TimeSpan.FromMinutes(5),
				progress.AddMinutes(5),
				expired);
			bool changed = barrier.Prune(
				id => true,
				System.TimeSpan.FromMinutes(5),
				progress.AddMinutes(5).AddSeconds(1),
				expired);
			if (early || !changed || barrier.IsActive || expired.Count != 1 || expired[0] != 7)
				return UnitTestResult.Fail("The snapshot lease did not expire from its last progress");

			return UnitTestResult.Pass("New replay progress renews only the bounded snapshot idle lease");
		}

		[UnitTest(name: "Sync barrier has an absolute lifetime despite replay progress", category: "Sync")]
		public static UnitTestResult SnapshotLeaseHasAbsoluteLifetime()
		{
			var started = System.DateTime.UtcNow;
			var barrier = new SyncBarrier();
			barrier.Add(7, false, started);
			barrier.MarkTransferStarted(7, 1);
			barrier.TryAcceptLoading(7, 77, 1);
			barrier.TryBeginWorldBaseline(7, 1, connectionGeneration: 1);
			var progress = started.AddMinutes(14);
			if (!barrier.RecordWorldBaselineProgress(7, progress))
				return UnitTestResult.Fail("Could not arrange forward replay progress");

			var expired = new System.Collections.Generic.List<ulong>();
			bool atBoundary = barrier.Prune(
				id => true, System.TimeSpan.FromMinutes(5), System.TimeSpan.FromMinutes(15),
				started.AddMinutes(15), expired);
			bool pastBoundary = barrier.Prune(
				id => true, System.TimeSpan.FromMinutes(5), System.TimeSpan.FromMinutes(15),
				started.AddMinutes(15).AddSeconds(1), expired);
			return !atBoundary && pastBoundary && expired.Count == 1 && expired[0] == 7
				? UnitTestResult.Pass("Continuous ACKs cannot renew the absolute barrier lifetime")
				: UnitTestResult.Fail("Replay progress could hold the barrier indefinitely");
		}

		[UnitTest(name: "Sync barrier transfers a reconnecting client ID", category: "Sync")]
		public static UnitTestResult TransfersReconnectingClientId()
		{
			var barrier = new SyncBarrier();
			barrier.Add(10, false);
			barrier.MarkTransferStarted(10, 7);
			if (!barrier.TryAcceptLoading(10, 1234, 7))
				return UnitTestResult.Fail("Valid loading proof was rejected");

			if (!barrier.Replace(10, 20, 1234) || barrier.Contains(10) || !barrier.Contains(20))
				return UnitTestResult.Fail("Pending client ID was not transferred atomically");
			if (barrier.CanComplete(20, 1234, 7)
			    || !barrier.TryBeginWorldBaseline(20, 7, connectionGeneration: 7)
			    || !barrier.CanComplete(20, 1234, 7)
			    || barrier.CanComplete(20, 1234, 6)
			    || barrier.CanComplete(20, 9999, 7))
				return UnitTestResult.Fail("Ready proof was not bound to token and snapshot generation");
			if (!barrier.Replace(20, 30, 1234)
			    || barrier.TryBeginWorldBaseline(30, 7)
			    || !barrier.RequiresFreshSnapshotAfterConnectionChange(30, 1)
			    || barrier.CanComplete(30, 1234, 7, connectionGeneration: 1))
				return UnitTestResult.Fail("Repeated reconnect resumed an unfinished world baseline");
			if (!barrier.Complete(30) || barrier.IsActive)
				return UnitTestResult.Fail("Transferred client could not complete the barrier");

			var collision = new SyncBarrier();
			collision.Add(10, false);
			collision.MarkTransferStarted(10, 7);
			collision.TryAcceptLoading(10, 1234, 7);
			collision.Add(20, false);
			if (collision.CanReplace(10, 20, 1234)
			    || collision.Replace(10, 20, 1234)
			    || !collision.Contains(10) || !collision.Contains(20))
				return UnitTestResult.Fail("Reconnect overwrote an existing target barrier");

			return UnitTestResult.Pass(
				"Pre-baseline reconnect transfers safely; active baseline reconnect requires a fresh snapshot");
		}

		[UnitTest(name: "Sync barrier rejects stale loading generations", category: "Sync")]
		public static UnitTestResult RejectsStaleLoadingGeneration()
		{
			var barrier = new SyncBarrier();
			barrier.Add(10, false);
			barrier.MarkTransferStarted(10, 41);

			if (barrier.TryAcceptLoading(10, 77, 40)
			    || barrier.TryAcceptLoading(10, 0, 41)
			    || barrier.Replace(10, 20, 77))
				return UnitTestResult.Fail("Stale generation, empty token, or unaccepted reconnect was allowed");
			if (!barrier.TryAcceptLoading(10, 77, 41)
			    || !barrier.HasReconnectProof(10, 77))
				return UnitTestResult.Fail("Exact loading proof was rejected");

			barrier.MarkTransferStarted(10, 42);
			if (barrier.HasReconnectProof(10, 77)
			    || barrier.CanComplete(10, 77, 41))
				return UnitTestResult.Fail("Rotating the snapshot retained stale loading proof");

			return UnitTestResult.Pass("Loading and Ready are bound to the current snapshot generation");
		}

		[UnitTest(name: "LAN reconnect tokens map concurrent loaders exactly", category: "Sync")]
		public static UnitTestResult ReconnectTokensMapExactClients()
		{
			var server = new RiptideServer();
			server.MarkClientLoading(10, 111);
			server.MarkClientLoading(20, 222);

			if (server.TryResumeLoadingClient(222, 200, _ => false, out _)
			    || !server.IsClientLoading(20))
				return UnitTestResult.Fail("Failed reconnect consumed its loading token");
			if (!server.TryResumeLoadingClient(222, 200, id => id == 20, out ulong previous)
			    || previous != 20)
				return UnitTestResult.Fail("Second loading token did not resume its own client");
			if (!server.IsClientLoading(10) || !server.IsClientLoading(200))
				return UnitTestResult.Fail("Resuming one token removed its recovery lease");
			if (!server.TryResumeLoadingClient(222, 201, id => id == 200, out previous)
			    || previous != 200 || !server.IsClientLoading(201))
				return UnitTestResult.Fail("A second disconnect could not reuse the loading token");
			if (!server.CompleteLoadingClient(222, 201)
			    || server.TryResumeLoadingClient(222, 202, _ => true, out _)
			    || server.TryResumeLoadingClient(999, 202, _ => true, out _))
				return UnitTestResult.Fail("Completed or unknown reconnect token was accepted");

			return UnitTestResult.Pass("LAN loading tokens survive repeated disconnects until Ready completion");
		}

		[UnitTest(name: "Sync barrier journals reliable post-snapshot deltas", category: "Sync")]
		public static UnitTestResult JournalsReliablePostSnapshotDeltas()
		{
			ReliableSyncBacklog.ClearAll();
			try
			{
				ReliableSyncBacklog.Begin(10);
				var first = new WorldCyclePacket { Cycle = 7, CycleTime = 12f };
				var second = new WorldCyclePacket { Cycle = 7, CycleTime = 13f };

				if (ReliableSyncBacklog.TryBuffer(10, first, PacketSendMode.Reliable)
				        != SyncBacklogResult.Buffered
				    || ReliableSyncBacklog.TryBuffer(10, second, PacketSendMode.ReliableImmediate)
				        != SyncBacklogResult.Buffered
				    || ReliableSyncBacklog.TryBuffer(10, second, PacketSendMode.Unreliable)
				        != SyncBacklogResult.NotBuffered
				    || ReliableSyncBacklog.CountForTests(10) != 2)
					return UnitTestResult.Fail("Reliable delta journal did not preserve exactly the ordered reliable events");

				if (!ReliableSyncBacklog.Transfer(10, 20)
				    || ReliableSyncBacklog.CountForTests(10) != 0
				    || ReliableSyncBacklog.CountForTests(20) != 2)
					return UnitTestResult.Fail("Reconnect did not transfer the post-snapshot journal");

				ReliableSyncBacklog.Begin(30);
				ReliableSyncBacklog.Begin(40);
				ReliableSyncBacklog.TryBuffer(30, first, PacketSendMode.Reliable);
				ReliableSyncBacklog.TryBuffer(40, second, PacketSendMode.Reliable);
				if (ReliableSyncBacklog.Transfer(30, 40)
				    || ReliableSyncBacklog.CountForTests(30) != 1
				    || ReliableSyncBacklog.CountForTests(40) != 1)
					return UnitTestResult.Fail("Reconnect overwrote an existing target journal");

				return UnitTestResult.Pass("Reliable post-snapshot deltas remain ordered across reconnect");
			}
			finally
			{
				ReliableSyncBacklog.ClearAll();
			}
		}
	}
}
