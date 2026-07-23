using System.Linq;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class TypedEvidenceScenarioTests
	{
		private static readonly string[] ExpectedScenarios =
		{
			"remote-dig", "building-lifecycle", "research", "priority", "schedule",
			"building-config", "door", "uproot", "toggle", "inventory", "storage",
			"pickup", "deconstruct", "effect", "chat", "cursor", "animation", "motion",
			"entity-lifecycle", "dlc-runtime", "rocket", "reconnect-world-state",
		};

		[UnitTest(name: "Typed evidence schema registry is exactly the 22 real scenarios", category: "Integration")]
		public static UnitTestResult RegistryIsExact()
		{
			string[] actual = TypedEvidenceContract.Scenarios.ToArray();
			return ExpectedScenarios.SequenceEqual(actual) && actual.Distinct().Count() == 22
				? UnitTestResult.Pass("Typed evidence registry contains exactly 22 unique scenarios")
				: UnitTestResult.Fail("Typed evidence schema registry differs from the real scenario matrix");
		}

		[UnitTest(name: "Typed evidence rejects target and state classes from another scenario", category: "Integration")]
		public static UnitTestResult ScenarioMustMatchConcreteTargetAndState()
		{
			TypedEvidenceEnvelope evidence = TypedEvidenceTestFixture.RemoteDigEnvelope();
			evidence.Scenario = "research";
			evidence.EntryId = "sync:test:research";
			evidence.RevisionDomain = "research";
			return TypedEvidenceContract.Validate(evidence).Count == 0
				? UnitTestResult.Fail("Research accepted remote-dig target and state classes")
				: UnitTestResult.Pass("Scenario selects and enforces its concrete target/state contract");
		}
	}
}
