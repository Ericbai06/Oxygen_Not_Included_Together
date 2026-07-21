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
			return new DeconstructPacket
			{
				cell = targetCell,
				distFromOrigin = distance,
				SenderId = NetworkConfig.GetLocalID(),
				Revision = MultiplayerSession.IsHost
					? NetworkIdentityRegistry.NextAuthorityRevision()
					: 0,
			};
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
			string state = CanonicalState(cell, distFromOrigin);
			IntegrationScenarioEvidenceCore.Log("deconstruct", "client-apply", (long)Revision, true, state);
			IntegrationScenarioEvidenceCore.Log("deconstruct", "final-state", (long)Revision, true, state);
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
			IntegrationScenarioEvidenceCore.Log(
				"deconstruct", phase, (long)Revision, Revision > _clientRevision,
				CanonicalState(cell, distFromOrigin));
#endif
		}

		internal void LogHostOutcome()
		{
#if DEBUG
			string state = CanonicalState(cell, distFromOrigin);
			IntegrationScenarioEvidenceCore.Log("deconstruct", "host-submit", (long)Revision, true, state);
			IntegrationScenarioEvidenceCore.Log("deconstruct", "final-state", (long)Revision, true, state);
#endif
		}

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
