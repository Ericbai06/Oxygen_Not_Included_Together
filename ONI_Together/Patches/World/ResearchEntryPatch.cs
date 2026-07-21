using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;

namespace ONI_Together.Patches.World
{
	[HarmonyPatch(typeof(ResearchEntry), "OnResearchClicked")]
	public static class ResearchEntryPatch
	{
		public static bool Prefix(ResearchEntry __instance)
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.InSession) return true; // Offline, operate normally
			if (MultiplayerSession.IsHost) return true; // Host operates normally

			var targetTech = __instance.targetTech;
			if (targetTech != null && ResearchSyncCoordinator.TrySendRequest(targetTech.Id))
			{
				ONI_Together.DebugTools.DebugConsole.Log($"[Client] Requested research: {targetTech.Id}");
#if DEBUG
				ONI_Together.DebugTools.IntegrationScenarioEvidenceCore.Log(
					"research", "client-original-blocked",
					ResearchSyncCoordinator.AppliedResearchRevision, false,
					"tech=" + System.Uri.EscapeDataString(targetTech.Id));
#endif
			}
			return false;
		}
	}
}
