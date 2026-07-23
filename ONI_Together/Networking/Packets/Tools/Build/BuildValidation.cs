using System;
using System.Collections.Generic;

namespace ONI_Together.Networking.Packets.Tools.Build
{
	internal static class BuildRequestValidator
	{
		internal const int MaxIdLength = 256;
		internal const int MaxWireCell = 4 * 1024 * 1024;
		internal const int MaxMaterialTagCount = 16;
		internal const int MaxMaterialTagLength = 128;
		internal const int MaxPathNodeCount = 8192;
		internal const string DefaultFacade = "DEFAULT_FACADE";

		internal static bool TryValidate(BuildRequest request, out BuildRejected rejection)
		{
			rejection = null;
			if (request == null || !request.OperationId.IsValid ||
				!IsBoundedId(request.PrefabId) || request.Geometry == null ||
				!IsBoundedFacade(request.FacadeId) ||
				!IsPriorityAllowed(request.PriorityClass, request.PriorityValue) ||
				!AreMaterialTagsWireValid(request.MaterialTags) ||
				request.ObjectLayer < 0)
				return Fail(request, BuildRejectionReason.InvalidRequest,
					"build request payload is invalid", out rejection);

			if (request.Geometry is BuildGeometry.SinglePlacement single)
				return ValidateSingle(request, single.Cell, single.Orientation, out rejection);
			if (request.Geometry is SinglePlacementGeometry singleGeometry)
				return ValidateSingle(request, singleGeometry.Cell, singleGeometry.Orientation,
					out rejection);
			if (request.Geometry is BuildGeometry.UtilityPath utility)
				return ValidatePath(request, utility.Cells, out rejection);
			if (request.Geometry is UtilityPathGeometry utilityGeometry)
				return ValidatePath(request, utilityGeometry.Cells, out rejection);
			return Fail(request, BuildRejectionReason.InvalidGeometry,
				"unsupported build geometry", out rejection);
		}

		internal static bool IsBoundedId(string value)
			=> !string.IsNullOrWhiteSpace(value) && value.Length <= MaxIdLength;

		internal static bool IsBoundedFacade(string value)
			=> !string.IsNullOrWhiteSpace(value) && value.Length <= MaxIdLength;

		internal static bool AreMaterialTagsWireValid(IReadOnlyList<string> tags)
		{
			if (tags == null || tags.Count == 0 || tags.Count > MaxMaterialTagCount)
				return false;
			foreach (string tag in tags)
				if (string.IsNullOrWhiteSpace(tag) || tag.Length > MaxMaterialTagLength)
					return false;
			return true;
		}

		internal static bool IsPriorityAllowed(int priorityClass, int priorityValue)
			=> priorityClass is 0 or 1
				? priorityValue is >= 1 and <= 9
				: priorityClass == 2 && priorityValue == 1;

		internal static bool IsWireCell(int cell)
			=> cell >= 0 && cell < MaxWireCell;

		internal static bool IsPathShapeWireValid(IReadOnlyList<int> cells)
		{
			if (cells == null || cells.Count == 0 || cells.Count > MaxPathNodeCount)
				return false;
			var seen = new HashSet<int>();
			foreach (int cell in cells)
				if (!IsWireCell(cell) || !seen.Add(cell))
					return false;
			return true;
		}

		internal static bool IsKnownOrientationValue(Orientation orientation)
			=> orientation is Orientation.Neutral or Orientation.R90 or Orientation.R180 or
				Orientation.R270 or Orientation.FlipH or Orientation.FlipV;

		internal static bool IsOrientationAllowed(Orientation orientation, PermittedRotations permitted)
		{
			if (orientation == Orientation.Neutral)
				return true;
			return permitted switch
			{
				PermittedRotations.R360 => orientation is Orientation.R90 or Orientation.R180 or Orientation.R270,
				PermittedRotations.R90 => orientation == Orientation.R90,
				PermittedRotations.FlipH => orientation == Orientation.FlipH,
				PermittedRotations.FlipV => orientation == Orientation.FlipV,
				_ => false
			};
		}

		private static bool ValidateSingle(
			BuildRequest request,
			int cell,
			Orientation orientation,
			out BuildRejected rejection)
		{
			if (!IsWireCell(cell) || !IsKnownOrientationValue(orientation))
				return Fail(request, BuildRejectionReason.InvalidGeometry,
					"single placement geometry is invalid", out rejection);
			rejection = null;
			return true;
		}

		private static bool ValidatePath(
			BuildRequest request,
			IReadOnlyList<int> cells,
			out BuildRejected rejection)
		{
			if (cells == null || cells.Count == 0 || cells.Count > MaxPathNodeCount)
				return Fail(request, BuildRejectionReason.InvalidGeometry,
					"utility path is empty or too long", out rejection);
			var seen = new HashSet<int>();
			foreach (int cell in cells)
				if (!IsWireCell(cell) || !seen.Add(cell))
					return Fail(request, BuildRejectionReason.InvalidGeometry,
						"utility path contains an invalid or duplicate cell", out rejection);
			rejection = null;
			return true;
		}

		private static bool Fail(
			BuildRequest request,
			BuildRejectionReason reason,
			string message,
			out BuildRejected rejection)
		{
			rejection = new BuildRejected(request?.OperationId ?? default, reason, message);
			return false;
		}
	}

	internal static class BuildCommitValidator
	{
		internal static bool TryValidate(BuildCommit commit, out string error)
		{
			error = string.Empty;
			if (commit == null || !commit.OperationId.IsValid || !commit.Revision.IsValid)
				return Fail("commit identity is invalid", out error);
			var cells = new HashSet<int>();
			foreach (PlacementOutcome outcome in commit.Placements)
			{
				if (outcome == null || !BuildRequestValidator.IsWireCell(outcome.Cell) ||
					!cells.Add(outcome.Cell) ||
					(outcome.NetId == 0) != (outcome.LifecycleRevision == 0))
					return Fail("commit contains an invalid placement outcome", out error);
			}
			var edges = new HashSet<UtilityEdge>();
			var placementCells = new HashSet<int>(cells);
			if (commit.Request?.Geometry is BuildGeometry.UtilityPath utility)
				if (!ValidateUtilityTopology(utility.Cells, placementCells, commit.Connections))
					return Fail("commit utility topology is not a successful-cell subset", out error);
			if (commit.Request?.Geometry is UtilityPathGeometry utilityGeometry)
				if (!ValidateUtilityTopology(utilityGeometry.Cells, placementCells, commit.Connections))
					return Fail("commit utility topology is not a successful-cell subset", out error);
			foreach (UtilityEdge edge in commit.Connections)
				if (!BuildRequestValidator.IsWireCell(edge.FromCell) ||
					!BuildRequestValidator.IsWireCell(edge.ToCell) ||
					!edges.Add(edge))
					return Fail("commit contains an invalid utility edge", out error);
			return true;
		}

		private static bool ValidateUtilityTopology(
			IReadOnlyList<int> path,
			ISet<int> placementCells,
			IReadOnlyList<UtilityEdge> connections)
		{
			var pathCells = new HashSet<int>(path);
			foreach (int cell in placementCells)
				if (!pathCells.Contains(cell))
					return false;
			foreach (UtilityEdge edge in connections)
				if (!placementCells.Contains(edge.FromCell) ||
					!placementCells.Contains(edge.ToCell))
					return false;
			return true;
		}

		private static bool Fail(string message, out string error)
		{
			error = message;
			return false;
		}
	}
}
