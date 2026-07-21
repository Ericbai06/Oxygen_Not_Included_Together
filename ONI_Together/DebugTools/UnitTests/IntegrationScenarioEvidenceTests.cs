using System;
using System.Linq;
using ONI_Together.Networking.Packets.World;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class IntegrationScenarioEvidenceTests
	{
		private static readonly string[] ExpectedScenarios =
		{
			"remote-dig", "building-lifecycle", "research", "priority", "schedule",
			"building-config", "door", "uproot", "toggle", "inventory", "storage",
			"pickup", "deconstruct", "effect", "chat", "cursor", "animation", "motion",
			"entity-lifecycle", "dlc-runtime", "rocket", "reconnect-world-state",
		};

		[UnitTest(name: "Integration evidence catalog is the exact real two-machine matrix", category: "Integration")]
		public static UnitTestResult CatalogIsExact()
		{
			string[] actual = IntegrationScenarioEvidenceCore.Scenarios.ToArray();
			if (!ExpectedScenarios.SequenceEqual(actual) || actual.Distinct().Count() != 22)
				return UnitTestResult.Fail("Integration scenario identifiers changed or are not unique");

			return UnitTestResult.Pass("Integration scenario catalog contains the exact 22 identifiers");
		}

		[UnitTest(name: "Every integration scenario requires complete causal evidence", category: "Integration")]
		public static UnitTestResult EveryScenarioRequiresCompleteEvidence()
		{
			foreach (string scenario in ExpectedScenarios)
			{
				IntegrationScenarioEvidence evidence = CompleteEvidence(scenario);
				if (scenario == "reconnect-world-state")
					SetMatchingPostReconnectState(evidence);
				if (!IntegrationScenarioEvidenceCore.Validate(evidence))
					return UnitTestResult.Fail("Complete evidence was rejected for scenario=" + scenario);
			}

			return UnitTestResult.Pass("All 22 scenarios accept complete structured evidence");
		}

		[UnitTest(name: "Integration evidence rejects missing or inconsistent causal facts", category: "Integration")]
		public static UnitTestResult MissingOrInconsistentEvidenceFails()
		{
			IntegrationScenarioEvidence evidence = CompleteEvidence("research");
			evidence.HostSubmitObserved = false;
			if (IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("Missing host submit was accepted");

			evidence = CompleteEvidence("research");
			evidence.ClientApplyObserved = false;
			if (IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("Missing client apply was accepted");

			evidence = CompleteEvidence("research");
			evidence.ClientOriginalBlocked = false;
			if (IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("Unblocked client original action was accepted");

			evidence = CompleteEvidence("research");
			evidence.ClientApplyRevision = 4;
			if (IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("Mismatched submit and apply revisions were accepted");

			evidence = CompleteEvidence("research");
			evidence.Accepted = IntegrationScenarioEvidenceCore.ProbeRevision(3, 3);
			if (IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("A rejected accepted-revision probe was accepted");

			evidence = CompleteEvidence("research");
			evidence.Duplicate = IntegrationScenarioEvidenceCore.ProbeRevision(2, 3);
			if (IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("An applied duplicate-revision probe was accepted");

			evidence = CompleteEvidence("research");
			evidence.OutOfOrder = IntegrationScenarioEvidenceCore.ProbeRevision(2, 3);
			if (IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("An applied out-of-order probe was accepted");

			evidence = CompleteEvidence("research");
			evidence.ClientState = "different";
			if (IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("Mismatched final state was accepted");

			evidence = CompleteEvidence("research");
			evidence.ClientHash = "sha256:different";
			return IntegrationScenarioEvidenceCore.Validate(evidence)
				? UnitTestResult.Fail("Mismatched final hash was accepted")
				: UnitTestResult.Pass("Incomplete and inconsistent causal evidence is rejected");
		}

		[UnitTest(name: "Reconnect evidence requires matching post-reconnect state and hash", category: "Integration")]
		public static UnitTestResult ReconnectRequiresPostReconnectMatch()
		{
			IntegrationScenarioEvidence evidence = CompleteEvidence("reconnect-world-state");
			if (IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("Reconnect evidence without a post-reconnect observation was accepted");

			SetMatchingPostReconnectState(evidence);
			if (!IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("Matching post-reconnect evidence was rejected");

			evidence.PostReconnectClientHash = "sha256:stale";
			return IntegrationScenarioEvidenceCore.Validate(evidence)
				? UnitTestResult.Fail("Stale post-reconnect client state was accepted")
				: UnitTestResult.Pass("Reconnect requires matching post-reconnect state and hash");
		}

		[UnitTest(name: "One-shot revision probe distinguishes all three outcomes", category: "Integration")]
		public static UnitTestResult RevisionProbeDistinguishesOutcomes()
		{
			RevisionProbeResult accepted = IntegrationScenarioEvidenceCore.ProbeRevision(2, 3);
			RevisionProbeResult duplicate = IntegrationScenarioEvidenceCore.ProbeRevision(3, 3);
			RevisionProbeResult outOfOrder = IntegrationScenarioEvidenceCore.ProbeRevision(3, 2);
			if (!accepted.Accepted || accepted.Duplicate || accepted.OutOfOrder || !accepted.Applied
			    || accepted.Revision != 3)
				return UnitTestResult.Fail("New revision was not classified as accepted and applied");
			if (duplicate.Accepted || !duplicate.Duplicate || duplicate.OutOfOrder || duplicate.Applied
			    || duplicate.Revision != 3)
				return UnitTestResult.Fail("Equal revision was not classified as duplicate and rejected");
			if (outOfOrder.Accepted || outOfOrder.Duplicate || !outOfOrder.OutOfOrder || outOfOrder.Applied
			    || outOfOrder.Revision != 2)
				return UnitTestResult.Fail("Older revision was not classified as out-of-order and rejected");

			return UnitTestResult.Pass("One-shot revision probe distinguishes accepted, duplicate, and out-of-order");
		}

		[UnitTest(name: "Integration evidence log grammar is stable and round trips", category: "Integration")]
		public static UnitTestResult EvidenceLogIsStableAndParseable()
		{
			const string expected = "[IntegrationEvidence] scenario=door;phase=client-apply;revision=7;applied=1;state=open;hash=sha256:abc";
			string serialized = EvidenceLogCodec.Serialize(
				"door", "client-apply", 7, true, "open", "sha256:abc");
			if (!string.Equals(expected, serialized, StringComparison.Ordinal))
				return UnitTestResult.Fail("Evidence serialization grammar changed");

			var parsed = EvidenceLogCodec.Parse(serialized);
			if (parsed.Scenario != "door" || parsed.Phase != "client-apply" || parsed.Revision != 7
			    || !parsed.Applied || parsed.State != "open" || parsed.Hash != "sha256:abc")
				return UnitTestResult.Fail("Evidence fields did not round trip through the machine parser");

			return UnitTestResult.Pass("Evidence log uses stable machine fields and round trips");
		}

		[UnitTest(name: "Spawn lifecycle evidence state is packet-local and deterministic", category: "Integration")]
		public static UnitTestResult SpawnLifecycleEvidenceStateIsPacketLocal()
		{
			string first = SpawnPrefabPacket.CanonicalEvidenceStateForTests(
				41, 7, 12345, 2, bindExistingOnly: true, isActive: false);
			string repeated = SpawnPrefabPacket.CanonicalEvidenceStateForTests(
				41, 7, 12345, 2, bindExistingOnly: true, isActive: false);
			string nextRevision = SpawnPrefabPacket.CanonicalEvidenceStateForTests(
				41, 8, 12345, 2, bindExistingOnly: true, isActive: false);
			if (string.IsNullOrEmpty(first) || !string.Equals(first, repeated, StringComparison.Ordinal))
				return UnitTestResult.Fail("Identical packet lifecycle fields produced unstable evidence state");
			if (string.Equals(first, nextRevision, StringComparison.Ordinal))
				return UnitTestResult.Fail("Lifecycle revision was omitted from canonical evidence state");

			return UnitTestResult.Pass("Spawn lifecycle evidence is deterministic from packet-local fields");
		}

		private static IntegrationScenarioEvidence CompleteEvidence(string scenario)
		{
			return new IntegrationScenarioEvidence
			{
				Scenario = scenario,
				HostSubmitObserved = true,
				HostSubmitRevision = 3,
				ClientApplyObserved = true,
				ClientApplyRevision = 3,
				ClientOriginalBlocked = true,
				Accepted = IntegrationScenarioEvidenceCore.ProbeRevision(2, 3),
				Duplicate = IntegrationScenarioEvidenceCore.ProbeRevision(3, 3),
				OutOfOrder = IntegrationScenarioEvidenceCore.ProbeRevision(3, 2),
				HostState = scenario + "-final",
				ClientState = scenario + "-final",
				HostHash = "sha256:final",
				ClientHash = "sha256:final",
			};
		}

		private static void SetMatchingPostReconnectState(IntegrationScenarioEvidence evidence)
		{
			evidence.PostReconnectHostState = evidence.HostState;
			evidence.PostReconnectClientState = evidence.ClientState;
			evidence.PostReconnectHostHash = evidence.HostHash;
			evidence.PostReconnectClientHash = evidence.ClientHash;
		}
	}
}
