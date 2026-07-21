#if DEBUG
using ONI_Together.DebugTools;
using ONI_Together.Patches.DLC.SpacedOut;

namespace ONI_Together.Networking.Packets.DLC.SpacedOut
{
	public sealed partial class RocketSettingsStatePacket
	{
		private const string RocketEvidenceScenario = "rocket";
		private static RocketSettingsStatePacket _clientEvidencePacket;

		private void RecordHostEvidence()
		{
			if (!MultiplayerSession.IsHost || Revision > long.MaxValue)
				return;
			string state = Data.CanonicalEvidenceState();
			IntegrationScenarioEvidenceCore.Log(
				RocketEvidenceScenario, "host-submit", (long)Revision, true, state);
			IntegrationScenarioEvidenceCore.Log(
				RocketEvidenceScenario, "final-state", (long)Revision, true, state);
		}

		private void BeginClientEvidence()
		{
			if (MultiplayerSession.IsClient && Revision <= long.MaxValue)
				_clientEvidencePacket = this;
		}

		private void EndClientEvidence()
		{
			if (ReferenceEquals(_clientEvidencePacket, this))
				_clientEvidencePacket = null;
		}

		internal static void RecordClientOriginalBlocked()
		{
			RocketSettingsStatePacket packet = _clientEvidencePacket;
			if (packet == null)
				return;
			IntegrationScenarioEvidenceCore.Log(
				RocketEvidenceScenario, "client-original-blocked", (long)packet.Revision,
				applied: false, packet.Data.CanonicalEvidenceState());
		}

		private void RecordClientAppliedEvidence()
		{
			if (!RocketSettingsSync.TryCaptureByTarget(Data, out RocketSettingsPacketData current))
				return;
			long revision = (long)Revision;
			string state = current.CanonicalEvidenceState();
			IntegrationScenarioEvidenceCore.Log(
				RocketEvidenceScenario, "client-apply", revision, true, state);
			IntegrationScenarioEvidenceCore.Log(
				RocketEvidenceScenario, "revision-accepted", revision, true, state);
			IntegrationScenarioEvidenceCore.Log(
				RocketEvidenceScenario, "final-state", revision, true, state);
			IntegrationScenarioEvidenceCore.Log(
				RocketEvidenceScenario, "revision-duplicate", revision,
				ShouldAcceptRevision(Revision, Revision), state);
			ulong older = Revision - 1;
			IntegrationScenarioEvidenceCore.Log(
				RocketEvidenceScenario, "revision-out-of-order", (long)older,
				ShouldAcceptRevision(Revision, older), state);
		}
	}
}
#endif
