#if DEBUG
using System;
using System.Collections.Generic;

namespace ONI_Together.DebugTools
{
	public sealed class FaultInjectionCase
	{
		internal FaultInjectionCase(string id, string domain, string seam, string value,
			string oracle, string invariant, string reset, string testId, string scenarioId,
			string tier)
		{
			Id = id;
			Domain = domain;
			InjectionSeam = seam;
			InjectionValue = value;
			ExpectedOracle = oracle;
			StateInvariant = invariant;
			Reset = reset;
			TestId = testId;
			ScenarioId = scenarioId;
			ExecutionTier = tier;
		}

		public string Id { get; }
		public string Domain { get; }
		public string InjectionSeam { get; }
		public string InjectionValue { get; }
		public string ExpectedOracle { get; }
		public string StateInvariant { get; }
		public string Reset { get; }
		public string CleanControl => "rerun same seam without injection";
		public string TestId { get; }
		public string ScenarioId { get; }
		public string ExecutionTier { get; }
	}

	public sealed class FaultInjectionReceipt
	{
		private FaultInjectionReceipt(
			bool succeeded, string stage, string detail,
			DeferredDestroyResetEvidence cleanupEvidence,
			FaultRuntimeTargetContext context)
		{
			Succeeded = succeeded;
			Stage = stage;
			Detail = detail;
			CleanupEvidence = cleanupEvidence;
			FaultRuntimeReceipt runtime = context?.Receipt;
			ReceiptId = runtime?.ReceiptId;
			CaseId = context?.CaseId;
			TargetId = runtime?.TargetId ?? context?.TargetId;
			Consumed = runtime?.Consumed ?? false;
		}

		public bool Succeeded { get; }
		public string Stage { get; }
		public string Detail { get; }
		public string ReceiptId { get; }
		public string CaseId { get; }
		public string TargetId { get; }
		public bool Consumed { get; }
		internal DeferredDestroyResetEvidence CleanupEvidence { get; }

		internal static FaultInjectionReceipt Pass(
			string stage, string detail,
			DeferredDestroyResetEvidence cleanupEvidence = null,
			FaultRuntimeTargetContext context = null)
			=> new FaultInjectionReceipt(
				true, stage, detail, cleanupEvidence, context);

		internal static FaultInjectionReceipt Fail(
			string stage, string detail,
			DeferredDestroyResetEvidence cleanupEvidence = null,
			FaultRuntimeTargetContext context = null)
			=> new FaultInjectionReceipt(
				false, stage, detail, cleanupEvidence, context);
	}

	public sealed class FaultInjectionExecution
	{
		internal FaultInjectionExecution(string caseId, bool oracle, bool invariant,
			bool reset, bool cleanControl, IReadOnlyList<string> trace,
			string productionGateSymbol)
		{
			CaseId = caseId;
			OracleObserved = oracle;
			InvariantPreserved = invariant;
			ResetCompleted = reset;
			CleanControlPassed = cleanControl;
			Trace = trace;
			ProductionGateSymbol = productionGateSymbol;
		}

		public string CaseId { get; }
		public bool OracleObserved { get; }
		public bool InvariantPreserved { get; }
		public bool ResetCompleted { get; }
		public bool CleanControlPassed { get; }
		public IReadOnlyList<string> Trace { get; }
		public string ProductionGateSymbol { get; }
	}

	public delegate FaultInjectionReceipt FaultInjectionHandler();

	public interface IFaultInputMutation
	{
		string CaseId { get; }
		bool Consumed { get; }
		bool Applied { get; }
		bool IsCleanControl { get; }
		string TargetId { get; }
		object RuntimeTarget { get; }
	}

	internal sealed class FaultInputMutation : IFaultInputMutation
	{
		internal FaultInputMutation(
			string caseId, bool consumed, bool applied, bool cleanControl,
			string targetId = null, object runtimeTarget = null)
		{
			CaseId = caseId;
			Consumed = consumed;
			Applied = applied;
			IsCleanControl = cleanControl;
			TargetId = targetId;
			RuntimeTarget = runtimeTarget;
		}

		public string CaseId { get; }
		public bool Consumed { get; }
		public bool Applied { get; }
		public bool IsCleanControl { get; }
		public string TargetId { get; }
		public object RuntimeTarget { get; }
	}
}
#endif
