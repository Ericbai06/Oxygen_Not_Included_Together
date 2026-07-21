using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;

namespace ONI_Together.Patches.World
{
	[HarmonyPatch(typeof(Research), "SetActiveResearch")]
	public static class ResearchPatch
	{
		public static void Postfix(Research __instance, Tech tech, bool clearQueue)
		{
			using var _ = Profiler.Scope();
			if (MultiplayerSession.IsHost && !ResearchSyncCoordinator.IsApplyingAuthoritativeState)
				ResearchSyncCoordinator.PublishHostState();
		}
	}
}
