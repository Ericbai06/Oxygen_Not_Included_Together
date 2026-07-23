using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace ONI_Together.DebugTools.UnitTests
{
	internal static class TransactionalSendPathValidator
	{
		internal static string ValidateSingleHost(
			MethodInfo execute, MethodInfo send, MethodInfo evidence, MethodInfo restore)
		{
			string failure = ValidateSend(execute, send) ?? EvidenceAfter(execute, evidence, send);
			if (failure != null) return failure;
			return FailurePath(execute, send, restore, true);
		}

		internal static string ValidateSingleCleanup(
			MethodInfo execute, MethodInfo restore, MethodInfo send, MethodInfo evidence)
		{
			string failure = ResultConsumed(execute, restore, "cleanup restore result is ignored")
			                 ?? ValidateSend(execute, send)
			                 ?? EvidenceAfter(execute, evidence, send);
			return failure ?? FailurePath(execute, send, null, false);
		}

		internal static string ValidatePickup(
			MethodInfo execute, IReadOnlyList<MethodInfo> sends, MethodInfo evidence,
			MethodInfo firstFailure, MethodInfo secondFailure)
		{
			if (sends == null || sends.Count != 2)
				return "pickup requires exactly two transactional sends";
			foreach (MethodInfo send in sends)
			{
				string failure = ValidateSend(execute, send);
				if (failure != null) return failure;
			}
			string ordering = EvidenceAfter(execute, evidence, sends[1]);
			if (ordering != null) return ordering;
			string first = FailurePath(execute, sends[0], firstFailure, true);
			if (first != null) return "first packet: " + first;
			string second = FailurePath(execute, sends[1], secondFailure, true);
			return second == null ? null : "second packet: " + second;
		}

		private static string ValidateSend(MethodInfo execute, MethodInfo send)
		{
			if (send == null || send.ReturnType != typeof(bool))
				return "send does not report bool outcome";
			return ConditionalAfter(execute, send) >= 0
				? null : "send outcome is not conditionally consumed";
		}

		private static string EvidenceAfter(
			MethodInfo execute, MethodInfo evidence, MethodInfo finalSend)
		{
			IReadOnlyList<ReflectedIlInstruction> values =
				ReflectionExecutionGraph.ReadInstructions(execute);
			int send = CallIndex(values, finalSend);
			int observed = CallIndex(values, evidence);
			return send >= 0 && observed > send
				? null : "success evidence is emitted before send completion";
		}

		private static string FailurePath(
			MethodInfo execute, MethodInfo send, MethodInfo required, bool consumeRequired)
		{
			IReadOnlyList<ReflectedIlInstruction> values =
				ReflectionExecutionGraph.ReadInstructions(execute);
			int branch = ConditionalAfter(execute, send);
			if (branch < 0) return "send outcome is not conditionally consumed";
			int start = FalsePathStart(values, branch);
			int end = FirstReturn(values, start);
			if (start < 0 || end < 0 || !ReturnsNull(values, end))
				return "send failure can return non-null success state";
			if (required == null) return null;
			int call = CallIndex(values, required, start, end);
			if (call < 0) return "send failure omits rollback or compensation";
			if (consumeRequired && ConditionalAfter(values, call) < 0)
				return "rollback or compensation result is ignored";
			return null;
		}

		private static string ResultConsumed(
			MethodInfo execute, MethodInfo method, string message)
		{
			IReadOnlyList<ReflectedIlInstruction> values =
				ReflectionExecutionGraph.ReadInstructions(execute);
			int call = CallIndex(values, method);
			return call >= 0 && ConditionalAfter(values, call) >= 0 ? null : message;
		}

		private static int ConditionalAfter(MethodInfo execute, MethodInfo called)
		{
			IReadOnlyList<ReflectedIlInstruction> values =
				ReflectionExecutionGraph.ReadInstructions(execute);
			int call = CallIndex(values, called);
			return ConditionalAfter(values, call);
		}

		private static int ConditionalAfter(
			IReadOnlyList<ReflectedIlInstruction> values, int call)
		{
			if (call < 0) return -1;
			for (int index = call + 1; index < values.Count && index <= call + 5; index++)
			{
				if (values[index].Code == OpCodes.Nop) continue;
				if (values[index].Code.FlowControl == FlowControl.Cond_Branch) return index;
				if (values[index].Operand is MethodBase) return -1;
			}
			return -1;
		}

		private static int FalsePathStart(
			IReadOnlyList<ReflectedIlInstruction> values, int branch)
		{
			OpCode code = values[branch].Code;
			if (code == OpCodes.Brtrue || code == OpCodes.Brtrue_S) return branch + 1;
			if (code != OpCodes.Brfalse && code != OpCodes.Brfalse_S) return -1;
			return values[branch].Operand is int target
				? values.ToList().FindIndex(value => value.Offset == target) : -1;
		}

		private static int FirstReturn(
			IReadOnlyList<ReflectedIlInstruction> values, int start)
		{
			for (int index = start; index >= 0 && index < values.Count; index++)
				if (values[index].Code == OpCodes.Ret) return index;
			return -1;
		}

		private static bool ReturnsNull(
			IReadOnlyList<ReflectedIlInstruction> values, int ret)
		{
			for (int index = ret - 1; index >= 0; index--)
			{
				if (values[index].Code == OpCodes.Nop) continue;
				return values[index].Code == OpCodes.Ldnull;
			}
			return false;
		}

		private static int CallIndex(
			IReadOnlyList<ReflectedIlInstruction> values, MethodInfo method,
			int start = 0, int end = int.MaxValue)
		{
			for (int index = Math.Max(0, start); index < values.Count && index <= end; index++)
				if (values[index].Operand is MethodInfo called
				    && ReflectionExecutionGraph.Same(called, method)) return index;
			return -1;
		}
	}
}
