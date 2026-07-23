using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace ONI_Together.DebugTools.UnitTests
{
	internal static class ScenarioActionConditionalSendValidator
	{
		internal static bool Applies(ScenarioActionLinearFlowContext context)
			=> context?.Expected?.Scenario is "rocket" or "dlc-runtime" or "pickup";

		internal static void BindHostOutcome(
			ScenarioActionLinearFlowContext context,
			IList<FlowCallContract> calls)
		{
			BindOutcomes(context, calls);
		}

		internal static void BindCleanupOutcome(
			ScenarioActionLinearFlowContext context,
			IList<FlowCallContract> calls)
			=> BindOutcomes(context, calls);

		internal static string ValidateHost(LinearMethodFlowContract contract)
		{
			FlowCallContract send = contract?.Calls?.LastOrDefault(
				call => call.OutcomePolicy == FlowCallOutcomePolicy.ConditionalRollback);
			if (send?.OutcomePolicy != FlowCallOutcomePolicy.ConditionalRollback
			    || send.FailureMethod == null || send.Method?.ReturnType != typeof(bool))
				return "conditional send rollback contract is incomplete";
			string failure = ValidateOrderedCalls(contract);
			if (failure != null) return failure;
			if (contract.Calls.Where(call => call.OutcomePolicy
			    == FlowCallOutcomePolicy.ConditionalRollback)
			    .Any(call => !ConditionalAfter(contract.Method, call.Method)))
				return "send failure outcome is not conditionally consumed";
			return DirectlyCalls(contract.Method, send.FailureMethod)
				? null : "send failure does not directly rollback host mutation";
		}

		internal static string ValidateCleanup(LinearMethodFlowContract contract)
		{
			string failure = ValidateOrderedCalls(contract);
			if (failure != null) return failure;
			return contract.Calls.Where(call => call.OutcomePolicy
					== FlowCallOutcomePolicy.ConditionalRollback)
				.Any(call => !ConditionalAfter(contract.Method, call.Method))
				? "send failure outcome is not conditionally consumed" : null;
		}

		private static string ValidateOrderedCalls(LinearMethodFlowContract contract)
		{
			if (contract?.Method == null || contract.Calls == null)
				return "conditional flow contract is incomplete";
			MethodInfo[] expected = contract.Calls.Select(call => call.Method).ToArray();
			int next = 0;
			foreach (ReflectedIlInstruction instruction in
			         ReflectionExecutionGraph.ReadInstructions(contract.Method))
			{
				if (instruction.Operand is not MethodInfo called) continue;
				if (next < expected.Length && ReflectionExecutionGraph.Same(called, expected[next]))
				{
					next++;
					continue;
				}
				if (expected.Any(method => ReflectionExecutionGraph.Same(called, method))
				    && !IsFailureMethod(contract, called))
					return "conditional flow stage order is invalid at " + called.Name;
			}
			return next == expected.Length
				? null : "conditional flow omitted stage " + next;
		}

		private static bool ConditionalAfter(MethodInfo caller, MethodInfo called)
		{
			IReadOnlyList<ReflectedIlInstruction> instructions =
				ReflectionExecutionGraph.ReadInstructions(caller);
			for (int index = 0; index < instructions.Count - 1; index++)
			{
				if (instructions[index].Operand is not MethodInfo candidate
				    || !ReflectionExecutionGraph.Same(candidate, called)) continue;
				int next = index + 1;
				while (next < instructions.Count && instructions[next].Code == OpCodes.Nop) next++;
				return next < instructions.Count
				       && instructions[next].Code.FlowControl == FlowControl.Cond_Branch;
			}
			return false;
		}

		private static bool DirectlyCalls(MethodInfo caller, MethodInfo called)
			=> ReflectionExecutionGraph.ReadInstructions(caller)
				.Any(value => value.Operand is MethodInfo candidate
					&& ReflectionExecutionGraph.Same(candidate, called));

		private static void BindOutcomes(
			ScenarioActionLinearFlowContext context, IEnumerable<FlowCallContract> calls)
		{
			if (!Applies(context)) return;
			foreach (FlowCallContract call in calls.Where(value => value.Method?.ReturnType == typeof(bool)))
			{
				call.OutcomePolicy = FlowCallOutcomePolicy.ConditionalRollback;
				call.FailureMethod = context.Flow.CleanupMutation;
			}
		}

		private static bool IsFailureMethod(
			LinearMethodFlowContract contract, MethodInfo method)
			=> contract.Calls.Any(call => ReflectionExecutionGraph.Same(call.FailureMethod, method));
	}
}
