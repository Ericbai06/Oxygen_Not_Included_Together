using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;

namespace ONI_Together.Patches.World
{
	/// <summary>
	/// Patch to detect when research completes and sync to all clients.
	/// Patches TechInstance.Purchased which is called when a tech is completed.
	/// </summary>
	[HarmonyPatch(typeof(TechInstance), "Purchased")]
	public static class ResearchCompletePatch
	{
		public static void Postfix(TechInstance __instance)
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.IsHost) return;
			if (__instance?.tech == null) return;

			if (ResearchSyncCoordinator.IsApplyingAuthoritativeState) return;
			ResearchSyncCoordinator.PublishHostCompletion(__instance.tech.Id);
			ONI_Together.DebugTools.DebugConsole.Log($"[ResearchCompletePatch] Sent completion for: {__instance.tech.Name}");
		}
	}
}
