#if DEBUG
using ONI_Together.DebugTools;
using ONI_Together.Patches.DLC.SpacedOut;

namespace ONI_Together.Networking.Packets.DLC.SpacedOut
{
	public sealed partial class RocketSettingsStatePacket
	{
		private const string RocketEvidenceScenario = "rocket";
		private const string RocketEvidenceEntryId = "sync:b4c22ab1203aecc282cef612";
		private static RocketSettingsStatePacket _clientEvidencePacket;

		private void RecordHostEvidence()
		{
			if (!MultiplayerSession.IsHost || Revision > long.MaxValue)
				return;
			RocketEvidenceSnapshot evidence = CaptureEvidence(Data, (long)Revision);
			LogEvidence("host-submit", (long)Revision, evidence);
			LogEvidence("final-state", (long)Revision, evidence);
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
			LogEvidence(
				"client-original-blocked", (long)packet.Revision,
				CaptureEvidence(packet.Data, (long)packet.Revision));
		}

		private void RecordClientAppliedEvidence()
		{
			if (!RocketSettingsSync.TryCaptureByTarget(Data, out RocketSettingsPacketData current))
				return;
			long revision = (long)Revision;
			RocketEvidenceSnapshot evidence = CaptureEvidence(current, revision);
			LogEvidence("client-apply", revision, evidence);
			LogEvidence("revision-accepted", revision, evidence);
			LogEvidence("final-state", revision, evidence);
			if (!ShouldAcceptRevision(Revision, Revision))
				LogEvidence("revision-duplicate", revision, evidence);
			ulong older = Revision - 1;
			if (!ShouldAcceptRevision(Revision, older))
				LogEvidence("revision-out-of-order", (long)older, evidence);
		}

		private static void LogEvidence(
			string phase, long revision, RocketEvidenceSnapshot evidence)
			=> IntegrationScenarioEvidenceCore.Log(TypedEvidenceRuntimeContext.Create(
				RocketEvidenceScenario, phase, revision, evidence.Target, evidence.State,
				RocketEvidenceEntryId));

		private static RocketEvidenceSnapshot CaptureEvidence(
			RocketSettingsPacketData data, long settingsRevision)
		{
			long padNetId = data.HasPad
				? data.PadNetId
				: data.HasCurrentPad ? data.CurrentPadNetId : 0;
			return new RocketEvidenceSnapshot
			{
				Target = new RocketTarget
				{
					RocketNetId = data.TargetNetId,
					PadNetId = padNetId,
				},
				State = new RocketState
				{
					Destination = data.HasDestination
						? data.DestinationQ + "," + data.DestinationR
						: "none",
					CraftPhase = data.CraftPhase.ToString(),
					SettingsRevision = settingsRevision,
				},
			};
		}

		private sealed class RocketEvidenceSnapshot
		{
			internal RocketTarget Target { get; set; }
			internal RocketState State { get; set; }
		}
	}
}
#endif
