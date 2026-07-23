using System.Linq;
using HarmonyLib;
using ONI_Together.Misc;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Synchronization;
using ONI_Together.Patches.World;
using Shared;
using Shared.Interfaces.Networking;
using UnityEngine;

#if DEBUG
namespace ONI_Together.Networking
{
	internal static class BuildingConfigProfileRuntime
	{
		internal static bool IsApplyingProfileToggle { get; private set; }

		internal static bool ReadCheckbox(LogicSwitch target)
			=> Traverse.Create(target).Field("switchedOn").GetValue<bool>();

		internal static bool ToggleCheckbox(LogicSwitch target)
		{
			IsApplyingProfileToggle = true;
			try { target.Toggle(); }
			finally { IsApplyingProfileToggle = false; }
			return ReadCheckbox(target);
		}

		internal static void PublishCheckboxState(LogicSwitch target)
		{
			NetworkIdentity identity = target?.GetComponent<NetworkIdentity>();
			if (identity == null || identity.NetId == 0)
				return;
			PacketSender.SendToAllClients(new BuildingConfigPacket
			{
				NetId = identity.NetId,
				Cell = Grid.PosToCell(target.gameObject),
				ConfigHash = NetworkingHash.ForConfigKey("LogicSwitchState"),
				Value = ReadCheckbox(target) ? 1f : 0f,
				ConfigType = BuildingConfigType.Boolean,
			});
		}
	}

	internal static class UprootProfileRuntime
	{
		internal static bool IsApplyingProfileMutation { get; private set; }

		internal static bool Mark(Uprootable target)
		{
			RunWithoutPatchSend(() => target.MarkForUproot(true));
			return target.IsMarkedForUproot;
		}

		internal static bool Restore(Uprootable target, bool marked)
		{
			if (target.IsMarkedForUproot != marked)
			{
				if (marked)
				{
					RunWithoutPatchSend(() => target.MarkForUproot(true));
				}
				else
				{
					RunWithoutPatchSend(() => target.ForceCancelUproot(null));
				}
			}
			return target.IsMarkedForUproot == marked;
		}

		internal static void Publish(Uprootable target, bool marked)
		{
			NetworkIdentity identity = target?.GetComponent<NetworkIdentity>();
			if (identity == null || identity.NetId == 0)
				return;
			PacketSender.SendToAllClients(new BuildingConfigPacket
			{
				NetId = identity.NetId,
				Cell = Grid.PosToCell(target.gameObject),
				ConfigHash = NetworkingHash.ForConfigKey("UprootPlant"),
				Value = marked ? 1f : 0f,
				ConfigType = BuildingConfigType.Boolean,
			});
		}

		private static void RunWithoutPatchSend(System.Action action)
		{
			IsApplyingProfileMutation = true;
			try { action(); }
			finally { IsApplyingProfileMutation = false; }
		}
	}

	internal static class InventoryProfileRuntime
	{
		private const float SandMassKg = 1f;
		private const float RoomTemperatureKelvin = 293.15f;

		internal static GameObject AddSand1000g()
		{
			MinionIdentity anchor = ResolveAnchor();
			if (anchor == null)
				return null;
			GameObject resource = SpawnUtils.KNetInstantiate(
				(int)SimHashes.Sand, anchor.transform.position, SandMassKg,
				RoomTemperatureKelvin, byte.MaxValue, 0);
			return resource;
		}

		internal static MinionIdentity ResolveAnchor()
			=> global::Components.LiveMinionIdentities?.Items
				.Where(value => value != null && !value.IsNullOrDestroyed())
				.OrderBy(value => value.GetComponent<NetworkIdentity>()?.NetId ?? int.MaxValue)
				.FirstOrDefault();

		internal static bool RemoveAddedResource(GameObject resource)
		{
			if (resource == null || resource.IsNullOrDestroyed())
				return true;
			NetworkIdentity identity = resource.GetComponent<NetworkIdentity>();
			if (identity != null && identity.RetireAuthoritativeLifecycle())
			{
				return true;
			}
			Util.KDestroyGameObject(resource);
			bool removed = resource == null || resource.IsNullOrDestroyed();
			return removed;
		}
	}

