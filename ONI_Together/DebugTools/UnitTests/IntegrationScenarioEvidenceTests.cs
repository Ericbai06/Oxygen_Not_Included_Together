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
			string[] actual = TypedEvidenceContract.Scenarios.ToArray();
			if (!ExpectedScenarios.SequenceEqual(actual) || actual.Distinct().Count() != 22)
				return UnitTestResult.Fail("Integration scenario identifiers changed or are not unique");

			return UnitTestResult.Pass("Integration scenario catalog contains the exact 22 identifiers");
		}

		[UnitTest(name: "Typed integration evidence requires complete causal facts", category: "Integration")]
		public static UnitTestResult CompleteTypedCausalEvidenceIsAccepted()
		{
			IntegrationScenarioEvidence remoteDig = CompleteRemoteDigEvidence();
			if (!IntegrationScenarioEvidenceCore.Validate(remoteDig))
				return UnitTestResult.Fail("Complete remote-dig typed causal evidence was rejected");

			IntegrationScenarioEvidence reconnect = CompleteReconnectEvidence();
			SetMatchingPostReconnectState(reconnect);
			return IntegrationScenarioEvidenceCore.Validate(reconnect)
				? UnitTestResult.Pass("Typed evidence preserves complete normal and reconnect causality")
				: UnitTestResult.Fail("Complete reconnect typed causal evidence was rejected");
		}

		[UnitTest(name: "Integration evidence rejects missing or inconsistent causal facts", category: "Integration")]
		public static UnitTestResult MissingOrInconsistentEvidenceFails()
		{
			IntegrationScenarioEvidence evidence = CompleteRemoteDigEvidence();
			evidence.HostSubmitObserved = false;
			if (IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("Missing host submit was accepted");

			evidence = CompleteRemoteDigEvidence();
			evidence.ClientApplyObserved = false;
			if (IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("Missing client apply was accepted");

			evidence = CompleteRemoteDigEvidence();
			evidence.ClientOriginalBlocked = false;
			if (IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("Unblocked client original action was accepted");

			evidence = CompleteRemoteDigEvidence();
			evidence.ClientApplyRevision = 4;
			if (IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("Mismatched submit and apply revisions were accepted");

			evidence = CompleteRemoteDigEvidence();
			evidence.Accepted = IntegrationScenarioEvidenceCore.ProbeRevision(3, 3);
			if (IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("A rejected accepted-revision probe was accepted");

			evidence = CompleteRemoteDigEvidence();
			evidence.Duplicate = IntegrationScenarioEvidenceCore.ProbeRevision(2, 3);
			if (IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("An applied duplicate-revision probe was accepted");

			evidence = CompleteRemoteDigEvidence();
			evidence.OutOfOrder = IntegrationScenarioEvidenceCore.ProbeRevision(2, 3);
			if (IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("An applied out-of-order probe was accepted");

			evidence = CompleteRemoteDigEvidence();
			evidence.ClientState.Target = new RemoteDigTarget
			{
				MinionNetId = 7,
				TargetNetId = 8,
				TargetCell = 43,
			};
			if (IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("Mismatched concrete target was accepted");

			evidence = CompleteRemoteDigEvidence();
			evidence.ClientState.StateHash = "sha256:" + new string('0', 64);
			return IntegrationScenarioEvidenceCore.Validate(evidence)
				? UnitTestResult.Fail("Mismatched final typed state hash was accepted")
				: UnitTestResult.Pass("Incomplete and inconsistent typed causal evidence is rejected");
		}

		[UnitTest(name: "Reconnect evidence requires matching typed post-reconnect state", category: "Integration")]
		public static UnitTestResult ReconnectRequiresPostReconnectMatch()
		{
			IntegrationScenarioEvidence evidence = CompleteReconnectEvidence();
			if (IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("Reconnect evidence without a post-reconnect observation was accepted");

			SetMatchingPostReconnectState(evidence);
			if (!IntegrationScenarioEvidenceCore.Validate(evidence))
				return UnitTestResult.Fail("Matching typed post-reconnect evidence was rejected");

			evidence.PostReconnectClientState.SnapshotGeneration--;
			return IntegrationScenarioEvidenceCore.Validate(evidence)
				? UnitTestResult.Fail("Stale post-reconnect snapshot generation was accepted")
				: UnitTestResult.Pass("Reconnect requires matching typed state and generations");
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

		[UnitTest(name: "Causal evidence round trips the strict typed JSON envelope", category: "Integration")]
		public static UnitTestResult TypedEvidenceLogIsStableAndParseable()
		{
			TypedEvidenceEnvelope expected = TypedEvidenceTestFixture.RemoteDigEnvelope();
			expected.Phase = "client-apply";
			expected.Role = "client";
			string serialized = TypedEvidenceLogCodec.Serialize(expected);
			if (!serialized.StartsWith("[IntegrationEvidence] {", StringComparison.Ordinal))
				return UnitTestResult.Fail("Evidence was not serialized as one-line typed JSON");

			TypedEvidenceEnvelope parsed = TypedEvidenceLogCodec.Parse(serialized);
			if (parsed.Scenario != expected.Scenario || parsed.EntryId != expected.EntryId
			    || parsed.Phase != "client-apply" || parsed.Role != "client"
			    || parsed.Revision != expected.Revision || parsed.Sequence != expected.Sequence
			    || !(parsed.Target is RemoteDigTarget target) || target.TargetCell != 42
			    || !(parsed.State is RemoteDigState) || parsed.StateHash != expected.StateHash)
				return UnitTestResult.Fail("Typed identity, target, state, or causal fields did not round trip");

			return UnitTestResult.Pass("Strict typed JSON evidence round trips causal and domain fields");
		}

		[UnitTest(name: "Spawn lifecycle evidence is typed and packet-local", category: "Integration")]
		public static UnitTestResult SpawnLifecycleEvidenceIsTypedAndPacketLocal()
		{
			TypedEvidenceEnvelope first = SpawnPrefabPacket.CreateLifecycleEvidenceForTests(
				"final-state", 7, 41, "Minion", 2, active: false, tombstone: false);
			TypedEvidenceEnvelope repeated = SpawnPrefabPacket.CreateLifecycleEvidenceForTests(
				"final-state", 7, 41, "Minion", 2, active: false, tombstone: false);
			TypedEvidenceEnvelope nextRevision = SpawnPrefabPacket.CreateLifecycleEvidenceForTests(
				"final-state", 8, 41, "Minion", 2, active: false, tombstone: false);
			if (!(first.Target is EntityLifecycleTarget target) || target.NetId != 41
			    || target.Prefab != "Minion" || target.WorldId != 2
			    || !(first.State is EntityLifecycleState state) || state.LifecycleRevision != 7
			    || state.Active || state.Tombstone)
				return UnitTestResult.Fail("Spawn packet did not expose its concrete lifecycle target and state");
			if (TypedEvidenceContract.Validate(first).Count != 0
			    || TypedEvidenceLogCodec.Serialize(first) != TypedEvidenceLogCodec.Serialize(repeated))
				return UnitTestResult.Fail("Identical packet lifecycle fields produced unstable typed evidence");
			if (first.StateHash == nextRevision.StateHash)
				return UnitTestResult.Fail("Lifecycle revision was omitted from canonical typed state");

			return UnitTestResult.Pass("Spawn lifecycle evidence is typed and deterministic from packet fields");
		}

		private static IntegrationScenarioEvidence CompleteRemoteDigEvidence()
		{
			TypedEvidenceEnvelope host = TypedEvidenceTestFixture.RemoteDigEnvelope();
			TypedEvidenceEnvelope client = Clone(host);
			client.Role = "client";
			return CompleteEvidence("remote-dig", host, client);
		}

		private static IntegrationScenarioEvidence CompleteReconnectEvidence()
		{
			TypedEvidenceEnvelope host = ReconnectEnvelope("host", "final-state");
			TypedEvidenceEnvelope client = ReconnectEnvelope("client", "final-state");
			return CompleteEvidence("reconnect-world-state", host, client);
		}

		private static IntegrationScenarioEvidence CompleteEvidence(
			string scenario, TypedEvidenceEnvelope host, TypedEvidenceEnvelope client)
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
				HostState = host,
				ClientState = client,
			};
		}

		private static TypedEvidenceEnvelope ReconnectEnvelope(string role, string phase)
		{
			var state = new ReconnectWorldStateState
			{
				ConnectionGeneration = 2,
				SnapshotGeneration = 3,
				Grid = DomainRecord(10, '1'),
				Entity = DomainRecord(20, '2'),
				World = DomainRecord(3, '3'),
				Storage = DomainRecord(4, '4'),
				ClusterRocket = DomainRecord(1, '5'),
			};
			return new TypedEvidenceEnvelope
			{
				SchemaVersion = 1,
				RunId = "run:typed-evidence",
				DllHash = "sha256:" + new string('1', 64),
				Scenario = "reconnect-world-state",
				EntryId = "sync:test:reconnect-world-state",
				Role = role,
				SessionEpoch = 8,
				ConnectionGeneration = 2,
				SnapshotGeneration = 3,
				Phase = phase,
				RevisionDomain = "reconnect-world-state",
				Revision = 3,
				Sequence = 1,
				Target = new ReconnectWorldStateTarget { PeerId = "peer:client" },
				State = state,
				StateHash = TypedEvidenceContract.ComputeStateHash(state),
			};
		}

		private static ReconnectDomainRecord DomainRecord(int count, char hashDigit)
			=> new ReconnectDomainRecord
			{
				Count = count,
				Hash = "sha256:" + new string(hashDigit, 64),
			};

		private static TypedEvidenceEnvelope Clone(TypedEvidenceEnvelope source)
			=> TypedEvidenceLogCodec.Parse(TypedEvidenceLogCodec.Serialize(source));

		private static void SetMatchingPostReconnectState(IntegrationScenarioEvidence evidence)
		{
			evidence.PostReconnectHostState = Clone(evidence.HostState);
			evidence.PostReconnectHostState.Phase = "post-reconnect-state";
			evidence.PostReconnectClientState = Clone(evidence.ClientState);
			evidence.PostReconnectClientState.Phase = "post-reconnect-state";
		}
	}
}
