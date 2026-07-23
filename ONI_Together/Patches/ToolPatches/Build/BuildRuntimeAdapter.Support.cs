using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ONI_Together.Networking.Packets.Tools.Build;
using UnityEngine;

namespace ONI_Together.Patches.ToolPatches.Build
{
	internal static partial class BuildRuntimeAdapter
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
