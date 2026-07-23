using System.Reflection;
using System.Linq;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class ReflectionExecutionGraphTests
	{
		[UnitTest(name: "Scenario action chain: reflection graph follows real calls only",
			category: "StaticContract")]
		public static UnitTestResult ConnectedAndDisconnectedCallsAreDistinguished()
		{
			MethodInfo root = Method(nameof(ConnectedRoot));
			MethodInfo leaf = Method(nameof(ConnectedLeaf));
			MethodInfo disconnected = Method(nameof(Disconnected));
			if (!ReflectionExecutionGraph.Reaches(root, leaf))
				return UnitTestResult.Fail("transitive method call was not discovered");
			return ReflectionExecutionGraph.Reaches(root, disconnected)
				? UnitTestResult.Fail("disconnected method was treated as reachable")
				: UnitTestResult.Pass("reflection graph distinguishes connected methods");
		}

		[UnitTest(name: "Scenario action chain: reflection graph observes constructed types",
			category: "StaticContract")]
		public static UnitTestResult ConstructedTypesAreObserved()
		{
			return ReflectionExecutionGraph.ReachesType(
					Method(nameof(CreateMarker)), typeof(GraphMarker))
				? UnitTestResult.Pass("constructed type is visible in the execution graph")
				: UnitTestResult.Fail("constructed type was not discovered");
		}

		[UnitTest(name: "Scenario action chain: real production PacketSender is recognized",
			category: "StaticContract")]
		public static UnitTestResult ProductionPacketSenderSymbolIsRecognized()
		{
			MethodInfo sender = typeof(global::ONI_Together.Networking.PacketSender)
				.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
				.First(method => method.Name == "SendToAllClients");
			return ReflectionExecutionGraph.ReachesPacketSender(sender)
				? UnitTestResult.Pass("recognized ONI_Together.Networking.PacketSender")
				: UnitTestResult.Fail("real ONI_Together.Networking.PacketSender was rejected");
		}

		private static MethodInfo Method(string name)
			=> typeof(ReflectionExecutionGraphTests).GetMethod(
				name, BindingFlags.Static | BindingFlags.NonPublic);

		private static void ConnectedRoot() => ConnectedMiddle();
		private static void ConnectedMiddle() => ConnectedLeaf();
		private static void ConnectedLeaf() { }
		private static void Disconnected() { }
		private static GraphMarker CreateMarker() => new();

		private sealed class GraphMarker { }
	}
}
