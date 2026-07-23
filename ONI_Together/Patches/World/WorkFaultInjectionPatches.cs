#if DEBUG
using System.Reflection;
using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;

namespace ONI_Together.Patches.Duplicant
{
	[HarmonyPatch]
	internal static class DiggableStartWorkFaultPatch
	{
		internal static MethodBase TargetMethod()
			=> AccessTools.Method(
				typeof(Diggable), nameof(Workable.StartWork), new[] { typeof(WorkerBase) });

		internal static bool Prefix(Diggable __instance)
		{
			Element originalDigElement = __instance?.originalDigElement;
			IFaultInputMutation mutation = ProductionFaultInputGates.MissingOriginalDigElement(
				ref originalDigElement);
			FaultInjectionUnitySeams.EmitReceipt(mutation, runtimeTarget: __instance);
			return originalDigElement != null;
		}
	}
}

namespace ONI_Together.Patches.World
{
	[HarmonyPatch(typeof(Workable), nameof(Workable.StartWork), typeof(WorkerBase))]
	internal static class WorkableStartWorkAuthorityPatch
	{
		internal static bool Prefix()
		{
			bool clientNativeStart = false;
			IFaultInputMutation mutation = ProductionFaultInputGates.ClientNativeStart(
				ref clientNativeStart);
			FaultInjectionUnitySeams.EmitReceipt(
				mutation, runtimeTarget: "runtime:work.client-native-start");
			return !clientNativeStart;
		}
	}
}
#endif
