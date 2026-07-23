#if DEBUG
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World;
using Shared.Interfaces.Networking;
using UnityEngine;
using static Storage;

namespace ONI_Together.Networking
{
	internal sealed class PickupActionMutation
	{
		internal long ItemNetId;
		internal long LifecycleRevision;
		internal long OriginalStorageNetId;
		internal Vector3 OriginalPosition;
		internal int TargetCell;
		internal PickupTarget Target;
		internal PickupProfileMutation Runtime;
		internal GroundItemPickedUpPacket PickupPacket;
		internal SpawnPrefabPacket SpawnPacket;
		internal StorageItemPacket StoragePacket;
	}

	internal sealed class PickupPreparedCommand { internal ScenarioActionCommand Command; }

	internal static class PickupActionFlow
	{
		private const string EntryId = "sync:175dd2dcf62dbbf0bf28d018";

		internal static PickupActionMutation ExecuteHost(ScenarioActionCommand command)
		{
			PickupPreparedCommand prepared = Prepare(command);
			PickupTarget target = Resolve(prepared);
			PickupActionMutation mutation = Mutate(target);
			PickupState state = CaptureState(mutation);
			GroundItemPickedUpPacket first = CreatePickupPacket(mutation);
			if (!SendPickup(first))
			{
				if (Restore(mutation) == null)
					Debug.LogError("[ONI_Together] Pickup first-send rollback failed");
				return null;
			}
			SpawnPrefabPacket second = CreateSpawnPacket(mutation);
			if (!SendSpawn(second))
			{
				if (!CompensateHostSecondSend(mutation))
					Debug.LogError("[ONI_Together] Pickup second-send compensation failed");
				return null;
			}
			ObserveHost(state);
			return mutation;
		}

		internal static PickupPreparedCommand Prepare(ScenarioActionCommand command)
			=> new() { Command = command };

		internal static PickupTarget Resolve(PickupPreparedCommand prepared)
		{
			int netId = 0;
			int cell = 0;
			prepared?.Command?.TryGetInt("itemNetId", out netId);
			prepared?.Command?.TryGetInt("targetCell", out cell);
			var target = new PickupTarget { ItemNetId = netId, TargetCell = cell };
			if (NetworkIdentityRegistry.TryGet(netId, out NetworkIdentity identity))
				ScenarioActionFlowContext.Attach(target, identity.gameObject);
			return target;
		}

		internal static PickupActionMutation Mutate(PickupTarget target)
		{
			GameObject item = ScenarioActionFlowContext.Get<GameObject>(target);
			PickupProfileMutation runtime = PickupProfileRuntime.PickupAndDrop(item, target.TargetCell);
			NetworkIdentity identity = item?.GetComponent<NetworkIdentity>();
			if (runtime == null || identity == null) return null;
			var pickup = new GroundItemPickedUpPacket(identity.NetId)
			{
				ScenarioActionProfile = ScenarioActionReceiverGate.Mark("pickup"),
			};
			identity.LifecycleRevision = NetworkIdentityRegistry.BeginLifecycle(identity.NetId);
			SpawnPrefabPacket spawn = SpawnPrefabPacket.FromIdentity(identity, true);
			if (spawn == null)
			{
				PickupProfileRuntime.Restore(runtime);
				return null;
			}
			spawn.BindExistingOnly = false;
			spawn.ScenarioActionProfile = ScenarioActionReceiverGate.Mark("pickup");
			spawn.ScenarioActionTerminal = true;
			return new PickupActionMutation
			{
				ItemNetId = identity.NetId, LifecycleRevision = (long)pickup.Revision,
				OriginalStorageNetId = runtime.OriginalStorageNetId,
				OriginalPosition = runtime.OriginalPosition, TargetCell = target.TargetCell,
				Target = target, Runtime = runtime, PickupPacket = pickup, SpawnPacket = spawn,
			};
		}

