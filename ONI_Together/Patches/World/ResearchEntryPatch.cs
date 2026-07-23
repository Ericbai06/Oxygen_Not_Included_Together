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
				var target = new ONI_Together.DebugTools.ResearchTarget
				{
					TechId = targetTech.Id,
				};
				var state = new ONI_Together.DebugTools.ResearchState
				{
					Revision = ResearchSyncCoordinator.AppliedResearchRevision,
					Completed = false,
					Progress = 0d,
				};
				ONI_Together.DebugTools.IntegrationScenarioEvidenceCore.Log(
					ONI_Together.DebugTools.TypedEvidenceRuntimeContext.Create(
						"research", "client-original-blocked", state.Revision,
						target, state, "sync:4c2bf0f30121f12d2324dadf"));
#endif
			}
			return false;
		}
	}
}
