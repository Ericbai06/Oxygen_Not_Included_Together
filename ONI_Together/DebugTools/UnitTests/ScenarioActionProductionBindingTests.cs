using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class ScenarioActionProductionBindingTests
	{
		private static readonly string[] ExpectedScenarios =
		{
			"animation", "building-config", "dlc-runtime", "effect",
			"entity-lifecycle", "inventory", "motion", "pickup", "rocket", "uproot",
		};

		[UnitTest(name: "Scenario action profiles: handlers bind real production runtime seams",
			category: "Integration")]
		public static UnitTestResult HandlersBindProductionRuntimeSeams()
		{
			IReadOnlyList<ScenarioActionProductionBinding> bindings =
				ScenarioActionProductionBindingRegistry.Bindings;
			string[] actual = bindings.Select(value => value.Scenario)
				.OrderBy(value => value, StringComparer.Ordinal).ToArray();
			if (!ExpectedScenarios.SequenceEqual(actual, StringComparer.Ordinal))
				return UnitTestResult.Fail("Production bindings do not exactly cover profile actions");

			foreach (ScenarioActionProductionBinding binding in bindings)
			{
				if (!ScenarioActionHandlerRegistry.TryResolve(
					    binding.Scenario, out ScenarioActionHandler handler)
				    || !SameMethod(handler.Method, binding.HandlerMethod))
					return UnitTestResult.Fail(binding.Scenario + ": registry delegate is not bound");
				string invalid = InvalidProductionMethod(binding.RuntimeMethod);
				if (invalid != null)
					return UnitTestResult.Fail(binding.Scenario + ": " + invalid);
				if (!CallsOrIs(binding.HandlerMethod, binding.RuntimeCallsite)
				    || !CallsOrIs(binding.RuntimeCallsite, binding.RuntimeMethod))
					return UnitTestResult.Fail(binding.Scenario + ": production call chain is disconnected");
			}
			return UnitTestResult.Pass("All profile actions are tied to real synchronization runtime methods");
		}

		[UnitTest(name: "Scenario action profiles: handlers arm reversible cleanup receipts",
			category: "Integration")]
		public static UnitTestResult HandlersArmCleanupReceipts()
		{
			MethodInfo arm = typeof(ScenarioActionTargets).GetMethod(
				"Arm", BindingFlags.Static | BindingFlags.NonPublic);
			if (arm == null)
				return UnitTestResult.Fail("Cleanup receipt arm seam is missing");
			foreach (ScenarioActionProductionBinding binding in
			         ScenarioActionProductionBindingRegistry.Bindings)
			{
				if (!ReferencesMethod(binding.HandlerMethod, arm)
				    && !ReferencesMethod(binding.RuntimeCallsite, arm))
					return UnitTestResult.Fail(
						binding.Scenario + ": handler never stores a cleanup receipt");
			}
			return UnitTestResult.Pass("Every profile action stores a concrete cleanup receipt");
		}

		private static string InvalidProductionMethod(MethodInfo method)
		{
			if (method == null || method.DeclaringType == null)
				return "production runtime method is missing";
			string owner = method.DeclaringType.FullName ?? string.Empty;
			if (method.Module.Assembly != typeof(ScenarioActionHandlerRegistry).Assembly)
				return "production runtime method belongs to another assembly";
			if (!owner.StartsWith("ONI_Together.Networking", StringComparison.Ordinal)
			    || owner.IndexOf("DebugTools", StringComparison.Ordinal) >= 0)
				return "runtime binding points to DebugTools instead of synchronization code";
			return null;
		}

		private static bool CallsOrIs(MethodInfo caller, MethodInfo callee)
			=> SameMethod(caller, callee) || ReferencesMethod(caller, callee);

		private static bool ReferencesMethod(MethodInfo caller, MethodInfo callee)
		{
			byte[] il = caller?.GetMethodBody()?.GetILAsByteArray();
			if (il == null || callee == null) return false;
			for (int offset = 0; offset <= il.Length - 5; offset++)
			{
				bool call = il[offset] == 0x28 || il[offset] == 0x6f;
				if (call && BitConverter.ToInt32(il, offset + 1) == callee.MetadataToken)
					return true;
			}
			return false;
		}

		private static bool SameMethod(MethodInfo actual, MethodInfo expected)
			=> actual != null && expected != null && actual.Module == expected.Module
			   && actual.MetadataToken == expected.MetadataToken;
	}
}
