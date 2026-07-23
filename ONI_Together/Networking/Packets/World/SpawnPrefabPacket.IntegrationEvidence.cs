#if DEBUG
using ONI_Together.DebugTools;

namespace ONI_Together.Networking.Packets.World
{
	public partial class SpawnPrefabPacket
	{
		private const string LifecycleEvidenceScenario = "entity-lifecycle";
		private const string LifecycleEvidenceEntryId = "sync:cacb70d14a00b307d259a5d2";

		private void RecordHostLifecycleEvidence()
		{
			if (!MultiplayerSession.IsHost || Revision > long.MaxValue)
				return;
			LogLifecycleEvidence("host-submit", (long)Revision);
			LogLifecycleEvidence("final-state", (long)Revision);
		}

		private void RecordClientLifecycleEvidence()
		{
			if (!MultiplayerSession.IsClient || Revision > long.MaxValue)
				return;
			long revision = (long)Revision;
			LogLifecycleEvidence("client-apply", revision);
			LogLifecycleEvidence("revision-accepted", revision);
			LogLifecycleEvidence("final-state", revision);

			if (!ShouldApply(
			    localIsHost: true, senderIsHost: true, entityExists: true,
			    lastRevision: Revision, incomingRevision: Revision, tombstoned: false))
				LogLifecycleEvidence("client-original-blocked", revision);
			if (!ShouldApply(false, true, true, Revision, Revision, false))
				LogLifecycleEvidence("revision-duplicate", revision);
			ulong older = Revision - 1;
			if (!ShouldApply(false, true, true, Revision, older, false))
				LogLifecycleEvidence("revision-out-of-order", (long)older);
		}

		private void LogLifecycleEvidence(string phase, long revision)
		{
			string prefab = new Tag(Hash).ToString();
			IntegrationScenarioEvidenceCore.Log(TypedEvidenceRuntimeContext.Create(
				LifecycleEvidenceScenario, phase, revision,
				new EntityLifecycleTarget
				{
					NetId = NetId,
					Prefab = prefab,
					WorldId = WorldId,
				},
				new EntityLifecycleState
				{
					LifecycleRevision = (long)Revision,
					Active = IsActive,
					Tombstone = false,
				},
				LifecycleEvidenceEntryId));
		}

		internal static TypedEvidenceEnvelope CreateLifecycleEvidenceForTests(
			string phase, long revision, long netId, string prefab,
			int worldId, bool active, bool tombstone)
		{
			var state = new EntityLifecycleState
			{
				LifecycleRevision = revision,
				Active = active,
				Tombstone = tombstone,
			};
			return new TypedEvidenceEnvelope
			{
				SchemaVersion = 1,
				RunId = "test:spawn-lifecycle",
				DllHash = "sha256:" + new string('0', 64),
				Scenario = LifecycleEvidenceScenario,
				EntryId = LifecycleEvidenceEntryId,
				Role = "host",
				SessionEpoch = 0,
				ConnectionGeneration = 0,
				SnapshotGeneration = 0,
				Phase = phase,
				RevisionDomain = LifecycleEvidenceScenario,
				Revision = revision,
				Sequence = 0,
				Target = new EntityLifecycleTarget
				{
					NetId = netId,
					Prefab = prefab,
					WorldId = worldId,
				},
				State = state,
				StateHash = TypedEvidenceContract.ComputeStateHash(state),
			};
		}
	}
}
#endif
