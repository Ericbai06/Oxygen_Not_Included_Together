using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace ONI_Together.DebugTools.UnitTests
{
	internal static class ReflectionExecutionGraph
	{
		private static readonly OpCode[] SingleByte = new OpCode[0x100];
		private static readonly OpCode[] DoubleByte = new OpCode[0x100];

		static ReflectionExecutionGraph()
		{
			foreach (FieldInfo field in typeof(OpCodes).GetFields(
				         BindingFlags.Public | BindingFlags.Static))
			{
				if (field.GetValue(null) is not OpCode code)
					continue;
				ushort value = unchecked((ushort)code.Value);
				if (value < 0x100)
					SingleByte[value] = code;
				else if ((value & 0xff00) == 0xfe00)
					DoubleByte[value & 0xff] = code;
			}
		}

		internal static bool Reaches(MethodBase start, MethodBase target)
		{
			if (Same(start, target))
				return true;
			foreach (MethodBase method in Traverse(start))
				if (Same(method, target))
					return true;
			return false;
		}

		internal static bool ReachesType(MethodBase start, Type target)
		{
			foreach (MethodBase method in Traverse(start))
			{
				if (method.DeclaringType == target)
					return true;
				if (method.IsGenericMethod && Array.IndexOf(
					    method.GetGenericArguments(), target) >= 0)
					return true;
			}
			return false;
		}

		internal static bool ReachesPacketSender(MethodBase start)
		{
			foreach (MethodBase method in Traverse(start))
				if (method.DeclaringType?.FullName ==
				    "ONI_Together.Networking.PacketSender"
				    && (method.Name.StartsWith("Send", StringComparison.Ordinal)
				        || method.Name.StartsWith("Flush", StringComparison.Ordinal)))
					return true;
			return false;
		}

		internal static bool DirectlyPassesThisTo(MethodBase start, MethodInfo target)
		{
			if (start == null || start.IsStatic || target == null || !target.IsStatic)
				return false;
			ParameterInfo[] parameters = target.GetParameters();
			if (parameters.Length != 1
			    || parameters[0].ParameterType != start.DeclaringType)
				return false;
			ReflectedIlInstruction previous = default;
			bool hasPrevious = false;
			foreach (ReflectedIlInstruction instruction in ReadInstructions(start))
			{
				if (instruction.Code == OpCodes.Nop) continue;
				if (instruction.Operand is MethodInfo called && Same(called, target))
					return hasPrevious && previous.Code == OpCodes.Ldarg_0;
				previous = instruction;
				hasPrevious = true;
			}
			return false;
		}

		internal static bool CallLastArgumentUsesGetter(
			MethodBase caller, MethodInfo call, MethodInfo getter)
		{
			IReadOnlyList<ReflectedIlInstruction> instructions = ReadInstructions(caller);
			for (int index = 1; index < instructions.Count; index++)
			{
				if (!(instructions[index].Operand is MethodInfo called) || !Same(called, call))
					continue;
				int previous = index - 1;
				while (previous >= 0 && instructions[previous].Code == OpCodes.Nop) previous--;
				if (previous < 0) continue;
				if (instructions[previous].Operand is MethodInfo direct
				    && Same(direct, getter)) return true;
				int argumentLocal = LoadedLocal(instructions[previous]);
				if (argumentLocal < 0) continue;
				for (int producer = index - 2; producer >= 1; producer--)
				{
					if (StoredLocal(instructions[producer]) != argumentLocal) continue;
					int value = producer - 1;
					while (value >= 0 && instructions[value].Code == OpCodes.Nop) value--;
					if (value >= 0 && instructions[value].Operand is MethodInfo source
					    && Same(source, getter)) return true;
				}
			}
			return false;
		}

		internal static bool NoConditionalBranchBetweenCalls(
			MethodBase caller, MethodInfo first, MethodInfo second)
		{
			IReadOnlyList<ReflectedIlInstruction> instructions = ReadInstructions(caller);
			int firstIndex = -1;
			for (int index = 0; index < instructions.Count; index++)
			{
				if (!(instructions[index].Operand is MethodInfo called)) continue;
				if (firstIndex < 0 && Same(called, first)) firstIndex = index;
				if (firstIndex < 0 || !Same(called, second)) continue;
				return !instructions.Skip(firstIndex + 1).Take(index - firstIndex - 1)
					.Any(value => value.Code.FlowControl == FlowControl.Cond_Branch);
			}
			return false;
		}

		internal static bool Same(MethodBase left, MethodBase right)
			=> left != null && right != null && left.Module == right.Module
			   && left.MetadataToken == right.MetadataToken;

		internal static IReadOnlyList<ReflectedIlInstruction> ReadInstructions(
			MethodBase method)
		{
			byte[] il = method.GetMethodBody()?.GetILAsByteArray();
			if (il == null)
				return Array.Empty<ReflectedIlInstruction>();
			var result = new List<ReflectedIlInstruction>();
			for (int offset = 0; offset < il.Length;)
			{
				int instructionOffset = offset;
				OpCode code = ReadOpCode(il, ref offset);
				object operand = ReadOperand(method, code.OperandType, il, ref offset);
				result.Add(new ReflectedIlInstruction(instructionOffset, code, operand));
			}
			return WithoutExecutionProbes(result);
		}

		private static IReadOnlyList<ReflectedIlInstruction> WithoutExecutionProbes(
			IReadOnlyList<ReflectedIlInstruction> instructions)
		{
			var result = new List<ReflectedIlInstruction>(instructions.Count);
			for (int index = 0; index < instructions.Count; index++)
			{
				if (index + 2 < instructions.Count
				    && instructions[index].Code == OpCodes.Ldstr
				    && instructions[index + 1].Code == OpCodes.Ldstr
				    && IsExecutionProbe(instructions[index + 2].Operand))
				{
					index += 2;
					continue;
				}
				result.Add(instructions[index]);
			}
			return result;
		}

		private static bool IsExecutionProbe(object operand)
		{
			if (operand is not MethodInfo method
			    || method.DeclaringType?.FullName != "__SyncExecutionProbe"
			    || method.Name != "Hit"
			    || method.ReturnType != typeof(void))
				return false;
			ParameterInfo[] parameters = method.GetParameters();
			return parameters.Length == 2
			       && parameters[0].ParameterType == typeof(string)
			       && parameters[1].ParameterType == typeof(string);
		}

		private static IEnumerable<MethodBase> Traverse(MethodBase start)
		{
			if (start == null)
				yield break;
			var pending = new Queue<MethodBase>();
			var visited = new HashSet<(Module Module, int Token)>();
			pending.Enqueue(start);
			while (pending.Count > 0)
			{
				MethodBase current = pending.Dequeue();
				if (!visited.Add((current.Module, current.MetadataToken)))
					continue;
				yield return current;
				foreach (MethodBase referenced in ReadReferencedMethods(current))
				{
					yield return referenced;
					if (referenced.Module.Assembly == start.Module.Assembly)
						pending.Enqueue(referenced);
				}
			}
		}

		private static IEnumerable<MethodBase> ReadReferencedMethods(MethodBase method)
		{
			foreach (MethodBase referenced in ReadInstructions(method)
				         .Where(value => HasMethodToken(value.Code))
				         .Select(value => value.Operand).OfType<MethodBase>())
				yield return referenced;
		}

		private static OpCode ReadOpCode(byte[] il, ref int offset)
		{
			byte first = il[offset++];
			if (first != 0xfe)
				return SingleByte[first];
			return DoubleByte[il[offset++]];
		}

		private static bool HasMethodToken(OpCode code)
			=> code.OperandType == OperandType.InlineMethod;

		private static int LoadedLocal(ReflectedIlInstruction instruction)
		{
			if (instruction.Code == OpCodes.Ldloc_0) return 0;
			if (instruction.Code == OpCodes.Ldloc_1) return 1;
			if (instruction.Code == OpCodes.Ldloc_2) return 2;
			if (instruction.Code == OpCodes.Ldloc_3) return 3;
			if (instruction.Code == OpCodes.Ldloc || instruction.Code == OpCodes.Ldloc_S)
				return instruction.Operand is int index ? index : -1;
			return -1;
		}

		private static int StoredLocal(ReflectedIlInstruction instruction)
		{
			if (instruction.Code == OpCodes.Stloc_0) return 0;
			if (instruction.Code == OpCodes.Stloc_1) return 1;
			if (instruction.Code == OpCodes.Stloc_2) return 2;
			if (instruction.Code == OpCodes.Stloc_3) return 3;
			if (instruction.Code == OpCodes.Stloc || instruction.Code == OpCodes.Stloc_S)
				return instruction.Operand is int index ? index : -1;
			return -1;
		}

		private static object ReadOperand(
			MethodBase owner, OperandType type, byte[] il, ref int offset)
		{
			switch (type)
			{
				case OperandType.InlineNone: return null;
				case OperandType.ShortInlineI: return unchecked((sbyte)il[offset++]);
				case OperandType.ShortInlineVar: return (int)il[offset++];
				case OperandType.InlineVar:
					int variable = BitConverter.ToUInt16(il, offset); offset += 2; return variable;
				case OperandType.InlineI:
					int integer = BitConverter.ToInt32(il, offset); offset += 4; return integer;
				case OperandType.InlineI8:
					long longValue = BitConverter.ToInt64(il, offset); offset += 8; return longValue;
				case OperandType.ShortInlineR:
					float single = BitConverter.ToSingle(il, offset); offset += 4; return single;
				case OperandType.InlineR:
					double real = BitConverter.ToDouble(il, offset); offset += 8; return real;
				case OperandType.InlineMethod:
					int methodToken = BitConverter.ToInt32(il, offset); offset += 4;
					return ResolveMethod(owner, methodToken);
				case OperandType.InlineString:
					int stringToken = BitConverter.ToInt32(il, offset); offset += 4;
					return owner.Module.ResolveString(stringToken);
				case OperandType.ShortInlineBrTarget:
					return offset + 1 + unchecked((sbyte)il[offset++]);
				case OperandType.InlineBrTarget:
					int delta = BitConverter.ToInt32(il, offset); offset += 4; return offset + delta;
				default:
					int size = OperandSize(type, il, offset);
					byte[] raw = new byte[size]; Array.Copy(il, offset, raw, 0, size);
					offset += size; return raw;
			}
		}

		private static MethodBase ResolveMethod(MethodBase owner, int token)
		{
			try
			{
				Type[] declaring = owner.DeclaringType?.IsGenericType == true
					? owner.DeclaringType.GetGenericArguments() : null;
				Type[] method = owner.IsGenericMethod ? owner.GetGenericArguments() : null;
				return owner.Module.ResolveMethod(token, declaring, method);
			}
			catch (ArgumentException)
			{
				return null;
			}
		}


		private static int OperandSize(OperandType type, byte[] il, int offset)
		{
			switch (type)
			{
				case OperandType.InlineNone: return 0;
				case OperandType.ShortInlineBrTarget:
				case OperandType.ShortInlineI:
				case OperandType.ShortInlineVar: return 1;
				case OperandType.InlineVar: return 2;
				case OperandType.InlineI8:
				case OperandType.InlineR:
					return 8;
				case OperandType.InlineSwitch:
					return 4 + BitConverter.ToInt32(il, offset) * 4;
				case OperandType.InlineBrTarget:
				case OperandType.InlineField:
				case OperandType.InlineI:
				case OperandType.InlineMethod:
				case OperandType.InlineSig:
				case OperandType.InlineString:
				case OperandType.InlineTok:
				case OperandType.InlineType:
				case OperandType.ShortInlineR:
					return 4;
				default:
					throw new InvalidOperationException("Unsupported IL operand type: " + type);
			}
		}
	}
}
