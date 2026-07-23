using System.IO;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.DebugTools;
using Shared.Interfaces.Networking;

#if DEBUG
namespace ONI_Together.Networking.Packets.DLC.SpacedOut
{
	internal sealed class DlcRuntimeProfilePacket : IPacket, IHostOnlyPacket
	{
		private const string RevisionDomain = "dlc-runtime-profile";

		public int NetId;
		public ulong Revision;
		public bool Working;
		internal string ScenarioActionProfile = string.Empty;

		public void Serialize(BinaryWriter writer)
		{
			Validate();
			writer.Write(NetId);
			writer.Write(Revision);
			writer.Write(Working);
			writer.Write(ScenarioActionProfile ?? string.Empty);
		}

		public void Deserialize(BinaryReader reader)
		{
			NetId = reader.ReadInt32();
			Revision = reader.ReadUInt64();
			Working = reader.ReadBoolean();
			ScenarioActionProfile = reader.ReadString();
			Validate();
		}

		public void OnDispatched()
		{
			if (!string.IsNullOrEmpty(ScenarioActionProfile))
			{
				if (ScenarioActionReceiverGate.TryEnter(
					ScenarioActionProfile, "dlc-runtime"))
					DlcRuntimeActionFlow.ExecuteClient(this);
				return;
			}
			ApplyRuntimePacket();
		}

		internal bool ApplyRuntimePacket()
		{
			if (MultiplayerSession.IsHost || !PacketHandler.CurrentContext.SenderIsHost
			    || !NetworkIdentityRegistry.TryGet(NetId, out NetworkIdentity identity)
			    || identity.gameObject.PrefabID() != ScoutRoverConfig.ID
			    || Revision <= NetworkIdentityRegistry.GetLastStateRevision(
				    NetId, RevisionDomain)
			    || !DlcRuntimeProfileRuntime.ApplyExplicitState(
				    identity.gameObject, Working))
				return false;
			if (!NetworkIdentityRegistry.TryAcceptStateRevision(
				    NetId, RevisionDomain, Revision))
				return false;
			return true;
		}

		internal static void Publish(int netId, bool working)
			=> PacketSender.SendToAllClients(new DlcRuntimeProfilePacket
			{
				NetId = netId,
				Revision = NetworkIdentityRegistry.NextAuthorityRevision(),
				Working = working,
			}, PacketSendMode.ReliableImmediate);

		private void Validate()
		{
			if (NetId == 0 || Revision == 0)
				throw new InvalidDataException("Invalid DLC runtime profile state");
		}

		private void LogClientEvidence()
		{
			IntegrationScenarioEvidenceCore.Log(CreateEvidence("client-apply"));
			IntegrationScenarioEvidenceCore.Log(CreateEvidence("final-state"));
		}

		internal TypedEvidenceEnvelope CreateEvidence(string phase)
			=> TypedEvidenceRuntimeContext.Create(
				"dlc-runtime", phase, (long)Revision,
				new DlcRuntimeTarget
				{
					DlcFamily = "SpacedOut",
					Prefab = ScoutRoverConfig.ID,
					Identity = "rover-7",
				},
				new DlcRuntimeState
				{
					StateMachineState = Working
						? "RobotIdleMonitor.working" : "RobotIdleMonitor.idle",
					AdmissionGeneration = (long)Revision,
				}, "sync:dlc-runtime-profile-client-apply");
	}
}
#endif
