#if DEBUG
using System;

namespace ONI_Together.DebugTools
{
	internal static partial class FaultUnityRuntimeDriver
	{
		private static FaultInjectionReceipt ExecuteCleanPhase(
			FaultUnityProductionBinding binding)
		{
			try
			{
				if (binding.CaseId == "building.destroy-deferred")
					return FaultDeferredDestroyRuntime.ExecuteClean(binding);
				FaultRuntimeTargetContext context = FaultUnityRuntimeStages.SetupClean(binding);
				FaultUnityRuntimeStages.Trigger(context);
				FaultUnityRuntimeStages.ReceiptBarrier(context);
				FaultUnityRuntimeStages.Snapshot(context);
				FaultUnityRuntimeStages.Oracle(context, context.Snapshot);
				return FaultUnityRuntimeStages.Validate(context)
					? FaultInjectionReceipt.Pass(
						"runtime", binding.CleanControlReceiptId, context: context)
					: FaultInjectionReceipt.Fail("clean-oracle",
						"clean-control-unsatisfied:" + binding.CaseId,
						context: context);
			}
			catch (Exception failure)
			{
				if (binding.CaseId == "building.destroy-deferred")
					FaultDeferredDestroyRuntime.AbortPending();
				FaultInjectionUnitySeams.Clean(binding.CaseId);
				return FaultInjectionReceipt.Fail("clean-exception",
					binding.CaseId + ":" + failure.GetType().Name + ":" + failure.Message);
			}
		}
	}
}
#endif
