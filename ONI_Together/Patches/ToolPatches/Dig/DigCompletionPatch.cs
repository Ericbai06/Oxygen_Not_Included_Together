using HarmonyLib;
using ONI_Together.Misc.World;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;

namespace ONI_Together.Patches.ToolPatches.Dig
{
	[HarmonyPatch(typeof(Diggable), "OnStopWork")]
	public static class DigCompletionPatch
	{
		public static void Postfix(int ___cached_cell, bool ___isDigComplete)
		{
			using var _ = Profiler.Scope();
			if (!MultiplayerSession.IsHostInSession || !___isDigComplete
			    || !Grid.IsValidCell(___cached_cell))
				return;
			WorldUpdateBatcher.Queue(CaptureFinalCell(___cached_cell));
		}

		internal static WorldUpdatePacket.CellUpdate CaptureFinalCell(int cell)
			=> new()
			{
				Cell = cell,
				ElementIdx = Grid.ElementIdx[cell],
				Temperature = Grid.Temperature[cell],
				Mass = Grid.Mass[cell],
				DiseaseIdx = Grid.DiseaseIdx[cell],
				DiseaseCount = Grid.DiseaseCount[cell],
				ReplaceType = SimMessages.ReplaceType.Replace
			};
	}
}
