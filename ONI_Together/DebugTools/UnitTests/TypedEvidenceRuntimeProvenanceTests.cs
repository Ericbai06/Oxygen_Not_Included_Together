using System;
using ONI_Together.Networking;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class TypedEvidenceRuntimeProvenanceTests
	{
		[UnitTest(name: "Typed evidence: host runtime rejects receiver outcomes",
			category: "Integration")]
		public static UnitTestResult HostRuntimeRejectsReceiverOutcomes()
		{
			bool previous = MultiplayerSession.IsHost;
			try
			{
				Configure(isHost: true);
				if (!Rejected(() => CrossTypePhaseFixture.CreateFromStaticField()))
					return UnitTestResult.Fail("Host accepted a receiver phase from a cross-type static field");
				if (!Rejected(() => CrossTypePhaseFixture.CreateFromRuntime("revision-duplicate")))
					return UnitTestResult.Fail("Host accepted a receiver phase from a runtime parameter");
				return UnitTestResult.Pass("Host rejects receiver outcomes at the Create boundary");
			}
			finally
			{
				TypedEvidenceRuntimeContext.Reset();
				MultiplayerSession.IsHost = previous;
			}
		}

		[UnitTest(name: "Typed evidence: client branch preserves role phase and entry",
			category: "Integration")]
		public static UnitTestResult ClientBranchPreservesProvenance()
		{
			bool previous = MultiplayerSession.IsHost;
			try
			{
				Configure(isHost: false);
				TypedEvidenceEnvelope evidence = CreateForCurrentEndpoint();
				if (evidence.Role != "client" || evidence.Phase != "revision-accepted"
				    || evidence.EntryId != CrossTypePhaseFixture.ClientGateEntry)
					return UnitTestResult.Fail("Client branch lost endpoint, phase, or entry provenance");
				return TypedEvidenceContract.Validate(evidence).Count == 0
					? UnitTestResult.Pass("Client-only receiver branch reaches the real Create boundary")
					: UnitTestResult.Fail("Client receiver evidence is not contract-valid");
			}
			finally
			{
				TypedEvidenceRuntimeContext.Reset();
				MultiplayerSession.IsHost = previous;
			}
		}

		[UnitTest(name: "Typed evidence: client runtime rejects host submit",
			category: "Integration")]
		public static UnitTestResult ClientRuntimeRejectsHostSubmit()
		{
			bool previous = MultiplayerSession.IsHost;
			try
			{
				Configure(isHost: false);
				return Rejected(() => CrossTypePhaseFixture.CreateFromRuntime("host-submit"))
					? UnitTestResult.Pass("Client rejects host-submit at the Create boundary")
					: UnitTestResult.Fail("Client accepted host-submit from a runtime parameter");
			}
			finally
			{
				TypedEvidenceRuntimeContext.Reset();
				MultiplayerSession.IsHost = previous;
			}
		}

		private static TypedEvidenceEnvelope CreateForCurrentEndpoint()
			=> MultiplayerSession.IsHost
				? CrossTypePhaseFixture.CreateFromRuntime("host-submit")
				: CrossTypePhaseFixture.CreateFromStaticField();

		private static bool Rejected(Func<TypedEvidenceEnvelope> action)
		{
			try
			{
				action();
				return false;
			}
			catch (InvalidOperationException)
			{
				return true;
			}
		}

		private static void Configure(bool isHost)
		{
			MultiplayerSession.IsHost = isHost;
			TypedEvidenceRuntimeContext.Configure(new TypedEvidenceContextSnapshot
			{
				RunId = "run:phase-provenance",
				DllHash = "sha256:" + new string('1', 64),
				SessionEpoch = 1,
				ConnectionGeneration = 2,
				SnapshotGeneration = 3,
			});
		}
	}

	internal static class CrossTypePhaseFixture
	{
		internal const string ClientGateEntry = "sync:client-revision-gate";
		private static readonly string ReceiverPhase = "revision-accepted";

		internal static TypedEvidenceEnvelope CreateFromStaticField()
			=> Create(ReceiverPhase, ClientGateEntry);

		internal static TypedEvidenceEnvelope CreateFromRuntime(string phase)
			=> Create(phase, phase == "host-submit" ? "sync:host-send" : ClientGateEntry);

		private static TypedEvidenceEnvelope Create(string phase, string entryId)
		{
			var state = new DoorState
			{
				LifecycleRevision = 1, StateRevision = 2, Control = "Open",
			};
			return TypedEvidenceRuntimeContext.Create(
				"door", phase, 2, new DoorTarget { TargetNetId = 7 }, state, entryId);
		}
	}
}
