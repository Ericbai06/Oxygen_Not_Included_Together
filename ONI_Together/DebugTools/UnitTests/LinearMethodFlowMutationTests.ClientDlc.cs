using System.Linq;

namespace ONI_Together.DebugTools.UnitTests
{
	public static partial class LinearMethodFlowMutationTests
	{
		[UnitTest(name: "Scenario action flow: client apply must feed an independent oracle",
			category: "StaticContract")]
		public static UnitTestResult NoOpClientOracleIsRejected()
		{
			var contract = Contract("NoOracleClient", "state", new[]
			{
				C("ApplyPacket", "state", "packet"),
				C("ObserveClientState", null, "state"),
			});
			return ExpectFailure(contract, "omitted stage");
		}

		[UnitTest(name: "Scenario action flow: dispatch passes the same packet instance",
			category: "StaticContract")]
		public static UnitTestResult SameDispatchPacketIsAccepted()
			=> ReflectionExecutionGraph.DirectlyPassesThisTo(
				ScenarioActionFlowFixtures.D("HappyDispatch"),
				ScenarioActionFlowFixtures.M("AcceptDispatch"))
				? UnitTestResult.Pass("dispatch forwarded this packet")
				: UnitTestResult.Fail("dispatch did not forward this packet");

		[UnitTest(name: "Scenario action flow: dispatch rejects another packet instance",
			category: "StaticContract")]
		public static UnitTestResult OtherDispatchPacketIsRejected()
			=> !ReflectionExecutionGraph.DirectlyPassesThisTo(
				ScenarioActionFlowFixtures.D("WrongDispatch"),
				ScenarioActionFlowFixtures.M("AcceptDispatch"))
				? UnitTestResult.Pass("dispatch rejected a replacement packet")
				: UnitTestResult.Fail("dispatch accepted a replacement packet");

		[UnitTest(name: "Scenario action flow: cleanup must reverse the original mutation",
			category: "StaticContract")]
		public static UnitTestResult UnrelatedCleanupMutationIsRejected()
		{
			var contract = Contract("WrongCleanup", "state", new[]
			{
				C("Restore", "state", "mutation"),
				C("ObserveCleanupState", null, "state"),
				C("CreateCleanupPacket", "packet", "state"),
				C("SendCleanupPacket", null, "packet"),
			});
			return ExpectFailure(contract, "stage order mismatch");
		}

		[UnitTest(name: "Scenario action flow: DLC fixture must attach to returned target",
			category: "StaticContract")]
		public static UnitTestResult DlcFixtureOnOtherObjectIsRejected()
		{
			var contract = Contract("AttachFixtureToOther", "target", new[]
			{
				C("AddFixture", null, "target"),
			});
			return ExpectFailure(contract, "stage order mismatch");
		}

		[UnitTest(name: "Scenario action flow: DLC transition From and To are ordered",
			category: "StaticContract")]
		public static UnitTestResult DlcUnsafeTransitionIsRejected()
		{
			var contract = Contract("WrongDlcState", "mutation", new[]
			{
				C("Prepare", "prepared", "command"),
				C("Resolve", "target", "prepared"),
				C("AttachFixture", "attached", "target"),
				C("Transition", "mutation", "attached",
					"literal:RobotIdleMonitor.idle", "literal:RobotIdleMonitor.working"),
			});
			return ExpectFailure(contract, "wrong artifact instance");
		}

		[UnitTest(name: "Scenario action flow: valid client cleanup and DLC flows are accepted",
			category: "StaticContract")]
		public static UnitTestResult ValidSecondaryFlowsAreAccepted()
		{
			LinearMethodFlowContract[] contracts =
			{
				Contract("HappyClient", "state", new[]
				{
					C("ApplyPacket", "state", "packet"),
					C("ObserveClientState", null, "state"),
				}),
				Contract("HappyCleanup", "state", new[]
				{
					C("Restore", "state", "mutation"),
					C("ObserveCleanupState", null, "state"),
					C("CreateCleanupPacket", "packet", "state"),
					C("SendCleanupPacket", null, "packet"),
				}),
				Contract("AttachFixture", "target", new[]
				{
					C("AddFixture", null, "target"),
				}),
				Contract("HappyDlc", "mutation", new[]
				{
					C("Prepare", "prepared", "command"),
					C("Resolve", "target", "prepared"),
					C("AttachFixture", "attached", "target"),
					C("Transition", "mutation", "attached",
						"literal:RobotIdleMonitor.idle", "literal:RobotIdleMonitor.working"),
				}),
			};
			foreach (LinearMethodFlowContract contract in contracts)
			{
				string failure = LinearMethodFlowValidator.Validate(contract);
				if (failure != null) return UnitTestResult.Fail(failure);
			}
			return UnitTestResult.Pass("client, cleanup and DLC artifact flows are linear and bound");
		}

		private static LinearMethodFlowContract Contract(
			string method, string returnToken, params FlowCallContract[] calls)
		{
			System.Reflection.MethodInfo execution = ScenarioActionFlowFixtures.M(method);
			string[] arguments = execution.GetParameters()
				.Select(parameter => parameter.Name).ToArray();
			return new LinearMethodFlowContract(execution, arguments, calls)
			{
				ReturnToken = returnToken,
			};
		}

		private static FlowCallContract C(
			string method, string output, params string[] inputs)
			=> new(ScenarioActionFlowFixtures.M(method), output, inputs);

		private static UnitTestResult ExpectFailure(
			LinearMethodFlowContract contract, string expected)
		{
			string failure = LinearMethodFlowValidator.Validate(contract);
			return failure?.IndexOf(expected, System.StringComparison.Ordinal) >= 0
				? UnitTestResult.Pass("rejected: " + failure)
				: UnitTestResult.Fail("expected " + expected + ", actual=" + failure);
		}
	}
}
