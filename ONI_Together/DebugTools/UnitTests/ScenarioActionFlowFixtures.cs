using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	internal static class ScenarioActionFlowFixtures
	{
		internal static MethodInfo M(string name)
			=> typeof(ScenarioActionFlowFixtures).GetMethod(
				name, BindingFlags.Static | BindingFlags.NonPublic);

		internal static MethodInfo D(string name)
			=> typeof(DispatchPacket).GetMethod(
				name, BindingFlags.Instance | BindingFlags.NonPublic);

		internal static Mutation HappyHost(Command command)
		{
			Prepared prepared = Prepare(command);
			Target target = Resolve(prepared);
			Mutation mutation = Mutate(target);
			State state = CaptureState(mutation);
			ObserveHostState(state);
			ExpectedPacket packet = CreatePacket(mutation);
			SendPacket(packet);
			return mutation;
		}

		internal static Mutation DormantHost(Command command, bool execute)
		{
			if (!execute) return null;
			return HappyHost(command);
		}

		internal static Mutation DiscardedResolverHost(Command command)
		{
			Prepared prepared = Prepare(command);
			Resolve(prepared);
			return Mutate(null);
		}

		internal static Mutation WrongPacketHost(Command command)
		{
			Prepared prepared = Prepare(command);
			Target target = Resolve(prepared);
			Mutation mutation = Mutate(target);
			CreatePacket(mutation);
			WrongPacket wrong = CreateWrongPacket(mutation);
			SendWrongPacket(wrong);
			return mutation;
		}

		internal static Mutation LocalOnlyHost(Command command)
		{
			Prepared prepared = Prepare(command);
			Target target = Resolve(prepared);
			return Mutate(target);
		}

		internal static Mutation HappySequenceHost(Command command)
		{
			Mutation mutation = Mutate(Resolve(Prepare(command)));
			SendPacket(CreatePacket(mutation));
			SendPacket2(CreatePacket2(mutation));
			return mutation;
		}

		internal static Mutation ReorderedSequenceHost(Command command)
		{
			Mutation mutation = Mutate(Resolve(Prepare(command)));
			SendPacket2(CreatePacket2(mutation));
			SendPacket(CreatePacket(mutation));
			return mutation;
		}

		internal static Mutation ReplacementSequenceHost(Command command)
		{
			Mutation mutation = Mutate(Resolve(Prepare(command)));
			SendPacket(CreatePacket(mutation));
			SendPacket2(CreatePacket2(CreateMutation()));
			return mutation;
		}

		internal static Mutation HappyConditionalHost(Command command)
		{
			Mutation mutation = ConditionalPrefix(command, out ExpectedPacket packet);
			if (!SendPacketBool(packet))
			{
				Restore(mutation);
				return null;
			}
			return mutation;
		}

		internal static Mutation IgnoredConditionalHost(Command command)
		{
			Mutation mutation = ConditionalPrefix(command, out ExpectedPacket packet);
			SendPacketBool(packet);
			return mutation;
		}

		internal static Mutation MissingRollbackConditionalHost(Command command)
		{
			Mutation mutation = ConditionalPrefix(command, out ExpectedPacket packet);
			return SendPacketBool(packet) ? mutation : null;
		}

		internal static State HappyClient(ExpectedPacket packet)
		{
			State state = ApplyPacket(packet);
			ObserveClientState(state);
			return state;
		}

		internal static State NoOracleClient(ExpectedPacket packet)
			=> ApplyPacket(packet);

		internal static State HappyCleanup(Mutation mutation)
		{
			State state = Restore(mutation);
			ObserveCleanupState(state);
			ExpectedPacket packet = CreateCleanupPacket(state);
			SendCleanupPacket(packet);
			return state;
		}

		internal static State WrongCleanup(Mutation mutation)
			=> Restore(CreateMutation());

		internal static Target AttachFixture(Target target)
		{
			AddFixture(target);
			return target;
		}

		internal static Target AttachFixtureToOther(Target target)
		{
			Target other = CreateTarget();
			AddFixture(other);
			return target;
		}

		internal static Mutation HappyDlc(Command command)
		{
			Prepared prepared = Prepare(command);
			Target target = Resolve(prepared);
			Target attached = AttachFixture(target);
			return Transition(attached, "RobotIdleMonitor.idle", "RobotIdleMonitor.working");
		}

		internal static Mutation WrongDlcState(Command command)
		{
			Prepared prepared = Prepare(command);
			Target target = Resolve(prepared);
			Target attached = AttachFixture(target);
			return Transition(attached, "RobotIdleMonitor.working", "RobotIdleMonitor.idle");
		}

		private static Prepared Prepare(Command command) => new();
		private static Target Resolve(Prepared prepared) => new();
		private static Mutation Mutate(Target target) => new();
		private static State CaptureState(Mutation mutation) => new();
		private static void ObserveHostState(State state) { }
		private static ExpectedPacket CreatePacket(Mutation mutation) => new();
		private static void SendPacket(ExpectedPacket packet) { }
		private static bool SendPacketBool(ExpectedPacket packet) => true;
		private static ExpectedPacket2 CreatePacket2(Mutation mutation) => new();
		private static void SendPacket2(ExpectedPacket2 packet) { }
		private static WrongPacket CreateWrongPacket(Mutation mutation) => new();
		private static void SendWrongPacket(WrongPacket packet) { }
		private static State ApplyPacket(ExpectedPacket packet) => new();
		private static void ObserveClientState(State state) { }
		private static State Restore(Mutation mutation) => new();
		private static void ObserveCleanupState(State state) { }
		private static ExpectedPacket CreateCleanupPacket(State state) => new();
		private static void SendCleanupPacket(ExpectedPacket packet) { }
		private static Mutation CreateMutation() => new();
		private static Target CreateTarget() => new();
		private static void AddFixture(Target target) { }
		private static Mutation Transition(Target target, string from, string to) => new();
		private static void AcceptDispatch(DispatchPacket packet) { }

		private static Mutation ConditionalPrefix(
			Command command, out ExpectedPacket packet)
		{
			Prepared prepared = Prepare(command);
			Target target = Resolve(prepared);
			Mutation mutation = Mutate(target);
			State state = CaptureState(mutation);
			ObserveHostState(state);
			packet = CreatePacket(mutation);
			return mutation;
		}

		internal sealed class Command { }
		internal sealed class Prepared { }
		internal sealed class Target { }
		internal sealed class Mutation { }
		internal sealed class State { }
		internal sealed class ExpectedPacket { }
		internal sealed class ExpectedPacket2 { }
		internal sealed class WrongPacket { }
		internal sealed class DispatchPacket
		{
			internal void HappyDispatch() => AcceptDispatch(this);
			internal void WrongDispatch() => AcceptDispatch(new DispatchPacket());
		}
	}
}
