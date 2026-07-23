using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class CursorViewportTests
	{
		[UnitTest(name: "Cursor viewport normalizes reversed camera corners", category: "Networking")]
		public static UnitTestResult NormalizesReversedCameraCorners()
		{
			var candidate = new CursorManager.CursorViewport
			{
				MinX = 12,
				MinY = 34,
				MaxX = -5,
				MaxY = -8,
			};
			bool accepted = CursorManager.TryNormalizeViewport(candidate, out var normalized);

			if (!accepted || normalized.MinX != -5 || normalized.MinY != -8
				|| normalized.MaxX != 12 || normalized.MaxY != 34)
				return UnitTestResult.Fail("Reversed camera corners were not normalized into an ordered viewport");

			return UnitTestResult.Pass("Reversed camera corners normalize to ViewMin <= ViewMax");
		}

		[UnitTest(name: "Cursor viewport rejects coordinates outside Int16", category: "Networking")]
		public static UnitTestResult RejectsCoordinatesOutsideInt16()
		{
			var invalid = new[]
			{
				new CursorManager.CursorViewport { MinX = short.MinValue - 1, MaxX = 0 },
				new CursorManager.CursorViewport { MinY = short.MinValue - 1, MaxY = 0 },
				new CursorManager.CursorViewport { MinX = 0, MaxX = short.MaxValue + 1 },
				new CursorManager.CursorViewport { MinY = 0, MaxY = short.MaxValue + 1 },
			};
			foreach (var candidate in invalid)
				if (CursorManager.TryNormalizeViewport(candidate, out _))
					return UnitTestResult.Fail("Cursor viewport accepted a coordinate outside Int16");

			return UnitTestResult.Pass("Cursor viewport rejects every coordinate outside Int16");
		}
	}
}
