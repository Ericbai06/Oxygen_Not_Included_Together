using System;
using System.Collections.Generic;
using System.Linq;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class FaultInjectionMatrixTests
	{
		[UnitTest(name: "Fault injection: registry exactly covers required matrix", category: "Integration")]
		public static UnitTestResult RegistryExactlyCoversRequiredMatrix()
		{
			IReadOnlyList<FaultInjectionCase> actual = FaultInjectionRegistry.Cases;
			IReadOnlyList<ExpectedFaultCase> expected = FaultInjectionMatrixFixture.Cases;
			string[] expectedIds = expected.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
			string[] actualIds = actual.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
			if (!expectedIds.SequenceEqual(actualIds, StringComparer.Ordinal))
				return UnitTestResult.Fail("Fault registry IDs differ from the required matrix");
			if (actualIds.Distinct(StringComparer.Ordinal).Count() != actualIds.Length)
				return UnitTestResult.Fail("Fault registry contains duplicate IDs");

			foreach (ExpectedFaultCase expectedCase in expected)
			{
				FaultInjectionCase actualCase = actual.Single(item => item.Id == expectedCase.Id);
				string mismatch = ContractMismatch(expectedCase, actualCase);
				if (mismatch != null)
					return UnitTestResult.Fail(expectedCase.Id + ": " + mismatch);
			}

			return UnitTestResult.Pass("All required fault cases have exact executable metadata");
		}

		[UnitTest(name: "Fault injection: pure cases execute red green clean-control", category: "Integration")]
		public static UnitTestResult HeadlessCasesExecuteFaultResetAndCleanControl()
		{
			foreach (ExpectedFaultCase expected in FaultInjectionMatrixFixture.Cases
				         .Where(item => item.ExecutionTier == "headless"))
			{
				FaultInjectionExecution actual = FaultInjectionController.ExecuteHeadless(expected.Id);
				if (expected.Id != actual.CaseId)
					return UnitTestResult.Fail($"Expected case ID {expected.Id}, actual {actual.CaseId}");
				if (!actual.OracleObserved)
					return UnitTestResult.Fail(expected.Id + ": expected fault oracle was not observed");
				if (!actual.InvariantPreserved)
					return UnitTestResult.Fail(expected.Id + ": expected state invariant was not preserved");
				if (!actual.ResetCompleted)
					return UnitTestResult.Fail(expected.Id + ": expected reset did not complete");
				if (!actual.CleanControlPassed)
					return UnitTestResult.Fail(expected.Id + ": expected clean-control rerun did not pass");
			}

			return UnitTestResult.Pass("Every pure fault executes fault, reset, and clean control headlessly");
		}

		[UnitTest(name: "Fault injection: Unity-only cases bind executable game tests", category: "Integration")]
		public static UnitTestResult UnityCasesBindConcreteExecutionHandlers()
		{
			foreach (ExpectedFaultCase expected in FaultInjectionMatrixFixture.Cases
				         .Where(item => item.ExecutionTier != "headless"))
			{
				string requiredPrefix = expected.ExecutionTier + ":";
				if (!expected.TestId.StartsWith(requiredPrefix, StringComparison.Ordinal))
					return UnitTestResult.Fail($"Expected {requiredPrefix} test ID, actual {expected.TestId}");
				if (!FaultInjectionHandlerRegistry.TryResolve(expected.Id, out FaultInjectionHandler handler)
				    || handler == null)
					return UnitTestResult.Fail(expected.Id + ": no executable Unity fault handler");
				if (!FaultInjectionHandlerRegistry.TryResolveCleanControl(
					    expected.Id, out FaultInjectionHandler cleanControl) || cleanControl == null)
					return UnitTestResult.Fail(expected.Id + ": no executable Unity clean-control handler");
			}

			return UnitTestResult.Pass("Every Unity-only case maps to a concrete game test and handler pair");
		}

		private static string ContractMismatch(ExpectedFaultCase expected, FaultInjectionCase actual)
		{
			if (expected.Domain != actual.Domain) return "domain mismatch";
			if (expected.InjectionSeam != actual.InjectionSeam) return "injection seam mismatch";
			if (expected.InjectionValue != actual.InjectionValue) return "injection value mismatch";
			if (expected.ExpectedOracle != actual.ExpectedOracle) return "oracle mismatch";
			if (expected.StateInvariant != actual.StateInvariant) return "state invariant mismatch";
			if (expected.Reset != actual.Reset) return "reset mismatch";
			if (expected.CleanControl != actual.CleanControl) return "clean-control mismatch";
			if (expected.TestId != actual.TestId) return "test ID mismatch";
			if (expected.ScenarioId != actual.ScenarioId) return "scenario ID mismatch";
			if (expected.ExecutionTier != actual.ExecutionTier) return "execution tier mismatch";
			return null;
		}
	}
}
