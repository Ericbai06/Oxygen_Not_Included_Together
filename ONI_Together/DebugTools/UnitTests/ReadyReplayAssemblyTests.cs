#if DEBUG
using System;
using System.Collections.Generic;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class ReadyReplayAssemblyTests
	{
		private const ulong Token = 71;
		private const long Generation = 42;
		private const ulong ReplayId = 99;
		private static readonly DispatchContext HostContext = new(1, true, 7, 11);

		[UnitTest(name: "Ready replay accepts B1, commit, B0 and applies B0 then B1", category: "Networking")]
		public static UnitTestResult BatchCommitBatchReordersBeforeApply()
			=> RunOrder(commitFirst: false);

		[UnitTest(name: "Ready replay accepts commit, B1, B0 and applies B0 then B1", category: "Networking")]
		public static UnitTestResult CommitBatchBatchReordersBeforeApply()
			=> RunOrder(commitFirst: true);

		[UnitTest(name: "Ready replay batch duplicates are idempotent or conflict-terminal", category: "Networking")]
		public static UnitTestResult BatchDuplicatesKeepFirstPayload()
		{
			ReadyReplayAssembly replay = NewReplay();
			byte[] first = Frame(1);
			ReadyReplayAssemblyResult buffered = replay.AcceptBatch(
				Header(1), new[] { first }, (HostContext, 1f));
			int expectedBytes = sizeof(int) + first.Length;
			ReadyReplayAssemblyResult duplicate = replay.AcceptBatch(
				Header(1), new[] { Frame(1) }, (HostContext, 2f));
			if (ReadyReplayAssemblyResult.Pending != buffered
			    || ReadyReplayAssemblyResult.Duplicate != duplicate
			    || 1 != replay.ReceivedBatchCountForTests
			    || expectedBytes != replay.BufferedBytesForTests
			    || replay.FailedForTests || replay.AppliedForTests
			    || replay.TryBeginApply(out _))
			{
				return UnitTestResult.Fail(
					"An identical future batch multiplied, replaced, applied, or failed the replay");
			}

			replay = NewReplay();
			replay.AcceptBatch(Header(1), new[] { Frame(1) }, (HostContext, 1f));
			ReadyReplayAssemblyResult conflict = replay.AcceptBatch(
				Header(1), new[] { Frame(9) }, (HostContext, 2f));
			ReadyReplayAssemblyResult afterFailure = replay.AcceptBatch(
				Header(0), new[] { Frame(0) }, (HostContext, 3f));
			return ReadyReplayAssemblyResult.Terminal == conflict
			       && ReadyReplayAssemblyResult.Terminal == afterFailure
			       && replay.FailedForTests && !replay.AppliedForTests
			       && 0 == replay.BufferedBytesForTests && !replay.TryBeginApply(out _)
				? UnitTestResult.Pass("Equal duplicates are idempotent; conflicting duplicates fail terminally")
				: UnitTestResult.Fail("A conflicting duplicate was accepted or left replay work/proof eligible");
		}

		[UnitTest(name: "Ready replay rejects stale identity and dispatch context", category: "Networking")]
		public static UnitTestResult StaleIdentityAndContextAreRejected()
		{
			ReadyReplayBatchHeader staleGeneration = Header(0);
			staleGeneration.SnapshotGeneration = Generation - 1;
			ReadyReplayBatchHeader staleReplay = Header(0);
			staleReplay.ReplayId = ReplayId - 1;
			ReadyReplayProof staleToken = Proof();
			staleToken.ReconnectToken = Token - 1;
			ReadyReplayProof staleCommitGeneration = Proof();
			staleCommitGeneration.SnapshotGeneration = Generation - 1;
			ReadyReplayProof staleCommitReplay = Proof();
			staleCommitReplay.ReplayId = ReplayId - 1;
			var oldConnection = new DispatchContext(
				HostContext.SenderId, true, HostContext.ConnectionGeneration - 1,
				HostContext.SessionEpoch);
			var otherSender = new DispatchContext(
				HostContext.SenderId + 1, true, HostContext.ConnectionGeneration,
				HostContext.SessionEpoch);
			var oldSession = new DispatchContext(
				HostContext.SenderId, true, HostContext.ConnectionGeneration,
				HostContext.SessionEpoch - 1);
			var nonHost = new DispatchContext(
				HostContext.SenderId, false, HostContext.ConnectionGeneration,
				HostContext.SessionEpoch);

			bool rejected = RejectsBatch(staleGeneration, HostContext)
			                && RejectsBatch(staleReplay, HostContext)
			                && RejectsCommit(staleToken, HostContext)
			                && RejectsCommit(staleCommitGeneration, HostContext)
			                && RejectsCommit(staleCommitReplay, HostContext)
			                && RejectsBatch(Header(0), oldConnection)
			                && RejectsBatch(Header(0), otherSender)
			                && RejectsBatch(Header(0), oldSession)
			                && RejectsBatch(Header(0), nonHost);
			return rejected
				? UnitTestResult.Pass("Generation, replay, token, connection, sender, session, and role are bound")
				: UnitTestResult.Fail("Stale replay identity or dispatch context became apply/proof eligible");
		}

		[UnitTest(name: "Ready replay commit batch count mismatch fails terminally", category: "Networking")]
		public static UnitTestResult CommitBatchCountMismatchIsTerminal()
		{
			ReadyReplayAssembly replay = NewReplay();
			replay.AcceptBatch(Header(1), new[] { Frame(1) }, (HostContext, 1f));
			ReadyReplayProof mismatch = Proof();
			mismatch.BatchCount = 1;
			ReadyReplayAssemblyResult actual = replay.AcceptCommit(
				mismatch, HostContext, 2f);
			return ReadyReplayAssemblyResult.Terminal == actual
			       && replay.FailedForTests && !replay.AppliedForTests
			       && 0 == replay.BufferedBytesForTests && !replay.TryBeginApply(out _)
				? UnitTestResult.Pass("Commit batch count mismatch clears work and fails terminally")
				: UnitTestResult.Fail("Commit batch count mismatch remained apply/proof eligible");
		}

		[UnitTest(name: "Ready replay apply failure is terminal and never Applied", category: "Networking")]
		public static UnitTestResult ApplyFailureWithholdsAppliedProof()
		{
			ReadyReplayAssembly replay = ArrangeReady();
			if (!replay.TryBeginApply(out ReadyReplayApply apply)
			    || apply == null || replay.AppliedForTests)
				return UnitTestResult.Fail("Could not begin the arranged replay apply exactly once");
			bool completed = replay.CompleteApply(succeeded: false);
			ReadyReplayAssemblyResult batchAfterFailure = replay.AcceptBatch(
				Header(0), new[] { Frame(0) }, (HostContext, 4f));
			ReadyReplayAssemblyResult commitAfterFailure = replay.AcceptCommit(
				Proof(), HostContext, 5f);
			return completed && replay.FailedForTests && !replay.AppliedForTests
			       && ReadyReplayAssemblyResult.Terminal == batchAfterFailure
			       && ReadyReplayAssemblyResult.Terminal == commitAfterFailure
			       && !replay.TryBeginApply(out _) && !replay.CompleteApply(succeeded: true)
				? UnitTestResult.Pass("Apply failure is terminal and cannot emit or later gain Applied proof")
				: UnitTestResult.Fail("Apply failure retried, accepted input, or became Applied");
		}

		[UnitTest(name: "Ready replay reset clears terminal cache for a new replay", category: "Networking")]
		public static UnitTestResult ResetAllowsFreshReplay()
		{
			ReadyReplayAssembly replay = NewReplay();
			replay.AcceptBatch(Header(1), new[] { Frame(1) }, (HostContext, 1f));
			replay.AcceptBatch(Header(1), new[] { Frame(9) }, (HostContext, 2f));
			if (!replay.FailedForTests)
				return UnitTestResult.Fail("Could not arrange a terminal replay before reset");
			replay.Reset();
			const ulong nextReplayId = ReplayId + 1;
			ReadyReplayAssemblyResult batch1 = replay.AcceptBatch(
				Header(1, nextReplayId), new[] { Frame(1) }, (HostContext, 3f));
			ReadyReplayAssemblyResult commit = replay.AcceptCommit(
				Proof(nextReplayId), HostContext, 4f);
			ReadyReplayAssemblyResult batch0 = replay.AcceptBatch(
				Header(0, nextReplayId), new[] { Frame(0) }, (HostContext, 5f));
			if (ReadyReplayAssemblyResult.Pending != batch1
			    || ReadyReplayAssemblyResult.Pending != commit
			    || ReadyReplayAssemblyResult.Ready != batch0
			    || replay.FailedForTests || replay.AppliedForTests)
				return UnitTestResult.Fail("Reset retained terminal/cache identity or rejected a fresh replay");
			return ApplyStrictlyOnce(replay) == null
				? UnitTestResult.Pass("Reset clears the old cache and permits a new replay")
				: UnitTestResult.Fail("Fresh replay did not apply B0 then B1 exactly once after reset");
		}

		private static UnitTestResult RunOrder(bool commitFirst)
		{
			ReadyReplayAssembly replay = NewReplay();
			if (commitFirst)
			{
				if (!PendingWithoutApply(replay.AcceptCommit(Proof(), HostContext, 1f), replay))
					return UnitTestResult.Fail("Commit applied or produced proof before any batch arrived");
			}
			if (!PendingWithoutApply(
				    replay.AcceptBatch(Header(1), new[] { Frame(1) }, (HostContext, 2f)), replay))
				return UnitTestResult.Fail("B1 applied or produced proof while B0 was missing");
			if (!commitFirst
			    && !PendingWithoutApply(replay.AcceptCommit(Proof(), HostContext, 3f), replay))
				return UnitTestResult.Fail("Commit applied or produced proof while B0 was missing");
			ReadyReplayAssemblyResult closing = replay.AcceptBatch(
				Header(0), new[] { Frame(0) }, (HostContext, 4f));
			if (ReadyReplayAssemblyResult.Ready != closing || replay.AppliedForTests)
				return UnitTestResult.Fail("Completing B0 did not make exactly one replay ready");
			string failure = ApplyStrictlyOnce(replay);
			return failure == null
				? UnitTestResult.Pass("All inputs wait, then B0 and B1 apply exactly once in index order")
				: UnitTestResult.Fail(failure);
		}

		private static bool PendingWithoutApply(
			ReadyReplayAssemblyResult actual, ReadyReplayAssembly replay)
			=> ReadyReplayAssemblyResult.Pending == actual
			   && !replay.AppliedForTests && !replay.TryBeginApply(out _);

		private static string ApplyStrictlyOnce(ReadyReplayAssembly replay)
		{
			if (!replay.TryBeginApply(out ReadyReplayApply apply) || apply == null)
				return "Ready replay could not begin apply";
			if (2 != apply.Batches.Count || 1 != apply.Batches[0].Length
			    || 1 != apply.Batches[1].Length || 0 != Marker(apply.Batches[0][0])
			    || 1 != Marker(apply.Batches[1][0]))
				return "Ready replay did not expose B0 then B1 exactly once";
			if (!replay.CompleteApply(succeeded: true) || !replay.AppliedForTests
			    || replay.TryBeginApply(out _) || replay.CompleteApply(succeeded: true))
				return "Ready replay was not terminally Applied exactly once";
			return null;
		}

		private static bool RejectsBatch(
			ReadyReplayBatchHeader candidate, DispatchContext context)
		{
			ReadyReplayAssembly replay = NewReplay();
			replay.AcceptBatch(Header(1), new[] { Frame(1) }, (HostContext, 1f));
			ReadyReplayAssemblyResult actual = replay.AcceptBatch(
				candidate, new[] { Frame(0) }, (context, 2f));
			return ReadyReplayAssemblyResult.Rejected == actual
			       && !replay.AppliedForTests && !replay.TryBeginApply(out _);
		}

		private static bool RejectsCommit(
			ReadyReplayProof candidate, DispatchContext context)
		{
			ReadyReplayAssembly replay = NewReplay();
			replay.AcceptBatch(Header(1), new[] { Frame(1) }, (HostContext, 1f));
			ReadyReplayAssemblyResult actual = replay.AcceptCommit(candidate, context, 2f);
			return ReadyReplayAssemblyResult.Rejected == actual
			       && !replay.AppliedForTests && !replay.TryBeginApply(out _);
		}

		private static ReadyReplayAssembly ArrangeReady()
		{
			ReadyReplayAssembly replay = NewReplay();
			replay.AcceptBatch(Header(1), new[] { Frame(1) }, (HostContext, 1f));
			replay.AcceptCommit(Proof(), HostContext, 2f);
			replay.AcceptBatch(Header(0), new[] { Frame(0) }, (HostContext, 3f));
			return replay;
		}

		private static ReadyReplayAssembly NewReplay()
			=> ReadyReplayAssembly.CreateForTests(Token, Generation, new ReadyReplayAssemblyLimits
			{
				StartedAt = 0f,
				IdleSeconds = 30f,
				AbsoluteSeconds = 120f,
				MaxBufferedBytes = 4096,
			});

		private static ReadyReplayBatchHeader Header(int index, ulong replayId = ReplayId)
			=> new()
			{
				SnapshotGeneration = Generation,
				ReplayId = replayId,
				BatchIndex = index,
				BatchCount = 2,
			};

		private static ReadyReplayProof Proof(ulong replayId = ReplayId)
			=> new()
			{
				ReconnectToken = Token,
				SnapshotGeneration = Generation,
				ReplayId = replayId,
				BatchCount = 2,
			};

		private static byte[] Frame(byte marker)
			=> new byte[] { 4, 3, 2, 1, marker };

		private static byte Marker(IReadOnlyList<byte> frame) => frame[4];
	}
}
#endif
