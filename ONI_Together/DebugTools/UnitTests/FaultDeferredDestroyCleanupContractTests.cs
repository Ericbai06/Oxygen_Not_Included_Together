using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class FaultDeferredDestroyCleanupContractTests
	{
		private static readonly string[] CleanupFields =
		{
			"FixtureDisposeRequested", "FixtureDisposeRequestedFrame",
			"FixtureDisposeObservedFrame", "FixtureAbsent", "PendingPreservedForRetry",
		};

		[UnitTest(name: "Fault deferred destroy: clean waits for observed fixture absence",
			category: "Integration")]
		public static UnitTestResult CleanRequiresNextFrameAbsenceBeforePass()
		{
			MethodInfo execute = RuntimeMethod("ExecuteClean");
			MethodInfo dispose = RuntimeMethod("DisposeFixture");
			MethodInfo barrier = RuntimeMethod("FixtureAbsenceBarrier");
			MethodInfo validate = RuntimeMethod("ValidateFixtureDisposal");
			MethodInfo clear = RuntimeMethod("ClearPending");
			MethodInfo retry = RuntimeMethod("DeferCleanup");
			if (new[] { execute, dispose, barrier, validate, clear, retry }.Any(
				    method => method == null) || dispose.ReturnType != typeof(bool))
				return UnitTestResult.Fail(
					"clean needs disposal request, next-frame absence barrier and retry seam");
			if (!CallsInOrder(execute, new[] { dispose, barrier, validate, clear })
			    || !ReflectionExecutionGraph.Reaches(execute, retry)
			    || !HasConditionalBranchBetween(execute, dispose, barrier))
				return UnitTestResult.Fail(
					"clean can pass/clear pending without branching on disposal and absence");
			return ValidateTypedCleanup(validate);
		}

		[UnitTest(name: "Fault deferred destroy: failed cleanup remains retryable",
			category: "Integration")]
		public static UnitTestResult FailedDisposePreservesPendingForRetry()
		{
			MethodInfo retry = RuntimeMethod("DeferCleanup");
			if (retry == null || retry.ReturnType != typeof(FaultInjectionReceipt)
			    || retry.GetParameters().Length != 1)
				return UnitTestResult.Fail("typed failed-disposal retry handler is required");
			FaultRuntimeTargetContext context = Context();
			FaultDeferredDestroyRuntime.StorePending(context);
			try
			{
				var receipt = (FaultInjectionReceipt)retry.Invoke(null, new object[] { context });
				bool samePending = ReferenceEquals(FaultDeferredDestroyRuntime.Pending(), context);
				bool preserved = (bool)context.DeferredDestroyEvidence.GetType()
					.GetProperty("PendingPreservedForRetry")
					.GetValue(context.DeferredDestroyEvidence);
				return !receipt.Succeeded && samePending
				       && preserved
					? UnitTestResult.Pass("Failed disposal keeps the same pending cleanup context")
					: UnitTestResult.Fail("failed disposal passed or cleared pending state");
			}
			finally { FaultDeferredDestroyRuntime.ClearPending(context); }
		}

		[UnitTest(name: "Fault deferred destroy: fixture ownership is tracked immediately",
			category: "Integration")]
		public static UnitTestResult BuildTracksOwnershipBeforeComponentValidation()
		{
			MethodInfo create = RuntimeMethod("CreateDisposableFixture");
			MethodInfo track = RuntimeMethod("TrackUncommittedFixture");
			MethodInfo build = typeof(BuildingDef).GetMethods(
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
				.SingleOrDefault(method => method.Name == "Build" && method.GetParameters().Length == 8);
			MethodInfo getComponent = typeof(UnityEngine.GameObject).GetMethods(
				BindingFlags.Instance | BindingFlags.Public).SingleOrDefault(method =>
					method.Name == "GetComponent" && method.IsGenericMethodDefinition
					&& method.GetParameters().Length == 0)?.MakeGenericMethod(typeof(BuildingComplete));
			Type[] trackParameters = track?.GetParameters()
				.Select(parameter => parameter.ParameterType).ToArray();
			bool validTrackSignature = track?.ReturnType == typeof(void)
				&& trackParameters.SequenceEqual(new[]
				{
					typeof(FaultRuntimeTargetContext), typeof(UnityEngine.GameObject),
				});
			return create != null && validTrackSignature && build != null && getComponent != null
			       && CallsInOrder(create, new[] { build, track, getComponent })
				? UnitTestResult.Pass("Owned fixture is recoverable before component validation")
				: UnitTestResult.Fail(
					"fixture ownership is registered only after a throwing component validation");
		}

		private static UnitTestResult ValidateTypedCleanup(MethodInfo validate)
		{
			Type evidence = validate.GetParameters().SingleOrDefault()?.ParameterType;
			if (evidence == null || CleanupFields.Any(name =>
				    evidence.GetProperty(name)?.CanWrite != true))
				return UnitTestResult.Fail("typed cleanup evidence fields are incomplete");
			object valid = CleanupEvidence(evidence);
			if (!(bool)validate.Invoke(null, new[] { valid }))
				return UnitTestResult.Fail("valid next-frame fixture absence was rejected");
			foreach ((string name, object value) in new[]
			         {
				         ("FixtureDisposeRequested", (object)false),
				         ("FixtureDisposeObservedFrame", 7L),
				         ("FixtureAbsent", false),
			         })
			{
				object mutation = CleanupEvidence(evidence);
				evidence.GetProperty(name).SetValue(mutation, value);
				if ((bool)validate.Invoke(null, new[] { mutation }))
					return UnitTestResult.Fail("cleanup mutation accepted: " + name);
			}
			return UnitTestResult.Pass("Only observed next-frame fixture absence can finalize clean");
		}

		private static object CleanupEvidence(Type type)
		{
			object value = Activator.CreateInstance(type);
			type.GetProperty("FixtureDisposeRequested").SetValue(value, true);
			type.GetProperty("FixtureDisposeRequestedFrame").SetValue(value, 7L);
			type.GetProperty("FixtureDisposeObservedFrame").SetValue(value, 8L);
			type.GetProperty("FixtureAbsent").SetValue(value, true);
			type.GetProperty("PendingPreservedForRetry").SetValue(value, false);
			return value;
		}

		private static FaultRuntimeTargetContext Context()
			=> new FaultRuntimeTargetContext
			{
				CaseId = "building.destroy-deferred",
				DeferredDestroyEvidence = new DeferredDestroyResetEvidence(),
			};

		private static MethodInfo RuntimeMethod(string name)
			=> typeof(FaultDeferredDestroyRuntime).GetMethod(
				name, BindingFlags.Static | BindingFlags.NonPublic);

		private static bool HasConditionalBranchBetween(
			MethodInfo caller, MethodInfo first, MethodInfo second)
		{
			IReadOnlyList<ReflectedIlInstruction> il = ReflectionExecutionGraph
				.ReadInstructions(caller);
			int start = il.ToList().FindIndex(value => value.Operand is MethodInfo method
				&& ReflectionExecutionGraph.Same(method, first));
			int end = il.ToList().FindIndex(value => value.Operand is MethodInfo method
				&& ReflectionExecutionGraph.Same(method, second));
			return start >= 0 && end > start && il.Skip(start + 1).Take(end - start - 1)
				.Any(value => value.Code.FlowControl == System.Reflection.Emit.FlowControl.Cond_Branch);
		}

		private static bool CallsInOrder(
			MethodInfo caller, IReadOnlyList<MethodInfo> stages)
		{
			IReadOnlyList<ReflectedIlInstruction> il = ReflectionExecutionGraph
				.ReadInstructions(caller);
			int stage = 0;
			foreach (ReflectedIlInstruction instruction in il)
				if (stage < stages.Count && instruction.Operand is MethodInfo called
				    && ReflectionExecutionGraph.Same(called, stages[stage])) stage++;
			return stage == stages.Count;
		}
	}
}
