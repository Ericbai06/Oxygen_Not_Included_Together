using System.Collections.Generic;
using System.IO;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using Shared.Interfaces.Networking;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.Tools
{
	public enum BuildingActionKind : byte
	{
		QueueDeconstruct = 1,
		CancelDeconstruct = 2,
		CancelConstruct = 3,
	}

	public sealed class BuildingActionPacket : IPacket, IClientRelayable,
		ISenderBoundRelay, IHostAuthoritativeRelay
	{
		private static readonly Dictionary<int, ulong> ClientRevisions = [];
		private static long _clientEpoch;

		public static bool ProcessingIncoming;
		public ulong SenderId;
		public int NetId;
		public ulong LifecycleRevision;
		public ulong Revision;
		public BuildingActionKind Action;

		ulong ISenderBoundRelay.RelaySenderId => SenderId;

		public static BuildingActionPacket CreateLocal(int netId, BuildingActionKind action)
		{
			ulong lifecycle = NetworkIdentityRegistry.GetLastLifecycleRevision(netId);
			return new BuildingActionPacket
			{
				SenderId = NetworkConfig.GetLocalID(),
				NetId = netId,
				LifecycleRevision = lifecycle,
				Revision = MultiplayerSession.IsHost
					? NetworkIdentityRegistry.NextAuthorityRevision()
					: 0,
				Action = action,
			};
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			ValidateWire();
			writer.Write(SenderId);
			writer.Write(NetId);
			writer.Write(LifecycleRevision);
			writer.Write(Revision);
			writer.Write((byte)Action);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			SenderId = reader.ReadUInt64();
			NetId = reader.ReadInt32();
			LifecycleRevision = reader.ReadUInt64();
			Revision = reader.ReadUInt64();
			Action = (BuildingActionKind)reader.ReadByte();
			ValidateWire();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();
			DispatchContext context = PacketHandler.CurrentContext;
			if (MultiplayerSession.IsHost)
			{
				if (!IsValidClientRequest(context) || !TryApply())
					return;
				SenderId = NetworkConfig.GetLocalID();
				Revision = NetworkIdentityRegistry.NextAuthorityRevision();
				PacketSender.SendToAllClients(this, PacketSendMode.ReliableImmediate);
				LogHostOutcome();
				return;
			}

			if (_clientEpoch != context.SessionEpoch)
			{
				_clientEpoch = context.SessionEpoch;
				ClientRevisions.Clear();
			}
			ulong current = ClientRevisions.TryGetValue(NetId, out ulong value) ? value : 0;
			if (!IsValidHostOutcome(context, current))
				return;
			LogRevisionOutcome(current);
			if (!ShouldAcceptRevision(current, Revision))
				return;
			if (!TryApply())
				return;
			ClientRevisions[NetId] = Revision;
#if DEBUG
			string state = CanonicalState(NetId, Action);
			IntegrationScenarioEvidenceCore.Log("deconstruct", "client-apply", (long)Revision, true, state);
			IntegrationScenarioEvidenceCore.Log("deconstruct", "final-state", (long)Revision, true, state);
#endif
		}

		private bool IsValidClientRequest(DispatchContext context)
			=> !context.SenderIsHost && context.IsVerifiedHostBroadcast
			   && context.SenderId == SenderId && Revision == 0
			   && PacketHandler.IsCurrentDispatchContext(context)
			   && IsCurrentLifecycle();

		private bool IsValidHostOutcome(DispatchContext context, ulong current)
		{
			return context.SenderIsHost && context.SenderId == MultiplayerSession.HostUserID
			       && context.SenderId == SenderId && Revision != 0
			       && PacketHandler.IsCurrentDispatchContext(context)
			       && IsCurrentLifecycle();
		}

		private void LogRevisionOutcome(ulong current)
		{
#if DEBUG
			string phase = Revision > current ? "revision-accepted"
				: Revision == current ? "revision-duplicate" : "revision-out-of-order";
			IntegrationScenarioEvidenceCore.Log(
				"deconstruct", phase, (long)Revision, Revision > current,
				CanonicalState(NetId, Action));
#endif
		}

		internal void LogHostOutcome()
		{
#if DEBUG
			string state = CanonicalState(NetId, Action);
			IntegrationScenarioEvidenceCore.Log("deconstruct", "host-submit", (long)Revision, true, state);
			IntegrationScenarioEvidenceCore.Log("deconstruct", "final-state", (long)Revision, true, state);
#endif
		}

		private bool IsCurrentLifecycle()
			=> LifecycleRevision != 0
			   && LifecycleRevision == NetworkIdentityRegistry.GetLastLifecycleRevision(NetId)
			   && !NetworkIdentityRegistry.IsLifecycleTombstoned(NetId);

		private bool TryApply()
		{
			if (!NetworkIdentityRegistry.TryGet(NetId, out NetworkIdentity identity)
			    || identity?.gameObject == null)
				return false;

			ProcessingIncoming = true;
			try
			{
				switch (Action)
				{
					case BuildingActionKind.QueueDeconstruct:
						return identity.gameObject.TryGetComponent(out Deconstructable queued)
						       && ApplyQueue(queued);
					case BuildingActionKind.CancelDeconstruct:
						return identity.gameObject.TryGetComponent(out Deconstructable cancelled)
						       && ApplyCancel(cancelled);
					case BuildingActionKind.CancelConstruct:
						identity.gameObject.Trigger(2127324410);
						return true;
					default:
						return false;
				}
			}
			finally
			{
				ProcessingIncoming = false;
			}
		}

		private static bool ApplyQueue(Deconstructable target)
		{
			target.QueueDeconstruction(userTriggered: true);
			return true;
		}

		private static bool ApplyCancel(Deconstructable target)
		{
			target.CancelDeconstruction();
			return true;
		}

		private void ValidateWire()
		{
			if (SenderId == 0 || NetId == 0 || LifecycleRevision == 0
			    || LifecycleRevision > long.MaxValue || Revision > long.MaxValue
			    || Action < BuildingActionKind.QueueDeconstruct
			    || Action > BuildingActionKind.CancelConstruct)
				throw new InvalidDataException("Invalid building action metadata");
		}

		internal static bool ShouldAcceptRevision(ulong current, ulong incoming)
			=> NetworkIdentityRegistry.IsNewerRevision(current, incoming);

		internal static string CanonicalState(int netId, BuildingActionKind action)
			=> netId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + action;

		internal static void ResetClientRevisionState()
		{
			_clientEpoch = 0;
			ClientRevisions.Clear();
		}
	}
}
