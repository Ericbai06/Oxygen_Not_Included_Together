#if DEBUG
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.Handshake;
using System;

namespace ONI_Together.DebugTools
{
	internal static class FaultDlcProbe
	{
		internal static FaultProbeResult Execute(string caseId)
		{
			if (caseId != "dlc.fingerprint-mismatch")
				throw new InvalidOperationException("Unsupported headless DLC fault " + caseId);
			string local = new string('a', 64);
			bool rejected = !ProtocolCompatibility.MatchesValues(
				local, new[] { "DLC-A" }, new string('b', 64), new[] { "DLC-A" });
			bool generationPreserved = !GameStateRequestPacket.ShouldAcceptAdmissionGeneration(8, 7);
			bool clean = ProtocolCompatibility.MatchesValues(
				local, new[] { "DLC-A" }, local, new[] { "DLC-A" })
				&& GameStateRequestPacket.ShouldAcceptAdmissionGeneration(7, 8);
			return new FaultProbeResult(rejected, generationPreserved, true, clean,
				"inject:remote-fingerprint-mismatch",
				"production-gate:ProtocolCompatibility.MatchesValues",
				"reset:stateless-admission-gate", "clean:matching-fingerprint-admitted");
		}
	}

	internal static class FaultReconnectProbe
	{
		private const ulong Token = 71;
		private const long Generation = 42;
		private const ulong ReplayId = 99;
		private static readonly DispatchContext Host = new DispatchContext(1, true, 7, 10);

		internal static FaultProbeResult Execute(string caseId)
		{
			switch (caseId)
			{
				case "reconnect.session-stale": return StaleContext(caseId, new DispatchContext(1, true, 7, 9));
				case "reconnect.connection-stale": return StaleContext(caseId, new DispatchContext(1, true, 6, 10));
				case "reconnect.snapshot-stale": return StaleSnapshot(caseId);
				case "reconnect.batch-missing": return MissingBatch(caseId);
				case "reconnect.batch-duplicate": return DuplicateBatch(caseId);
				case "reconnect.ack-lost": return LostAck(caseId);
				case "reconnect.disconnect-mid-apply": return DisconnectMidApply(caseId);
				default: throw new InvalidOperationException("Unsupported reconnect fault " + caseId);
			}
		}

		private static FaultProbeResult StaleContext(string id, DispatchContext stale)
		{
			ReadyReplayAssembly replay = NewReplay();
			replay.AcceptBatch(Header(1), new[] { Frame(1) }, (Host, 1f));
			bool rejected = replay.AcceptBatch(Header(0), new[] { Frame(0) }, (stale, 2f))
			                == ReadyReplayAssemblyResult.Rejected;
			bool invariant = !replay.TryBeginApply(out _);
			return ResetAndClean(id, replay, rejected, invariant,
				"ReadyReplayAssembly.MatchOrBind");
		}

		private static FaultProbeResult StaleSnapshot(string id)
		{
			ReadyReplayAssembly replay = NewReplay();
			ReadyReplayProof stale = Proof();
			stale.SnapshotGeneration--;
			bool rejected = replay.AcceptCommit(stale, Host, 1f)
			                == ReadyReplayAssemblyResult.Rejected;
			return ResetAndClean(id, replay, rejected, !replay.TryBeginApply(out _),
				"ReadyReplayAssembly.AcceptCommit");
		}

		private static FaultProbeResult MissingBatch(string id)
		{
			ReadyReplayAssembly replay = NewReplay();
			replay.AcceptBatch(Header(1), new[] { Frame(1) }, (Host, 1f));
			bool pending = replay.AcceptCommit(Proof(), Host, 2f) == ReadyReplayAssemblyResult.Pending;
			return ResetAndClean(id, replay, pending && !replay.TryBeginApply(out _), true,
				"ReadyReplayAssembly.AcceptCommit+TryBeginApply");
		}

		private static FaultProbeResult DuplicateBatch(string id)
		{
			ReadyReplayAssembly replay = NewReplay();
			replay.AcceptBatch(Header(1), new[] { Frame(1) }, (Host, 1f));
			bool duplicate = replay.AcceptBatch(Header(1), new[] { Frame(1) }, (Host, 2f))
			                 == ReadyReplayAssemblyResult.Duplicate;
			return ResetAndClean(id, replay, duplicate, !replay.TryBeginApply(out _),
				"ReadyReplayAssembly.AcceptDuplicate");
		}

		private static FaultProbeResult LostAck(string id)
		{
			bool retry = ReadyManager.ShouldRetryReadyAcceptance(retainedProof: true);
			bool clean = !ReadyManager.ShouldRetryReadyAcceptance(retainedProof: false);
			return new FaultProbeResult(retry, retry, true, clean,
				"inject:ack-delivery-omitted",
				"production-gate:ReadyManager.ShouldRetryReadyAcceptance",
				"reset:stateless-retry-gate", "clean:acknowledged-proof-does-not-retry");
		}

		private static FaultProbeResult DisconnectMidApply(string id)
		{
			ReadyReplayAssembly replay = NewReplay();
			ArrangeReady(replay);
			bool began = replay.TryBeginApply(out ReadyReplayApply apply) && apply != null;
			bool rolledBack = ReadyReplayAssembly.ShouldRollbackApply(false)
			                  && began && replay.CompleteApply(false) && !replay.TryBeginApply(out _);
			return ResetAndClean(id, replay, rolledBack, rolledBack,
				"ReadyReplayAssembly.CompleteApply");
		}

		private static FaultProbeResult ResetAndClean(string id, ReadyReplayAssembly replay,
			bool oracle, bool invariant, string symbol)
		{
			replay.Reset();
			bool reset = !replay.TryBeginApply(out _);
			bool clean = ArrangeReady(replay) && replay.TryBeginApply(out ReadyReplayApply apply)
			             && apply != null && replay.CompleteApply(true);
			return new FaultProbeResult(oracle, invariant, reset, clean,
				"inject:" + id, "production-gate:" + symbol,
				"reset:ReadyReplayAssembly.Reset", "clean:complete-replay-applied");
		}

		private static bool ArrangeReady(ReadyReplayAssembly replay)
			=> replay.AcceptBatch(Header(1), new[] { Frame(1) }, (Host, 1f))
			       == ReadyReplayAssemblyResult.Pending
			   && replay.AcceptCommit(Proof(), Host, 2f) == ReadyReplayAssemblyResult.Pending
			   && replay.AcceptBatch(Header(0), new[] { Frame(0) }, (Host, 3f))
			       == ReadyReplayAssemblyResult.Ready;

		private static ReadyReplayAssembly NewReplay()
			=> new ReadyReplayAssembly(Token, Generation, new ReadyReplayAssemblyLimits
			{
				StartedAt = 0f, IdleSeconds = 30f, AbsoluteSeconds = 120f,
				MaxBufferedBytes = 4096,
			});

		private static ReadyReplayBatchHeader Header(int index)
			=> new ReadyReplayBatchHeader
			{
				SnapshotGeneration = Generation, ReplayId = ReplayId,
				BatchIndex = index, BatchCount = 2,
			};

		private static ReadyReplayProof Proof()
			=> new ReadyReplayProof
			{
				ReconnectToken = Token, SnapshotGeneration = Generation,
				ReplayId = ReplayId, BatchCount = 2,
			};

		private static byte[] Frame(byte marker) => new byte[] { 4, 3, 2, 1, marker };
	}
}
#endif
