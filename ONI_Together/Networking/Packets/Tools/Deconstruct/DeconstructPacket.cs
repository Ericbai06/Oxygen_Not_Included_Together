using System.IO;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using Shared.Interfaces.Networking;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.Tools.Deconstruct
{
	public sealed class DeconstructPacket : DragToolPacket, ISenderBoundRelay,
		IHostAuthoritativeRelay
	{
		private static ulong _clientRevision;
		private static long _clientEpoch;
#if DEBUG
		private int _evidenceBuildingNetId;
#endif

		public ulong SenderId;
		public ulong Revision;

		ulong ISenderBoundRelay.RelaySenderId => SenderId;

		public DeconstructPacket()
		{
			using var _ = Profiler.Scope();
			ToolInstance = DeconstructTool.Instance;
			ToolMode = DragToolMode.OnDragTool;
		}

		internal static DeconstructPacket CreateLocal(int targetCell, int distance)
		{
			var packet = new DeconstructPacket
			{
				cell = targetCell,
				distFromOrigin = distance,
				SenderId = NetworkConfig.GetLocalID(),
				Revision = MultiplayerSession.IsHost
					? NetworkIdentityRegistry.NextAuthorityRevision()
					: 0,
			};
#if DEBUG
			packet._evidenceBuildingNetId = ResolveBuildingNetId(targetCell);
#endif
			return packet;
		}

		public override void Serialize(BinaryWriter writer)
		{
			base.Serialize(writer);
			ValidateWire();
			writer.Write(SenderId);
			writer.Write(Revision);
		}

		public override void Deserialize(BinaryReader reader)
		{
			base.Deserialize(reader);
			SenderId = reader.ReadUInt64();
			Revision = reader.ReadUInt64();
			ValidateWire();
		}

		public override void OnDispatched()
		{
			DispatchContext context = PacketHandler.CurrentContext;
			if (MultiplayerSession.IsHost)
			{
				if (!IsValidClientRequest(context))
					return;
				base.OnDispatched();
				SenderId = NetworkConfig.GetLocalID();
				Revision = NetworkIdentityRegistry.NextAuthorityRevision();
				PacketSender.SendToAllClients(this, PacketSendMode.ReliableImmediate);
				LogHostOutcome();
				return;
			}

			if (_clientEpoch != context.SessionEpoch)
			{
				_clientEpoch = context.SessionEpoch;
				_clientRevision = 0;
			}
			if (!IsValidHostOutcome(context))
				return;
			LogRevisionOutcome();
			if (!ShouldAcceptRevision(_clientRevision, Revision))
				return;
			base.OnDispatched();
			_clientRevision = Revision;
#if DEBUG
			LogEvidence("client-apply", Revision, "sync:69ec0b319cb1b56d3833df00");
			LogEvidence("final-state", Revision, "sync:69ec0b319cb1b56d3833df00");
#endif
		}

		private bool IsValidClientRequest(DispatchContext context)
			=> !context.SenderIsHost && context.IsVerifiedHostBroadcast
			   && context.SenderId == SenderId && Revision == 0
			   && PacketHandler.IsCurrentDispatchContext(context);

		private bool IsValidHostOutcome(DispatchContext context)
			=> context.SenderIsHost && context.SenderId == MultiplayerSession.HostUserID
			   && context.SenderId == SenderId && Revision != 0
			   && PacketHandler.IsCurrentDispatchContext(context);

		private void LogRevisionOutcome()
		{
#if DEBUG
			string phase = Revision > _clientRevision ? "revision-accepted"
				: Revision == _clientRevision ? "revision-duplicate" : "revision-out-of-order";
			LogEvidence(phase, Revision, "sync:69ec0b319cb1b56d3833df00");
#endif
		}

		internal void LogHostOutcome(string entryId = "sync:b39d9aed5709080211e3bdba")
		{
#if DEBUG
			LogEvidence("host-submit", Revision, entryId);
			LogEvidence("final-state", Revision, entryId);
#endif
		}

#if DEBUG
		internal void LogOriginalBlocked(string entryId)
			=> LogEvidence("client-original-blocked", Revision, entryId);

		private void LogEvidence(string phase, ulong revision, string entryId)
		{
			if (_evidenceBuildingNetId == 0)
				_evidenceBuildingNetId = ResolveBuildingNetId(cell);
			IntegrationScenarioEvidenceCore.Log(TypedEvidenceRuntimeContext.Create(
				"deconstruct", phase, (long)revision,
				new DeconstructTarget
				{
					BuildingNetId = _evidenceBuildingNetId,
					TargetCell = cell,
				},
				new DeconstructState { Action = "drag", Tombstone = false }, entryId));
		}

		private static int ResolveBuildingNetId(int targetCell)
		{
			if (!Grid.IsValidCell(targetCell))
				return 0;
			UnityEngine.GameObject building = Grid.Objects[targetCell, (int)ObjectLayer.Building];
			return building?.GetComponent<ONI_Together.Networking.Components.NetworkIdentity>()?.NetId ?? 0;
		}
#endif

		private void ValidateWire()
		{
			if (SenderId == 0 || Revision > long.MaxValue || !Grid.IsValidCell(cell)
			    || distFromOrigin < 0 || distFromOrigin > Grid.CellCount)
				throw new InvalidDataException("Invalid deconstruct drag metadata");
		}

		internal static bool ShouldAcceptRevision(ulong current, ulong incoming)
			=> NetworkIdentityRegistry.IsNewerRevision(current, incoming);

		internal static string CanonicalState(int targetCell, int distance)
			=> targetCell.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"
			   + distance.ToString(System.Globalization.CultureInfo.InvariantCulture);

		internal static void ResetClientRevisionState()
		{
			_clientEpoch = 0;
			_clientRevision = 0;
		}
	}
}