		internal static PickupState CaptureState(PickupActionMutation mutation)
			=> Attach(mutation?.Target, mutation?.SpawnPacket, "dropped", false);
		internal static void ObserveHost(PickupState state)
			=> ScenarioActionFlowTransport.ObserveHost(state);
		internal static GroundItemPickedUpPacket CreatePickupPacket(PickupActionMutation mutation)
			=> mutation?.PickupPacket;
		internal static SpawnPrefabPacket CreateSpawnPacket(PickupActionMutation mutation)
			=> mutation?.SpawnPacket;
		internal static bool SendPickup(GroundItemPickedUpPacket packet)
			=> Packets.Architecture.ScenarioActionPacketTransport.SendScenarioAction(
				packet, PacketSendMode.ReliableImmediate);
		internal static bool SendSpawn(SpawnPrefabPacket packet)
			=> Packets.Architecture.ScenarioActionPacketTransport.SendScenarioAction(
				packet, PacketSendMode.ReliableImmediate);

		internal static bool CompensateHostSecondSend(PickupActionMutation mutation)
		{
			if (Restore(mutation) == null) return false;
			if (!SendSpawn(mutation.SpawnPacket)) return false;
			return SendStorage(mutation.StoragePacket);
		}

		internal static PickupState ExecutePickupClient(GroundItemPickedUpPacket packet)
		{
			PickupState state = ApplyPickupClient(packet);
			ObserveClient(state);
			return state;
		}
		internal static PickupState ApplyPickupClient(GroundItemPickedUpPacket packet)
		{
			if (packet == null) return null;
			if (!packet.ApplyRuntimePacket()) return null;
			return Attach(new PickupTarget { ItemNetId = packet.NetId }, packet, "picked-up", true);
		}

		internal static PickupState ExecuteSpawnClient(SpawnPrefabPacket packet)
		{
			PickupState state = ApplySpawnClient(packet);
			ObserveClient(state);
			return state;
		}
		internal static PickupState ApplySpawnClient(SpawnPrefabPacket packet)
		{
			if (packet == null) return null;
			if (!packet.ApplyScenarioProfile()) return null;
			return Attach(new PickupTarget
			{
				ItemNetId = packet.NetId, TargetCell = Grid.PosToCell(packet.Position),
			}, packet, "dropped", false);
		}

		internal static PickupState ExecuteStorageClient(StorageItemPacket packet)
		{
			PickupState state = ApplyStorageClient(packet);
			ObserveClient(state);
			return state;
		}
		internal static PickupState ApplyStorageClient(StorageItemPacket packet)
		{
			if (packet == null) return null;
			if (!packet.ApplyRuntimePacket()) return null;
			int targetCell = packet.ScenarioActionTargetCell;
			return Attach(new PickupTarget
			{
				ItemNetId = packet.ScenarioActionItemNetId,
				TargetCell = targetCell,
			}, packet, "restored", false);
		}

		internal static void ObserveClient(PickupState state)
		{
			ScenarioActionFlowEvidence evidence =
				ScenarioActionFlowContext.Get<ScenarioActionFlowEvidence>(state);
			if (evidence?.Packet is GroundItemPickedUpPacket) return;
			if (evidence?.Packet is SpawnPrefabPacket spawn
			    && !spawn.ScenarioActionTerminal) return;
			ScenarioActionFlowTransport.ObserveClient(state);
		}

		internal static PickupState ExecuteCleanup(PickupActionMutation mutation)
		{
			PickupState state = Restore(mutation);
			if (state == null) return null;
			SpawnPrefabPacket first = CreateCleanupSpawnPacket(mutation);
			if (!SendSpawn(first))
			{
				if (Restore(mutation) == null)
					Debug.LogError("[ONI_Together] Pickup cleanup rollback failed");
				return null;
			}
			StorageItemPacket second = CreateCleanupStoragePacket(mutation);
			if (!SendStorage(second))
			{
				if (!CompensateCleanupSecondSend(mutation))
					Debug.LogError("[ONI_Together] Pickup cleanup compensation failed");
				return null;
			}
			ObserveCleanup(state);
			return state;
		}

