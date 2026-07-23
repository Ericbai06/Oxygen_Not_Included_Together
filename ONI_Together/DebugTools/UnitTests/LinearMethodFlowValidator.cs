using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace ONI_Together.DebugTools.UnitTests
{
	internal static class LinearMethodFlowValidator
	{
		internal static string Validate(LinearMethodFlowContract contract)
		{
			string failure = ValidateContract(contract);
			if (failure != null)
				return failure;
			var stack = new List<string>();
			var locals = new Dictionary<int, string>();
			int callIndex = 0;
			foreach (ReflectedIlInstruction instruction in
			         ReflectionExecutionGraph.ReadInstructions(contract.Method))
			{
				failure = Apply(
					instruction, contract, stack, locals, ref callIndex);
				if (failure != null)
					return failure;
			}
			return callIndex == contract.Calls.Count
				? null : "execution method omitted stage " + callIndex;
		}

		private static string ValidateContract(LinearMethodFlowContract contract)
		{
			if (contract?.Method == null || !contract.Method.IsStatic)
				return "execution method must be a concrete static method";
			if (contract.Arguments == null
			    || contract.Arguments.Count != contract.Method.GetParameters().Length)
				return "execution argument tokens do not match the method signature";
			if (contract.Calls == null || contract.Calls.Count == 0)
				return "execution stages are required";
			MethodInfo[] methods = contract.Calls.Select(value => value?.Method).ToArray();
			if (methods.Any(value => value == null))
				return "execution stage method is missing";
			if (methods.Select(MethodKey).Distinct().Count() != methods.Length)
				return "semantic stages cannot reuse the same MethodInfo";
			return null;
		}

		private static string Apply(
			ReflectedIlInstruction instruction,
			LinearMethodFlowContract contract,
			IList<string> stack,
			IDictionary<int, string> locals,
			ref int callIndex)
		{
			OpCode code = instruction.Code;
			if (code.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch)
				return "execution witness contains a dormant or alternate control-flow branch";
			if (code == OpCodes.Nop)
				return null;
			if (TryLoadArgument(code, instruction.Operand, contract.Arguments, out string token)
			    || TryLoadLocal(code, instruction.Operand, locals, out token))
			{
				stack.Add(token);
				return null;
			}
			if (TryStoreLocal(code, instruction.Operand, stack, locals, out string failure))
				return failure;
			if (code == OpCodes.Ldstr)
			{
				stack.Add("literal:" + instruction.Operand);
				return null;
			}
			if (code == OpCodes.Ldnull)
			{
				stack.Add("null");
				return null;
			}
			if (code == OpCodes.Dup)
				return Duplicate(stack);
			if (code == OpCodes.Pop)
				return Pop(stack, out _);
			if (code is { FlowControl: FlowControl.Call }
			    && instruction.Operand is MethodInfo called)
				return ApplyCall(called, contract, stack, ref callIndex);
			if (code == OpCodes.Ret)
				return ApplyReturn(contract, stack);
			return "unsupported execution opcode " + code.Name;
		}

		private static string ApplyCall(
			MethodInfo called,
			LinearMethodFlowContract contract,
			IList<string> stack,
			ref int callIndex)
		{
			if (callIndex >= contract.Calls.Count)
				return "execution method contains an unbound call to " + called.Name;
			FlowCallContract expected = contract.Calls[callIndex];
			if (!ReflectionExecutionGraph.Same(called, expected.Method))
				return "stage order mismatch at " + callIndex + ": " + called.Name;
			int receiverOffset = called.IsStatic ? 0 : 1;
			var inputs = new string[called.GetParameters().Length + receiverOffset];
			for (int index = inputs.Length - 1; index >= receiverOffset; index--)
			{
				string failure = Pop(stack, out inputs[index]);
				if (failure != null) return failure;
			}
			if (!called.IsStatic)
			{
				string failure = Pop(stack, out inputs[0]);
				if (failure != null) return failure;
			}
			if (!inputs.SequenceEqual(expected.Inputs, StringComparer.Ordinal))
				return "stage " + called.Name + " consumed the wrong artifact instance";
			if (called.ReturnType != typeof(void))
				stack.Add(expected.Output ?? "unknown:" + called.Name);
			else if (expected.Output != null)
				return "void stage " + called.Name + " cannot produce an artifact";
			callIndex++;
			return null;
		}

		private static string ApplyReturn(
			LinearMethodFlowContract contract, IList<string> stack)
		{
			if (contract.Method.ReturnType == typeof(void))
				return contract.ReturnToken == null ? null : "void method has a return token";
			string failure = Pop(stack, out string actual);
			if (failure != null) return failure;
			return actual == contract.ReturnToken
				? null : "execution method returned the wrong artifact instance";
		}

		private static bool TryLoadArgument(
			OpCode code, object operand, IReadOnlyList<string> arguments, out string token)
		{
			int index = code == OpCodes.Ldarg_0 ? 0 : code == OpCodes.Ldarg_1 ? 1
				: code == OpCodes.Ldarg_2 ? 2 : code == OpCodes.Ldarg_3 ? 3
				: code == OpCodes.Ldarg || code == OpCodes.Ldarg_S ? (int)operand : -1;
			token = index >= 0 && index < arguments.Count ? arguments[index] : null;
			return token != null;
		}

		private static bool TryLoadLocal(
			OpCode code, object operand, IDictionary<int, string> locals, out string token)
		{
			int index = LocalIndex(code, operand, load: true);
			return locals.TryGetValue(index, out token);
		}

		private static bool TryStoreLocal(
			OpCode code, object operand, IList<string> stack,
			IDictionary<int, string> locals, out string failure)
		{
			int index = LocalIndex(code, operand, load: false);
			if (index < 0)
			{
				failure = null;
				return false;
			}
			failure = Pop(stack, out string token);
			if (failure == null) locals[index] = token;
			return true;
		}

		private static int LocalIndex(OpCode code, object operand, bool load)
		{
			if (code == (load ? OpCodes.Ldloc_0 : OpCodes.Stloc_0)) return 0;
			if (code == (load ? OpCodes.Ldloc_1 : OpCodes.Stloc_1)) return 1;
			if (code == (load ? OpCodes.Ldloc_2 : OpCodes.Stloc_2)) return 2;
			if (code == (load ? OpCodes.Ldloc_3 : OpCodes.Stloc_3)) return 3;
			if (code == (load ? OpCodes.Ldloc : OpCodes.Stloc)
			    || code == (load ? OpCodes.Ldloc_S : OpCodes.Stloc_S))
				return (int)operand;
			return -1;
		}

		private static string Duplicate(IList<string> stack)
		{
			if (stack.Count == 0) return "execution stack underflow";
			stack.Add(stack[stack.Count - 1]);
			return null;
		}

		private static string Pop(IList<string> stack, out string token)
		{
			if (stack.Count == 0)
			{
				token = null;
				return "execution stack underflow";
			}
			token = stack[stack.Count - 1];
			stack.RemoveAt(stack.Count - 1);
			return null;
		}

		private static string MethodKey(MethodInfo method)
			=> method.Module.ModuleVersionId + ":" + method.MetadataToken;
	}
}
