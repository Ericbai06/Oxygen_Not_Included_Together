#if DEBUG
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Tools.Build;
using UnityEngine;

namespace ONI_Together.DebugTools
{
	internal static partial class FaultDeferredDestroyRuntime
	{
		private static bool RebindLifecycle(FaultRuntimeTargetContext context)
		{
			NetworkIdentity replacement = context.DeferredReplacement
				.GetComponent<NetworkIdentity>();
			if (replacement != null && replacement.NetId != 0)
				NetworkIdentityRegistry.Unregister(replacement, replacement.NetId);
			NetworkIdentityRegistry.RestoreLifecycleRevisionState(
				context.DeferredOriginalNetId, context.DeferredLifecycleState);
			return NetworkIdentityRegistry.TryBindAuthoritativeLifecycle(
				context.DeferredReplacement, context.DeferredOriginalNetId,
				context.DeferredOriginalLifecycle);
		}

		private static bool TryFindFixtureCell(BuildingDef def, out int fixtureCell)
		{
			for (int cell = 0; cell < Grid.CellCount; cell++)
			{
				if (!Grid.IsValidCell(cell) || !Grid.IsVisible(cell)
				    || Grid.Objects[cell, (int)def.ObjectLayer] != null)
					continue;
				Vector3 position = Grid.CellToPosCBC(cell, Grid.SceneLayer.Building);
				if (def.IsValidBuildLocation(null, position, Orientation.Neutral)
				    && def.IsValidPlaceLocation(null, position, Orientation.Neutral, out _))
				{
					fixtureCell = cell;
					return true;
				}
			}
			fixtureCell = Grid.InvalidCell;
			return false;
		}
	}
}
#endif
