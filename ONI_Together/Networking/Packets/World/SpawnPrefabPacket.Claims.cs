using ONI_Together.Networking.Components;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World
{
	public partial class SpawnPrefabPacket
	{
		private bool TryFinishClaimedRuntimeObject(
			NetworkIdentityRegistry.IdentityClaim displaced)
		{
			NetworkIdentityRegistry.IdentityClaim claim;
			bool claimed = BindExistingOnly
				? NetworkIdentityRegistry.TryBeginAuthorityBindingClaim(
					Hash, Position, WorldId, NetId, out claim)
				: NetworkIdentityRegistry.TryBeginUnassignedClaim(
					Hash, Position, WorldId, NetId, out claim);
			if (!claimed)
				return false;
			if (FinishRuntimeMaterialization(claim.GameObject))
			{
				RetireDisplaced(displaced);
				return true;
			}
			NetworkIdentityRegistry.RollbackClaim(claim);
			NetworkIdentityRegistry.RollbackClaim(displaced);
			StorePendingBinding(this);
			return true;
		}

		private static void RetireDisplaced(
			NetworkIdentityRegistry.IdentityClaim displaced)
		{
			GameObject gameObject = displaced?.GameObject;
			if (gameObject == null || gameObject.IsNullOrDestroyed())
				return;
			gameObject.SetActive(false);
			Util.KDestroyGameObject(gameObject);
		}
	}
}
