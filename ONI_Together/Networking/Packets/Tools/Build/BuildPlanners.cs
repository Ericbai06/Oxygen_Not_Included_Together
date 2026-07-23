using System;
using System.Collections.Generic;
using System.Linq;

namespace ONI_Together.Networking.Packets.Tools.Build
{
	public static class SinglePlacementPlanner
	{
		public static BuildGeometry.SinglePlacement Plan(int cell, Orientation orientation)
			=> new(cell, orientation);

		public static bool TryPlan(
			int cell,
			Orientation orientation,
			out BuildGeometry geometry)
		{
			if (!BuildRequestValidator.IsWireCell(cell) ||
				!BuildRequestValidator.IsKnownOrientationValue(orientation))
			{
				geometry = null;
				return false;
			}
			geometry = new BuildGeometry.SinglePlacement(cell, orientation);
			return true;
		}
    }

	public static class UtilityPathPlanner
	{
		public static BuildGeometry.UtilityPath Plan(IEnumerable<int> cells)
			=> new(cells);

		public static bool TryPlan(
			IEnumerable<int> cells,
			out BuildGeometry geometry)
		{
			int[] path = (cells ?? Enumerable.Empty<int>()).ToArray();
			if (!BuildRequestValidator.IsPathShapeWireValid(path))
			{
				geometry = null;
				return false;
			}
			geometry = new BuildGeometry.UtilityPath(path);
			return true;
		}

		public static IReadOnlyList<UtilityEdge> BuildConnections(
			IEnumerable<int> requestedPath,
			IEnumerable<int> successfulCells)
		{
			int[] path = (requestedPath ?? Enumerable.Empty<int>()).ToArray();
			var successful = new HashSet<int>(successfulCells ?? Enumerable.Empty<int>());
			var result = new List<UtilityEdge>();
			for (int index = 1; index < path.Length; index++)
				if (successful.Contains(path[index - 1]) && successful.Contains(path[index]))
					result.Add(new UtilityEdge(path[index - 1], path[index]));
			return result;
		}
	}
}
