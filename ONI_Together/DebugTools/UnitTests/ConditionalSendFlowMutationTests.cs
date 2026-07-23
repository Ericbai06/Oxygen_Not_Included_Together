using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class ConditionalSendFlowMutationTests
	{
		[UnitTest(name: "Scenario conditional send: valid rollback branch is accepted",
			category: "StaticContract")]
		public static UnitTestResult ValidRollbackBranchIsAccepted()
		{
			string failure = ScenarioActionConditionalSendValidator.ValidateHost(
				Contract("HappyConditionalHost", "SendPacketBool"));
			return failure == null ? UnitTestResult.Pass("bool send is consumed and rolls back")
				: UnitTestResult.Fail(failure);
		}

		[UnitTest(name: "Scenario conditional send: ignored bool outcome is rejected",
			category: "StaticContract")]
		public static UnitTestResult IgnoredOutcomeIsRejected()
			=> Reject("IgnoredConditionalHost", "SendPacketBool", "conditionally consumed");

		[UnitTest(name: "Scenario conditional send: missing rollback is rejected",
			category: "StaticContract")]
		public static UnitTestResult MissingRollbackIsRejected()
			=> Reject("MissingRollbackConditionalHost", "SendPacketBool", "rollback");

		[UnitTest(name: "Scenario conditional send: legacy void sender is rejected",
			category: "StaticContract")]
		public static UnitTestResult VoidSenderIsRejected()
			=> Reject("HappyHost", "SendPacket", "incomplete");

		private static UnitTestResult Reject(
			string host, string send, string expected)
		{
			string failure = ScenarioActionConditionalSendValidator.ValidateHost(
				Contract(host, send));
			return failure?.Contains(expected) == true
				? UnitTestResult.Pass("rejected: " + failure)
				: UnitTestResult.Fail("expected " + expected + ", actual=" + failure);
		}

		private static LinearMethodFlowContract Contract(string host, string send)
		{
			MethodInfo restore = ScenarioActionFlowFixtures.M("Restore");
			var sendCall = new FlowCallContract(
				ScenarioActionFlowFixtures.M(send), null, "packet")
			{
				OutcomePolicy = FlowCallOutcomePolicy.ConditionalRollback,
				FailureMethod = restore,
			};
			return new LinearMethodFlowContract(
				ScenarioActionFlowFixtures.M(host), new[] { "command" }, new[]
				{
					C("ConditionalPrefix", "mutation", "command", "packet"), sendCall,
				}) { ReturnToken = "mutation" };
		}

		private static FlowCallContract C(
			string method, string output, params string[] inputs)
			=> new(ScenarioActionFlowFixtures.M(method), output, inputs);
	}
}
