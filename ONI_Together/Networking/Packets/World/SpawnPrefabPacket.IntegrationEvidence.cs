#if DEBUG
using System;
using ONI_Together.DebugTools;

namespace ONI_Together.Networking.Packets.World
{
	public partial class SpawnPrefabPacket
	{
		private const string LifecycleEvidenceScenario = "entity-lifecycle";

		private void RecordHostLifecycleEvidence()
		{
			if (!MultiplayerSession.IsHost || Revision > long.MaxValue)
				return;
			string state = CaptureLifecycleEvidenceState();
			IntegrationScenarioEvidenceCore.Log(
				LifecycleEvidenceScenario, "host-submit", (long)Revision, true, state);
			IntegrationScenarioEvidenceCore.Log(
				LifecycleEvidenceScenario, "final-state", (long)Revision, true, state);
		}

		private void RecordClientLifecycleEvidence()
		{
			if (!MultiplayerSession.IsClient || Revision > long.MaxValue)
				return;
			long revision = (long)Revision;
			string state = CaptureLifecycleEvidenceState();
			IntegrationScenarioEvidenceCore.Log(
				LifecycleEvidenceScenario, "client-apply", revision, true, state);
			IntegrationScenarioEvidenceCore.Log(
				LifecycleEvidenceScenario, "revision-accepted", revision, true, state);
			IntegrationScenarioEvidenceCore.Log(
				LifecycleEvidenceScenario, "final-state", revision, true, state);

			bool originalApplied = ShouldApply(
				localIsHost: true, senderIsHost: true, entityExists: true,
				lastRevision: Revision, incomingRevision: Revision, tombstoned: false);
			IntegrationScenarioEvidenceCore.Log(
				LifecycleEvidenceScenario, "client-original-blocked",
				revision, originalApplied, state);
			IntegrationScenarioEvidenceCore.Log(
				LifecycleEvidenceScenario, "revision-duplicate", revision,
				ShouldApply(false, true, true, Revision, Revision, false), state);
			ulong older = Revision - 1;
			IntegrationScenarioEvidenceCore.Log(
				LifecycleEvidenceScenario, "revision-out-of-order", (long)older,
				ShouldApply(false, true, true, Revision, older, false), state);
		}

		private string CaptureLifecycleEvidenceState()
			=> CanonicalEvidenceStateForTests(
				NetId, Revision, Hash, WorldId, BindExistingOnly, IsActive);

		internal static string CanonicalEvidenceStateForTests(
			int netId,
			ulong revision,
			int prefabHash,
			int worldId,
			bool bindExistingOnly,
			bool isActive)
			=> FormattableString.Invariant(
				$"netId={netId}|revision={revision}|prefabHash={prefabHash}|worldId={worldId}|bindExistingOnly={bindExistingOnly}|isActive={isActive}");
	}
}
#endif
