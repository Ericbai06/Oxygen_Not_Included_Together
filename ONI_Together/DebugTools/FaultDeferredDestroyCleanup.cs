#if DEBUG
using System;
using UnityEngine;

namespace ONI_Together.DebugTools
{
	internal static partial class FaultDeferredDestroyRuntime
	{
		internal static FaultInjectionReceipt ExecuteClean(
			FaultUnityProductionBinding binding)
		{
			FaultRuntimeTargetContext context = Pending();
			if (context.DeferredDestroyEvidence.FixtureDisposeRequested)
				return FinishDeferredCleanup(binding, context);
			FaultUnityRuntimeStages.Reset(context);
			FaultUnityRuntimeStages.CleanControlTrigger(context);
			FaultUnityRuntimeStages.ReceiptBarrier(context);
			FaultUnityRuntimeStages.Snapshot(context);
			FaultUnityRuntimeStages.Oracle(context, context.Snapshot);
			if (!FaultUnityRuntimeStages.Validate(context))
			{
				Abort(context);
				return FaultInjectionReceipt.Fail("clean-oracle",
					"clean-control-unsatisfied:" + binding.CaseId);
			}
			if (!DisposeFixture(context)) return DeferCleanup(context);
			if (!FixtureAbsenceBarrier(context)) return DeferCleanup(context);
			if (!ValidateFixtureDisposal(context.DeferredDestroyEvidence))
				return DeferCleanup(context);
			ClearPending(context);
			return FaultInjectionReceipt.Pass(
				"runtime", binding.CleanControlReceiptId,
				context.DeferredDestroyEvidence, context);
		}

		private static FaultInjectionReceipt FinishDeferredCleanup(
			FaultUnityProductionBinding binding, FaultRuntimeTargetContext context)
		{
			if (!FixtureAbsenceBarrier(context)) return DeferCleanup(context);
			if (!ValidateFixtureDisposal(context.DeferredDestroyEvidence))
				return DeferCleanup(context);
			ClearPending(context);
			return FaultInjectionReceipt.Pass(
				"runtime", binding.CleanControlReceiptId,
				context.DeferredDestroyEvidence, context);
		}

		internal static FaultInjectionReceipt DeferCleanup(FaultRuntimeTargetContext context)
		{
			context.DeferredDestroyEvidence.PendingPreservedForRetry = true;
			return FaultInjectionReceipt.Fail(
				"cleanup-pending", "fixture-absence-not-observed:" + context.CaseId,
				context.DeferredDestroyEvidence, context);
		}

		internal static bool FixtureAbsenceBarrier(FaultRuntimeTargetContext context)
		{
			DeferredDestroyResetEvidence evidence = context.DeferredDestroyEvidence;
			evidence.FixtureDisposeObservedFrame = Time.frameCount;
			evidence.FixtureAbsent = context.DeferredReplacement == null
			                         && context.DeferredOriginal == null;
			if (evidence.FixtureAbsent)
			{
				evidence.FixtureDisposed = true;
				evidence.PendingPreservedForRetry = false;
			}
			return evidence.FixtureAbsent
			       && evidence.FixtureDisposeObservedFrame
			       > evidence.FixtureDisposeRequestedFrame;
		}

		internal static bool ValidateFixtureDisposal(DeferredDestroyResetEvidence evidence)
			=> evidence != null && evidence.FixtureDisposeRequested
			   && evidence.FixtureDisposeObservedFrame
			   > evidence.FixtureDisposeRequestedFrame
			   && evidence.FixtureAbsent
			   && !evidence.PendingPreservedForRetry;

		internal static bool DisposeFixture(FaultRuntimeTargetContext context)
		{
			GameObject replacement = context?.DeferredReplacement;
			GameObject original = context?.DeferredOriginal;
			GameObject fixture = !ReferenceEquals(replacement, null) ? replacement : original;
			if (ReferenceEquals(fixture, null)
			    || context?.DeferredDestroyEvidence?.DisposableFixtureOwned != true)
				return false;
			return RequestUnityDestroy(context, fixture);
		}

		private static bool RequestUnityDestroy(
			FaultRuntimeTargetContext context, GameObject fixture)
		{
			if (!fixture.name.StartsWith(FixturePrefix, StringComparison.Ordinal)) return false;
			DeferredDestroyResetEvidence evidence = context.DeferredDestroyEvidence;
			evidence.FixtureDisposeRequested = true;
			evidence.FixtureDisposeRequestedFrame = Time.frameCount;
			evidence.PendingPreservedForRetry = false;
			UnityEngine.Object.Destroy(fixture);
			return true;
		}

		internal static void AbortUncommitted()
		{
			FaultRuntimeTargetContext context;
			lock (Sync) context = uncommitted;
			if (context != null) Abort(context);
		}

		internal static void AbortPending()
		{
			FaultRuntimeTargetContext context;
			lock (Sync) context = pending;
			if (context != null) Abort(context);
		}

		internal static void Abort(FaultRuntimeTargetContext context)
		{
			bool disposed = DisposeFixture(context);
			lock (Sync)
			{
				if (ReferenceEquals(pending, context)) pending = null;
				if (ReferenceEquals(uncommitted, context)) uncommitted = null;
			}
			FaultInjectionReceipt cleaned = FaultInjectionUnitySeams.Clean(context.CaseId);
			DeferredDestroyResetEvidence evidence = context.DeferredDestroyEvidence;
			if (evidence == null) return;
			evidence.FixtureDisposed = disposed;
			evidence.PendingCleared = true;
			evidence.SeamCleaned = cleaned.Succeeded;
		}
	}
}
#endif
