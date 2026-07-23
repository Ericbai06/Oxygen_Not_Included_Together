using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class TransactionalSendPathProductionTests
	{
		[UnitTest(name: "Scenario transactional: Rocket and DLC host paths fail closed", category: "StaticContract")]
		public static UnitTestResult RocketAndDlcHostsFailClosed()
		{
			var failures = new List<string>();
			foreach (string name in new[] { "RocketActionFlow", "DlcRuntimeActionFlow" })
			{
				Type flow = Flow(name);
				string failure = TransactionalSendPathValidator.ValidateSingleHost(
					M(flow, "ExecuteHost"), M(flow, "Send"), M(flow, "ObserveHost"), M(flow, "Restore"));
				if (failure != null) failures.Add(name + ": " + failure);
			}
			return Result(failures, "Rocket/DLC host send, rollback, and evidence are fail-closed");
		}

		[UnitTest(name: "Scenario transactional: Rocket and DLC cleanup paths fail closed", category: "StaticContract")]
		public static UnitTestResult RocketAndDlcCleanupFailClosed()
		{
			var failures = new List<string>();
			foreach (string name in new[] { "RocketActionFlow", "DlcRuntimeActionFlow" })
			{
				Type flow = Flow(name);
				string failure = TransactionalSendPathValidator.ValidateSingleCleanup(
					M(flow, "ExecuteCleanup"), M(flow, "Restore"), M(flow, "Send"),
					M(flow, "ObserveCleanup"));
				if (failure != null) failures.Add(name + ": " + failure);
			}
			return Result(failures, "Rocket/DLC cleanup retains receipt on send failure");
		}

		[UnitTest(name: "Scenario transactional: Pickup host consumes both packet outcomes", category: "StaticContract")]
		public static UnitTestResult PickupHostConsumesBothPackets()
		{
			Type flow = Flow("PickupActionFlow");
			MethodInfo compensation = M(flow, "CompensateHostSecondSend");
			if (compensation == null)
				return UnitTestResult.Fail("PickupActionFlow: CompensateHostSecondSend is missing");
			string failure = TransactionalSendPathValidator.ValidatePickup(
				M(flow, "ExecuteHost"), new[] { M(flow, "SendPickup"), M(flow, "SendSpawn") },
				M(flow, "ObserveHost"), M(flow, "Restore"), compensation);
			return failure == null ? UnitTestResult.Pass("Pickup host two-packet transaction is fail-closed")
				: UnitTestResult.Fail("PickupActionFlow: " + failure);
		}

		[UnitTest(name: "Scenario transactional: Pickup cleanup consumes both packet outcomes", category: "StaticContract")]
		public static UnitTestResult PickupCleanupConsumesBothPackets()
		{
			Type flow = Flow("PickupActionFlow");
			MethodInfo compensation = M(flow, "CompensateCleanupSecondSend");
			if (compensation == null)
				return UnitTestResult.Fail("PickupActionFlow: CompensateCleanupSecondSend is missing");
			string failure = TransactionalSendPathValidator.ValidatePickup(
				M(flow, "ExecuteCleanup"), new[] { M(flow, "SendSpawn"), M(flow, "SendStorage") },
				M(flow, "ObserveCleanup"), M(flow, "Restore"), compensation);
			return failure == null ? UnitTestResult.Pass("Pickup cleanup two-packet transaction is fail-closed")
				: UnitTestResult.Fail("PickupActionFlow: " + failure);
		}

		private static UnitTestResult Result(ICollection<string> failures, string passed)
			=> failures.Count == 0 ? UnitTestResult.Pass(passed)
				: UnitTestResult.Fail(string.Join(", ", failures));

		private static Type Flow(string name)
			=> Assembly.GetType("ONI_Together.Networking." + name);

		private static MethodInfo M(Type type, string name)
			=> type?.GetMethods(BindingFlags.Static | BindingFlags.Instance
					| BindingFlags.Public | BindingFlags.NonPublic)
				.FirstOrDefault(method => method.Name == name);

		private static Assembly Assembly => typeof(ScenarioActionHandlerRegistry).Assembly;
	}
}
