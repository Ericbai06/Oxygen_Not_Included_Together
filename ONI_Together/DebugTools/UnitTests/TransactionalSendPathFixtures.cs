using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	internal static class TransactionalSendPathFixtures
	{
		internal static MethodInfo M(string name) => typeof(TransactionalSendPathFixtures)
			.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

		internal static Mutation ValidHost(Command command)
		{
			Mutation mutation = Mutate(command);
			Packet packet = Create(mutation);
			if (!SendFirst(packet))
			{
				State restored = Restore(mutation);
				if (restored != null) RecordRollback(restored);
				return null;
			}
			ObserveHost(Capture(mutation));
			return mutation;
		}

		internal static Mutation EvidenceBeforeHost(Command command)
		{
			Mutation mutation = Mutate(command);
			ObserveHost(Capture(mutation));
			if (!SendFirst(Create(mutation))) return null;
			return mutation;
		}

		internal static Mutation IgnoredRestoreHost(Command command)
		{
			Mutation mutation = Mutate(command);
			if (!SendFirst(Create(mutation)))
			{
				Restore(mutation);
				return null;
			}
			ObserveHost(Capture(mutation));
			return mutation;
		}

		internal static State ValidCleanup(Mutation mutation)
		{
			State state = Restore(mutation);
			if (state == null) return null;
			if (!SendFirst(Create(mutation))) return null;
			ObserveCleanup(state);
			return state;
		}

		internal static State IgnoredCleanupSend(Mutation mutation)
		{
			State state = Restore(mutation);
			if (state == null) return null;
			SendFirst(Create(mutation));
			ObserveCleanup(state);
			return state;
		}

		internal static State EvidenceBeforeCleanupSend(Mutation mutation)
		{
			State state = Restore(mutation);
			if (state == null) return null;
			ObserveCleanup(state);
			if (!SendFirst(Create(mutation))) return null;
			return state;
		}

		internal static State IgnoredRestoreCleanup(Mutation mutation)
		{
			State state = Restore(mutation);
			if (!SendFirst(Create(mutation))) return null;
			ObserveCleanup(state);
			return state;
		}

		internal static Mutation ValidPickupHost(Command command)
		{
			Mutation mutation = Mutate(command);
			if (!SendFirst(Create(mutation)))
			{
				State restored = Restore(mutation);
				if (restored != null) RecordRollback(restored);
				return null;
			}
			if (!SendSecond(CreateSecond(mutation)))
			{
				if (Compensate(mutation)) RecordCompensation();
				return null;
			}
			ObserveHost(Capture(mutation));
			return mutation;
		}

		internal static Mutation PickupIgnoresSecond(Command command)
		{
			Mutation mutation = Mutate(command);
			if (!SendFirst(Create(mutation)))
			{
				State restored = Restore(mutation);
				if (restored != null) RecordRollback(restored);
				return null;
			}
			SendSecond(CreateSecond(mutation));
			ObserveHost(Capture(mutation));
			return mutation;
		}

		internal static Mutation PickupOmitsCompensation(Command command)
		{
			Mutation mutation = Mutate(command);
			if (!SendFirst(Create(mutation)))
			{
				State restored = Restore(mutation);
				if (restored != null) RecordRollback(restored);
				return null;
			}
			if (!SendSecond(CreateSecond(mutation))) return null;
			ObserveHost(Capture(mutation));
			return mutation;
		}

		private static Mutation Mutate(Command command) => new();
		private static State Capture(Mutation mutation) => new();
		private static State Restore(Mutation mutation) => new();
		private static Packet Create(Mutation mutation) => new();
		private static Packet2 CreateSecond(Mutation mutation) => new();
		private static bool SendFirst(Packet packet) => true;
		private static bool SendSecond(Packet2 packet) => true;
		private static bool Compensate(Mutation mutation) => true;
		private static void RecordRollback(State state) { }
		private static void RecordCompensation() { }
		private static void ObserveHost(State state) { }
		private static void ObserveCleanup(State state) { }

		internal sealed class Command { }
		internal sealed class Mutation { }
		internal sealed class State { }
		internal sealed class Packet { }
		internal sealed class Packet2 { }
	}
}
