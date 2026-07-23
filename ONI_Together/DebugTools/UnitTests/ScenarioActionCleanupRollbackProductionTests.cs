using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ONI_Together.Networking.Packets.World;
using static Storage;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class ScenarioActionCleanupRollbackProductionTests
	{
		[UnitTest(name: "Scenario adversarial: pickup cleanup target is canonical on host and client",
			category: "StaticContract")]
		public static UnitTestResult PickupCleanupTargetIsCanonical()
		{
			StorageItemPacket copy = Roundtrip(Packet(95028));
			if (copy.ScenarioActionTargetCell != 95028)
				return UnitTestResult.Fail("Storage Debug wire dropped pickup cleanup TargetCell");
			Type flow = Assembly.GetType("ONI_Together.Networking.PickupActionFlow");
			FieldInfo cell = typeof(StorageItemPacket).GetField("ScenarioActionTargetCell",
				BindingFlags.Instance | BindingFlags.NonPublic);
			bool hostReads = Reads(M(flow, "Restore"), cell);
			bool clientReads = Reads(M(flow, "ApplyStorageClient"), cell);
			if (!hostReads || !clientReads)
				return UnitTestResult.Fail("pickup cleanup canonical target readers: host="
					+ hostReads + ", client=" + clientReads);
			bool zeroRejected = false;
			try { Roundtrip(Packet(0)); }
			catch (InvalidDataException) { zeroRejected = true; }
			return zeroRejected
				? UnitTestResult.Pass("nonzero cleanup target roundtrips and zero is rejected")
				: UnitTestResult.Fail("Storage Debug wire accepts zero pickup cleanup TargetCell");
		}

		[UnitTest(name: "Scenario adversarial: Rocket and DLC send failures rollback",
			category: "StaticContract")]
		public static UnitTestResult RocketAndDlcSendFailuresRollback()
		{
			var failures = new List<string>();
			foreach (string name in new[] { "RocketActionFlow", "DlcRuntimeActionFlow" })
			{
				Type flow = Assembly.GetType("ONI_Together.Networking." + name);
				MethodInfo execute = M(flow, "ExecuteHost");
				MethodInfo send = M(flow, "Send");
				MethodInfo restore = M(flow, "Restore");
				if (send?.ReturnType != typeof(bool))
				{
					failures.Add(name + ": send does not report failure");
					continue;
				}
				if (!ConsumesBoolean(execute, send))
					failures.Add(name + ": send outcome is ignored");
				if (!DirectlyCalls(execute, restore))
					failures.Add(name + ": send failure does not rollback host mutation");
			}
			return failures.Count == 0
				? UnitTestResult.Pass("Rocket and DLC consume send failure and rollback")
				: UnitTestResult.Fail(string.Join(", ", failures));
		}

		private static StorageItemPacket Packet(int targetCell)
			=> new()
			{
				NetId = 41, StorageNetId = 42, Revision = 1,
				FxPrefix = FXPrefix.Delivered, ConsumedAmount = 1,
				ScenarioActionProfile = "pickup-test", ScenarioActionItemNetId = 41,
				ScenarioActionTargetCell = targetCell,
			};

		private static StorageItemPacket Roundtrip(StorageItemPacket packet)
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
				packet.Serialize(writer);
			stream.Position = 0;
			var copy = new StorageItemPacket();
			using var reader = new BinaryReader(stream);
			copy.Deserialize(reader);
			return copy;
		}

		private static bool Reads(MethodInfo method, FieldInfo field)
		{
			if (method == null || field == null) return false;
			foreach (ReflectedIlInstruction instruction in
			         ReflectionExecutionGraph.ReadInstructions(method))
			{
				if (instruction.Code.OperandType != OperandType.InlineField
				    || instruction.Operand is not byte[] raw || raw.Length != sizeof(int)) continue;
				try
				{
					FieldInfo read = method.Module.ResolveField(BitConverter.ToInt32(raw, 0));
					if (read.Module == field.Module && read.MetadataToken == field.MetadataToken)
						return true;
				}
				catch (ArgumentException) { }
			}
			return false;
		}

		private static bool ConsumesBoolean(MethodInfo caller, MethodInfo called)
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
			=> caller != null && called != null && ReflectionExecutionGraph.ReadInstructions(caller)
				.Any(value => value.Operand is MethodInfo candidate
					&& ReflectionExecutionGraph.Same(candidate, called));

		private static MethodInfo M(Type type, string name)
			=> type?.GetMethods(BindingFlags.Static | BindingFlags.Instance
					| BindingFlags.Public | BindingFlags.NonPublic)
				.FirstOrDefault(method => method.Name == name);

		private static Assembly Assembly => typeof(ScenarioActionHandlerRegistry).Assembly;
	}
}
