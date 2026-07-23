using System;
using System.Collections.Generic;
using ONI_Together.Networking.Packets.Tools.Build;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class BuildCompletePendingTests
	{
		[UnitTest(name: "Build commit applies exactly once", category: "Sync")]
		public static UnitTestResult PendingCompletionBindsOnce()
		{
			var runtime = new FakeRuntime();
			IBuildRuntime previous = BuildRuntimeProvider.Current;
			try
			{
				BuildRuntimeProvider.Current = runtime;
				BuildCommitApplier.Reset();
				BuildCommit commit = Commit(new BuildOperationId(8, 17, 1), 17);
				ApplyResult first = BuildCommitApplier.Apply(commit);
				ApplyResult duplicate = BuildCommitApplier.Apply(commit);
				if (!first.Applied || first.Duplicate || !duplicate.Applied || !duplicate.Duplicate
				    || runtime.ApplyCount != 1)
					return UnitTestResult.Fail("Build commit was applied more than once");
				return UnitTestResult.Pass("Commit identity/revision makes client materialization idempotent");
			}
			finally
			{
				BuildCommitApplier.Reset();
				BuildRuntimeProvider.Current = previous;
			}
		}

		[UnitTest(name: "Build commit rejects a stale revision for same operation", category: "Sync")]
		public static UnitTestResult PendingCompletionLifecycleOrdering()
		{
			var runtime = new FakeRuntime();
			IBuildRuntime previous = BuildRuntimeProvider.Current;
			try
			{
				BuildRuntimeProvider.Current = runtime;
				BuildCommitApplier.Reset();
				BuildOperationId operation = new(8, 17, 2);
				ApplyResult first = BuildCommitApplier.Apply(Commit(operation, 20));
				ApplyResult replacement = BuildCommitApplier.Apply(Commit(operation, 19));
				if (!first.Applied || replacement.Applied || runtime.ApplyCount != 1)
					return UnitTestResult.Fail("A second lifecycle revision replaced an applied operation");
				return UnitTestResult.Pass("Applied build operation retains one authoritative revision");
			}
			finally
			{
				BuildCommitApplier.Reset();
				BuildRuntimeProvider.Current = previous;
			}
		}

		[UnitTest(name: "Build execution deduplicates operation requests", category: "Sync")]
		public static UnitTestResult PendingCompletionBounds()
		{
			var runtime = new FakeRuntime();
			IBuildRuntime previous = BuildRuntimeProvider.Current;
			try
			{
				BuildRuntimeProvider.Current = runtime;
				AuthoritativeBuildExecutor.Reset();
				BuildOperationId operation = new(8, 17, 3);
				BuildRequest request = Request(operation);
				bool first = AuthoritativeBuildExecutor.Execute(
					request, new HostBuildPolicy(false), out BuildCommit firstCommit, out _);
				bool second = AuthoritativeBuildExecutor.Execute(
					request, new HostBuildPolicy(false), out BuildCommit secondCommit, out _);
				if (!first || !second || firstCommit?.Revision.Value != secondCommit?.Revision.Value
				    || runtime.ExecuteCount != 1)
					return UnitTestResult.Fail("Duplicate operation executed the runtime more than once");
				return UnitTestResult.Pass("Host returns the original commit for duplicate operation identity");
			}
			finally
			{
				AuthoritativeBuildExecutor.Reset();
				BuildRuntimeProvider.Current = previous;
			}
		}

		[UnitTest(name: "Build rejection is remembered without reconnect semantics", category: "Sync")]
		public static UnitTestResult PendingCompletionSessionReset()
		{
			var runtime = new FakeRuntime { Reject = true };
			IBuildRuntime previous = BuildRuntimeProvider.Current;
			try
			{
				BuildRuntimeProvider.Current = runtime;
				AuthoritativeBuildExecutor.Reset();
				BuildRequest request = Request(new BuildOperationId(8, 17, 4));
				bool first = AuthoritativeBuildExecutor.Execute(
					request, new HostBuildPolicy(false), out _, out BuildRejected firstRejection);
				bool second = AuthoritativeBuildExecutor.Execute(
					request, new HostBuildPolicy(false), out _, out BuildRejected secondRejection);
				if (first || second || firstRejection == null || secondRejection == null
				    || firstRejection.Reason != BuildRejectionReason.Occupied
				    || secondRejection.OperationId != request.OperationId || runtime.ExecuteCount != 1)
					return UnitTestResult.Fail("Normal domain rejection was not remembered idempotently");
				return UnitTestResult.Pass("Rejected build stays a domain result and does not force transport reconnect");
			}
			finally
			{
				AuthoritativeBuildExecutor.Reset();
				BuildRuntimeProvider.Current = previous;
			}
		}

		private static BuildRequest Request(BuildOperationId operation)
			=> new(operation, "Tile", new SinglePlacementGeometry(
				10, Orientation.Neutral), new[] { "SandStone" },
				"DEFAULT_FACADE", 0, 5, (int)ObjectLayer.Building);

		private static BuildCommit Commit(BuildOperationId operation, ulong revision)
			=> new(Request(operation), operation,
				new[] { new PlacementOutcome(10, BuildPlacementKind.Queued) },
				Array.Empty<UtilityEdge>(), new BuildRevision(revision));

		private sealed class FakeRuntime : IBuildRuntime
		{
			internal int ExecuteCount { get; private set; }
			internal int ApplyCount { get; private set; }
			internal bool Reject { get; set; }

			public bool TryExecute(BuildRequest request, HostBuildPolicy policy,
				out BuildExecutionResult result, out BuildRejected rejection)
			{
				ExecuteCount++;
				if (Reject)
				{
					result = null;
					rejection = new BuildRejected(
						request.OperationId, BuildRejectionReason.Occupied, "occupied");
					return false;
				}
				rejection = null;
				result = new BuildExecutionResult(
					new[] { new PlacementOutcome(10, policy.InstantBuild
						? BuildPlacementKind.Completed : BuildPlacementKind.Queued) },
					Array.Empty<UtilityEdge>());
				return true;
			}

			public ApplyResult Apply(BuildCommit commit)
			{
				ApplyCount++;
				return ApplyResult.Success();
			}
		}
	}
}
