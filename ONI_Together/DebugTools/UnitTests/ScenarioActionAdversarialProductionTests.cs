using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Patches.World;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class ScenarioActionAdversarialProductionTests
	{
		[UnitTest(name: "Scenario adversarial: pickup spawn proves client rematerialization",
			category: "StaticContract")]
		public static UnitTestResult PickupSpawnRequiresMaterializationOutcome()
		{
			MethodInfo apply = M(typeof(SpawnPrefabPacket), "ApplyScenarioProfile");
			Type flow = Assembly.GetType("ONI_Together.Networking.PickupActionFlow");
			MethodInfo client = M(flow, "ApplySpawnClient");
			if (apply == null || apply.ReturnType != typeof(bool))
				return UnitTestResult.Fail(
					"SpawnPrefabPacket apply does not report rematerialization success");
			return ReflectionExecutionGraph.Reaches(client, apply)
				? UnitTestResult.Pass("pickup state requires successful rematerialization")
				: UnitTestResult.Fail("pickup client state bypasses materialization outcome");
		}

		[UnitTest(name: "Scenario adversarial: pickup mutation suppresses Harmony transport",
			category: "StaticContract")]
		public static UnitTestResult PickupMutationSuppressesHarmonyTransport()
		{
			Type runtime = Assembly.GetType("ONI_Together.Networking.PickupProfileRuntime");
			MethodInfo mutate = M(runtime, "PickupAndDrop");
			MethodInfo restore = M(runtime, "Restore");
			Type patches = typeof(StoragePatches);
			if (!ReflectionExecutionGraph.ReachesType(mutate, patches)
			    || !ReflectionExecutionGraph.ReachesType(restore, patches))
				return UnitTestResult.Fail("pickup storage mutation is not suppression-scoped");
			if (ReflectionExecutionGraph.ReachesPacketSender(mutate)
			    || ReflectionExecutionGraph.ReachesPacketSender(restore))
				return UnitTestResult.Fail("pickup runtime emits transport outside exact sequence");
			return UnitTestResult.Pass("pickup runtime suppresses membership and carry sends");
		}

		[UnitTest(name: "Scenario adversarial: marked packet requires actual receiver gate",
			category: "StaticContract")]
		public static UnitTestResult ProductionPacketsRequireReceiverGate()
		{
			Type gate = Assembly.GetType("ONI_Together.Networking.ScenarioActionReceiverGate");
			MethodInfo enter = M(gate, "TryEnter");
			if (enter == null || enter.ReturnType != typeof(bool))
				return UnitTestResult.Fail("ScenarioActionReceiverGate.TryEnter is missing");
			MethodInfo context = M(typeof(PacketHandler), "IsCurrentDispatchContext");
			if (!ReflectionExecutionGraph.Reaches(enter, context))
				return UnitTestResult.Fail("receiver gate does not require actual dispatch delivery");
			var failures = new List<string>();
			foreach (ScenarioActionTransportBinding step in TransportSteps())
			{
				FieldInfo marker = step.PacketType.GetField("ScenarioActionProfile",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (marker?.FieldType != typeof(string)) failures.Add(step.PacketType.Name + ":marker");
				if (!ReflectionExecutionGraph.Reaches(step.ClientDispatchMethod, enter))
					failures.Add(step.PacketType.Name + ":gate");
			}
			return failures.Count == 0
				? UnitTestResult.Pass("all scenario packets require marker and receiver gate")
				: UnitTestResult.Fail(string.Join(", ", failures.Distinct()));
		}

		[UnitTest(name: "Scenario adversarial: receiver gate binds armed action provenance",
			category: "StaticContract")]
		public static UnitTestResult ReceiverGateRequiresArmedActionProvenance()
		{
			Type gate = Assembly.GetType("ONI_Together.Networking.ScenarioActionReceiverGate");
			Type admission = Assembly.GetType("ONI_Together.Networking.ScenarioActionAdmission");
			if (gate == null)
				return UnitTestResult.Fail("ScenarioActionReceiverGate is missing");
			if (admission == null)
				return UnitTestResult.Fail("typed ScenarioActionAdmission is missing");
			if (!HasValue(admission, "Generation", typeof(long))
			    || !HasValue(admission, "Correlation", typeof(string))
			    || !HasValue(admission, "Sequence", typeof(long)))
				return UnitTestResult.Fail("action admission lacks generation/correlation/sequence");
			MethodInfo arm = M(gate, "ArmExpected");
			if (arm == null || arm.GetParameters().All(parameter =>
				parameter.ParameterType != admission))
				return UnitTestResult.Fail("receiver gate cannot arm an explicit expected action");
			MethodInfo enter = gate.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
				.FirstOrDefault(method => method.Name == "TryEnter"
					&& (method.ReturnType == admission || method.GetParameters().Any(parameter =>
						parameter.IsOut && parameter.ParameterType.GetElementType() == admission)));
			if (enter == null)
				return UnitTestResult.Fail("receiver gate does not return accepted action provenance");
			Type flowEvidence = Assembly.GetType("ONI_Together.Networking.ScenarioActionFlowEvidence");
			if (!HasValue(flowEvidence, "Admission", admission))
				return UnitTestResult.Fail("client evidence is detached from receiver admission");
			Type envelope = typeof(TypedEvidenceEnvelope);
			bool provenance = HasValue(envelope, "ActionGeneration", typeof(long))
			                  && HasValue(envelope, "ActionCorrelation", typeof(string))
			                  && HasValue(envelope, "ActionSequence", typeof(long));
			return provenance
				? UnitTestResult.Pass("armed generation/correlation reaches evidence provenance")
				: UnitTestResult.Fail("typed evidence omits action generation/correlation/sequence");
		}

		[UnitTest(name: "Scenario adversarial: ordinary packet apply cannot synthesize action evidence",
			category: "StaticContract")]
		public static UnitTestResult OrdinaryPacketApplyDoesNotSynthesizeEvidence()
		{
			var failures = new List<string>();
			foreach (Type packet in TransportSteps().Select(step => step.PacketType).Distinct())
			{
				MethodInfo apply = M(packet, "ApplyScenarioProfile");
				if (apply != null && ReflectionExecutionGraph.ReachesType(
					apply, typeof(IntegrationScenarioEvidenceCore)))
					failures.Add(packet.Name);
			}
			return failures.Count == 0
				? UnitTestResult.Pass("ordinary apply path emits no scenario evidence")
				: UnitTestResult.Fail("ordinary apply synthesizes evidence: "
					+ string.Join(", ", failures));
		}

		[UnitTest(name: "Scenario adversarial: null cleanup produces no state",
			category: "StaticContract")]
		public static UnitTestResult NullCleanupProducesNoState()
		{
			foreach (ScenarioActionProductionBinding binding in
			         ScenarioActionProductionBindingRegistry.Bindings)
			{
				object state = binding.CleanupExecutionMethod.Invoke(null, new object[] { null });
				if (state != null)
					return UnitTestResult.Fail(binding.Scenario + " cleanup observed null mutation");
			}
			return UnitTestResult.Pass("null cleanup produces no state or transport payload");
		}

		[UnitTest(name: "Scenario adversarial: failed entity materialization rolls back host mutation",
			category: "StaticContract")]
		public static UnitTestResult FailedEntityMaterializationRollsBack()
		{
			Type flow = Assembly.GetType("ONI_Together.Networking.EntityLifecycleActionFlow");
			MethodInfo mutate = M(flow, "Mutate");
			MethodInfo restore = M(flow, "Restore");
			return ReflectionExecutionGraph.Reaches(mutate, restore)
				? UnitTestResult.Pass("failed packet materialization restores active state")
				: UnitTestResult.Fail("entity mutation can fail after SetActive without rollback");
		}

		private static IEnumerable<ScenarioActionTransportBinding> TransportSteps()
			=> ScenarioActionProductionBindingRegistry.Bindings.SelectMany(binding =>
				binding.HostTransportSteps.Concat(binding.CleanupTransportSteps));

		private static MethodInfo M(Type type, string name)
			=> type?.GetMethods(BindingFlags.Static | BindingFlags.Instance
					| BindingFlags.Public | BindingFlags.NonPublic)
				.FirstOrDefault(method => method.Name == name);

		private static bool HasValue(Type type, string name, Type valueType)
		{
			if (type == null) return false;
			FieldInfo field = type.GetField(name,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			PropertyInfo property = type.GetProperty(name,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			return field?.FieldType == valueType || property?.PropertyType == valueType;
		}

		private static Assembly Assembly => typeof(ScenarioActionHandlerRegistry).Assembly;
	}
}
