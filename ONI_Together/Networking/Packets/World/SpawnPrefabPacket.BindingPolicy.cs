using ONI_Together.Networking.Components;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World
{
	public partial class SpawnPrefabPacket
	{
		private static bool RequiresExistingSnapshotBinding(
			NetworkIdentity identity, GameObject gameObject, bool requirePersistent)
			=> RequiresNativeMaterialization(
				gameObject.GetComponent<Constructable>() != null,
				gameObject.GetComponent<BuildingComplete>() != null,
				gameObject.GetComponent<Diggable>() != null)
			   || RequiresExistingSnapshotBinding(
				identity.RequiresExistingBinding,
				gameObject.GetComponent<SaveLoadRoot>() != null,
				requirePersistent,
				ElementLoader.GetElement(gameObject.PrefabID()) != null);

		private static bool RequiresNativeMaterialization(GameObject gameObject)
			=> gameObject != null && RequiresNativeMaterialization(
				gameObject.GetComponent<Constructable>() != null,
				gameObject.GetComponent<BuildingComplete>() != null,
				gameObject.GetComponent<Diggable>() != null);

		internal static bool RequiresNativeMaterialization(
			bool hasConstructable, bool hasCompletedBuilding, bool hasDiggable)
			=> hasConstructable || hasCompletedBuilding || hasDiggable;

		internal static bool RequiresExistingSnapshotBinding(
			bool identityRequiresExisting, bool hasSaveLoadRoot,
			bool requirePersistent, bool hasElementData)
			=> !hasElementData
			   && (identityRequiresExisting || requirePersistent && hasSaveLoadRoot);
	}
}
