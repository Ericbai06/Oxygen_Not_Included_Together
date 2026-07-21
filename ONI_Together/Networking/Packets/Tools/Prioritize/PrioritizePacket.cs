using System.IO;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.World;
using Shared.Interfaces.Networking;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.Tools.Prioritize
{
    public class PrioritizePacket : DragToolPacket
    {
        public PrioritizePacket()
        {
            using var _ = Profiler.Scope();

            ToolInstance = PrioritizeTool.Instance;
            ToolMode     = DragToolMode.OnDragTool;
        }

		public override void Serialize(BinaryWriter writer)
		{
			base.Serialize(writer);
			if (!PriorityAuthority.IsValidToolRequest(cell, distFromOrigin, Priority))
				throw new InvalidDataException("Invalid prioritize tool request");
		}

		public override void Deserialize(BinaryReader reader)
		{
			base.Deserialize(reader);
			if (!PriorityAuthority.IsValidToolRequest(cell, distFromOrigin, Priority))
				throw new InvalidDataException("Invalid prioritize tool request");
		}

		public override void OnDispatched()
		{
			DispatchContext context = PacketHandler.CurrentContext;
			bool protocolVerified = MultiplayerSession.GetPlayer(context.SenderId)?.ProtocolVerified == true;
			if (!ShouldAccept(MultiplayerSession.IsHost, context, protocolVerified) ||
			    !PriorityAuthority.IsValidToolRequest(cell, distFromOrigin, Priority))
				return;

			base.OnDispatched();
		}

		internal static bool ShouldAccept(
			bool localIsHost,
			DispatchContext context,
			bool protocolVerified)
			=> localIsHost && !context.SenderIsHost && context.IsVerifiedHostBroadcast && protocolVerified;
    }

	internal static class PriorityAuthority
	{
		internal static bool IsValidClientPriority(PrioritySetting priority)
		{
			switch (priority.priority_class)
			{
				case PriorityScreen.PriorityClass.basic:
				case PriorityScreen.PriorityClass.high:
					return priority.priority_value >= 1 && priority.priority_value <= 9;
				case PriorityScreen.PriorityClass.topPriority:
					return priority.priority_value == 1;
				default:
					return false;
			}
		}

		internal static bool IsValidStatePriority(int priorityClass, int priorityValue)
		{
			if (priorityClass < (int)PriorityScreen.PriorityClass.idle ||
			    priorityClass > (int)PriorityScreen.PriorityClass.compulsory)
				return false;
			return priorityValue >= -1 && priorityValue <= 9;
		}

		internal static bool IsValidToolRequest(int cell, int distance, PrioritySetting priority)
			=> Grid.IsValidCell(cell) && distance >= 0 && distance <= Grid.CellCount &&
			   IsValidClientPriority(priority);
	}

	public sealed class PrioritizeTargetRequestPacket : IPacket, IClientRelayable,
		ISenderBoundRelay, IHostAuthoritativeRelay
	{
		public ulong SenderId;
		ulong ISenderBoundRelay.RelaySenderId => SenderId;
		public ulong ClientRequestId;
		public int NetId;
		public ulong TargetLifecycleRevision;
		public ulong BasePriorityRevision;
		public int PriorityClass;
		public int PriorityValue;

		public void Serialize(BinaryWriter writer)
		{
			Validate();
			writer.Write(SenderId);
			writer.Write(ClientRequestId);
			writer.Write(NetId);
			writer.Write(TargetLifecycleRevision);
			writer.Write(BasePriorityRevision);
			writer.Write(PriorityClass);
			writer.Write(PriorityValue);
		}

		public void Deserialize(BinaryReader reader)
		{
			SenderId = reader.ReadUInt64();
			ClientRequestId = reader.ReadUInt64();
			NetId = reader.ReadInt32();
			TargetLifecycleRevision = reader.ReadUInt64();
			BasePriorityRevision = reader.ReadUInt64();
			PriorityClass = reader.ReadInt32();
			PriorityValue = reader.ReadInt32();
			Validate();
		}

		public void OnDispatched()
		{
			DispatchContext context = PacketHandler.CurrentContext;
			bool protocolVerified = MultiplayerSession.GetPlayer(context.SenderId)?.ProtocolVerified == true;
			if (!ShouldAccept(MultiplayerSession.IsHost, context, protocolVerified) ||
			    !NetworkIdentityRegistry.TryGet(NetId, out NetworkIdentity identity) ||
			    !PrioritizeStatePacket.IsCurrentHostIdentity(identity) ||
			    !identity.TryGetComponent(out Prioritizable prioritizable) ||
			    !prioritizable.IsPrioritizable())
				return;

			ulong hostRevision = PrioritizeStatePacket.GetHostRevision(
				NetId, identity.LifecycleRevision);
			if (identity.LifecycleRevision != TargetLifecycleRevision
			    || NetworkIdentityRegistry.IsLifecycleTombstoned(NetId)
			    || BasePriorityRevision != hostRevision)
			{
				PrioritizeStatePacket.SendHostCorrection(identity, SenderId, ClientRequestId);
				return;
			}
			var priority = new PrioritySetting((PriorityScreen.PriorityClass)PriorityClass, PriorityValue);
			if (prioritizable.GetMasterPriority().Equals(priority))
			{
				PrioritizeStatePacket.SendHostCorrection(identity, SenderId, ClientRequestId);
				return;
			}
			PrioritizeStatePacket.ApplyHostRequest(new PrioritizeStatePacket.HostRequestContext
			{
				Identity = identity,
				Prioritizable = prioritizable,
				Setting = priority,
				Receipt = new PrioritizeStatePacket.RequestReceipt
				{
					SenderId = SenderId,
					RequestId = ClientRequestId
				}
			});
		}

		private bool ShouldAccept(
			bool localIsHost,
			DispatchContext context,
			bool protocolVerified)
			=> localIsHost && !context.SenderIsHost && context.IsVerifiedHostBroadcast
			   && protocolVerified && SenderId != 0 && SenderId == context.SenderId
			   && PacketHandler.IsCurrentDispatchContext(context);

		internal static bool TryCreateClientRequest(
			NetworkIdentity identity, PrioritySetting priority,
			out PrioritizeTargetRequestPacket request)
		{
			request = null;
			if (identity == null || identity.NetId == 0
			    || !NetworkIdentityRegistry.IsRegistered(identity, identity.NetId)
			    || NetworkIdentityRegistry.IsLifecycleTombstoned(identity.NetId))
				return false;
			ulong lifecycle = NetworkIdentityRegistry.GetLastLifecycleRevision(identity.NetId);
			if (lifecycle == 0 || identity.LifecycleRevision != lifecycle)
				return false;
			request = new PrioritizeTargetRequestPacket
			{
				SenderId = MultiplayerSession.LocalUserID,
				ClientRequestId = PrioritizeStatePacket.BeginClientRequest(identity.NetId),
				NetId = identity.NetId,
				TargetLifecycleRevision = lifecycle,
				BasePriorityRevision = PrioritizeStatePacket.GetClientRevision(
					identity.NetId, lifecycle),
				PriorityClass = (int)priority.priority_class,
				PriorityValue = priority.priority_value
			};
			return true;
		}

		private void Validate()
		{
			if (SenderId == 0 || ClientRequestId == 0 || ClientRequestId > long.MaxValue
			    || NetId == 0 || TargetLifecycleRevision == 0
			    || TargetLifecycleRevision > long.MaxValue
			    || BasePriorityRevision > long.MaxValue
			    || !PriorityAuthority.IsValidClientPriority(
				    new PrioritySetting((PriorityScreen.PriorityClass)PriorityClass, PriorityValue)))
				throw new InvalidDataException("Invalid prioritize target request");
		}
	}
}
