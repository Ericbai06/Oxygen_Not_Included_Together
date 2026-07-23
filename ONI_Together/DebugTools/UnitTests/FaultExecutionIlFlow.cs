using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace ONI_Together.DebugTools.UnitTests
{
	internal static class FaultExecutionIlFlow
	{
		private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue =
			typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
				.Where(field => field.FieldType == typeof(OpCode))
				.Select(field => (OpCode)field.GetValue(null))
				.ToDictionary(opcode => opcode.Value);

		internal static bool UsesSameSetupLocal(
			MethodInfo execution,
			MethodInfo setup,
			IReadOnlyList<MethodInfo> stages)
		{
			if (execution == null || setup == null || stages.Count == 0) return false;
			List<IlInstruction> instructions = Read(execution);
			int setupCall = FindCall(instructions, setup, 0);
			if (setupCall < 0 || setupCall + 1 >= instructions.Count) return false;
			int? contextLocal = StoredLocal(instructions[setupCall + 1]);
			if (contextLocal == null) return false;
			if (instructions.Skip(setupCall + 2).Any(instruction =>
				    StoredLocal(instruction) == contextLocal
				    || LoadedLocalAddress(instruction) == contextLocal)) return false;
			var flow = new ContextFlow(instructions, setup.ReturnType, contextLocal.Value);
			int cursor = setupCall + 2;
			foreach (MethodInfo stage in stages)
			{
				int call = FindCall(instructions, stage, cursor);
				if (call <= 0 || !LoadsContextForCall(flow, call, stage))
					return false;
				cursor = call + 1;
			}
			return true;
		}

		private static int FindCall(
			IReadOnlyList<IlInstruction> instructions,
			MethodInfo expected,
			int start)
		{
			for (int index = start; index < instructions.Count; index++)
				if ((instructions[index].Code == OpCodes.Call
			     || instructions[index].Code == OpCodes.Callvirt)
			    && ReflectionExecutionGraph.Same(instructions[index].Method, expected))
					return index;
			return -1;
		}

		private static bool LoadsContextForCall(
			ContextFlow flow,
			int call,
			MethodInfo stage)
		{
			ParameterInfo[] parameters = stage.GetParameters();
			int contextParameter = Array.FindIndex(parameters, parameter =>
				parameter.ParameterType == flow.ContextType);
			if (contextParameter < 0) return false;
			int cursor = call - 1;
			var arguments = new ValueSource[parameters.Length];
			for (int index = arguments.Length - 1; index >= 0; index--)
				arguments[index] = ReadValue(flow.Instructions, ref cursor);
			return ResolveLocal(arguments[contextParameter]) == flow.ContextLocal;
		}

		private static ValueSource ReadValue(
			IReadOnlyList<IlInstruction> instructions,
			ref int cursor)
		{
			while (cursor >= 0 && !PushesValue(instructions[cursor])) cursor--;
			if (cursor < 0) return ValueSource.Unknown;
			IlInstruction producer = instructions[cursor--];
			int inputs = PopCount(producer);
			var arguments = new ValueSource[inputs];
			for (int index = inputs - 1; index >= 0; index--)
				arguments[index] = ReadValue(instructions, ref cursor);
			return new ValueSource(LoadedLocal(producer), producer.Method, arguments);
		}

		private static int? ResolveLocal(ValueSource source)
		{
			if (source.LocalIndex != null) return source.LocalIndex;
			if (source.Method is MethodInfo method
			    && ReturnedParameter(method) is int parameter
			    && parameter >= 0 && parameter < source.Arguments.Count)
				return ResolveLocal(source.Arguments[parameter]);
			return null;
		}

		private static int? ReturnedParameter(MethodInfo method)
		{
			List<IlInstruction> body = Read(method).Where(instruction =>
				instruction.Code != OpCodes.Nop).ToList();
			if (body.Count != 2 || body[1].Code != OpCodes.Ret) return null;
			int? argument = LoadedArgument(body[0]);
			if (argument == null) return null;
			int parameter = method.IsStatic ? argument.Value : argument.Value - 1;
			return parameter >= 0 && parameter < method.GetParameters().Length
				? parameter : null;
		}

		private static int PopCount(IlInstruction instruction)
		{
			if (instruction.Code == OpCodes.Call || instruction.Code == OpCodes.Callvirt)
			{
				if (instruction.Method == null) return 0;
				return instruction.Method.GetParameters().Length
				       + (instruction.Method.IsStatic ? 0 : 1);
			}
			if (instruction.Code == OpCodes.Newobj)
				return instruction.Method?.GetParameters().Length ?? 0;
			return FixedPopCount(instruction.Code.StackBehaviourPop);
		}

		private static int FixedPopCount(StackBehaviour behavior)
		{
			switch (behavior)
			{
				case StackBehaviour.Pop0: return 0;
				case StackBehaviour.Pop1:
				case StackBehaviour.Popi:
				case StackBehaviour.Popref: return 1;
				case StackBehaviour.Pop1_pop1:
				case StackBehaviour.Popi_pop1:
				case StackBehaviour.Popi_popi:
				case StackBehaviour.Popi_popi8:
				case StackBehaviour.Popi_popr4:
				case StackBehaviour.Popi_popr8:
				case StackBehaviour.Popref_pop1:
				case StackBehaviour.Popref_popi: return 2;
				default: return 3;
			}
		}

		private static bool PushesValue(IlInstruction instruction)
		{
			if (instruction.Code == OpCodes.Newobj) return true;
			if (instruction.Code == OpCodes.Call || instruction.Code == OpCodes.Callvirt)
				return instruction.Method is MethodInfo method
				       && method.ReturnType != typeof(void);
			return instruction.Code.StackBehaviourPush != StackBehaviour.Push0;
		}

		private static List<IlInstruction> Read(MethodInfo method)
		{
			byte[] bytes = method.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
			var cursor = new IlCursor(method, bytes);
			var result = new List<IlInstruction>();
				while (cursor.Offset < bytes.Length)
				{
					OpCode code = ReadCode(cursor);
					object operand = ReadOperand(cursor, code.OperandType);
					result.Add(new IlInstruction(code, operand as MethodBase, operand));
				}
				return WithoutExecutionProbes(result);
			}

			private static List<IlInstruction> WithoutExecutionProbes(
				IReadOnlyList<IlInstruction> instructions)
			{
				var result = new List<IlInstruction>(instructions.Count);
				for (int index = 0; index < instructions.Count; index++)
				{
					if (IsExecutionProbe(instructions, index))
					{
						index += 2;
						continue;
					}
					result.Add(instructions[index]);
				}
				return result;
			}

			private static bool IsExecutionProbe(
				IReadOnlyList<IlInstruction> instructions,
				int start)
			{
				if (start + 2 >= instructions.Count
				    || instructions[start].Code != OpCodes.Ldstr
				    || instructions[start].Operand is not string
				    || instructions[start + 1].Code != OpCodes.Ldstr
				    || instructions[start + 1].Operand is not string
				    || instructions[start + 2].Code != OpCodes.Call
				    || instructions[start + 2].Method is not MethodInfo hit)
					return false;
				ParameterInfo[] parameters = hit.GetParameters();
				return hit.DeclaringType?.FullName == "__SyncExecutionProbe"
				       && hit.Name == "Hit"
				       && hit.IsStatic
				       && hit.ReturnType == typeof(void)
				       && parameters.Length == 2
				       && parameters.All(parameter =>
					       parameter.ParameterType == typeof(string));
			}

		private static OpCode ReadCode(IlCursor cursor)
		{
			short value = cursor.Bytes[cursor.Offset++] == 0xfe
				? (short)(0xfe00 | cursor.Bytes[cursor.Offset++])
				: cursor.Bytes[cursor.Offset - 1];
			return OpCodesByValue[value];
		}

		private static object ReadOperand(IlCursor cursor, OperandType type)
		{
			int size = OperandSize(type, cursor.Bytes, cursor.Offset);
			object value = size == 1 ? cursor.Bytes[cursor.Offset]
				: size == 2 ? BitConverter.ToUInt16(cursor.Bytes, cursor.Offset)
				: size >= 4 ? BitConverter.ToInt32(cursor.Bytes, cursor.Offset) : null;
			cursor.Offset += size;
				if ((type == OperandType.InlineMethod || type == OperandType.InlineTok)
				    && value is int token)
					try { return cursor.Owner.Module.ResolveMethod(token); } catch (ArgumentException) { }
				if (type == OperandType.InlineString && value is int stringToken)
					try { return cursor.Owner.Module.ResolveString(stringToken); } catch (ArgumentException) { }
				return value;
			}

		private static int OperandSize(OperandType type, byte[] bytes, int offset)
		{
			if (type == OperandType.InlineSwitch)
				return 4 + BitConverter.ToInt32(bytes, offset) * 4;
			if (type == OperandType.InlineI8 || type == OperandType.InlineR) return 8;
			if (type == OperandType.InlineVar) return 2;
			if (type == OperandType.ShortInlineR) return 4;
			if (type == OperandType.ShortInlineBrTarget || type == OperandType.ShortInlineI
			    || type == OperandType.ShortInlineVar) return 1;
			return type == OperandType.InlineNone ? 0 : 4;
		}

		private static int? StoredLocal(IlInstruction instruction)
			=> LocalIndex(instruction, OpCodes.Stloc_0, OpCodes.Stloc_1,
				OpCodes.Stloc_2, OpCodes.Stloc_3, OpCodes.Stloc_S, OpCodes.Stloc);

		private static int? LoadedLocal(IlInstruction instruction)
			=> LocalIndex(instruction, OpCodes.Ldloc_0, OpCodes.Ldloc_1,
				OpCodes.Ldloc_2, OpCodes.Ldloc_3, OpCodes.Ldloc_S, OpCodes.Ldloc);

		private static int? LoadedArgument(IlInstruction instruction)
			=> LocalIndex(instruction, OpCodes.Ldarg_0, OpCodes.Ldarg_1,
				OpCodes.Ldarg_2, OpCodes.Ldarg_3, OpCodes.Ldarg_S, OpCodes.Ldarg);

		private static int? LoadedLocalAddress(IlInstruction instruction)
		{
			if (instruction.Code != OpCodes.Ldloca_S
			    && instruction.Code != OpCodes.Ldloca) return null;
			return Convert.ToInt32(instruction.Operand);
		}

		private static int? LocalIndex(IlInstruction instruction, params OpCode[] forms)
		{
			for (int index = 0; index < 4; index++)
				if (instruction.Code == forms[index]) return index;
			if (instruction.Code == forms[4] || instruction.Code == forms[5])
				return Convert.ToInt32(instruction.Operand);
			return null;
		}

		private sealed class IlInstruction
		{
			internal IlInstruction(OpCode code, MethodBase method, object operand)
			{
				Code = code;
				Method = method;
				Operand = operand;
			}

			internal OpCode Code { get; }
			internal MethodBase Method { get; }
			internal object Operand { get; }
		}

		private sealed class ContextFlow
		{
			internal ContextFlow(
				IReadOnlyList<IlInstruction> instructions,
				Type contextType,
				int contextLocal)
			{
				Instructions = instructions;
				ContextType = contextType;
				ContextLocal = contextLocal;
			}

			internal IReadOnlyList<IlInstruction> Instructions { get; }
			internal Type ContextType { get; }
			internal int ContextLocal { get; }
		}

		private sealed class IlCursor
		{
			internal IlCursor(MethodInfo owner, byte[] bytes)
			{
				Owner = owner;
				Bytes = bytes;
			}

			internal MethodInfo Owner { get; }
			internal byte[] Bytes { get; }
			internal int Offset { get; set; }
		}

		private sealed class ValueSource
		{
			internal static readonly ValueSource Unknown =
				new ValueSource(null, null, Array.Empty<ValueSource>());

			internal ValueSource(
				int? localIndex,
				MethodBase method,
				IReadOnlyList<ValueSource> arguments)
			{
				LocalIndex = localIndex;
				Method = method;
				Arguments = arguments;
			}

			internal int? LocalIndex { get; }
			internal MethodBase Method { get; }
			internal IReadOnlyList<ValueSource> Arguments { get; }
		}
	}
}
