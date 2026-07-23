#if DEBUG
using ONI_Together.Networking;
using System;
using System.Collections.Generic;

namespace ONI_Together.DebugTools
{
	public static class FaultInjectionHandlerRegistry
	{
		private static readonly IReadOnlyDictionary<string, FaultInjectionHandler> FaultHandlers =
			BuildHandlers(cleanControl: false);
		private static readonly IReadOnlyDictionary<string, FaultInjectionHandler> CleanHandlers =
			BuildHandlers(cleanControl: true);

		public static bool TryResolve(string caseId, out FaultInjectionHandler handler)
			=> FaultHandlers.TryGetValue(caseId, out handler);

		public static bool TryResolveCleanControl(string caseId, out FaultInjectionHandler handler)
			=> CleanHandlers.TryGetValue(caseId, out handler);

		private static IReadOnlyDictionary<string, FaultInjectionHandler> BuildHandlers(bool cleanControl)
		{
			var result = new Dictionary<string, FaultInjectionHandler>(StringComparer.Ordinal);
			foreach (FaultInjectionCase item in FaultInjectionRegistry.Cases)
			{
				if (item.ExecutionTier == "headless") continue;
				string id = item.Id;
				result.Add(id, cleanControl
					? (FaultInjectionHandler)(() => FaultUnityBindingRegistry.ExecuteCleanControl(id))
					: () => FaultUnityBindingRegistry.ExecuteFault(id));
			}
			return result;
		}
	}

	internal static class FaultInjectionUnitySeams
	{
		private enum InjectionMode
		{
			Fault,
			CleanControl,
		}

		private static readonly object Sync = new object();
		private static readonly Dictionary<string, InjectionMode> Armed =
			new Dictionary<string, InjectionMode>(StringComparer.Ordinal);
		private static readonly Dictionary<string, FaultInjectionReceipt> Receipts =
			new Dictionary<string, FaultInjectionReceipt>(StringComparer.Ordinal);
		private static readonly Dictionary<string, string> ArmedTargets =
			new Dictionary<string, string>(StringComparer.Ordinal);
		private static readonly Dictionary<string, string> ReceiptTargets =
			new Dictionary<string, string>(StringComparer.Ordinal);
		private static readonly Dictionary<string, object> ArmedRuntimeTargets =
			new Dictionary<string, object>(StringComparer.Ordinal);

		internal static FaultInjectionReceipt Arm(
			string caseId, string tier, bool cleanControl = false,
			string targetId = null, object runtimeTarget = null)
		{
			string unavailable = RequiredRuntimeUnavailable(caseId, tier);
			if (unavailable != null)
				return FaultInjectionReceipt.Fail("precondition", unavailable);
			lock (Sync)
			{
				if (Armed.ContainsKey(caseId))
					return FaultInjectionReceipt.Fail("arm", "fault-already-armed:" + caseId);
				Armed.Add(caseId, cleanControl
					? InjectionMode.CleanControl : InjectionMode.Fault);
				if (!string.IsNullOrEmpty(targetId)) ArmedTargets[caseId] = targetId;
				if (runtimeTarget != null) ArmedRuntimeTargets[caseId] = runtimeTarget;
				Receipts.Remove(caseId);
				ReceiptTargets.Remove(caseId);
			}
			return FaultInjectionReceipt.Pass(
				"execute", "production-seam-scheduled:" + caseId);
		}

		internal static FaultInjectionReceipt Clean(string caseId)
		{
			lock (Sync)
			{
				Armed.Remove(caseId);
				ArmedTargets.Remove(caseId);
				ArmedRuntimeTargets.Remove(caseId);
				Receipts.Remove(caseId);
				ReceiptTargets.Remove(caseId);
			}
			return FaultInjectionReceipt.Pass("cleanup", "fault-disarmed:" + caseId);
		}

