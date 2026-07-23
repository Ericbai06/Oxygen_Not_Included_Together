using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class FaultDeferredDestroyResetContractTests
	{
		private static readonly string[] ResetStages =
		{
			"DeferredDestroyBarrierMethod",
			"ReplacementRecreateMethod",
			"ReplacementRebindMethod",
			"BaselineRestoreMethod",
		};
		private static readonly string[] EvidenceFields =
		{
			"FixtureIdentity", "DisposableFixtureOwned",
			"LogicalTargetIdBefore", "LogicalTargetIdAfter",
			"OriginalInstanceId", "ReplacementInstanceId",
			"DestroyRequestedFrame", "DestroyBarrierFrame",
			"OriginalDestroyed", "ReplacementBound",
			"BaselineHashBefore", "BaselineHashAfter",
			"CleanControlInstanceId", "CleanControlTargetId",
		};

		[UnitTest(name: "Fault deferred destroy: reset recreates the logical target",
			category: "Integration")]
		public static UnitTestResult ResetRecreatesAndRebindsReplacementTarget()
		{
			FaultUnityProductionBinding binding = Binding();
			MethodInfo setup = Method(binding, "SetupMethod");
			MethodInfo create = Method(binding, "DisposableFixtureCreateMethod");
			MethodInfo identity = Method(binding, "DisposableFixtureIdentityMethod");
			if (setup == null || create == null || identity == null
			    || !CallsInOrder(setup, new[] { create, identity }))
				return UnitTestResult.Fail(
					"setup must create an owned disposable fixture before assigning identity");

			MethodInfo reset = Method(binding, "ResetMethod");
			MethodInfo[] stages = ResetStages.Select(name => Method(binding, name)).ToArray();
			if (reset == null || stages.Any(stage => stage == null)
			    || !CallsInOrder(reset, stages))
				return UnitTestResult.Fail(
					"reset must await Destroy, recreate, rebind and restore the baseline");

			MethodInfo clean = Method(binding, "CleanControlTriggerMethod");
			MethodInfo replacementClean = Method(binding, "ReplacementCleanControlMethod");
			return clean != null && replacementClean != null
			       && ReflectionExecutionGraph.Reaches(clean, replacementClean)
				? UnitTestResult.Pass("Clean control executes on a rebound replacement instance")
				: UnitTestResult.Fail("clean control is not bound to the replacement instance");
		}

		[UnitTest(name: "Fault deferred destroy: typed reset evidence rejects lifecycle drift",
			category: "Integration")]
		public static UnitTestResult TypedResetEvidenceRejectsEveryLifecycleMutation()
		{
			MethodInfo validate = Method(Binding(), "DeferredDestroyValidationMethod");
			if (validate == null || validate.ReturnType != typeof(bool)
			    || validate.GetParameters().Length != 1)
				return UnitTestResult.Fail("typed deferred-destroy reset validator is required");
			Type evidenceType = validate.GetParameters()[0].ParameterType;
			if (EvidenceFields.Any(name =>
				    evidenceType.GetProperty(name)?.CanWrite != true))
				return UnitTestResult.Fail(
					"typed reset evidence must include fixture ownership and lifecycle fields");
			MethodInfo runtimeValidate = Method(Binding(), "ValidationMethod");
			if (runtimeValidate == null
			    || !ReflectionExecutionGraph.Reaches(runtimeValidate, validate))
				return UnitTestResult.Fail(
					"runtime validation must execute the deferred-destroy reset validator");
			object valid = Evidence(evidenceType);
			if (!Invoke(validate, valid))
				return UnitTestResult.Fail("valid replacement reset evidence was rejected");
			foreach ((string property, object value) in Mutations())
			{
				object mutation = Evidence(valid.GetType());
				Set(mutation, property, value);
				if (Invoke(validate, mutation))
					return UnitTestResult.Fail("deferred reset mutation accepted: " + property);
			}
			return UnitTestResult.Pass("Deferred Destroy and replacement lifecycle drift is rejected");
		}

		[UnitTest(name: "Fault deferred destroy: validation is phase aware",
			category: "Integration")]
		public static UnitTestResult ValidationAcceptsFaultPhaseBeforeReset()
		{
			FaultRuntimeTargetContext fault = RuntimeContext(clean: false,
				includeResetEvidence: false);
			if (!FaultUnityRuntimeStages.Validate(fault))
				return UnitTestResult.Fail(
					"fault phase was rejected before reset, so StorePending is unreachable");

			try
			{
				FaultDeferredDestroyRuntime.StorePending(fault);
				if (!ReferenceEquals(FaultDeferredDestroyRuntime.Pending(), fault))
					return UnitTestResult.Fail("validated fault phase was not stored as pending");
			}
			finally
			{
				FaultDeferredDestroyRuntime.ClearPending(fault);
			}

			FaultRuntimeTargetContext clean = RuntimeContext(clean: true,
				includeResetEvidence: true);
			if (!FaultUnityRuntimeStages.Validate(clean))
				return UnitTestResult.Fail("complete clean phase evidence was rejected");
			clean.DeferredDestroyEvidence.ReplacementBound = false;
			return FaultUnityRuntimeStages.Validate(clean)
				? UnitTestResult.Fail("clean phase accepted incomplete reset evidence")
				: UnitTestResult.Pass(
					"Fault phase reaches pending storage; clean phase requires full reset evidence");
		}

		[UnitTest(name: "Fault deferred destroy: receipt preserves logical target identity",
			category: "Integration")]
		public static UnitTestResult ReceiptUsesArmedLogicalTargetIdentity()
		{
			const string caseId = "building.destroy-deferred";
			const string logicalTarget = "building:fixture:tile:17";
			PropertyInfo mutationTarget = typeof(IFaultInputMutation).GetProperty("TargetId");
			if (mutationTarget == null || mutationTarget.PropertyType != typeof(string))
				return UnitTestResult.Fail(
					"consumed mutation must carry the exact setup logical target ID");
			FaultInjectionUnitySeams.Clean(caseId);
			try
			{
				SeedArmed(caseId, logicalTarget);
				FaultInputMutation mutation = FaultInjectionUnitySeams.Consume(caseId);
				if ((string)mutationTarget.GetValue(mutation) != logicalTarget)
					return UnitTestResult.Fail("Consume lost the armed logical target ID");
				FaultInjectionUnitySeams.EmitReceipt(mutation, runtimeTarget: new object());
				bool taken = FaultInjectionUnitySeams.TryTakeReceipt(
					caseId, out _, out string observedTarget);
				return taken && observedTarget == logicalTarget
					? UnitTestResult.Pass("Receipt barrier correlates the setup logical target")
					: UnitTestResult.Fail(
						"receipt recomputed runtime/unity identity instead of the setup target");
			}
			finally
			{
				FaultInjectionUnitySeams.Clean(caseId);
			}
		}

		[UnitTest(name: "Fault receipt: non-deferred target cannot self-assert",
			category: "Integration")]
		public static UnitTestResult NonDeferredReceiptRejectsWrongRuntimeTarget()
		{
			const string caseId = "work.target-missing";
			const string expectedTarget = "net:17";
			FaultInjectionUnitySeams.Clean(caseId);
			try
			{
				SeedArmed(caseId, expectedTarget);
				FaultInputMutation mutation = FaultInjectionUnitySeams.Consume(caseId);
				FaultInjectionUnitySeams.EmitReceipt(mutation, runtimeTarget: "net:99");
				return FaultInjectionUnitySeams.TryTakeReceipt(caseId, out _, out _)
					? UnitTestResult.Fail(
						"non-deferred receipt accepted the armed claim over the actual runtime target")
					: UnitTestResult.Pass("Non-deferred receipts still validate actual runtime targets");
			}
			finally
			{
				FaultInjectionUnitySeams.Clean(caseId);
			}
		}

		[UnitTest(name: "Fault deferred destroy: failure cleanup clears state without false disposal claim",
			category: "Integration")]
		public static UnitTestResult FailureCleanupClearsStateWithoutFalseDisposalClaim()
		{
			const string caseId = "building.destroy-deferred";
			MethodInfo abort = typeof(FaultDeferredDestroyRuntime).GetMethod(
				"Abort", BindingFlags.Static | BindingFlags.NonPublic);
			PropertyInfo disposed = typeof(DeferredDestroyResetEvidence)
				.GetProperty("FixtureDisposed");
			PropertyInfo pendingCleared = typeof(DeferredDestroyResetEvidence)
				.GetProperty("PendingCleared");
			PropertyInfo seamCleaned = typeof(DeferredDestroyResetEvidence)
				.GetProperty("SeamCleaned");
			if (abort == null || disposed == null || pendingCleared == null || seamCleaned == null)
				return UnitTestResult.Fail(
					"fault timeout/oracle failure needs typed fixture, pending and seam cleanup");
			string wiringFailure = AbortWiringFailure(abort);
			if (wiringFailure != null) return UnitTestResult.Fail(wiringFailure);
			FaultRuntimeTargetContext context = RuntimeContext(false, false);
			FaultDeferredDestroyRuntime.StorePending(context);
			SeedArmed(caseId, context.TargetId);
			SeedReceipt(caseId, context.TargetId);
			try
			{
				abort.Invoke(null, new object[] { context });
				bool pendingGone = ThrowsPending();
				bool evidence = !(bool)disposed.GetValue(context.DeferredDestroyEvidence)
				                && (bool)pendingCleared.GetValue(context.DeferredDestroyEvidence)
				                && (bool)seamCleaned.GetValue(context.DeferredDestroyEvidence);
				bool receiptGone = !HasSeamState(caseId);
				return pendingGone && !FaultInjectionUnitySeams.IsArmed(caseId)
				       && receiptGone && evidence
					? UnitTestResult.Pass("Failure cleanup clears runtime state without claiming a null fixture was disposed")
					: UnitTestResult.Fail("failure cleanup leaked fixture, pending state or seam");
			}
			finally
			{
				FaultDeferredDestroyRuntime.ClearPending(context);
				FaultInjectionUnitySeams.Clean(caseId);
			}
		}

		private static IReadOnlyList<(string Property, object Value)> Mutations()
			=> new (string, object)[]
			{
				("FixtureIdentity", "building:other"),
				("DisposableFixtureOwned", false),
				("LogicalTargetIdBefore", string.Empty),
				("LogicalTargetIdAfter", "building:other"),
				("ReplacementInstanceId", 41),
				("DestroyBarrierFrame", 7L),
				("OriginalDestroyed", false),
				("ReplacementBound", false),
				("BaselineHashAfter", Hash('2')),
				("CleanControlInstanceId", 41),
				("CleanControlTargetId", "building:other"),
			};

		private static object Evidence(Type type)
		{
			object value = Activator.CreateInstance(type);
			Set(value, "FixtureIdentity", "building:fixture:17");
			Set(value, "DisposableFixtureOwned", true);
			Set(value, "LogicalTargetIdBefore", "building:fixture:17");
			Set(value, "LogicalTargetIdAfter", "building:fixture:17");
			Set(value, "OriginalInstanceId", 41);
			Set(value, "ReplacementInstanceId", 42);
			Set(value, "DestroyRequestedFrame", 7L);
			Set(value, "DestroyBarrierFrame", 8L);
			Set(value, "OriginalDestroyed", true);
			Set(value, "ReplacementBound", true);
			Set(value, "BaselineHashBefore", Hash('1'));
			Set(value, "BaselineHashAfter", Hash('1'));
			Set(value, "CleanControlInstanceId", 42);
			Set(value, "CleanControlTargetId", "building:fixture:17");
			return value;
		}

		private static FaultRuntimeTargetContext RuntimeContext(
			bool clean, bool includeResetEvidence)
		{
			const string targetId = "building:fixture:17";
			string beforeHash = Hash('1');
			string afterHash = clean ? beforeHash : Hash('2');
			var setup = new FaultRuntimeTargetContext
			{
				TargetId = targetId,
				BaselineHash = beforeHash,
			};
			var evidence = (DeferredDestroyResetEvidence)Evidence(
				typeof(DeferredDestroyResetEvidence));
			if (!includeResetEvidence)
			{
				evidence.ReplacementInstanceId = 0;
				evidence.DestroyBarrierFrame = 0;
				evidence.OriginalDestroyed = false;
				evidence.ReplacementBound = false;
				evidence.BaselineHashBefore = null;
				evidence.BaselineHashAfter = null;
				evidence.CleanControlInstanceId = 0;
				evidence.CleanControlTargetId = null;
			}
			return new FaultRuntimeTargetContext
			{
				CaseId = "building.destroy-deferred",
				TargetId = targetId,
				BaselineHash = beforeHash,
				CleanControl = clean,
				Setup = setup,
				Receipt = new FaultRuntimeReceipt
				{
					ReceiptId = (clean ? "fault-clean-receipt:" : "fault-receipt:")
					            + "building.destroy-deferred",
					TargetId = targetId,
					Consumed = true,
					Succeeded = true,
				},
				Snapshot = new FaultRuntimeSnapshot
				{
					TargetId = targetId,
					StateHash = afterHash,
					InvariantPreserved = true,
				},
				Oracle = new TypedEvidenceEnvelope
				{
					ObservedTargetId = targetId,
					BeforeHash = beforeHash,
					AfterHash = afterHash,
					Passed = true,
					InvariantPreserved = true,
				},
				DeferredDestroyEvidence = evidence,
			};
		}

		private static void SeedArmed(string caseId, string targetId)
		{
			Type seams = typeof(FaultInjectionUnitySeams);
			IDictionary armed = (IDictionary)seams.GetField(
				"Armed", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
			IDictionary targets = (IDictionary)seams.GetField(
				"ArmedTargets", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
			Type mode = seams.GetNestedType("InjectionMode", BindingFlags.NonPublic);
			armed[caseId] = Enum.Parse(mode, "Fault");
			targets[caseId] = targetId;
		}

		private static void SeedReceipt(string caseId, string targetId)
		{
			Type seams = typeof(FaultInjectionUnitySeams);
			IDictionary receipts = (IDictionary)seams.GetField(
				"Receipts", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
			IDictionary targets = (IDictionary)seams.GetField(
				"ReceiptTargets", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
			receipts[caseId] = FaultInjectionReceipt.Pass("runtime", "seeded");
			targets[caseId] = targetId;
		}

		private static bool HasSeamState(string caseId)
		{
			Type seams = typeof(FaultInjectionUnitySeams);
			foreach (string name in new[]
			         {
				         "Armed", "ArmedTargets", "ArmedRuntimeTargets",
				         "Receipts", "ReceiptTargets",
			         })
			{
				FieldInfo field = seams.GetField(
					name, BindingFlags.Static | BindingFlags.NonPublic);
				if (field == null) continue;
				IDictionary values = (IDictionary)field.GetValue(null);
				if (values.Contains(caseId)) return true;
			}
			return false;
		}

		private static string AbortWiringFailure(MethodInfo abort)
		{
			MethodInfo dispose = typeof(FaultDeferredDestroyRuntime).GetMethod(
				"DisposeFixture", BindingFlags.Static | BindingFlags.NonPublic);
			MethodInfo original = ContextGetter("DeferredOriginal");
			MethodInfo replacement = ContextGetter("DeferredReplacement");
			MethodInfo executeFault = typeof(FaultUnityRuntimeDriver).GetMethod(
				"ExecuteFault", BindingFlags.Static | BindingFlags.NonPublic);
			MethodInfo executeLifecycle = typeof(FaultUnityRuntimeDriver).GetMethod(
				"ExecuteLifecycle", BindingFlags.Static | BindingFlags.NonPublic);
			MethodInfo executeCleanPhase = typeof(FaultUnityRuntimeDriver).GetMethod(
				"ExecuteCleanPhase", BindingFlags.Static | BindingFlags.NonPublic);
			MethodInfo executeClean = typeof(FaultDeferredDestroyRuntime).GetMethod(
				"ExecuteClean", BindingFlags.Static | BindingFlags.NonPublic);
			if (dispose == null || dispose.ReturnType != typeof(bool)
			    || !ReflectionExecutionGraph.Reaches(abort, dispose))
				return "Abort does not execute the owned fixture disposal seam";
			if (!ReflectionExecutionGraph.Reaches(dispose, replacement)
			    || !ReflectionExecutionGraph.Reaches(dispose, original))
				return "fixture disposal must observe replacement and original ownership";
			foreach (MethodInfo exit in new[]
			         {
				         executeFault, executeLifecycle, executeCleanPhase, executeClean,
			         })
				if (exit == null || !ReflectionExecutionGraph.Reaches(exit, abort))
					return "runtime failure exit is not wired to Abort: " + exit?.Name;
			return null;
		}

		private static MethodInfo ContextGetter(string name)
			=> typeof(FaultRuntimeTargetContext).GetProperty(name,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
				?.GetGetMethod(nonPublic: true);

		private static bool ThrowsPending()
		{
			try
			{
				FaultDeferredDestroyRuntime.Pending();
				return false;
			}
			catch (InvalidOperationException)
			{
				return true;
			}
		}

		private static bool CallsInOrder(MethodInfo caller, IReadOnlyList<MethodInfo> stages)
		{
			byte[] il = caller.GetMethodBody()?.GetILAsByteArray();
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

		private static FaultUnityProductionBinding Binding()
			=> FaultUnityBindingRegistry.Bindings.Single(binding =>
				binding.CaseId == "building.destroy-deferred");

		private static MethodInfo Method(FaultUnityProductionBinding binding, string name)
			=> binding.GetType().GetProperty(name)?.GetValue(binding) as MethodInfo;

		private static bool Invoke(MethodInfo method, object input)
			=> (bool)method.Invoke(null, new[] { input });

		private static void Set(object owner, string name, object value)
		{
			PropertyInfo property = owner.GetType().GetProperty(name);
			if (property == null || !property.CanWrite)
				throw new InvalidOperationException("Writable reset evidence required: " + name);
			property.SetValue(owner, value);
		}

		private static string Hash(char value) => "sha256:" + new string(value, 64);
	}
}
