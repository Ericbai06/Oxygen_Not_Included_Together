#if DEBUG
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World;
using Shared.Interfaces.Networking;

namespace ONI_Together.Networking
{
	internal sealed class EntityLifecycleActionMutation
	{
		internal EntityLifecycleTarget Target;
		internal NetworkIdentity Identity;
		internal bool PreviousActive;
		internal SpawnPrefabPacket Packet;
	}

	internal sealed class EntityLifecyclePreparedCommand { internal ScenarioActionCommand Command; }

	internal static class EntityLifecycleActionFlow
	{
		private const string EntryId = "sync:entity-lifecycle-profile";

		internal static EntityLifecycleActionMutation ExecuteHost(ScenarioActionCommand command)
		{
			EntityLifecyclePreparedCommand prepared = Prepare(command);
			EntityLifecycleTarget target = Resolve(prepared);
			EntityLifecycleActionMutation mutation = Mutate(target);
			EntityLifecycleState state = CaptureState(mutation);
			ObserveHost(state);
			SpawnPrefabPacket packet = CreatePacket(mutation);
			Send(packet);
			return mutation;
		}

		internal static EntityLifecyclePreparedCommand Prepare(ScenarioActionCommand command)
			=> new() { Command = command };

		internal static EntityLifecycleTarget Resolve(EntityLifecyclePreparedCommand prepared)
		{
			int netId = 0;
			prepared?.Command?.TryGetInt("netId", out netId);
			NetworkIdentityRegistry.TryGet(netId, out NetworkIdentity identity);
			var target = new EntityLifecycleTarget
			{
				NetId = netId,
				Prefab = identity?.gameObject?.PrefabID().Name,
				WorldId = identity?.gameObject?.GetMyWorldId() ?? -1,
			};
			return ScenarioActionFlowContext.Attach(target, identity);
		}

		internal static EntityLifecycleActionMutation Mutate(EntityLifecycleTarget target)
		{
			NetworkIdentity identity = ScenarioActionFlowContext.Get<NetworkIdentity>(target);
			if (identity == null || identity.IsNullOrDestroyed() || !identity.gameObject.activeSelf)
				return null;
			identity.gameObject.SetActive(false);
			identity.LifecycleRevision = NetworkIdentityRegistry.BeginLifecycle(identity.NetId);
			var mutation = new EntityLifecycleActionMutation
			{
				Target = target, Identity = identity, PreviousActive = true,
			};
			mutation.Packet = SpawnPrefabPacket.FromIdentity(identity, true);
			if (mutation.Packet == null)
			{
				Restore(mutation);
				return null;
			}
			mutation.Packet.ScenarioActionProfile =
				ScenarioActionReceiverGate.Mark("entity-lifecycle");
			return mutation;
		}

		internal static EntityLifecycleState CaptureState(EntityLifecycleActionMutation mutation)
			=> State(mutation);
		internal static void ObserveHost(EntityLifecycleState state)
			=> ScenarioActionFlowTransport.ObserveHost(state);
		internal static SpawnPrefabPacket CreatePacket(EntityLifecycleActionMutation mutation)
			=> mutation?.Packet;
		internal static void Send(SpawnPrefabPacket packet)
			=> Packets.Architecture.ScenarioActionPacketTransport.SendScenarioAction(
				packet, PacketSendMode.ReliableImmediate);

		internal static EntityLifecycleState ExecuteClient(SpawnPrefabPacket packet)
		{
			EntityLifecycleState state = ApplyClient(packet);
			ObserveClient(state);
			return state;
		}

		internal static EntityLifecycleState ApplyClient(SpawnPrefabPacket packet)
		{
			if (packet == null) return null;
			if (!packet.ApplyScenarioProfile()) return null;
			return Attach(new EntityLifecycleState
			{
				LifecycleRevision = (long)packet.Revision,
				Active = packet.IsActive, Tombstone = !packet.IsActive,
			}, new EntityLifecycleTarget
			{
				NetId = packet.NetId, Prefab = new Tag(packet.Hash).Name, WorldId = packet.WorldId,
			}, packet);
		}

		internal static void ObserveClient(EntityLifecycleState state)
			=> ScenarioActionFlowTransport.ObserveClient(state);

		internal static EntityLifecycleState ExecuteCleanup(EntityLifecycleActionMutation mutation)
		{
			EntityLifecycleState state = Restore(mutation);
			ObserveCleanup(state);
			SpawnPrefabPacket packet = CreateCleanupPacket(mutation);
			Send(packet);
			return state;
		}

		internal static EntityLifecycleState Restore(EntityLifecycleActionMutation mutation)
		{
			if (mutation?.Identity == null || mutation.Identity.IsNullOrDestroyed()) return null;
			mutation.Identity.gameObject.SetActive(mutation.PreviousActive);
			mutation.Identity.LifecycleRevision =
				NetworkIdentityRegistry.BeginLifecycle(mutation.Identity.NetId);
			mutation.Packet = SpawnPrefabPacket.FromIdentity(mutation.Identity, true);
			if (mutation.Packet == null) return null;
			mutation.Packet.ScenarioActionProfile =
				ScenarioActionReceiverGate.Mark("entity-lifecycle");
			return State(mutation);
		}

		internal static void ObserveCleanup(EntityLifecycleState state)
			=> ScenarioActionFlowTransport.ObserveCleanup(state);
		internal static SpawnPrefabPacket CreateCleanupPacket(EntityLifecycleActionMutation mutation)
			=> mutation?.Packet;

		private static EntityLifecycleState State(EntityLifecycleActionMutation mutation)
			=> mutation == null ? null : Attach(new EntityLifecycleState
			{
				LifecycleRevision = (long)mutation.Packet.Revision,
				Active = mutation.Packet.IsActive, Tombstone = !mutation.Packet.IsActive,
			}, mutation.Target, mutation.Packet);

		private static EntityLifecycleState Attach(
			EntityLifecycleState state, EntityLifecycleTarget target, SpawnPrefabPacket packet)
			=> ScenarioActionFlowContext.Attach(state, new ScenarioActionFlowEvidence
			{
				Scenario = "entity-lifecycle", Revision = state.LifecycleRevision,
				Target = target, State = state, EntryId = EntryId, Packet = packet,
			});
	}
}
#endif
