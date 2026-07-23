using System.Collections.Generic;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Tools.Build;
using UnityEngine;

namespace ONI_Together.Patches.ToolPatches.Build
{
	internal static partial class BuildRuntimeAdapter
	{
		internal static ApplyResult Apply(BuildCommit commit)
		{
			if (commit.Request == null)
				return ApplyResult.Reject("commit does not carry build metadata");
			if (!TryResolve(commit.Request, out BuildingDef def, out List<Tag> materials,
				out PrioritySetting priority, out _))
				return ApplyResult.Reject("client cannot resolve commit prefab or material");
			foreach (PlacementOutcome outcome in commit.Placements)
			{
				if (!TryApplyPlacement(commit.Request, def, materials, priority, outcome,
					out string error))
					return ApplyResult.Reject(error);
			}
			ApplyConnections(def, commit);
			return ApplyResult.Success();
		}

		private static bool TryApplyPlacement(
			BuildRequest request,
			BuildingDef def,
			IList<Tag> materials,
			PrioritySetting priority,
			PlacementOutcome outcome,
			out string error)
		{
			error = string.Empty;
			if (!BuildRequestValidator.IsWireCell(outcome.Cell) || outcome.NetId == 0 ||
				outcome.LifecycleRevision == 0)
			{
				error = "placement outcome has invalid lifecycle identity";
				return false;
			}
			if (!TryFindPlacement(def, outcome.Cell, outcome.Kind, out GameObject existing))
			{
				bool replacement = IsReplacement(outcome.Kind);
				bool completed = IsCompleted(outcome.Kind);
				if (!TryPlace(request, def, materials, priority, outcome.Cell,
					GetOrientation(request), completed, replacement, out existing))
				{
					error = "authoritative placement cannot be materialized";
					return false;
				}
			}
			if (!NetworkIdentityRegistry.TryBindAuthoritativeLifecycle(
				existing, outcome.NetId, outcome.LifecycleRevision))
			{
				error = "placement lifecycle identity is stale or conflicting";
				return false;
			}
			return true;
		}

		private static void ApplyConnections(BuildingDef def, BuildCommit commit)
		{
			IUtilityNetworkMgr manager = def.BuildingComplete
				.GetComponent<IHaveUtilityNetworkMgr>()?.GetNetworkManager();
			if (manager == null)
				return;
			foreach (UtilityEdge edge in commit.Connections)
			{
				if (!TryFindPlacement(def, edge.FromCell, BuildPlacementKind.Queued,
					out GameObject from) ||
					!TryFindPlacement(def, edge.ToCell, BuildPlacementKind.Queued,
						out GameObject to))
					continue;
				UtilityConnections forward = UtilityConnectionsExtensions.DirectionFromToCell(
					edge.FromCell, edge.ToCell);
				UtilityConnections backward = forward.InverseDirection();
				if (forward == 0 || !manager.CanAddConnection(forward, edge.FromCell,
					false, out _) || !manager.CanAddConnection(backward, edge.ToCell,
					false, out _))
					continue;
				manager.AddConnection(forward, edge.FromCell, false);
				manager.AddConnection(backward, edge.ToCell, false);
			}
		}

		private static PlacementOutcome Observe(
			GameObject gameObject,
			int cell,
			BuildPlacementKind kind,
			BuildOperationId operationId)
		{
			NetworkIdentity identity = gameObject.AddOrGet<NetworkIdentity>();
			identity.RegisterIdentity();
			if (identity.NetId != 0)
				BuildLifecycleRegistry.Bind(identity.NetId, operationId);
			return new PlacementOutcome(cell, kind, identity.NetId, identity.LifecycleRevision);
		}

		private static bool TryFindPlacement(
			BuildingDef def,
			int cell,
			BuildPlacementKind kind,
			out GameObject result)
		{
			bool replacement = IsReplacement(kind);
			global::ObjectLayer layer = replacement ? def.ReplacementLayer : def.ObjectLayer;
			result = layer == global::ObjectLayer.NumLayers
				? null
				: Grid.Objects[cell, (int)layer];
			if (result == null && !replacement && def.TileLayer != global::ObjectLayer.NumLayers)
				result = Grid.Objects[cell, (int)def.TileLayer];
			return result?.GetComponent<Building>()?.Def == def;
		}

		private static bool IsReplacement(GameObject gameObject, BuildingDef def)
			=> def.ReplacementLayer != global::ObjectLayer.NumLayers &&
				gameObject != null && Grid.Objects[Grid.PosToCell(gameObject),
					(int)def.ReplacementLayer] == gameObject;

		private static bool IsReplacement(BuildPlacementKind kind)
			=> kind is BuildPlacementKind.QueuedReplacement or BuildPlacementKind.CompletedReplacement;

		private static bool IsCompleted(BuildPlacementKind kind)
			=> kind is BuildPlacementKind.Completed or BuildPlacementKind.CompletedReplacement;
	}
}
