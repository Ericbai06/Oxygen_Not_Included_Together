#if DEBUG
using System.Collections.Generic;
using System.Linq;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Synchronization;
using Shared.Interfaces.Networking;
using UnityEngine;

namespace ONI_Together.Networking
{
	internal sealed class InventoryActionMutation
	{
		internal InventoryTarget Target;
		internal GameObject Resource;
		internal ResourceCountPacket Packet;
	}

	internal sealed class InventoryPreparedCommand { internal ScenarioActionCommand Command; }

	internal static class InventoryActionFlow
	{
		private const string EntryId = "sync:caf3257322aa8047f6dea261";

		internal static InventoryActionMutation ExecuteHost(ScenarioActionCommand command)
		{
			InventoryPreparedCommand prepared = Prepare(command);
			InventoryTarget target = Resolve(prepared);
			InventoryActionMutation mutation = Mutate(target);
			InventoryState state = CaptureState(mutation);
			ObserveHost(state);
			ResourceCountPacket packet = CreatePacket(mutation);
			Send(packet);
			return mutation;
		}

		internal static InventoryPreparedCommand Prepare(ScenarioActionCommand command)
			=> new() { Command = command };
		internal static InventoryTarget Resolve(InventoryPreparedCommand prepared)
			=> new();

		internal static InventoryActionMutation Mutate(InventoryTarget target)
		{
			GameObject resource = InventoryProfileRuntime.AddSand1000g();
			ResourceCountPacket packet = ResourceSyncer.CreateSnapshot();
			if (resource != null && packet == null)
				InventoryProfileRuntime.RemoveAddedResource(resource);
			if (packet != null)
				packet.ScenarioActionProfile = ScenarioActionReceiverGate.Mark("inventory");
			return resource == null || packet == null ? null : new InventoryActionMutation
			{
				Target = target, Resource = resource, Packet = packet,
			};
		}

		internal static InventoryState CaptureState(InventoryActionMutation mutation) => State(mutation);
		internal static void ObserveHost(InventoryState state)
			=> ScenarioActionFlowTransport.ObserveHost(state);
		internal static ResourceCountPacket CreatePacket(InventoryActionMutation mutation)
			=> mutation?.Packet;
		internal static void Send(ResourceCountPacket packet)
			=> Packets.Architecture.ScenarioActionPacketTransport.SendScenarioAction(
				packet, PacketSendMode.ReliableImmediate);

		internal static InventoryState ExecuteClient(ResourceCountPacket packet)
		{
			InventoryState state = ApplyClient(packet);
			ObserveClient(state);
			return state;
		}

		internal static InventoryState ApplyClient(ResourceCountPacket packet)
			=> packet != null && packet.ApplyRuntimePacket()
				? Attach(new InventoryTarget(), packet) : null;
		internal static void ObserveClient(InventoryState state)
			=> ScenarioActionFlowTransport.ObserveClient(state);

		internal static InventoryState ExecuteCleanup(InventoryActionMutation mutation)
		{
			InventoryState state = Restore(mutation);
			ObserveCleanup(state);
			ResourceCountPacket packet = CreateCleanupPacket(mutation);
			Send(packet);
			return state;
		}

		internal static InventoryState Restore(InventoryActionMutation mutation)
		{
			if (mutation == null || !InventoryProfileRuntime.RemoveAddedResource(mutation.Resource))
				return null;
			mutation.Packet = ResourceSyncer.CreateSnapshot();
			if (mutation.Packet != null)
				mutation.Packet.ScenarioActionProfile = ScenarioActionReceiverGate.Mark("inventory");
			return mutation.Packet == null ? null : State(mutation);
		}

		internal static void ObserveCleanup(InventoryState state)
			=> ScenarioActionFlowTransport.ObserveCleanup(state);
		internal static ResourceCountPacket CreateCleanupPacket(InventoryActionMutation mutation)
			=> mutation?.Packet;

		private static InventoryState State(InventoryActionMutation mutation)
			=> mutation == null ? null : Attach(mutation.Target, mutation.Packet);
		private static InventoryState Attach(InventoryTarget target, ResourceCountPacket packet)
		{
			var values = packet.Resources.OrderBy(value => value.Key)
				.Select(value => new InventoryResourceState { Tag = value.Key, Amount = value.Value })
				.ToList();
			var state = new InventoryState { Resources = values };
			return ScenarioActionFlowContext.Attach(state, new ScenarioActionFlowEvidence
			{
				Scenario = "inventory", Revision = (long)packet.Revision, Target = target,
				State = state, EntryId = EntryId, Packet = packet,
			});
		}
	}
}
#endif
