using ONI_Together.DebugTools;

namespace ONI_Together.Networking.Packets.Handshake
{
	public partial class GameStateRequestPacket
	{
		private static long _lastAdmissionGeneration;
		private static void ResetAdmissionGeneration() => _lastAdmissionGeneration = 0;

		internal static bool ShouldAcceptAdmissionGeneration(long current, long incoming)
			=> incoming > 0 && incoming > current;

		internal static bool TryAcceptAdmissionGeneration(long generation)
		{
			if (!ShouldAcceptAdmissionGeneration(_lastAdmissionGeneration, generation))
				return false;
			_lastAdmissionGeneration = generation;
			return true;
		}

#if DEBUG
		private void RecordAcceptedDlcAdmission()
		{
			string state = ProtocolCompatibility.CanonicalAdmissionState(
				ActiveDlcIds, ModBuildFingerprint);
			IntegrationScenarioEvidenceCore.Log(
				"dlc-runtime", "client-apply", AdmissionGeneration, true, state);
			IntegrationScenarioEvidenceCore.Log(
				"dlc-runtime", "revision-accepted", AdmissionGeneration, true, state);
			IntegrationScenarioEvidenceCore.Log(
				"dlc-runtime", "final-state", AdmissionGeneration, true, state);
			RecordDlcAdmissionGuardProbe(AdmissionGeneration, state);
		}

		private static void RecordDlcAdmissionGuardProbe(long generation, string state)
		{
			IntegrationScenarioEvidenceCore.Log(
				"dlc-runtime", "client-original-blocked", generation, false, state);
			IntegrationScenarioEvidenceCore.Log(
				"dlc-runtime", "revision-duplicate", generation,
				ShouldAcceptAdmissionGeneration(_lastAdmissionGeneration, generation), state);
			long older = generation - 1;
			IntegrationScenarioEvidenceCore.Log(
				"dlc-runtime", "revision-out-of-order", older,
				ShouldAcceptAdmissionGeneration(_lastAdmissionGeneration, older), state);
		}

		private static void LogDlcAdmission(
			string phase, long generation, bool applied, GameStateRequestPacket packet)
		{
			string state = ProtocolCompatibility.CanonicalAdmissionState(
				packet.ActiveDlcIds, packet.ModBuildFingerprint);
			IntegrationScenarioEvidenceCore.Log(
				"dlc-runtime", phase, generation, applied, state);
			if (phase == "host-submit")
				IntegrationScenarioEvidenceCore.Log(
					"dlc-runtime", "final-state", generation, true, state);
		}
#endif
	}
}
