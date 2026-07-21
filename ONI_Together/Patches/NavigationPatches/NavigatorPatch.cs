using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using Shared.Profiling;

namespace ONI_Together.Patches.Navigation
{
	[HarmonyPatch(typeof(Navigator), nameof(Navigator.AdvancePath))]
	public static class NavigatorPatch
	{
		static bool Prefix(Navigator __instance)
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.InSession)
				return true;

			if (!__instance.TryGetComponent<NetworkIdentity>(out var ni))
				return true;

			if (MultiplayerSession.IsHost)
				return true;

			LogClientOriginalBlocked(ni.NetId, __instance);
			return false;
		}

		internal static void LogClientOriginalBlocked(int netId, Navigator navigator)
		{
#if DEBUG
			string state = $"netId={netId},position={navigator.transform.position}";
			IntegrationScenarioEvidenceCore.Log(
				"motion", "client-original-blocked", 0, false, state);
			IntegrationScenarioEvidenceCore.Log(
				"remote-dig", "client-original-blocked", 0, false, state);
#endif
		}
	}

	[HarmonyPatch(typeof(Navigator), nameof(Navigator.GoTo), new[] {
		typeof(KMonoBehaviour), typeof(CellOffset[]), typeof(NavTactic)
})]
	public static class Navigator_GoTo_Target_Patch
	{
		static bool Prefix(Navigator __instance)
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.InSession)
				return true;

			if (__instance.TryGetComponent<NetworkIdentity>(out var netIdentity))
			{
				if (MultiplayerSession.IsHost)
					return true;
				NavigatorPatch.LogClientOriginalBlocked(netIdentity.NetId, __instance);
				return false;
			}

			return true;
		}
	}

	[HarmonyPatch(typeof(Navigator), nameof(Navigator.BeginTransition))]
	public static class Navigator_BeginTransition_Patch
	{
		static void Postfix(Navigator __instance, NavGrid.Transition transition)
		{
			using var _ = Profiler.Scope();
			RemoteMotionPresenter.PublishTransition(__instance, transition);
		}
	}

	[HarmonyPatch(typeof(Navigator), nameof(Navigator.Stop))]
	public static class Navigator_Stop_Patch
	{
		static void Postfix(Navigator __instance, bool arrived_at_destination, bool play_idle)
		{
			using var _ = Profiler.Scope();
			if (play_idle)
				RemoteMotionPresenter.PublishStop(__instance);
		}
	}
}
