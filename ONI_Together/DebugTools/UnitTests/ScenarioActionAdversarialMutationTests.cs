namespace ONI_Together.DebugTools.UnitTests
{
	public static class ScenarioActionAdversarialMutationTests
	{
		[UnitTest(name: "Scenario adversarial: valid receiver transaction is accepted",
			category: "StaticContract")]
		public static UnitTestResult ValidTraceIsAccepted()
		{
			var trace = Valid();
			string failure = ScenarioActionAdversarialContract.Validate(trace);
			bool provenance = trace.EvidenceGeneration == 7
			                  && trace.EvidenceCorrelation == "action-42"
			                  && trace.EvidenceSequence == 1;
			return failure == null && provenance
				? UnitTestResult.Pass("valid trace accepted with gate provenance")
				: UnitTestResult.Fail(failure);
		}

		[UnitTest(name: "Scenario adversarial: unseen old generation is rejected before current",
			category: "StaticContract")]
		public static UnitTestResult OldGenerationFirstIsRejected()
		{
			ScenarioActionExpectedAdmission expected = Expected();
			ScenarioActionAdmissionResult old = ScenarioActionReceiverAdmissionContract.TryEnter(
				expected, Token(6, "action-42", 1));
			ScenarioActionAdmissionResult current = ScenarioActionReceiverAdmissionContract.TryEnter(
				expected, Token(7, "action-42", 1));
			bool passed = old.State == ScenarioActionAdmissionState.GenerationMismatch
			              && current.Accepted && expected.LastAcceptedSequence == 1;
			return passed ? UnitTestResult.Pass("old-first rejected; current generation accepted")
				: UnitTestResult.Fail("unseen old generation poisoned current admission");
		}

		[UnitTest(name: "Scenario adversarial: monotonic-only old-first mutant is rejected",
			category: "StaticContract")]
		public static UnitTestResult MonotonicOnlyOldFirstIsRejected()
		{
			var trace = Valid();
			trace.Admission = new ScenarioActionAdmissionResult
			{
				State = ScenarioActionAdmissionState.Accepted,
				Generation = 6, Correlation = "action-42", Sequence = 1,
			};
			trace.EvidenceGeneration = 6;
			return Reject(trace, "does not match armed action");
		}

		[UnitTest(name: "Scenario adversarial: wrong action correlation is rejected first",
			category: "StaticContract")]
		public static UnitTestResult WrongCorrelationFirstIsRejected()
		{
			ScenarioActionExpectedAdmission expected = Expected();
			ScenarioActionAdmissionResult wrong = ScenarioActionReceiverAdmissionContract.TryEnter(
				expected, Token(7, "action-previous", 1));
			ScenarioActionAdmissionResult current = ScenarioActionReceiverAdmissionContract.TryEnter(
				expected, Token(7, "action-42", 1));
			bool passed = wrong.State == ScenarioActionAdmissionState.CorrelationMismatch
			              && current.Accepted && expected.LastAcceptedSequence == 1;
			return passed ? UnitTestResult.Pass("wrong correlation rejected; armed action accepted")
				: UnitTestResult.Fail("wrong correlation poisoned armed action admission");
		}

		[UnitTest(name: "Scenario adversarial: bind-existing pickup restore is rejected",
			category: "StaticContract")]
		public static UnitTestResult BindExistingOnlyIsRejected()
		{
			var trace = Valid();
			trace.BindExistingOnly = true;
			return Reject(trace, "rematerialized");
		}

		[UnitTest(name: "Scenario adversarial: extra Harmony membership send is rejected",
			category: "StaticContract")]
		public static UnitTestResult ExtraHarmonySendIsRejected()
		{
			var trace = Valid();
			trace.ExtraMembershipSend = true;
			return Reject(trace, "outside the exact packet sequence");
		}

		[UnitTest(name: "Scenario adversarial: unmarked local evidence is rejected",
			category: "StaticContract")]
		public static UnitTestResult UnmarkedEvidenceIsRejected()
		{
			var trace = Valid();
			trace.Admission = null;
			return Reject(trace, "accepted receiver delivery");
		}

		[UnitTest(name: "Scenario adversarial: duplicate evidence is rejected",
			category: "StaticContract")]
		public static UnitTestResult DuplicateEvidenceIsRejected()
		{
			var trace = Valid();
			ScenarioActionExpectedAdmission expected = Expected();
			ScenarioActionReceiverAdmissionContract.TryEnter(
				expected, Token(7, "action-42", 1));
			trace.Admission = ScenarioActionReceiverAdmissionContract.TryEnter(
				expected, Token(7, "action-42", 1));
			return Reject(trace, "accepted receiver delivery");
		}

		[UnitTest(name: "Scenario adversarial: out-of-order evidence is rejected",
			category: "StaticContract")]
		public static UnitTestResult OutOfOrderEvidenceIsRejected()
		{
			var trace = Valid();
			ScenarioActionExpectedAdmission expected = Expected();
			ScenarioActionReceiverAdmissionContract.TryEnter(
				expected, Token(7, "action-42", 2));
			trace.Admission = ScenarioActionReceiverAdmissionContract.TryEnter(
				expected, Token(7, "action-42", 1));
			return Reject(trace, "accepted receiver delivery");
		}

		[UnitTest(name: "Scenario adversarial: null mutation side effects are rejected",
			category: "StaticContract")]
		public static UnitTestResult NullMutationSideEffectsAreRejected()
		{
			var trace = Valid();
			trace.MutationCreated = false;
			return Reject(trace, "null mutation");
		}

		[UnitTest(name: "Scenario adversarial: missing rollback is rejected",
			category: "StaticContract")]
		public static UnitTestResult MissingRollbackIsRejected()
		{
			var trace = Valid();
			trace.DownstreamFailed = true;
			trace.RolledBack = false;
			return Reject(trace, "left host mutation applied");
		}

		[UnitTest(name: "Scenario adversarial: pickup cleanup zero target is rejected",
			category: "StaticContract")]
		public static UnitTestResult PickupCleanupZeroTargetIsRejected()
		{
			var trace = Valid();
			trace.CleanupWireTargetCell = 0;
			return Reject(trace, "target cell is missing");
		}

		[UnitTest(name: "Scenario adversarial: pickup cleanup client target drift is rejected",
			category: "StaticContract")]
		public static UnitTestResult PickupCleanupClientTargetDriftIsRejected()
		{
			var trace = Valid();
			trace.CleanupClientTargetCell = 95029;
			return Reject(trace, "canonical target drifted");
		}

		[UnitTest(name: "Scenario adversarial: pickup cleanup host target drift is rejected",
			category: "StaticContract")]
		public static UnitTestResult PickupCleanupHostTargetDriftIsRejected()
		{
			var trace = Valid();
			trace.CleanupHostTargetCell = 95027;
			return Reject(trace, "canonical target drifted");
		}

		[UnitTest(name: "Scenario adversarial: rocket send failure outcome is consumed",
			category: "StaticContract")]
		public static UnitTestResult RocketIgnoredFailureOutcomeIsRejected()
		{
			var trace = Valid();
			trace.DownstreamFailed = true;
			trace.RolledBack = true;
			trace.FailureOutcomeConsumed = false;
			trace.FailureDomain = "rocket";
			return Reject(trace, "rocket downstream failure outcome was ignored");
		}

		[UnitTest(name: "Scenario adversarial: DLC send failure rolls back mutation",
			category: "StaticContract")]
		public static UnitTestResult DlcFailureWithoutRollbackIsRejected()
		{
			var trace = Valid();
			trace.DownstreamFailed = true;
			trace.RolledBack = false;
			trace.FailureDomain = "dlc-runtime";
			return Reject(trace, "left host mutation applied");
		}

		private static ScenarioActionAdversarialTrace Valid()
		{
			ScenarioActionAdmissionResult admission =
				ScenarioActionReceiverAdmissionContract.TryEnter(
					Expected(), Token(7, "action-42", 1));
			return new ScenarioActionAdversarialTrace
			{
				Tombstoned = true, Materialized = true, ReplicationSuppressed = true,
				ExpectedGeneration = 7, ExpectedCorrelation = "action-42",
				Admission = admission, EvidenceObserved = true,
				EvidenceGeneration = admission.Generation,
				EvidenceCorrelation = admission.Correlation,
				EvidenceSequence = admission.Sequence,
				MutationCreated = true, HostObserved = true, PacketSent = true,
				RolledBack = true, FailureOutcomeConsumed = true,
				CleanupHostTargetCell = 95028, CleanupWireTargetCell = 95028,
				CleanupClientTargetCell = 95028,
			};
		}

		private static ScenarioActionExpectedAdmission Expected()
			=> new() { Armed = true, Generation = 7, Correlation = "action-42" };

		private static ScenarioActionAdmissionToken Token(
			long generation, string correlation, long sequence)
			=> new()
			{
				Generation = generation, Correlation = correlation, Sequence = sequence,
			};

		private static UnitTestResult Reject(
			ScenarioActionAdversarialTrace trace, string expected)
		{
			string failure = ScenarioActionAdversarialContract.Validate(trace);
			return failure?.Contains(expected) == true
				? UnitTestResult.Pass("rejected: " + failure)
				: UnitTestResult.Fail("expected " + expected + ", actual=" + failure);
		}
	}
}