	internal sealed class PickupProfileMutation
	{
		internal GameObject Item;
		internal Storage OriginalStorage;
		internal Vector3 OriginalPosition;
		internal int ItemNetId;
		internal int OriginalStorageNetId;
		internal int TargetCell;
		internal Storage Carrier;
	}

	internal static class PickupProfileRuntime
	{
		internal static PickupProfileMutation PickupAndDrop(
			GameObject item,
			int targetCell)
		{
			Storage carrier = ResolveCarrier();
			Pickupable pickupable = item?.GetComponent<Pickupable>();
			if (carrier == null || pickupable == null
			    || carrier.items.Any(existing => existing != item
				    && existing != null && existing.PrefabID() == item.PrefabID()))
				return null;
			var mutation = new PickupProfileMutation
			{
				Item = item,
				OriginalStorage = pickupable.storage,
				OriginalPosition = item.transform.position,
				ItemNetId = item.GetComponent<NetworkIdentity>()?.NetId ?? 0,
				OriginalStorageNetId = pickupable.storage?.GetComponent<NetworkIdentity>()?.NetId ?? 0,
				TargetCell = targetCell,
				Carrier = carrier,
			};
			StoragePatches.RunWithoutReplication(() =>
			{
				mutation.OriginalStorage?.Remove(item, do_disease_transfer: false);
			});
			GameObject stored = StoragePatches.RunWithoutReplication(() => carrier.Store(
				item, hide_popups: true, block_events: false,
				do_disease_transfer: false, is_deserializing: false));
			if (stored != item || !carrier.items.Contains(item))
			{
				Restore(mutation);
				return null;
			}
			GameObject dropped = StoragePatches.RunWithoutReplication(() =>
				carrier.Drop(item, do_disease_transfer: false));
			if (dropped != item)
			{
				Restore(mutation);
				return null;
			}
			dropped.transform.SetPosition(Grid.CellToPosCCC(targetCell, Grid.SceneLayer.Ore));
			return mutation.ItemNetId != 0 ? mutation : null;
		}

		internal static Storage ResolveCarrier()
			=> global::Components.LiveMinionIdentities?.Items
				.Where(value => value != null && !value.IsNullOrDestroyed())
				.OrderBy(value => value.GetComponent<NetworkIdentity>()?.NetId ?? int.MaxValue)
				.Select(value => value.GetComponent<Storage>())
				.FirstOrDefault(value => value != null);

		internal static bool Restore(PickupProfileMutation mutation)
		{
			GameObject item = mutation?.Item;
			if (item == null || item.IsNullOrDestroyed())
				return false;
			Pickupable pickupable = item.GetComponent<Pickupable>();
			StoragePatches.RunWithoutReplication(() =>
			{
				pickupable?.storage?.Remove(item, do_disease_transfer: false);
			});
			if (mutation.OriginalStorage != null)
				StoragePatches.RunWithoutReplication(() =>
				{
					mutation.OriginalStorage.Store(
						item, hide_popups: true, block_events: false,
						do_disease_transfer: false, is_deserializing: false);
				});
			else
			{
				item.transform.SetPosition(mutation.OriginalPosition);
			}
			return mutation.OriginalStorage == null
				? pickupable?.storage == null
				: mutation.OriginalStorage.items.Contains(item);
		}

		private static bool PublishPosition(GameObject item)
			=> PublishLifecycle(item, tombstone: false);

		internal static bool PublishLifecycle(GameObject item, bool tombstone)
		{
			NetworkIdentity identity = item?.GetComponent<NetworkIdentity>();
			if (identity == null || identity.NetId == 0)
				return false;
			if (tombstone)
			{
				var pickup = new GroundItemPickedUpPacket(identity.NetId);
				PacketSender.SendToAllClients(pickup, PacketSendMode.ReliableImmediate);
				pickup.LogHostOutcome("sync:175dd2dcf62dbbf0bf28d018");
				return true;
			}
			identity.LifecycleRevision = NetworkIdentityRegistry.BeginLifecycle(identity.NetId);
			SpawnPrefabPacket packet = SpawnPrefabPacket.FromIdentity(
				identity, requireExistingPersistentObject: true);
			if (packet == null)
				return false;
			PacketSender.SendToAllClients(packet, PacketSendMode.ReliableImmediate);
			return true;
		}
	}
}
#endif
