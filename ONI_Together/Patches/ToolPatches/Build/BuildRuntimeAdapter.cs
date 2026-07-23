using System;
using System.Collections.Generic;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Tools.Build;
using UnityEngine;

namespace ONI_Together.Patches.ToolPatches.Build
{
	internal static partial class BuildRuntimeAdapter
	{
		internal static bool TryExecute(
			BuildRequest request,
			HostBuildPolicy policy,
			out BuildExecutionResult result,
			out BuildRejected rejection)
		{
			result = null;
			rejection = null;
			if (!TryResolve(request, out BuildingDef def, out List<Tag> materials,
				out PrioritySetting priority, out rejection))
				return false;
			NetworkIdentity.BeginManagedSpawn();
			try
			{
				if (request.Geometry is BuildGeometry.UtilityPath utility)
					return TryExecuteUtility(request, def, materials, priority, utility.Cells,
						policy, out result, out rejection);
				if (request.Geometry is UtilityPathGeometry utilityGeometry)
					return TryExecuteUtility(request, def, materials, priority,
						utilityGeometry.Cells, policy, out result, out rejection);
				int cell;
				Orientation orientation;
				if (request.Geometry is BuildGeometry.SinglePlacement single)
				{
					cell = single.Cell;
					orientation = single.Orientation;
				}
				else if (request.Geometry is SinglePlacementGeometry singleGeometry)
				{
					cell = singleGeometry.Cell;
					orientation = singleGeometry.Orientation;
				}
				else
				{
					rejection = Reject(request, BuildRejectionReason.InvalidGeometry,
						"unsupported build geometry");
					return false;
				}
				return TryExecuteSingle(request, def, materials, priority, cell, orientation,
					policy, out result, out rejection);
			}
			finally
			{
				NetworkIdentity.EndManagedSpawn();
			}
		}

		private static bool TryExecuteSingle(
			BuildRequest request,
			BuildingDef def,
			IList<Tag> materials,
			PrioritySetting priority,
			int cell,
			Orientation orientation,
			HostBuildPolicy policy,
			out BuildExecutionResult result,
			out BuildRejected rejection)
		{
			result = null;
			rejection = null;
			bool completed = policy.InstantBuild;
			if (TryPlace(request, def, materials, priority, cell, orientation,
				completed, replacement: false, out GameObject placed) ||
				TryPlace(request, def, materials, priority, cell, orientation,
				completed, replacement: true, out placed))
			{
				BuildPlacementKind kind = (completed, IsReplacement(placed, def)) switch
				{
					(true, true) => BuildPlacementKind.CompletedReplacement,
					(true, false) => BuildPlacementKind.Completed,
					(false, true) => BuildPlacementKind.QueuedReplacement,
					_ => BuildPlacementKind.Queued
				};
				PlacementOutcome outcome = Observe(placed, cell, kind, request.OperationId);
				result = new BuildExecutionResult(
					new[] { outcome }, Array.Empty<UtilityEdge>());
				return true;
			}
			rejection = Reject(request, BuildRejectionReason.Occupied,
				"build location is occupied or invalid");
			return false;
		}

		private static bool TryExecuteUtility(
			BuildRequest request,
			BuildingDef def,
			IList<Tag> materials,
			PrioritySetting priority,
			IReadOnlyList<int> cells,
			HostBuildPolicy policy,
			out BuildExecutionResult result,
			out BuildRejected rejection)
		{
			result = null;
			rejection = null;
			if (def.TileLayer == global::ObjectLayer.NumLayers ||
				def.BuildingComplete?.GetComponent<IHaveUtilityNetworkMgr>() == null ||
				!IsPathShapeValid(cells))
			{
				rejection = Reject(request, BuildRejectionReason.InvalidGeometry,
					"utility prefab or path geometry is invalid");
				return false;
			}
			bool completed = policy.InstantBuild;
			var outcomes = new List<PlacementOutcome>();
			var successfulCells = new HashSet<int>();
			foreach (int cell in cells)
			{
				if (!TryPlace(request, def, materials, priority, cell, Orientation.Neutral,
					completed, replacement: false, out GameObject placed) &&
					!TryPlace(request, def, materials, priority, cell, Orientation.Neutral,
						completed, replacement: true, out placed))
					continue;
				BuildPlacementKind kind = (completed, IsReplacement(placed, def)) switch
				{
					(true, true) => BuildPlacementKind.CompletedReplacement,
					(true, false) => BuildPlacementKind.Completed,
					(false, true) => BuildPlacementKind.QueuedReplacement,
					_ => BuildPlacementKind.Queued
				};
				outcomes.Add(Observe(placed, cell, kind, request.OperationId));
				successfulCells.Add(cell);
			}
			if (outcomes.Count == 0)
			{
				rejection = Reject(request, BuildRejectionReason.Occupied,
					"no utility path cell could be placed");
				return false;
			}
			IUtilityNetworkMgr manager = def.BuildingComplete
				.GetComponent<IHaveUtilityNetworkMgr>()?.GetNetworkManager();
			var edges = new List<UtilityEdge>();
			if (manager != null)
				for (int index = 1; index < cells.Count; index++)
				{
					int from = cells[index - 1];
					int to = cells[index];
					if (!successfulCells.Contains(from) || !successfulCells.Contains(to))
						continue;
					UtilityConnections forward = UtilityConnectionsExtensions.DirectionFromToCell(from, to);
					UtilityConnections backward = forward.InverseDirection();
					if (forward == 0 || !manager.CanAddConnection(forward, from, false, out _) ||
						!manager.CanAddConnection(backward, to, false, out _))
						continue;
					manager.AddConnection(forward, from, false);
					manager.AddConnection(backward, to, false);
					edges.Add(new UtilityEdge(from, to));
				}
			result = new BuildExecutionResult(outcomes, edges);
			return true;
		}

		private static bool TryPlace(
			BuildRequest request,
			BuildingDef def,
			IList<Tag> materials,
			PrioritySetting priority,
			int cell,
			Orientation orientation,
			bool completed,
			bool replacement,
			out GameObject placed)
		{
			placed = null;
			if (!Grid.IsValidCell(cell) || !Grid.IsVisible(cell))
				return false;
			Vector3 position = Grid.CellToPosCBC(cell, Grid.SceneLayer.Building);
			GameObject candidate = replacement ? def.GetReplacementCandidate(cell) : null;
			if (replacement && (candidate == null || !def.CanReplace(candidate)))
				return false;
			if (!def.IsValidBuildLocation(null, position, orientation, replacement) ||
				!def.IsValidPlaceLocation(null, position, orientation, replacement, out _))
				return false;
			if (completed)
			{
				if (replacement)
					candidate?.DeleteObject();
				placed = def.Build(cell, orientation, null, materials,
					def.Temperature, NormalizeFacade(request.FacadeId), false,
					GameClock.Instance.GetTime());
			}
			else
			{
				placed = replacement
					? def.TryReplaceTile(null, position, orientation, materials,
						NormalizeFacade(request.FacadeId))
					: def.TryPlace(null, position, orientation, materials,
						NormalizeFacade(request.FacadeId));
			}
			if (placed == null)
				return false;
			SetPriority(placed, priority);
			return true;
		}
	}
}
