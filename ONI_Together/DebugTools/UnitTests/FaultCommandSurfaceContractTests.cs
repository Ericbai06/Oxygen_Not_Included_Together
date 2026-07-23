using System;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class FaultCommandSurfaceContractTests
	{
		[UnitTest(name: "Fault deferred destroy: uncommitted ownership is executable",
			category: "Integration")]
		public static UnitTestResult UncommittedClaimExistsBeforeComponentValidation()
		{
			MethodInfo track = RuntimeMethod("TrackUncommittedFixture");
			MethodInfo claim = RuntimeMethod("ClaimUncommittedOwnership");
			if (claim == null || claim.ReturnType != typeof(void)
			    || claim.GetParameters().Length != 1
			    || claim.GetParameters()[0].ParameterType != typeof(FaultRuntimeTargetContext)
			    || track == null || !ReflectionExecutionGraph.Reaches(track, claim))
				return UnitTestResult.Fail(
					"TrackUncommittedFixture needs a pure executable ownership claim");
			var context = new FaultRuntimeTargetContext
			{
				CaseId = "building.destroy-deferred",
			};
			claim.Invoke(null, new object[] { context });
			DeferredDestroyResetEvidence evidence = context.DeferredDestroyEvidence;
			return evidence != null && evidence.DisposableFixtureOwned
				? UnitTestResult.Pass("Build ownership exists before component validation")
				: UnitTestResult.Fail("tracked-only fixture is not marked as owned");
		}

		[UnitTest(name: "Fault command surface: cleanup stage and typed state survive logging",
			category: "Integration")]
		public static UnitTestResult CleanupPendingSerializesStableTypedOutcome()
		{
			const string command = "fault-clean:building.destroy-deferred";
			const string targetId = "building:fixture:tile:17";
			MethodInfo convert = typeof(DebugCommandOutcome).GetMethod(
				"FromFaultReceipt", BindingFlags.Static | BindingFlags.NonPublic);
			FaultRuntimeTargetContext context = CleanupReceiptContext(targetId);
			MethodInfo defer = RuntimeMethod("DeferCleanup");
			if (convert == null || defer == null)
				return UnitTestResult.Fail("fault receipt needs a structured command converter");
			var receipt = (FaultInjectionReceipt)defer.Invoke(null, new object[] { context });
			var outcome = (DebugCommandOutcome)convert.Invoke(
				null, new object[] { command, receipt });
			FaultRuntimeTargetContext alternate = CleanupReceiptContext(
				"building:fixture:tile:23");
			var alternateReceipt = (FaultInjectionReceipt)defer.Invoke(
				null, new object[] { alternate });
			var alternateOutcome = (DebugCommandOutcome)convert.Invoke(
				null, new object[] { command, alternateReceipt });
			string expected = "[DebugCommand][FAIL] command=fault-clean:building.destroy-deferred "
			                  + "receiptId=fault-clean-receipt:building.destroy-deferred "
			                  + "caseId=building.destroy-deferred targetId=" + targetId + " "
			                  + "consumed=true passed=false stage=cleanup-pending "
			                  + "fixtureDisposeRequested=true "
			                  + "fixtureDisposeRequestedFrame=7 fixtureDisposeObservedFrame=7 "
			                  + "fixtureAbsent=false reason=fixture-absence-not-observed:"
			                  + "building.destroy-deferred";
			string alternateLog = alternateOutcome.ToLogLine();
			bool contextIdentityPreserved = alternateLog.Contains(
				"targetId=building:fixture:tile:23 ")
				&& !alternateLog.Contains("targetId=" + targetId + " ");
			return outcome.ToLogLine() == expected && contextIdentityPreserved
				? UnitTestResult.Pass("Cleanup pending typed outcome survives the real log surface")
				: UnitTestResult.Fail(
					"fault command log dropped receipt-context identity or typed cleanup state");
		}

		private static FaultRuntimeTargetContext CleanupReceiptContext(string targetId)
		{
			const string caseId = "building.destroy-deferred";
			var setup = new FaultRuntimeTargetContext { CaseId = caseId, TargetId = targetId };
			return new FaultRuntimeTargetContext
			{
				CaseId = caseId,
				TargetId = targetId,
				Setup = setup,
				Receipt = new FaultRuntimeReceipt
				{
					ReceiptId = "fault-clean-receipt:" + caseId,
					TargetId = targetId,
					Consumed = true,
					Succeeded = true,
				},
				DeferredDestroyEvidence = new DeferredDestroyResetEvidence
				{
					FixtureIdentity = targetId,
					DisposableFixtureOwned = true,
					LogicalTargetIdBefore = targetId,
					LogicalTargetIdAfter = targetId,
					FixtureDisposeRequested = true,
					FixtureDisposeRequestedFrame = 7,
					FixtureDisposeObservedFrame = 7,
					FixtureAbsent = false,
				},
			};
		}

		private static MethodInfo RuntimeMethod(string name)
			=> typeof(FaultDeferredDestroyRuntime).GetMethod(
				name, BindingFlags.Static | BindingFlags.NonPublic);
	}
}
