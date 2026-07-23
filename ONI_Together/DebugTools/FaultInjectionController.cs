#if DEBUG
using System;
using System.Collections.Generic;

namespace ONI_Together.DebugTools
{
	internal sealed class FaultProbeResult
	{
		internal FaultProbeResult(bool oracle, bool invariant, bool reset, bool clean,
			params string[] trace)
		{
			Oracle = oracle;
			Invariant = invariant;
			Reset = reset;
			Clean = clean;
			Trace = trace;
		}

		internal bool Oracle { get; }
		internal bool Invariant { get; }
		internal bool Reset { get; }
		internal bool Clean { get; }
		internal IReadOnlyList<string> Trace { get; }
	}

	public static class FaultInjectionController
	{
		public static FaultInjectionExecution ExecuteHeadless(string caseId)
		{
			if (string.IsNullOrEmpty(caseId))
				throw new ArgumentException("Fault case ID is required", nameof(caseId));
			FaultInjectionCase definition = Find(caseId);
			if (definition.ExecutionTier != "headless")
				throw new InvalidOperationException(caseId + " requires " + definition.ExecutionTier);
			FaultProbeResult result = Dispatch(caseId);
			FaultProductionBinding binding = FaultProductionBindingRegistry.Resolve(caseId);
			return new FaultInjectionExecution(caseId, result.Oracle, result.Invariant,
				result.Reset, result.Clean, result.Trace,
				binding.GateMethod.DeclaringType.FullName + "." + binding.GateMethod.Name);
		}

		private static FaultProbeResult Dispatch(string caseId)
			=> FaultProductionBindingRegistry.ExecuteHeadless(caseId);

		private static FaultInjectionCase Find(string caseId)
		{
			foreach (FaultInjectionCase item in FaultInjectionRegistry.Cases)
				if (item.Id == caseId)
					return item;
			throw new KeyNotFoundException("Unknown fault case " + caseId);
		}
	}
}
#endif
