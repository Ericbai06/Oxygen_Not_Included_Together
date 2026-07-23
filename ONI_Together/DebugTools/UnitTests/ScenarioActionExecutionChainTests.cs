using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class ScenarioActionExecutionChainTests
	{
		[UnitTest(name: "Scenario action chain: animation declares an executable chain", category: "StaticContract")]
		public static UnitTestResult AnimationChain()
			=> Validate(ScenarioActionExecutionExpectations.Animation);

		[UnitTest(name: "Scenario action chain: building config declares an executable chain", category: "StaticContract")]
		public static UnitTestResult BuildingConfigChain()
			=> Validate(ScenarioActionExecutionExpectations.BuildingConfig);

		[UnitTest(name: "Scenario action chain: DLC fixture and transition declare an executable chain", category: "StaticContract")]
		public static UnitTestResult DlcRuntimeChain()
			=> Validate(ScenarioActionExecutionExpectations.DlcRuntime);

		[UnitTest(name: "Scenario action chain: effect declares an executable chain", category: "StaticContract")]
		public static UnitTestResult EffectChain()
			=> Validate(ScenarioActionExecutionExpectations.Effect);

		[UnitTest(name: "Scenario action chain: entity lifecycle declares an executable chain", category: "StaticContract")]
		public static UnitTestResult EntityLifecycleChain()
			=> Validate(ScenarioActionExecutionExpectations.EntityLifecycle);

		[UnitTest(name: "Scenario action chain: inventory declares an executable chain", category: "StaticContract")]
		public static UnitTestResult InventoryChain()
			=> Validate(ScenarioActionExecutionExpectations.Inventory);

		[UnitTest(name: "Scenario action chain: motion declares an executable chain", category: "StaticContract")]
		public static UnitTestResult MotionChain()
			=> Validate(ScenarioActionExecutionExpectations.Motion);

		[UnitTest(name: "Scenario action chain: pickup declares an executable chain", category: "StaticContract")]
		public static UnitTestResult PickupChain()
			=> Validate(ScenarioActionExecutionExpectations.Pickup);

		[UnitTest(name: "Scenario action chain: rocket declares an executable chain", category: "StaticContract")]
		public static UnitTestResult RocketChain()
			=> Validate(ScenarioActionExecutionExpectations.Rocket);

		[UnitTest(name: "Scenario action chain: uproot declares an executable chain", category: "StaticContract")]
		public static UnitTestResult UprootChain()
			=> Validate(ScenarioActionExecutionExpectations.Uproot);

		private static UnitTestResult Validate(ScenarioActionExecutionExpectation expected)
		{
			ScenarioActionProductionBinding[] matches =
				ScenarioActionProductionBindingRegistry.Bindings.Where(
					value => string.Equals(value.Scenario, expected.Scenario,
						StringComparison.Ordinal)).Take(2).ToArray();
			if (matches.Length == 0)
				return UnitTestResult.Fail(expected.Scenario + ": binding is missing");
			if (matches.Length > 1)
				return UnitTestResult.Fail(expected.Scenario + ": binding is duplicated");
			ScenarioActionProductionBinding binding = matches[0];
			if (!TryReadContract(binding, out ExecutionContract contract, out string failure))
				return UnitTestResult.Fail(expected.Scenario + ": " + failure);
			failure = ScenarioActionLinearFlowValidator.Validate(binding, expected, contract)
			          ?? ValidateShape(expected, contract);
			return failure == null
				? UnitTestResult.Pass(expected.Scenario + ": executable chain is complete")
				: UnitTestResult.Fail(expected.Scenario + ": " + failure);
		}

		private static string ValidateShape(
			ScenarioActionExecutionExpectation expected,
			ExecutionContract contract)
		{
			if (!string.Equals(contract.DeterministicTargetRule,
				expected.DeterministicTargetRule, StringComparison.Ordinal))
				return "deterministic target rule is missing or ambiguous";
			if (contract.TargetType != expected.TargetType || contract.StateType != expected.StateType)
				return "typed target/state contract does not match the scenario";
			if (!expected.PacketNames.Contains(
				    contract.PacketType.FullName, StringComparer.Ordinal))
				return "packet binding is not the domain synchronization packet";
			foreach (MethodInfo method in contract.ProductionMethods)
				if (!IsProductionMethod(method))
					return method?.Name + " is not a production synchronization method";
			return null;
		}

		private static bool TryReadContract(
			ScenarioActionProductionBinding binding,
			out ExecutionContract contract,
			out string failure)
		{
			contract = null;
			var missing = new List<string>();
			Type bindingType = binding.GetType();
			string[] methodNames =
			{
				"TargetPreparationMethod", "TargetResolverMethod", "HostMutationMethod",
				"NetworkEmitterMethod", "ClientDispatchMethod", "ClientApplyMethod",
				"TypedOracleMethod", "CleanupMethod",
			};
			var methods = new MethodInfo[methodNames.Length];
			for (int index = 0; index < methodNames.Length; index++)
			{
				methods[index] = bindingType.GetProperty(methodNames[index],
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
					?.GetValue(binding, null) as MethodInfo;
				if (methods[index] == null) missing.Add(methodNames[index]);
			}
			string rule = ReadValue<string>(binding, "DeterministicTargetRule", missing);
			Type packet = ReadValue<Type>(binding, "PacketType", missing);
			Type target = ReadValue<Type>(binding, "TargetType", missing);
			Type state = ReadValue<Type>(binding, "StateType", missing);
			if (missing.Count > 0)
			{
				failure = "binding contract missing: " + string.Join(", ", missing);
				return false;
			}
			contract = new ExecutionContract(rule, packet, target, state, methods);
			failure = null;
			return true;
		}

		private static T ReadValue<T>(
			object owner, string name, ICollection<string> missing) where T : class
		{
			T value = owner.GetType().GetProperty(name,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
				?.GetValue(owner, null) as T;
			if (value == null) missing.Add(name);
			return value;
		}

		private static bool IsProductionMethod(MethodInfo method)
		{
			string owner = method?.DeclaringType?.FullName ?? string.Empty;
			return method != null
			       && method.Module.Assembly == typeof(ScenarioActionHandlerRegistry).Assembly
			       && (owner.StartsWith("ONI_Together.Networking", StringComparison.Ordinal)
			           || owner.StartsWith("ONI_Together.Patches", StringComparison.Ordinal));
		}

	}
}