		internal static PickupState Restore(PickupActionMutation mutation)
		{
			if (mutation?.Runtime == null || !PickupProfileRuntime.Restore(mutation.Runtime))
				return null;
			NetworkIdentity identity = mutation.Runtime.Item.GetComponent<NetworkIdentity>();
			identity.LifecycleRevision = NetworkIdentityRegistry.BeginLifecycle(identity.NetId);
			mutation.SpawnPacket = SpawnPrefabPacket.FromIdentity(identity, true);
			if (mutation.SpawnPacket == null) return null;
			mutation.SpawnPacket.BindExistingOnly = false;
			mutation.SpawnPacket.ScenarioActionProfile =
				ScenarioActionReceiverGate.Mark("pickup");
			mutation.StoragePacket = CreateStorage(mutation);
			if (mutation.StoragePacket == null) return null;
			int targetCell = mutation.StoragePacket.ScenarioActionTargetCell;
			return Attach(new PickupTarget
				{
					ItemNetId = mutation.ItemNetId,
					TargetCell = targetCell,
				}, mutation.StoragePacket, "restored", false);
		}

		internal static void ObserveCleanup(PickupState state)
			=> ScenarioActionFlowTransport.ObserveCleanup(state);
		internal static SpawnPrefabPacket CreateCleanupSpawnPacket(PickupActionMutation mutation)
			=> mutation?.SpawnPacket;
		internal static StorageItemPacket CreateCleanupStoragePacket(PickupActionMutation mutation)
			=> mutation?.StoragePacket;
		internal static bool SendStorage(StorageItemPacket packet)
			=> Packets.Architecture.ScenarioActionPacketTransport.SendScenarioAction(
				packet, PacketSendMode.ReliableImmediate);

		internal static bool CompensateCleanupSecondSend(PickupActionMutation mutation)
			=> mutation?.StoragePacket != null && SendStorage(mutation.StoragePacket);

		private static StorageItemPacket CreateStorage(PickupActionMutation mutation)
		{
			bool restoreMembership = mutation.OriginalStorageNetId != 0;
			int storageNetId = restoreMembership
				? (int)mutation.OriginalStorageNetId
				: mutation.Runtime.Carrier?.GetComponent<NetworkIdentity>()?.NetId ?? 0;
			if (storageNetId == 0) return null;
			float amount = mutation.Runtime.Item.GetComponent<PrimaryElement>()?.Mass ?? 1f;
			return new StorageItemPacket
			{
				NetId = restoreMembership ? (int)mutation.ItemNetId : 0,
				StorageNetId = storageNetId,
				Revision = NetworkIdentityRegistry.NextAuthorityRevision(),
				FxPrefix = restoreMembership ? FXPrefix.Delivered : FXPrefix.PickedUp,
				ConsumedAmount = amount,
				ConsumedPrefabHash = mutation.Runtime.Item.PrefabID().GetHashCode(),
				ScenarioActionProfile = ScenarioActionReceiverGate.Mark("pickup"),
				ScenarioActionItemNetId = (int)mutation.ItemNetId,
				ScenarioActionTargetCell = Grid.PosToCell(mutation.OriginalPosition),
			};
		}

		private static PickupState Attach(
			PickupTarget target, object packet, string action, bool tombstone)
		{
			if (target == null || packet == null) return null;
			long revision = packet switch
			{
				GroundItemPickedUpPacket pickup => (long)pickup.Revision,
				SpawnPrefabPacket spawn => (long)spawn.Revision,
				StorageItemPacket storage => (long)storage.Revision,
				_ => 0,
			};
			var state = new PickupState { Action = action, Tombstone = tombstone };
			return ScenarioActionFlowContext.Attach(state, new ScenarioActionFlowEvidence
			{
				Scenario = "pickup", Revision = revision, Target = target,
				State = state, EntryId = EntryId, Packet = packet,
			});
		}
	}
}
#endif
