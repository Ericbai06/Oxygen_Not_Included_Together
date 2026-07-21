using HarmonyLib;
using ONI_Together.Networking;

namespace ONI_Together.Patches.GamePatches
{
	[HarmonyPatch(typeof(StateMachineUpdater),
		nameof(StateMachineUpdater.AdvanceOneSimSubTick))]
	internal static class PresentationTickClockPatch
	{
		[HarmonyPostfix]
		private static void Postfix() => PresentationTickClock.AdvanceLocalTick();
	}
}