		internal static FaultInputMutation Consume(string caseId)
		{
			lock (Sync)
			{
				if (!Armed.TryGetValue(caseId, out InjectionMode mode))
					return new FaultInputMutation(caseId, false, false, false);
				Armed.Remove(caseId);
				bool clean = mode == InjectionMode.CleanControl;
				ArmedTargets.TryGetValue(caseId, out string targetId);
				ArmedRuntimeTargets.TryGetValue(caseId, out object runtimeTarget);
				return new FaultInputMutation(
					caseId, true, !clean, clean, targetId, runtimeTarget);
			}
		}

		internal static void EnsureExpectedRuntimeTarget(
			IFaultInputMutation mutation, object candidate)
		{
			if (mutation == null || !mutation.Consumed) return;
			if (mutation.RuntimeTarget == null
			    || !ReferenceEquals(mutation.RuntimeTarget, candidate))
				throw new InvalidOperationException("fault-runtime-target-mismatch");
		}

		internal static void EmitReceipt(
			IFaultInputMutation mutation, bool oracleSatisfied = true,
			object runtimeTarget = null)
		{
			if (mutation == null || !mutation.Consumed)
				return;
			string id = (mutation.IsCleanControl ? "fault-clean-receipt:" : "fault-receipt:")
			            + mutation.CaseId;
			string detail = oracleSatisfied ? id
				: mutation.CaseId.StartsWith("dlc.family-", StringComparison.Ordinal)
					? "concrete-runtime-target-oracle-required:" + mutation.CaseId
					: "runtime-oracle-unsatisfied:" + mutation.CaseId;
			FaultInjectionReceipt receipt = oracleSatisfied
				? FaultInjectionReceipt.Pass("runtime", detail)
				: FaultInjectionReceipt.Fail("runtime-oracle", detail);
			lock (Sync)
			{
				Receipts[mutation.CaseId] = receipt;
				string targetId = mutation.CaseId == "building.destroy-deferred"
					? mutation.TargetId
					: FaultUnityTargetSelector.Identity(mutation.CaseId, runtimeTarget);
				if (ArmedTargets.TryGetValue(mutation.CaseId, out string expectedTarget)
				    && targetId == expectedTarget)
					ReceiptTargets[mutation.CaseId] = targetId;
				ArmedTargets.Remove(mutation.CaseId);
				ArmedRuntimeTargets.Remove(mutation.CaseId);
			}
			if (oracleSatisfied) DebugConsole.Log("[FaultInjection] " + id);
			else DebugConsole.LogWarning("[FaultInjection][ORACLE-FAIL] " + id);
		}

		internal static bool IsArmed(string caseId)
		{
			lock (Sync)
				return Armed.ContainsKey(caseId);
		}

		internal static bool TryTakeReceipt(
			string caseId, out FaultInjectionReceipt receipt, out string targetId)
		{
			lock (Sync)
			{
				targetId = null;
				if (!Receipts.TryGetValue(caseId, out receipt)
				    || !ReceiptTargets.TryGetValue(caseId, out targetId)) return false;
				Receipts.Remove(caseId);
				ReceiptTargets.Remove(caseId);
				return true;
			}
		}

		private static string RequiredRuntimeUnavailable(string caseId, string tier)
		{
			if (Game.Instance == null)
				return "loaded-world-required:" + caseId;
			if (tier == "real" && !MultiplayerSession.InSession)
				return "multiplayer-session-required:" + caseId;
			string dlcId = RequiredDlc(caseId);
			if (dlcId != null && !DlcManager.GetActiveDLCIds().Contains(dlcId))
				return "active-dlc-required:" + dlcId;
			return null;
		}

		private static string RequiredDlc(string caseId)
		{
			switch (caseId)
			{
				case "dlc.family-aquatic": return DlcManager.DLC5_ID;
				case "dlc.family-bionic": return DlcManager.DLC3_ID;
				case "dlc.family-frosty": return DlcManager.DLC2_ID;
				case "dlc.family-prehistoric": return DlcManager.DLC4_ID;
				case "dlc.family-spaced-out": return DlcManager.EXPANSION1_ID;
				default: return null;
			}
		}
	}
}
#endif
