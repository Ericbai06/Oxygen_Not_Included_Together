using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class FaultDeferredDestroyCandidateContractTests
	{
		[UnitTest(name: "Fault deferred destroy: receipt candidate is the owned fixture",
			category: "Integration")]
		public static UnitTestResult TriggerVerifiesActualDestroyedCandidate()
		{
			MethodInfo guard = typeof(FaultInjectionUnitySeams).GetMethod(
				"EnsureExpectedRuntimeTarget", BindingFlags.Static | BindingFlags.NonPublic);
			PropertyInfo expectedTarget = typeof(IFaultInputMutation)
				.GetProperty("RuntimeTarget");
			if (guard == null || guard.ReturnType != typeof(void)
			    || expectedTarget?.PropertyType != typeof(object))
				return UnitTestResult.Fail(
					"consumed mutation needs an executable pre-destroy runtime target guard");
			try
			{
				guard.Invoke(null, new object[]
				{
					new FaultInputMutation("building.destroy-deferred", false, false, false),
					new object(),
				});
			}
			catch (TargetInvocationException failure)
			{ return UnitTestResult.Fail("unconsumed production call was not a no-op: "
			                              + failure.InnerException); }
			object owned = new object();
			FaultInputMutation mutation = ConsumeExpectedTarget(owned, clean: false);
			if (mutation == null || !ReferenceEquals(expectedTarget.GetValue(mutation), owned))
				return UnitTestResult.Fail("Arm to Consume lost the owned runtime reference");
			if (!GuardRejects(guard, mutation, null)
			    || !GuardRejects(guard, mutation, new object()))
				return UnitTestResult.Fail("pre-destroy guard accepted null or wrong candidate");
			try { guard.Invoke(null, new object[] { mutation, owned }); }
			catch (TargetInvocationException failure)
			{ return UnitTestResult.Fail("owned candidate rejected: " + failure.InnerException); }
			FaultInputMutation cleanMutation = ConsumeExpectedTarget(owned, clean: true);
			if (cleanMutation == null || !cleanMutation.IsCleanControl
			    || !GuardRejects(guard, cleanMutation, new object()))
				return UnitTestResult.Fail("clean-control accepted a wrong runtime candidate");
			MethodInfo destroy = typeof(UnityEngine.Object).GetMethod(
				"Destroy", BindingFlags.Static | BindingFlags.Public, null,
				new[] { typeof(UnityEngine.Object) }, null);
			MethodInfo replacement = Binding().RuntimeCallsite;
			MethodInfo emit = typeof(FaultInjectionUnitySeams).GetMethod(
				"EmitReceipt", BindingFlags.Static | BindingFlags.NonPublic);
			bool unconditional = ReflectionExecutionGraph.NoConditionalBranchBetweenCalls(
				replacement, Binding().GateMethod, guard);
			return unconditional
			       && CallsInOrder(replacement, new[] { Binding().GateMethod, guard, destroy })
			       && CallsInOrder(replacement, new[] { guard, emit })
				? UnitTestResult.Pass("Owned candidate is verified before Destroy scheduling")
				: UnitTestResult.Fail(
					"fault/clean candidate guard is conditional, post-hoc or after Destroy");
		}

		[UnitTest(name: "Fault deferred destroy: zero lifecycle identity cannot bind",
			category: "Integration")]
		public static UnitTestResult ZeroIdentityCannotClaimReplacementBound()
		{
			MethodInfo guard = typeof(FaultDeferredDestroyRuntime).GetMethod(
				"EnsureOriginalLifecycleIdentity", BindingFlags.Static | BindingFlags.NonPublic);
			MethodInfo rebind = typeof(FaultDeferredDestroyRuntime).GetMethod(
				"ReplacementRebind", BindingFlags.Static | BindingFlags.NonPublic);
			MethodInfo apply = typeof(FaultDeferredDestroyRuntime).GetMethod(
				"RebindLifecycle", BindingFlags.Static | BindingFlags.NonPublic);
			if (guard == null || guard.ReturnType != typeof(void)
			    || guard.GetParameters().Length != 1)
				return UnitTestResult.Fail("replacement needs an executable lifecycle identity guard");
			if (!LifecycleGuardRejects(guard, 0, 7)
			    || !LifecycleGuardRejects(guard, 17, 0))
				return UnitTestResult.Fail("zero NetId or lifecycle passed the identity guard");
			try { guard.Invoke(null, new object[] { IdentityContext(17, 7) }); }
			catch (TargetInvocationException failure)
			{
				return UnitTestResult.Fail("valid lifecycle identity rejected: "
				                           + failure.InnerException?.GetType().Name);
			}
			MethodInfo setup = Binding().SetupMethod;
			MethodInfo identity = BindingMethod(Binding(), "DisposableFixtureIdentityMethod");
			MethodInfo arm = typeof(FaultInjectionUnitySeams).GetMethod(
				"Arm", BindingFlags.Static | BindingFlags.NonPublic);
			MethodInfo register = typeof(ONI_Together.Networking.Components.NetworkIdentity)
				.GetMethod("RegisterIdentity", BindingFlags.Instance | BindingFlags.Public);
			bool setupGuarded = CallsInOrder(setup, new[] { identity, guard, arm })
			                    && CallsAddOrGetIdentity(identity)
			                    && ReflectionExecutionGraph.Reaches(identity, register);
			return setupGuarded && CallsInOrder(rebind, new[] { guard, apply })
				? UnitTestResult.Pass("Replacement binding requires a real lifecycle identity")
				: UnitTestResult.Fail(
					"fixture identity is not registered and guarded before fault/rebind");
		}

		[UnitTest(name: "Fault deferred destroy: arm tracks active phase object",
			category: "Integration")]
		public static UnitTestResult ArmUsesOriginalThenReplacementRuntimeObject()
		{
			MethodInfo arm = typeof(FaultInjectionUnitySeams).GetMethod(
				"Arm", BindingFlags.Static | BindingFlags.NonPublic);
			if (arm == null || !arm.GetParameters().Any(parameter =>
				    parameter.Name == "runtimeTarget" && parameter.ParameterType == typeof(object)))
				return UnitTestResult.Fail("Arm must accept the owned runtime object reference");
			MethodInfo original = ContextGetter("DeferredOriginal");
			MethodInfo replacement = ContextGetter("DeferredReplacement");
			MethodInfo setup = Binding().SetupMethod;
			MethodInfo clean = BindingMethod(Binding(), "ReplacementCleanControlMethod");
			bool setupUsesOriginal = ReflectionExecutionGraph.CallLastArgumentUsesGetter(
				setup, arm, original);
			bool cleanUsesReplacement = ReflectionExecutionGraph.CallLastArgumentUsesGetter(
				clean, arm, replacement);
			return setupUsesOriginal && cleanUsesReplacement
				? UnitTestResult.Pass("Fault and clean arm the original then replacement object")
				: UnitTestResult.Fail(
					"Arm runtime operand mismatch: setupOriginal=" + setupUsesOriginal
					+ ", cleanReplacement=" + cleanUsesReplacement);
		}

		[UnitTest(name: "Fault runtime: handlers expose separate reachable phase paths",
			category: "Integration")]
		public static UnitTestResult HandlersUseRealFaultAndCleanExecutionPaths()
		{
			FaultUnityProductionBinding binding = Binding();
			Type driver = typeof(FaultUnityRuntimeDriver);
			Type mode = driver.GetNestedType("ExecutionMode", BindingFlags.NonPublic);
			if (mode != null && Enum.GetNames(mode).Contains("Full"))
				return UnitTestResult.Fail(
					"deferred lifecycle cannot depend on an unreachable same-frame Full mode");
			MethodInfo fault = BindingMethod(binding, "FaultExecutionMethod");
			MethodInfo clean = BindingMethod(binding, "CleanExecutionMethod");
			if (fault == null || clean == null || fault == clean
			    || fault.DeclaringType != driver || clean.DeclaringType != driver)
				return UnitTestResult.Fail(
					"binding must expose distinct real fault and clean driver methods");
			MethodInfo registryFault = typeof(FaultUnityBindingRegistry).GetMethod(
				"ExecuteFault", BindingFlags.Static | BindingFlags.NonPublic);
			MethodInfo registryClean = typeof(FaultUnityBindingRegistry).GetMethod(
				"ExecuteCleanControl", BindingFlags.Static | BindingFlags.NonPublic);
			MethodInfo store = typeof(FaultDeferredDestroyRuntime).GetMethod(
				"StorePending", BindingFlags.Static | BindingFlags.NonPublic);
			MethodInfo pending = typeof(FaultDeferredDestroyRuntime).GetMethod(
				"Pending", BindingFlags.Static | BindingFlags.NonPublic);
			MethodInfo dispose = typeof(FaultDeferredDestroyRuntime).GetMethod(
				"DisposeFixture", BindingFlags.Static | BindingFlags.NonPublic);
			bool faultPath = ReflectionExecutionGraph.Reaches(registryFault, fault)
			                 && ReflectionExecutionGraph.Reaches(fault, binding.TriggerMethod)
			                 && ReflectionExecutionGraph.Reaches(fault, binding.ReceiptBarrierMethod)
			                 && ReflectionExecutionGraph.Reaches(fault, store);
			bool cleanPath = ReflectionExecutionGraph.Reaches(registryClean, clean)
			                 && ReflectionExecutionGraph.Reaches(clean, pending)
			                 && ReflectionExecutionGraph.Reaches(clean, binding.ResetMethod)
			                 && ReflectionExecutionGraph.Reaches(
				                 clean, binding.CleanControlTriggerMethod)
			                 && ReflectionExecutionGraph.Reaches(clean, dispose);
			return faultPath && cleanPath
				? UnitTestResult.Pass("Handlers reach the real two-phase deferred lifecycle")
				: UnitTestResult.Fail("handler path depends on an unreachable Full-mode branch");
		}

		private static bool GuardRejects(
			MethodInfo guard, IFaultInputMutation mutation, object candidate)
		{
			try { guard.Invoke(null, new object[] { mutation, candidate }); return false; }
			catch (TargetInvocationException failure)
			{ return failure.InnerException is InvalidOperationException; }
		}

		private static FaultInputMutation ConsumeExpectedTarget(object expected, bool clean)
		{
			const string caseId = "building.destroy-deferred";
			FaultInjectionUnitySeams.Clean(caseId);
			SeedArmed(caseId, "building:fixture:17", clean);
			FieldInfo field = typeof(FaultInjectionUnitySeams).GetField(
				"ArmedRuntimeTargets", BindingFlags.Static | BindingFlags.NonPublic);
			if (!(field?.GetValue(null) is System.Collections.IDictionary values))
				return null;
			values[caseId] = expected;
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume(caseId);
			FaultInjectionUnitySeams.Clean(caseId);
			return mutation;
		}

		private static void SeedArmed(string caseId, string targetId, bool clean)
		{
			Type seams = typeof(FaultInjectionUnitySeams);
			var armed = (System.Collections.IDictionary)seams.GetField(
				"Armed", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
			var targets = (System.Collections.IDictionary)seams.GetField(
				"ArmedTargets", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
			Type mode = seams.GetNestedType("InjectionMode", BindingFlags.NonPublic);
			armed[caseId] = Enum.Parse(mode, clean ? "CleanControl" : "Fault");
			targets[caseId] = targetId;
		}

		private static bool LifecycleGuardRejects(MethodInfo guard, int id, ulong lifecycle)
		{
			try { guard.Invoke(null, new object[] { IdentityContext(id, lifecycle) }); return false; }
			catch (TargetInvocationException failure)
			{ return failure.InnerException is InvalidOperationException; }
		}

		private static FaultRuntimeTargetContext IdentityContext(int id, ulong lifecycle)
			=> new FaultRuntimeTargetContext
			{
				DeferredOriginalNetId = id,
				DeferredOriginalLifecycle = lifecycle,
				DeferredDestroyEvidence = new DeferredDestroyResetEvidence(),
			};

		private static bool CallsInOrder(
			MethodInfo caller, IReadOnlyList<MethodInfo> stages)
		{
			byte[] il = caller?.GetMethodBody()?.GetILAsByteArray();
			if (il == null) return false;
			int stage = 0;
			for (int offset = 0; offset <= il.Length - 5 && stage < stages.Count; offset++)
				if ((il[offset] == 0x28 || il[offset] == 0x6f)
				    && Resolves(caller, BitConverter.ToInt32(il, offset + 1), stages[stage]))
					stage++;
			return stage == stages.Count;
		}

		private static bool Resolves(MethodInfo caller, int token, MethodInfo expected)
		{
			try { return ReflectionExecutionGraph.Same(caller.Module.ResolveMethod(token), expected); }
			catch (ArgumentException) { return false; }
		}

		private static bool CallsAddOrGetIdentity(MethodInfo caller)
		{
			return ReflectionExecutionGraph.ReadInstructions(caller)
				.Select(value => value.Operand).OfType<MethodInfo>()
				.Any(method => method.Name == "AddOrGet" && method.IsGenericMethod
				               && method.GetGenericArguments().Contains(
					               typeof(ONI_Together.Networking.Components.NetworkIdentity)));
		}

		private static FaultUnityProductionBinding Binding()
			=> FaultUnityBindingRegistry.Bindings.Single(value =>
				value.CaseId == "building.destroy-deferred");

		private static MethodInfo BindingMethod(
			FaultUnityProductionBinding binding, string name)
			=> binding.GetType().GetProperty(name)?.GetValue(binding) as MethodInfo;

		private static MethodInfo ContextGetter(string name)
			=> typeof(FaultRuntimeTargetContext).GetProperty(name,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
				?.GetGetMethod(nonPublic: true);
	}
}
