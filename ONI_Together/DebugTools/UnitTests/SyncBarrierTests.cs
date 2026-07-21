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
		[UnitTest(name: "All-ready signal waits for active barriers", category: "Sync")]
		public static UnitTestResult AllReadySignalWaitsForBarrier()
		{
			if (ReadyManager.CanSignalAllReady(
				    barrierActive: true,
				    hasConnectedRemote: false,
				    everyoneReady: true)
			    || ReadyManager.CanSignalAllReady(true, true, true)
			    || ReadyManager.CanSignalAllReady(false, true, false)
			    || !ReadyManager.CanSignalAllReady(false, true, true)
			    || !ReadyManager.CanSignalAllReady(false, false, false))
				return UnitTestResult.Fail("All-ready signal ignored an active barrier or remote Ready state");

			return UnitTestResult.Pass("Active loading barriers suppress global Ready even after disconnect");
		}

		[UnitTest(name: "Ready refresh treats one connected remote as a client", category: "Sync")]
		public static UnitTestResult OneConnectedRemoteIsNotHostOnly()
		{
			if (!ReadyManager.IsConnectedRemoteClient(22, 11, hasConnection: true)
			    || ReadyManager.IsConnectedRemoteClient(11, 11, hasConnection: true)
			    || ReadyManager.IsConnectedRemoteClient(22, 11, hasConnection: false))
				return UnitTestResult.Fail("Ready refresh retained the roster-count shortcut");

			return UnitTestResult.Pass("A single connected remote still requires an acknowledgement");
		}

		[UnitTest(name: "Ready acknowledgement is bound to exact proof", category: "Sync")]
		public static UnitTestResult ReadyAcknowledgementRequiresExactProof()
		{
			const ulong token = 1234;
			const long generation = 77;
			if (!ReadyManager.IsExactReadyAcceptance(token, generation, token, generation)
			    || ReadyManager.IsExactReadyAcceptance(token, generation, 9999, generation)
			    || ReadyManager.IsExactReadyAcceptance(token, generation, token, generation - 1)
			    || ReadyManager.IsExactReadyAcceptance(0, generation, token, generation)
			    || ReadyManager.IsExactReadyAcceptance(token, 0, token, generation)
			    || ReadyManager.IsExactReadyAcceptance(0, 0, token, generation))
				return UnitTestResult.Fail("Ready acknowledgement accepted stale, mismatched, or duplicate proof");

			return UnitTestResult.Pass("Only the current token and snapshot generation are accepted");
		}

		[UnitTest(name: "Ready acknowledgement packet validates proof", category: "Sync")]
		public static UnitTestResult ReadyAcknowledgementPacketValidatesProof()
		{
			var source = new ReadyAcceptedPacket
			{
				ReconnectToken = 4321,
				SnapshotGeneration = 88
			};
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
				source.Serialize(writer);
			stream.Position = 0;
			var copy = new ReadyAcceptedPacket();
			using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
				copy.Deserialize(reader);
			if (copy.ReconnectToken != source.ReconnectToken
			    || copy.SnapshotGeneration != source.SnapshotGeneration)
				return UnitTestResult.Fail("Ready acknowledgement proof changed during serialization");

			try
			{
				new ReadyAcceptedPacket { ReconnectToken = 0, SnapshotGeneration = 1 }
					.Serialize(new BinaryWriter(new MemoryStream()));
				return UnitTestResult.Fail("Empty Ready acknowledgement proof was serialized");
			}
			catch (InvalidDataException)
			{
				return UnitTestResult.Pass("Ready acknowledgement round-trips and rejects empty proof");
			}
		}

		[UnitTest(name: "Ready completion acknowledgement validates proof", category: "Sync")]
		public static UnitTestResult ReadyCompletionAcknowledgementValidatesProof()
		{
			var source = new ReadyAcceptedAckPacket
			{
				ReconnectToken = 8765,
				SnapshotGeneration = 91
			};
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
				source.Serialize(writer);
			stream.Position = 0;
			var copy = new ReadyAcceptedAckPacket();
			using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
				copy.Deserialize(reader);
			if (copy.ReconnectToken != source.ReconnectToken
			    || copy.SnapshotGeneration != source.SnapshotGeneration)
				return UnitTestResult.Fail("Ready completion proof changed during serialization");

			try
			{
				new ReadyAcceptedAckPacket { ReconnectToken = 0, SnapshotGeneration = 1 }
					.Serialize(new BinaryWriter(new MemoryStream()));
				return UnitTestResult.Fail("Empty Ready completion proof was serialized");
			}
			catch (InvalidDataException)
			{
				return UnitTestResult.Pass("Ready completion acknowledgement is exact and bounded");
			}
		}

		[UnitTest(name: "Sync barrier accepts only exact Ready", category: "Sync")]
		public static UnitTestResult ExactReadyOnly()
		{
			if (!SyncBarrier.IsExactReady(ClientReadyState.Ready)
				|| SyncBarrier.IsExactReady(ClientReadyState.Unready)
				|| SyncBarrier.IsExactReady(ClientReadyState.Loading)
				|| SyncBarrier.IsExactReady(ClientReadyState.Aborted)
				|| !SyncBarrier.IsValidReadyState(ClientReadyState.Aborted)
				|| SyncBarrier.IsValidReadyState((ClientReadyState)99))
				return UnitTestResult.Fail("Ready-state validation accepted a non-Ready or invalid value");

			return UnitTestResult.Pass("Only exact Ready passes and invalid enum values are rejected");
		}

		[UnitTest(name: "World-load abort requires exact proof", category: "Sync")]
		public static UnitTestResult WorldLoadAbortRequiresExactProof()
		{
			var barrier = new SyncBarrier();
			barrier.Add(10, false);
			barrier.MarkTransferStarted(10, 7);
			barrier.TryAcceptLoading(10, 1234, 7);

			if (barrier.CanAbort(11, 1234, 7)
			    || barrier.CanAbort(10, 9999, 7)
			    || barrier.CanAbort(10, 1234, 6)
			    || !barrier.CanAbort(10, 1234, 7))
			{
				return UnitTestResult.Fail("Forged client, token, or generation could abort a loading peer");
			}
			if (!barrier.Complete(10) || barrier.IsActive)
				return UnitTestResult.Fail("Exact abort proof did not release the barrier");

			return UnitTestResult.Pass("Only the exact loading proof releases the barrier");
		}

		[UnitTest(name: "Sync barrier matches payload sender", category: "Sync")]
		public static UnitTestResult PayloadSenderMustMatch()
		{
			if (!SyncBarrier.SenderMatches(42, 42) || SyncBarrier.SenderMatches(42, 7))
				return UnitTestResult.Fail("Payload sender matching is incorrect");

			return UnitTestResult.Pass("Payload sender must match transport sender");
		}

		[UnitTest(name: "Sync barrier tracks pending clients and pause ownership", category: "Sync")]
		public static UnitTestResult PendingClientsAndPauseOwnership()
		{
			var barrier = new SyncBarrier();
			barrier.Add(1, false);
			barrier.Add(2, true);
			if (barrier.PendingCount != 2 || barrier.WasPausedBeforeStart)
				return UnitTestResult.Fail("Barrier lost pending clients or original pause state");

			barrier.Complete(1);
			if (!barrier.IsActive || barrier.ShouldUnpauseAfterCompletion)
				return UnitTestResult.Fail("Barrier completed before every client was ready");

			barrier.Complete(2);
			if (barrier.IsActive || !barrier.ShouldUnpauseAfterCompletion)
				return UnitTestResult.Fail("Barrier did not restore an originally running simulation");

			var alreadyPaused = new SyncBarrier();
			alreadyPaused.Add(3, true);
			alreadyPaused.Complete(3);
			if (alreadyPaused.ShouldUnpauseAfterCompletion)
				return UnitTestResult.Fail("Barrier would unpause a simulation that was already paused");

			var reconnectPause = new SyncBarrier();
			reconnectPause.Add(4, true, pauseOwnedBySession: true);
			reconnectPause.Complete(4);
			if (!reconnectPause.ShouldUnpauseAfterCompletion)
				return UnitTestResult.Fail("Barrier retained a pause owned by the multiplayer session");
			if (!ReadyManager.ShouldReleaseAutomaticPause(true, true)
			    || ReadyManager.ShouldReleaseAutomaticPause(true, false)
			    || ReadyManager.ShouldReleaseAutomaticPause(false, true))
				return UnitTestResult.Fail("Pause release ignored current ownership");

			return UnitTestResult.Pass("Barrier waits for all clients and preserves pause ownership");
		}

		[UnitTest(name: "Sync barrier rejects every non-paused speed", category: "Sync")]
		public static UnitTestResult BarrierRejectsEveryNonPausedSpeed()
		{
			foreach (SpeedChangePacket.SpeedState speed in new[]
			         {
				         SpeedChangePacket.SpeedState.Paused,
				         SpeedChangePacket.SpeedState.Normal,
				         SpeedChangePacket.SpeedState.Double,
				         SpeedChangePacket.SpeedState.Triple
			         })
			{
				bool expected = speed == SpeedChangePacket.SpeedState.Paused;
				if (SpeedChangePacket.CanApplyDuringSyncBarrier(true, speed) != expected
				    || !SpeedChangePacket.CanApplyDuringSyncBarrier(false, speed))
					return UnitTestResult.Fail($"Barrier speed gate accepted {speed}={expected}");
			}

			return UnitTestResult.Pass("Host-local and relayed commands share the paused-only barrier gate");
		}

		[UnitTest(name: "Sync barrier retains automatic pause ownership", category: "Sync")]
		public static UnitTestResult BarrierRetainsAutomaticPauseOwnership()
		{
			if (ReadyManager.ShouldClearAutomaticPauseOwnership(barrierActive: true)
			    || !ReadyManager.ShouldClearAutomaticPauseOwnership(barrierActive: false))
				return UnitTestResult.Fail("User speed input could discard active barrier pause ownership");

			return UnitTestResult.Pass("Pause ownership is cleared only after the barrier ends");
		}

		[UnitTest(name: "Sync barrier reset restores only its own pause", category: "Sync")]
		public static UnitTestResult BarrierResetRestoresOwnedPause()
		{
			int restored = 0;
			if (!ReadyManager.ShouldRestoreAutomaticPauseOnReset(
				    barrierActive: true, wasPausedBeforeStart: false, ownsAutomaticPause: true)
			    || ReadyManager.ShouldRestoreAutomaticPauseOnReset(
				    barrierActive: false, wasPausedBeforeStart: false, ownsAutomaticPause: true)
			    || ReadyManager.ShouldRestoreAutomaticPauseOnReset(
				    barrierActive: true, wasPausedBeforeStart: true, ownsAutomaticPause: true)
			    || ReadyManager.ShouldRestoreAutomaticPauseOnReset(
				    barrierActive: true, wasPausedBeforeStart: false, ownsAutomaticPause: false)
			    || !ReadyManager.RestoreAutomaticPauseOnReset(true, () => restored++)
			    || ReadyManager.RestoreAutomaticPauseOnReset(false, () => restored++)
			    || restored != 1)
			{
				return UnitTestResult.Fail("Session reset could retain or steal pause ownership");
			}

			return UnitTestResult.Pass("Reset restores only an active barrier-owned pause");
		}

		[UnitTest(name: "Sync barrier blocks every local unpause path", category: "Sync")]
		public static UnitTestResult BarrierBlocksEveryLocalUnpausePath()
		{
			if (!SpeedChangePacket.ShouldBlockLocalSpeedControl(true, false, true)
			    || SpeedChangePacket.ShouldBlockLocalSpeedControl(false, false, true)
			    || SpeedChangePacket.ShouldBlockLocalSpeedControl(true, true, true)
			    || SpeedChangePacket.ShouldBlockLocalSpeedControl(true, false, false))
				return UnitTestResult.Fail("A direct speed, toggle, or Unpause call can bypass the barrier lock");

			return UnitTestResult.Pass("SetSpeed, TogglePause, and Unpause share one barrier lock");
		}

		[UnitTest(name: "Soak barrier owns client speed against delayed packets", category: "Sync")]
		public static UnitTestResult SoakBarrierIgnoresDelayedAuthoritativeSpeed()
		{
			return SpeedChangePacket.ShouldIgnoreAuthoritativeSpeed(soakControlsSpeed: true)
			       && !SpeedChangePacket.ShouldIgnoreAuthoritativeSpeed(soakControlsSpeed: false)
				? UnitTestResult.Pass("Fixed-tick barrier cannot be paused by delayed ready traffic")
				: UnitTestResult.Fail("Delayed authoritative speed can interrupt a fixed-tick barrier");
		}

		[UnitTest(name: "Sync barrier pause lock is authenticated on wire", category: "Sync")]
		public static UnitTestResult BarrierPauseLockIsAuthenticatedOnWire()
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
			{
				writer.Write((int)SpeedChangePacket.SpeedState.Paused);
				writer.Write(3L);
				writer.Write(true);
			}
			stream.Position = 0;
			var packet = new SpeedChangePacket();
			using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
				packet.Deserialize(reader);

			if (!packet.BarrierPauseLocked || packet.Speed != SpeedChangePacket.SpeedState.Paused)
				return UnitTestResult.Fail("Authoritative barrier lock was lost during decode");
			if (!RejectsUnpausedBarrierLock())
				return UnitTestResult.Fail("Wire input installed a barrier lock without Paused speed");
			return UnitTestResult.Pass("Only the host-authenticated paused command installs the lock");
		}

		private static bool RejectsUnpausedBarrierLock()
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
			{
				writer.Write((int)SpeedChangePacket.SpeedState.Normal);
				writer.Write(3L);
				writer.Write(true);
			}
			stream.Position = 0;
			try
			{
				using var reader = new BinaryReader(stream);
				new SpeedChangePacket().Deserialize(reader);
				return false;
			}
			catch (InvalidDataException)
			{
				return true;
			}
		}

	}
}
