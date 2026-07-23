using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class FaultUnityRuntimeDriverContractTests
	{
		private static readonly IReadOnlyDictionary<string, string> DlcTargets =
			new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["dlc.family-aquatic"] = "MinnowImperativePOIAConfig",
				["dlc.family-bionic"] = "Electrobank",
				["dlc.family-frosty"] = "GeothermalController+StatesInstance",
				["dlc.family-prehistoric"] = "LargeImpactorStatus+Instance",
				["dlc.family-spaced-out"] = "ArtifactPOIConfig",
				["dlc.family-common"] = "POITechItemUnlockWorkable",
			};

		private static readonly string[] LifecycleMethods =
		{
			"FaultExecutionMethod",
			"CleanExecutionMethod",
			"SetupMethod",
			"TriggerMethod",
			"ReceiptBarrierMethod",
			"SnapshotMethod",
			"OracleMethod",
			"ValidationMethod",
			"ResetMethod",
			"CleanControlTriggerMethod",
		};

		[UnitTest(name: "Fault runtime driver: all Unity cases declare executable lifecycle",
			category: "Integration")]
		public static UnitTestResult EveryUnityCaseDeclaresExecutableLifecycle()
		{
			foreach (FaultUnityProductionBinding binding in FaultUnityBindingRegistry.Bindings)
			{
				foreach (string propertyName in LifecycleMethods)
				{
					MethodInfo method = ReadMethod(binding, propertyName);
					if (method == null)
						return UnitTestResult.Fail(binding.CaseId + ": " + propertyName + " is required");
					if (method == binding.GateMethod)
						return UnitTestResult.Fail(binding.CaseId + ": " + propertyName + " cannot be the arm gate");
				}
			}
			return UnitTestResult.Pass("All 18 Unity faults declare setup, trigger, receipt, oracle, reset and clean-control methods");
		}

		[UnitTest(name: "Fault runtime driver: execution graph cannot be metadata-only",
			category: "Integration")]
		public static UnitTestResult ExecutionGraphCallsPinnedStagesInOrder()
		{
			foreach (FaultUnityProductionBinding binding in FaultUnityBindingRegistry.Bindings)
			{
				MethodInfo fault = ReadMethod(binding, "FaultExecutionMethod");
				MethodInfo clean = ReadMethod(binding, "CleanExecutionMethod");
				if (fault == null || clean == null || fault == clean)
					return UnitTestResult.Fail(binding.CaseId
					                           + ": separate fault and clean graphs are required");
				string failure = ValidateOrderedCalls(binding, fault, clean);
				if (failure != null) return UnitTestResult.Fail(binding.CaseId + ": " + failure);
			}
			return UnitTestResult.Pass("Every Unity fault executes real fault and clean phase graphs");
		}

		[UnitTest(name: "Fault runtime driver: runtime stages share setup target context",
			category: "Integration")]
		public static UnitTestResult RuntimeStagesShareSetupTargetContext()
		{
			foreach (FaultUnityProductionBinding binding in FaultUnityBindingRegistry.Bindings)
			{
				MethodInfo setup = ReadMethod(binding, "SetupMethod");
				if (setup == null || setup.ReturnType == typeof(void))
					return UnitTestResult.Fail(binding.CaseId + ": setup must return a runtime target context");
				foreach (string name in LifecycleMethods.Skip(3))
				{
					MethodInfo stage = ReadMethod(binding, name);
					if (stage == null || !stage.GetParameters().Any(parameter =>
						    parameter.ParameterType == setup.ReturnType))
						return UnitTestResult.Fail(binding.CaseId + ": " + name
							+ " must consume the setup runtime target context");
				}
				MethodInfo fault = ReadMethod(binding, "FaultExecutionMethod");
				MethodInfo trigger = ReadMethod(binding, "TriggerMethod");
				MethodInfo barrier = ReadMethod(binding, "ReceiptBarrierMethod");
				MethodInfo snapshot = ReadMethod(binding, "SnapshotMethod");
				MethodInfo oracle = ReadMethod(binding, "OracleMethod");
				MethodInfo validate = ReadMethod(binding, "ValidationMethod");
				MethodInfo[] faultStages = { trigger, barrier, snapshot, oracle, validate };
				if (!FaultExecutionIlFlow.UsesSameSetupLocal(fault, setup, faultStages))
					return UnitTestResult.Fail(binding.CaseId
						+ ": fault path must preserve the setup context instance");
				string cleanFailure = ValidateCleanContextFlow(binding);
				if (cleanFailure != null)
					return UnitTestResult.Fail(binding.CaseId + ": " + cleanFailure);
			}
			return UnitTestResult.Pass("Fault and clean phases preserve their handed-off context");
		}

		[UnitTest(name: "Fault runtime driver: handler executes trigger then fixed receipt barrier",
			category: "Integration")]
		public static UnitTestResult HandlerExecutesTriggerThenFixedReceiptBarrier()
		{
			Type driver = typeof(FaultUnityBindingRegistry).Assembly.GetType(
				"ONI_Together.DebugTools.FaultUnityRuntimeDriver");
			MethodInfo inject = UniqueMethod(driver, "ExecuteFault");
			MethodInfo clean = UniqueMethod(driver, "ExecuteCleanControl");
			if (inject == null || clean == null)
				return UnitTestResult.Fail("FaultUnityRuntimeDriver must execute trigger and wait for fixed receipts");

			MethodInfo registryInject = UniqueMethod(typeof(FaultUnityBindingRegistry), "ExecuteFault");
			MethodInfo registryClean = UniqueMethod(typeof(FaultUnityBindingRegistry), "ExecuteCleanControl");
			if (!ReferencesMethod(registryInject, inject) || !ReferencesMethod(registryClean, clean))
				return UnitTestResult.Fail("fault-inject/fault-clean handlers still stop after arming the callsite");
			MethodInfo arm = UniqueMethod(typeof(FaultInjectionUnitySeams), "Arm");
			if (ReferencesMethod(inject, arm) || ReferencesMethod(clean, arm))
				return UnitTestResult.Fail("runtime driver cannot only arm the next production callsite");
			foreach (FaultUnityProductionBinding binding in FaultUnityBindingRegistry.Bindings)
			{
				MethodInfo faultExecution = ReadMethod(binding, "FaultExecutionMethod");
				MethodInfo cleanExecution = ReadMethod(binding, "CleanExecutionMethod");
				if (faultExecution == null || cleanExecution == null
				    || !ReflectionExecutionGraph.Reaches(inject, faultExecution)
				    || !ReflectionExecutionGraph.Reaches(clean, cleanExecution))
					return UnitTestResult.Fail(binding.CaseId
						+ ": handlers must reach their distinct real execution phase");
			}
			return UnitTestResult.Pass("Handlers execute production triggers and wait for paired fixed receipts");
		}

		[UnitTest(name: "Fault runtime driver: DLC families use concrete target and typed oracle",
			category: "Integration")]
		public static UnitTestResult DlcFamiliesUseConcreteTargetAndTypedOracle()
		{
			foreach ((string caseId, string targetName) in DlcTargets)
			{
				FaultUnityProductionBinding binding = FaultUnityBindingRegistry.Bindings
					.Single(item => item.CaseId == caseId);
				Type target = ReadType(binding, "ConcreteRuntimeTargetType");
				if (target == null || target.FullName != targetName)
					return UnitTestResult.Fail("concrete-runtime-target-oracle-required:" + caseId);
				MethodInfo oracle = ReadMethod(binding, "OracleMethod");
				if (oracle == null || oracle.ReturnType != typeof(TypedEvidenceEnvelope))
					return UnitTestResult.Fail("dlc-runtime-typed-evidence-required:" + caseId);
				if (ReadType(binding, "OracleTargetType") != typeof(DlcRuntimeTarget)
				    || ReadType(binding, "OracleStateType") != typeof(DlcRuntimeState))
					return UnitTestResult.Fail("dlc-runtime-identity-state-admission-oracle-required:" + caseId);
			}
			return UnitTestResult.Pass("Six DLC family faults bind concrete targets and DlcRuntime typed evidence");
		}

		[UnitTest(name: "Fault runtime driver: destroyed minion proves no component identity or exception",
			category: "Integration")]
		public static UnitTestResult DestroyedMinionUsesObservableBeforeAfterOracle()
		{
			FaultUnityProductionBinding binding = FaultUnityBindingRegistry.Bindings.Single(
				item => item.CaseId == "duplicant.destroyed-add-component");
			MethodInfo snapshot = ReadMethod(binding, "SnapshotMethod");
			MethodInfo oracle = ReadMethod(binding, "OracleMethod");
			if (snapshot == null || oracle == null)
				return UnitTestResult.Fail("destroyed minion requires snapshot and typed oracle methods");
			string[] snapshotFields = { "ComponentCount", "IdentityPresent", "ExceptionCount" };
			string[] oracleFields = { "NoNewComponents", "NoNewIdentity", "ExceptionFree" };
			if (!HasReadableProperties(snapshot.ReturnType, snapshotFields))
				return UnitTestResult.Fail("destroyed snapshot must observe components, identity and exceptions");
			if (!HasTrueProperties(oracle.ReturnType, oracleFields))
				return UnitTestResult.Fail("destroyed oracle must prove no new component/identity and no exception");
			return UnitTestResult.Pass("Destroyed-object fault has an observable before/after safety oracle");
		}

		private static MethodInfo ReadMethod(FaultUnityProductionBinding binding, string propertyName)
			=> binding.GetType().GetProperty(propertyName)?.GetValue(binding) as MethodInfo;

		private static Type ReadType(FaultUnityProductionBinding binding, string propertyName)
			=> binding.GetType().GetProperty(propertyName)?.GetValue(binding) as Type;

		private static MethodInfo UniqueMethod(Type owner, string name)
		{
			if (owner == null) return null;
			MethodInfo[] methods = owner.GetMethods(BindingFlags.Static | BindingFlags.Public
				| BindingFlags.NonPublic).Where(method => method.Name == name).ToArray();
			return methods.Length == 1 ? methods[0] : null;
		}

		private static bool HasReadableProperties(Type type, IEnumerable<string> names)
			=> type != null && names.All(name => type.GetProperty(name)?.CanRead == true);

		private static bool HasTrueProperties(Type type, IEnumerable<string> names)
			=> type != null && names.All(name => type.GetProperty(name) is PropertyInfo property
				&& property.CanRead && property.PropertyType == typeof(bool));

		private static string ValidateOrderedCalls(
			FaultUnityProductionBinding binding,
			MethodInfo fault,
			MethodInfo clean)
		{
			MethodInfo setup = ReadMethod(binding, "SetupMethod");
			MethodInfo trigger = ReadMethod(binding, "TriggerMethod");
			MethodInfo barrier = ReadMethod(binding, "ReceiptBarrierMethod");
			MethodInfo snapshot = ReadMethod(binding, "SnapshotMethod");
			MethodInfo oracle = ReadMethod(binding, "OracleMethod");
			MethodInfo validate = ReadMethod(binding, "ValidationMethod");
			MethodInfo reset = ReadMethod(binding, "ResetMethod");
			MethodInfo cleanTrigger = ReadMethod(binding, "CleanControlTriggerMethod");
			if (new[] { setup, trigger, barrier, snapshot, oracle, validate, reset, cleanTrigger }.Any(
				    method => method == null)) return "lifecycle metadata is incomplete";
			if (!ReflectionExecutionGraph.Reaches(trigger, binding.RuntimeCallsite))
				return "trigger does not reach the pinned production callsite";
			if (!InOrder(CallOffsets(fault,
				    setup, trigger, barrier, snapshot, oracle, validate)))
				return "fault setup/trigger/barrier/oracle order is not executable";
			MethodInfo cleanBody = CleanBody(binding, clean);
			MethodInfo cleanSetup = binding.CaseId == "building.destroy-deferred"
				? null : UniqueMethod(typeof(FaultUnityRuntimeStages), "SetupClean");
			MethodInfo[] cleanStages = binding.CaseId == "building.destroy-deferred"
				? new[] { reset, cleanTrigger, barrier, snapshot, oracle, validate }
				: new[] { cleanSetup, trigger, barrier, snapshot, oracle, validate };
			if (!ReflectionExecutionGraph.Reaches(clean, cleanBody)
			    || !InOrder(CallOffsets(cleanBody, cleanStages)))
				return "clean pending/reset/trigger/barrier/oracle order is not executable";
			return null;
		}

		private static string ValidateCleanContextFlow(FaultUnityProductionBinding binding)
		{
			MethodInfo clean = ReadMethod(binding, "CleanExecutionMethod");
			MethodInfo body = CleanBody(binding, clean);
			MethodInfo setup = binding.CaseId == "building.destroy-deferred"
				? UniqueMethod(typeof(FaultDeferredDestroyRuntime), "Pending")
				: UniqueMethod(typeof(FaultUnityRuntimeStages), "SetupClean");
			MethodInfo trigger = binding.CaseId == "building.destroy-deferred"
				? ReadMethod(binding, "ResetMethod") : ReadMethod(binding, "TriggerMethod");
			MethodInfo[] stages =
			{
				trigger, ReadMethod(binding, "CleanControlTriggerMethod"),
				ReadMethod(binding, "ReceiptBarrierMethod"), ReadMethod(binding, "SnapshotMethod"),
				ReadMethod(binding, "OracleMethod"), ReadMethod(binding, "ValidationMethod"),
			};
			if (binding.CaseId != "building.destroy-deferred")
				stages = stages.Where(method => method != ReadMethod(
					binding, "CleanControlTriggerMethod")).ToArray();
			return FaultExecutionIlFlow.UsesSameSetupLocal(body, setup, stages)
				? null : "clean path must preserve pending/setup context";
		}

		private static MethodInfo CleanBody(
			FaultUnityProductionBinding binding, MethodInfo clean)
			=> binding.CaseId == "building.destroy-deferred"
				? UniqueMethod(typeof(FaultDeferredDestroyRuntime), "ExecuteClean") : clean;

		private static bool InOrder(IReadOnlyList<int> offsets)
			=> offsets.All(offset => offset >= 0)
			   && offsets.SequenceEqual(offsets.OrderBy(offset => offset));

		private static int[] CallOffsets(MethodInfo caller, params MethodInfo[] stages)
		{
			byte[] il = caller?.GetMethodBody()?.GetILAsByteArray();
			var result = new int[stages.Length];
			Array.Fill(result, -1);
			if (il == null) return result;
			int stage = 0;
			for (int offset = 0; offset <= il.Length - 5 && stage < stages.Length; offset++)
				if ((il[offset] == 0x28 || il[offset] == 0x6f)
				    && ResolvesTo(caller, BitConverter.ToInt32(il, offset + 1), stages[stage]))
					result[stage++] = offset;
			return result;
		}

		private static bool ResolvesTo(MethodInfo caller, int token, MethodInfo expected)
		{
			try
			{
				return ReflectionExecutionGraph.Same(caller.Module.ResolveMethod(token), expected);
			}
			catch (ArgumentException)
			{
				return false;
			}
		}

		private static bool ReferencesMethod(MethodInfo caller, MethodInfo callee)
		{
			byte[] il = caller?.GetMethodBody()?.GetILAsByteArray();
			if (il == null || callee == null) return false;
			for (int offset = 0; offset <= il.Length - 5; offset++)
				if ((il[offset] == 0x28 || il[offset] == 0x6f)
				    && BitConverter.ToInt32(il, offset + 1) == callee.MetadataToken) return true;
			return false;
		}
	}
}
