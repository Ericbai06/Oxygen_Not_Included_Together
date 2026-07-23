namespace ONI_Together.DebugTools.UnitTests
{
	public static partial class LinearMethodFlowMutationTests
	{
		[UnitTest(name: "Scenario action flow: valid host artifact chain is accepted",
			category: "StaticContract")]
		public static UnitTestResult ValidHostFlowIsAccepted()
		{
			string failure = LinearMethodFlowValidator.Validate(HostContract("HappyHost"));
			return failure == null
				? UnitTestResult.Pass("host target, mutation, packet and send share one flow")
				: UnitTestResult.Fail(failure);
		}

		[UnitTest(name: "Scenario action flow: dormant branch is rejected",
			category: "StaticContract")]
		public static UnitTestResult DormantBranchIsRejected()
			=> ExpectFailure(HostContract("DormantHost"), "branch");

		[UnitTest(name: "Scenario action flow: discarded resolver output is rejected",
			category: "StaticContract")]
		public static UnitTestResult DiscardedResolverOutputIsRejected()
		{
			var contract = Contract("DiscardedResolverHost", "mutation",
				new[]
				{
					C("Prepare", "prepared", "command"),
					C("Resolve", "target", "prepared"),
					C("Mutate", "mutation", "target"),
				});
			return ExpectFailure(contract, "wrong artifact instance");
		}

		[UnitTest(name: "Scenario action flow: semantic stages cannot self-satisfy",
			category: "StaticContract")]
		public static UnitTestResult ReusedStageMethodIsRejected()
		{
			var contract = Contract("HappyHost", "target",
				new[]
				{
					C("Prepare", "prepared", "command"),
					C("Prepare", "target", "prepared"),
				});
			return ExpectFailure(contract, "cannot reuse");
		}

		[UnitTest(name: "Scenario action flow: expected packet cannot be discarded",
			category: "StaticContract")]
		public static UnitTestResult WrongPacketSendIsRejected()
		{
			var contract = Contract("WrongPacketHost", "mutation",
				new[]
				{
					C("Prepare", "prepared", "command"),
					C("Resolve", "target", "prepared"),
					C("Mutate", "mutation", "target"),
					C("CreatePacket", "packet", "mutation"),
					C("SendPacket", null, "packet"),
				});
			return ExpectFailure(contract, "stage order mismatch");
		}

		[UnitTest(name: "Scenario action flow: local-only mutation is rejected",
			category: "StaticContract")]
		public static UnitTestResult LocalOnlyMutationIsRejected()
		{
			var contract = Contract("LocalOnlyHost", "mutation",
				new[]
				{
					C("Prepare", "prepared", "command"),
					C("Resolve", "target", "prepared"),
					C("Mutate", "mutation", "target"),
					C("CreatePacket", "packet", "mutation"),
					C("SendPacket", null, "packet"),
				});
			return ExpectFailure(contract, "omitted stage");
		}

		[UnitTest(name: "Scenario action flow: ordered packet sequence is accepted",
			category: "StaticContract")]
		public static UnitTestResult OrderedPacketSequenceIsAccepted()
		{
			string failure = LinearMethodFlowValidator.Validate(
				SequenceContract("HappySequenceHost"));
			return failure == null
				? UnitTestResult.Pass("ordered packets share the original mutation")
				: UnitTestResult.Fail(failure);
		}

		[UnitTest(name: "Scenario action flow: reordered packet sequence is rejected",
			category: "StaticContract")]
		public static UnitTestResult ReorderedPacketSequenceIsRejected()
			=> ExpectFailure(SequenceContract("ReorderedSequenceHost"), "stage order mismatch");

		[UnitTest(name: "Scenario action flow: replacement packet identity is rejected",
			category: "StaticContract")]
		public static UnitTestResult ReplacementPacketIdentityIsRejected()
			=> ExpectFailure(SequenceContract("ReplacementSequenceHost"), "stage order mismatch");

		private static LinearMethodFlowContract HostContract(string method)
		{
			return Contract(method, "mutation", new[]
			{
				C("Prepare", "prepared", "command"),
				C("Resolve", "target", "prepared"),
				C("Mutate", "mutation", "target"),
				C("CaptureState", "state", "mutation"),
				C("ObserveHostState", null, "state"),
				C("CreatePacket", "packet", "mutation"),
				C("SendPacket", null, "packet"),
			});
		}

		private static LinearMethodFlowContract SequenceContract(string method)
			=> Contract(method, "mutation", new[]
			{
				C("Prepare", "prepared", "command"),
				C("Resolve", "target", "prepared"),
				C("Mutate", "mutation", "target"),
				C("CreatePacket", "packet:0", "mutation"),
				C("SendPacket", null, "packet:0"),
				C("CreatePacket2", "packet:1", "mutation"),
				C("SendPacket2", null, "packet:1"),
			});
	}
}
