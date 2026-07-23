using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Patches.Duplicant;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class TypedEvidencePhaseProvenanceTests
	{
		private static readonly string[] ReceiverPhases =
		{
			"revision-accepted", "revision-duplicate", "revision-out-of-order",
		};

		[UnitTest(name: "Typed evidence: host senders cannot synthesize receiver phases",
			category: "Integration")]
		public static UnitTestResult HostSendersCannotSynthesizeReceiverPhases()
		{
			var senders = new[]
			{
				Method(typeof(EffectsPatch), "FlushDirtyEffects"),
				Method(typeof(DuplicantStateSender), "LogHostEvidence"),
				Method(typeof(RemoteMotionPresenter), "FlushPending"),
				Method(typeof(PlayerCursorPacket), "LogHostEvidence"),
			};
			string[] violations = senders.Select(InvalidHostSender)
				.Where(value => value != null).ToArray();
			return violations.Length == 0
				? UnitTestResult.Pass("Host senders emit only submit/local observations")
				: UnitTestResult.Fail(string.Join("; ", violations));
		}

		[UnitTest(name: "Typed evidence: production emitters preserve phase provenance",
			category: "Integration")]
		public static UnitTestResult ProductionEmittersPreservePhaseProvenance()
		{
			MethodInfo[] violations = typeof(TypedEvidenceRuntimeContext).Assembly.GetTypes()
				.Where(IsProductionType)
				.SelectMany(type => type.GetMethods(AllMethods))
				.Where(SynthesizesReceiverOutcome)
				.OrderBy(ProductionSymbol, StringComparer.Ordinal)
				.ToArray();
			return violations.Length == 0
				? UnitTestResult.Pass("No production emitter synthesizes receiver outcomes")
				: UnitTestResult.Fail("Host send methods synthesize accepted/duplicate/out-of-order: "
					+ string.Join(", ", violations.Select(ProductionSymbol)));
		}

		private const BindingFlags AllMethods = BindingFlags.Static | BindingFlags.Instance |
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

		private static MethodInfo Method(Type owner, string name)
			=> owner.GetMethod(name, AllMethods)
			   ?? throw new MissingMethodException(owner.FullName, name);

		private static string InvalidHostSender(MethodInfo sender)
		{
			HashSet<string> phases = TransitiveStrings(sender).ToHashSet(StringComparer.Ordinal);
			string[] invalid = ReceiverPhases.Where(phases.Contains).ToArray();
			return invalid.Length == 0 ? null
				: ProductionSymbol(sender) + " emitted " + string.Join(",", invalid);
		}

		private static bool SynthesizesReceiverOutcome(MethodInfo method)
		{
			HashSet<string> direct = LoadedStrings(method).ToHashSet(StringComparer.Ordinal);
			if (!direct.Contains("host-submit")) return false;
			HashSet<string> reachable = TransitiveStrings(method).ToHashSet(StringComparer.Ordinal);
			return ReceiverPhases.Any(reachable.Contains);
		}

		private static IEnumerable<string> TransitiveStrings(MethodInfo start)
		{
			var pending = new Queue<MethodInfo>();
			var visited = new HashSet<(Module Module, int Token)>();
			pending.Enqueue(start);
			while (pending.Count > 0)
			{
				MethodInfo method = pending.Dequeue();
				if (!visited.Add((method.Module, method.MetadataToken))) continue;
				foreach (string value in LoadedStrings(method)) yield return value;
				foreach (MethodInfo called in CalledMethods(method))
					if (called.DeclaringType == start.DeclaringType) pending.Enqueue(called);
			}
		}

		private static bool IsProductionType(Type type)
		{
			string value = type.FullName ?? string.Empty;
			return value.StartsWith("ONI_Together.", StringComparison.Ordinal)
			       && !value.StartsWith("ONI_Together.DebugTools", StringComparison.Ordinal);
		}

		private static string ProductionSymbol(MethodBase method)
			=> (method.DeclaringType?.FullName ?? "<unknown>") + "." + method.Name;

		private static IEnumerable<string> LoadedStrings(MethodInfo method)
		{
			byte[] il = method.GetMethodBody()?.GetILAsByteArray();
			if (il == null) yield break;
			for (int offset = 0; offset < il.Length;)
			{
				OpCode code = ReadOpCode(il, ref offset);
				if (code == OpCodes.Ldstr)
					yield return method.Module.ResolveString(BitConverter.ToInt32(il, offset));
				offset += OperandSize(code.OperandType, il, offset);
			}
		}

		private static IEnumerable<MethodInfo> CalledMethods(MethodInfo method)
		{
			byte[] il = method.GetMethodBody()?.GetILAsByteArray();
			if (il == null) yield break;
			for (int offset = 0; offset < il.Length;)
			{
				OpCode code = ReadOpCode(il, ref offset);
				if (code.OperandType == OperandType.InlineMethod)
				{
					int token = BitConverter.ToInt32(il, offset);
					MethodBase called = ResolveMethod(method, token);
					if (called is MethodInfo info) yield return info;
				}
				offset += OperandSize(code.OperandType, il, offset);
			}
		}

		private static MethodBase ResolveMethod(MethodInfo owner, int token)
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

		private static OpCode ReadOpCode(byte[] il, ref int offset)
		{
			byte first = il[offset++];
			if (first != 0xfe) return SingleByte[first];
			return DoubleByte[il[offset++]];
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
				case OperandType.InlineR: return 8;
				case OperandType.InlineSwitch:
					return 4 + BitConverter.ToInt32(il, offset) * 4;
				default: return 4;
			}
		}

		private static readonly OpCode[] SingleByte = BuildOpCodes(doubleByte: false);
		private static readonly OpCode[] DoubleByte = BuildOpCodes(doubleByte: true);

		private static OpCode[] BuildOpCodes(bool doubleByte)
		{
			var values = new OpCode[0x100];
			foreach (FieldInfo field in typeof(OpCodes).GetFields(
				         BindingFlags.Public | BindingFlags.Static))
			{
				if (field.GetValue(null) is not OpCode code) continue;
				ushort encoded = unchecked((ushort)code.Value);
				if (!doubleByte && encoded < 0x100) values[encoded] = code;
				if (doubleByte && (encoded & 0xff00) == 0xfe00) values[encoded & 0xff] = code;
			}
			return values;
		}
	}
}
