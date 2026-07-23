using ONI_Together.DebugTools;

namespace ONI_Together.Networking.Packets.Handshake
{
	public partial class GameStateRequestPacket
	{
		private const string DlcEvidenceEntryId = "sync:a458db708cc0920b66d4dca9";
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
			LogDlcEvidence("client-apply", AdmissionGeneration, this);
			LogDlcEvidence("revision-accepted", AdmissionGeneration, this);
			LogDlcEvidence("final-state", AdmissionGeneration, this);
			RecordDlcAdmissionGuardProbe(AdmissionGeneration, this);
		}

		private static void RecordDlcAdmissionGuardProbe(
			long generation, GameStateRequestPacket packet)
		{
			LogDlcEvidence("client-original-blocked", generation, packet);
			if (!ShouldAcceptAdmissionGeneration(_lastAdmissionGeneration, generation))
				LogDlcEvidence("revision-duplicate", generation, packet);
			long older = generation - 1;
			if (!ShouldAcceptAdmissionGeneration(_lastAdmissionGeneration, older))
				LogDlcEvidence("revision-out-of-order", older, packet);
		}

		private static void LogDlcAdmission(
			string phase, long generation, bool applied, GameStateRequestPacket packet)
		{
			LogDlcEvidence(phase, generation, packet);
			if (phase == "host-submit")
				LogDlcEvidence("final-state", generation, packet);
		}

		private static void LogDlcEvidence(
			string phase, long generation, GameStateRequestPacket packet)
		{
			var target = new DlcRuntimeTarget
			{
				DlcFamily = DlcFamily(packet.ActiveDlcIds),
				Prefab = nameof(GameStateRequestPacket),
				Identity = packet.ModBuildFingerprint,
			};
			IntegrationScenarioEvidenceCore.Log(TypedEvidenceRuntimeContext.Create(
				"dlc-runtime", phase, generation, target,
				new DlcRuntimeState
				{
					StateMachineState = "admitted",
					AdmissionGeneration = packet.AdmissionGeneration,
				},
				DlcEvidenceEntryId));
		}

		private static string DlcFamily(System.Collections.Generic.ISet<string> ids)
		{
			if (ids.Contains(DlcManager.DLC5_ID)) return "Aquatic";
			if (ids.Contains(DlcManager.DLC4_ID)) return "Prehistoric";
			if (ids.Contains(DlcManager.DLC3_ID)) return "Bionic";
			if (ids.Contains(DlcManager.DLC2_ID)) return "Frosty";
			if (ids.Contains(DlcManager.EXPANSION1_ID)) return "SpacedOut";
			return "Common";
		}
#endif
	}
}
