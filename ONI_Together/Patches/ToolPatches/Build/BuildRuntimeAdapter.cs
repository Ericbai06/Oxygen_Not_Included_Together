using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Tools.Build;
using UnityEngine;

namespace ONI_Together.Patches.ToolPatches.Build
{
	internal static class OniBuildRuntimeAdapter
	{
		private static readonly FieldInfo PriorityClassField =
			typeof(PrioritySetting).GetField(
				nameof(PrioritySetting.priority_class),
				BindingFlags.Instance | BindingFlags.Public)
			?? throw new MissingFieldException(
				typeof(PrioritySetting).FullName,
				nameof(PrioritySetting.priority_class));

		internal static bool TryGetReplacement(
			BuildingDef def,
			int cell,
			Orientation orientation,
			IReadOnlyList<Tag> materials,
			out GameObject candidate)
		{
			candidate = def?.GetReplacementCandidate(cell);
			if (candidate == null || materials == null || materials.Count == 0 ||
				def.ReplacementLayer == global::ObjectLayer.NumLayers)
				return false;
			bool occupied = false;
			def.RunOnArea(cell, orientation, offset =>
				occupied |= def.IsReplacementLayerOccupied(offset));
			BuildingComplete complete = candidate.GetComponent<BuildingComplete>();
			return !occupied && complete != null && complete.Def.Replaceable &&
				def.CanReplace(candidate);
		}

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

		private static bool TryResolve(
			BuildRequest request,
			out BuildingDef def,
			out List<Tag> materials,
			out PrioritySetting priority,
			out BuildRejected rejection)
		{
			def = null;
			materials = null;
			priority = default;
			rejection = null;
			if (!BuildRequestValidator.TryValidate(request, out rejection))
				return false;
			def = Assets.GetBuildingDef(request.PrefabId);
			if (def == null || request.ObjectLayer != (int)def.ObjectLayer)
			{
				rejection = Reject(request, BuildRejectionReason.UnknownPrefab,
					"unknown prefab or object layer");
				return false;
			}
			if (!TryResolveMaterials(def, request.MaterialTags, out materials))
			{
				rejection = Reject(request, BuildRejectionReason.InvalidMaterial,
					"material selection is not valid for prefab");
				return false;
			}
			string facade = NormalizeFacade(request.FacadeId);
			if (facade != BuildRequestValidator.DefaultFacade &&
				(def.AvailableFacades == null || !def.AvailableFacades.Contains(facade)))
			{
				rejection = Reject(request, BuildRejectionReason.InvalidFacade,
					"facade is unavailable for prefab");
				return false;
			}
			priority = ToPriority(request.PriorityClass, request.PriorityValue);
			return true;
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

		private static bool IsPathShapeValid(IReadOnlyList<int> cells)
		{
			if (!BuildRequestValidator.IsWireCell(cells[0]))
				return false;
			for (int i = 1; i < cells.Count; i++)
			{
				int previous = cells[i - 1];
				int current = cells[i];
				if (!Grid.IsValidCell(previous) || !Grid.IsValidCell(current) ||
					Math.Abs(current % Grid.WidthInCells - previous % Grid.WidthInCells) +
					Math.Abs(current / Grid.WidthInCells - previous / Grid.WidthInCells) != 1)
					return false;
			}
			return true;
		}

		private static bool TryResolveMaterials(
			BuildingDef def,
			IReadOnlyList<string> tags,
			out List<Tag> result)
		{
			result = null;
			if (def.MaterialCategory == null || tags.Count != def.MaterialCategory.Length)
				return false;
			result = new List<Tag>(tags.Count);
			for (int i = 0; i < tags.Count; i++)
			{
				Tag tag = TagManager.Create(tags[i]);
				if (!MaterialSelector.GetValidMaterials(def.MaterialCategory[i]).Contains(tag))
					return false;
				result.Add(tag);
			}
			return true;
		}

		private static PrioritySetting ToPriority(int priorityClass, int priorityValue)
		{
			PrioritySetting setting = default;
			object boxed = setting;
			PriorityClassField.SetValue(
				boxed,
				Enum.ToObject(PriorityClassField.FieldType, priorityClass));
			setting = (PrioritySetting)boxed;
			setting.priority_value = priorityValue;
			return setting;
		}

		private static void SetPriority(GameObject gameObject, PrioritySetting priority)
			=> gameObject.GetComponent<Prioritizable>()?.SetMasterPriority(priority);

		private static Orientation GetOrientation(BuildRequest request)
			=> request.Geometry is BuildGeometry.SinglePlacement single
				? single.Orientation
				: request.Geometry is SinglePlacementGeometry singleGeometry
					? singleGeometry.Orientation
					: Orientation.Neutral;

		private static string NormalizeFacade(string facade)
			=> string.IsNullOrWhiteSpace(facade)
				? BuildRequestValidator.DefaultFacade
				: facade;

		private static BuildRejected Reject(
			BuildRequest request,
			BuildRejectionReason reason,
			string message)
			=> new(request.OperationId, reason, message);
	}
}
