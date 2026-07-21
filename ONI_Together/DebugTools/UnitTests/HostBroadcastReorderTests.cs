#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using Shared.Interfaces.Networking;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class HostBroadcastReorderTests
	{
		private const ulong Sender = 7;
		private const long Generation = 1;

		private sealed class DummyCommandRelay : IPacket, IClientRelayable
		{
			internal int Value;
			public void Serialize(BinaryWriter writer) => writer.Write(Value);
			public void Deserialize(BinaryReader reader) => Value = reader.ReadInt32();
			public void OnDispatched() { }
		}

		private sealed class DummyLatestRelay : IPacket, IClientRelayable
		{
			internal int Value;
			public void Serialize(BinaryWriter writer) => writer.Write(Value);
			public void Deserialize(BinaryReader reader) => Value = reader.ReadInt32();
			public void OnDispatched() { }
		}

		[UnitTest(name: "Host broadcast reorder: must-execute drains once in order", category: "Networking")]
		public static UnitTestResult MustExecuteDrainsOnceInOrder()
		{
			var calls = new List<object>();
			var reorder = Create(calls);
			var first = new DummyCommandRelay { Value = 1 };
			var second = new DummyCommandRelay { Value = 2 };
			var duplicate = new DummyCommandRelay { Value = 22 };
			reorder.Accept(Relay(second, 2), 0f);
			reorder.Accept(Relay(duplicate, 2), 0f);
			reorder.Accept(Relay(first, 1), 0f);
			reorder.Accept(Relay(first, 1), 0f);
			if (!Expected(calls, first, second))
				return UnitTestResult.Fail("#2 -> #1 did not keep the first future value exactly once");

			calls.Clear();
			reorder.Reset();
			var one = new DummyLatestRelay { Value = 1 };
			var two = new DummyLatestRelay { Value = 2 };
			var three = new DummyLatestRelay { Value = 3 };
			reorder.Accept(Relay(three, 3), 0f);
			reorder.Accept(Relay(two, 2), 0f);
			reorder.Accept(Relay(one, 1), 0f);
			reorder.Accept(Relay(three, 3), 0f);
			return Expected(calls, one, two, three)
				? UnitTestResult.Pass("Must-execute relays drain contiguously and never replay")
				: UnitTestResult.Fail("#3 -> #2 -> #1 was not dispatched as #1, #2, #3 exactly once");
		}

		[UnitTest(name: "Host broadcast reorder: sender and generation are isolated", category: "Networking")]
		public static UnitTestResult SenderAndGenerationAreIsolated()
		{
			var calls = new List<object>();
			var reorder = Create(calls);
			var oldPending = new DummyCommandRelay { Value = 12 };
			var senderB = new DummyCommandRelay { Value = 21 };
			reorder.Accept(new SequencedRelay<object>
			{
				SenderId = 10, Generation = 1, Domain = HostBroadcastPacket.RelayDomain.MustExecute,
				Sequence = 2, Bytes = 1, Value = oldPending,
			}, 0f);
			reorder.Accept(new SequencedRelay<object>
			{
				SenderId = 20, Generation = 1, Domain = HostBroadcastPacket.RelayDomain.MustExecute,
				Sequence = 1, Bytes = 1, Value = senderB,
			}, 0f);
			if (!Expected(calls, senderB))
				return UnitTestResult.Fail("Sender A's gap blocked sender B");

			reorder.DropConnectionState(10, 1);
			var newGeneration = new DummyCommandRelay { Value = 101 };
			reorder.Accept(new SequencedRelay<object>
			{
				SenderId = 10, Generation = 2, Domain = HostBroadcastPacket.RelayDomain.MustExecute,
				Sequence = 1, Bytes = 1, Value = newGeneration,
			}, 1f);
			if (reorder.TryGetSnapshot(10, 1, out _)
			    || !reorder.TryGetSnapshot(10, 2, out HostBroadcastReorderSnapshot snapshot)
			    || 0 != snapshot.PendingCount || !Expected(calls, senderB, newGeneration))
				return UnitTestResult.Fail("Reconnect retained old pending work or rejected new generation #1");
			return UnitTestResult.Pass("Senders and connection generations drain independently");
		}

		[UnitTest(name: "Host broadcast reorder: latest-state is LWW and domain-local", category: "Networking")]
		public static UnitTestResult LatestStateIsLwwAndDomainLocal()
		{
			var calls = new List<object>();
			var reorder = Create(calls);
			var commandTwo = new DummyCommandRelay { Value = 2 };
			var latestTwo = new DummyLatestRelay { Value = 2 };
			var latestOne = new DummyLatestRelay { Value = 1 };
			var commandOne = new DummyCommandRelay { Value = 1 };
			reorder.Accept(Relay(commandTwo, 2), 0f);
			reorder.Accept(Relay(latestTwo, 2, HostBroadcastPacket.RelayDomain.LatestState), 0f);
			reorder.Accept(Relay(latestOne, 1, HostBroadcastPacket.RelayDomain.LatestState), 0f);
			reorder.Accept(Relay(commandOne, 1), 0f);
			if (!Expected(calls, latestTwo, commandOne, commandTwo))
				return UnitTestResult.Fail("Latest-state regressed or shared a false gap with commands");
			if (!reorder.TryGetSnapshot(Sender, Generation, out HostBroadcastReorderSnapshot snapshot)
			    || 3UL != snapshot.NextExpected || 2UL != snapshot.LatestSequence)
				return UnitTestResult.Fail("Command and latest-state cursors were not tracked independently");
			return UnitTestResult.Pass("Latest-state applies LWW while must-execute remains contiguous");
		}

		[UnitTest(name: "Host broadcast reorder: invalid bounds terminate once", category: "Networking")]
		public static UnitTestResult InvalidBoundsTerminateOnce()
		{
			var sequenceZeroCalls = new List<object>();
			var sequenceZero = Create(sequenceZeroCalls);
			sequenceZero.Accept(Relay(new DummyCommandRelay(), 0), 0f);
			if (0 != sequenceZeroCalls.Count)
				return UnitTestResult.Fail("Sequence zero was dispatched");

			string error = Terminal(
				reorder => reorder.Accept(Relay(new DummyCommandRelay(), HostBroadcastReorder<object>.MaxGap + 2), 0f),
				reorder => reorder.Accept(Relay(new DummyCommandRelay(), 1), 0f));
			if (error != null)
				return UnitTestResult.Fail("Gap 257: " + error);
			error = Terminal(PendingOverflow, PendingOverflow);
			if (error != null)
				return UnitTestResult.Fail("Pending 129: " + error);
			error = Terminal(ByteOverflow, ByteOverflow);
			if (error != null)
				return UnitTestResult.Fail("Pending byte cap: " + error);
			error = Terminal(Timeout, Timeout);
			if (error != null)
				return UnitTestResult.Fail("Gap timeout: " + error);
			error = Terminal(DispatchFailure, DispatchFailure, _ => false);
			return error == null
				? UnitTestResult.Pass("Invalid bounds clear pending state and terminate exactly once")
				: UnitTestResult.Fail("Dispatch failure: " + error);
		}

		[UnitTest(name: "Host broadcast reorder: gap fill before timeout drains", category: "Networking")]
		public static UnitTestResult GapFillBeforeTimeoutDrains()
		{
			var calls = new List<object>();
			var reorder = Create(calls);
			var one = new DummyCommandRelay { Value = 1 };
			var two = new DummyCommandRelay { Value = 2 };
			reorder.Accept(Relay(two, 2), 10f);
			reorder.CheckTimeouts(14.9f, 5f);
			reorder.Accept(Relay(one, 1), 14.9f);
			if (!Expected(calls, one, two)
			    || !reorder.TryGetSnapshot(Sender, Generation, out HostBroadcastReorderSnapshot snapshot)
			    || 3UL != snapshot.NextExpected || 0 != snapshot.PendingCount || snapshot.Failed)
				return UnitTestResult.Fail("A gap filled before its deadline failed to drain");
			return UnitTestResult.Pass("Pre-timeout completion drains the contiguous relay range");
		}

		[UnitTest(name: "Host broadcast reorder: full future window still accepts its head", category: "Networking")]
		public static UnitTestResult FullFutureWindowAcceptsHead()
		{
			var calls = new List<int>();
			int kicks = 0;
			var reorder = new HostBroadcastReorder<DummyCommandRelay>(
				relay => { calls.Add(relay.Value); return true; }, _ => kicks++);
			for (ulong sequence = 2; sequence <= (ulong)HostBroadcastReorder<object>.MaxPending + 1; sequence++)
				reorder.Accept(new SequencedRelay<DummyCommandRelay>
				{
					SenderId = Sender, Generation = Generation,
					Domain = HostBroadcastPacket.RelayDomain.MustExecute,
					Sequence = sequence, Bytes = 1,
					Value = new DummyCommandRelay { Value = (int)sequence },
				}, 0f);
			reorder.Accept(new SequencedRelay<DummyCommandRelay>
			{
				SenderId = Sender, Generation = Generation,
				Domain = HostBroadcastPacket.RelayDomain.MustExecute,
				Sequence = 1, Bytes = 1, Value = new DummyCommandRelay { Value = 1 },
			}, 0f);
			if (0 != kicks || HostBroadcastReorder<object>.MaxPending + 1 != calls.Count)
				return UnitTestResult.Fail("The missing head was rejected by the full future window");
			for (int expected = 1; expected <= calls.Count; expected++)
				if (expected != calls[expected - 1])
					return UnitTestResult.Fail("The full future window did not drain contiguously");
			return UnitTestResult.Pass("A full future window still admits its missing head and drains");
		}

		[UnitTest(name: "Host broadcast client: reliable send failure disconnects", category: "Networking")]
		public static UnitTestResult ReliableSendFailureDisconnects()
		{
			int disconnects = 0;
			PacketSender.HandleClientRelaySendResult(PacketSendMode.Unreliable, false, () => disconnects++);
			PacketSender.HandleClientRelaySendResult(PacketSendMode.Reliable, true, () => disconnects++);
			PacketSender.HandleClientRelaySendResult(PacketSendMode.Reliable, false, () => disconnects++);
			return 1 == disconnects
				? UnitTestResult.Pass("Only a failed reliable client relay disconnects")
				: UnitTestResult.Fail($"Expected one reliable-failure disconnect, got {disconnects}");
		}

		[UnitTest(name: "Host broadcast client: session and reconnect reset domain sequences", category: "Networking")]
		public static UnitTestResult SessionAndReconnectResetDomainSequences()
		{
			HostBroadcastPacket.ResetClientRequestSequences();
			HostBroadcastPacket commandOne = PacketSender.CreateHostRelayForClient(new DummyCommandRelay(), Sender);
			HostBroadcastPacket cursorOne = PacketSender.CreateHostRelayForClient(
				new PlayerCursorPacket { PlayerID = Sender, Revision = 1 }, Sender);
			HostBroadcastPacket commandTwo = PacketSender.CreateHostRelayForClient(new DummyCommandRelay(), Sender);
			HostBroadcastPacket cursorTwo = PacketSender.CreateHostRelayForClient(
				new PlayerCursorPacket { PlayerID = Sender, Revision = 2 }, Sender);
			if (1UL != commandOne.RequestId || 1UL != cursorOne.RequestId
			    || 2UL != commandTwo.RequestId || 2UL != cursorTwo.RequestId)
				return UnitTestResult.Fail("Command and cursor client sequences shared a false gap");

			HostBroadcastPacket.ResetSessionState();
			HostBroadcastPacket afterSession = PacketSender.CreateHostRelayForClient(new DummyCommandRelay(), Sender);
			HostBroadcastPacket.ResetClientRequestSequences();
			HostBroadcastPacket afterReconnect = PacketSender.CreateHostRelayForClient(new DummyCommandRelay(), Sender);
			return 1UL == afterSession.RequestId && 1UL == afterReconnect.RequestId
				? UnitTestResult.Pass("Session and reconnect each restart client relay domains at one")
				: UnitTestResult.Fail("Session or reconnect retained a prior client relay sequence");
		}

		private static HostBroadcastReorder<object> Create(List<object> calls)
			=> new(value => { calls.Add(value); return true; }, _ => { });

		private static SequencedRelay<object> Relay(
			object value, ulong sequence,
			HostBroadcastPacket.RelayDomain domain = HostBroadcastPacket.RelayDomain.MustExecute)
			=> new()
			{
				SenderId = Sender, Generation = Generation, Domain = domain,
				Sequence = sequence, Bytes = 1, Value = value,
			};

		private static bool Expected(List<object> actual, params object[] expected)
		{
			if (expected.Length != actual.Count)
				return false;
			for (int i = 0; i < expected.Length; i++)
				if (!ReferenceEquals(expected[i], actual[i]))
					return false;
			return true;
		}

		private static string Terminal(
			Action<HostBroadcastReorder<object>> trigger,
			Action<HostBroadcastReorder<object>> repeat,
			Func<object, bool> dispatch = null)
		{
			int kicks = 0;
			var reorder = new HostBroadcastReorder<object>(dispatch ?? (_ => true), _ => kicks++);
			trigger(reorder);
			if (!reorder.TryGetSnapshot(Sender, Generation, out HostBroadcastReorderSnapshot snapshot))
				return "terminal state was not retained";
			if (!snapshot.Failed || !snapshot.KickIssued || 1 != kicks
			    || 0 != snapshot.PendingCount || 0 != snapshot.PendingBytes)
				return "state was not failed, cleared, and kicked once";
			repeat(reorder);
			return 1 == kicks ? null : "terminal input kicked more than once";
		}

		private static void PendingOverflow(HostBroadcastReorder<object> reorder)
		{
			for (ulong sequence = 2; sequence <= (ulong)HostBroadcastReorder<object>.MaxPending + 2; sequence++)
				reorder.Accept(Relay(new DummyCommandRelay(), sequence), 0f);
		}

		private static void ByteOverflow(HostBroadcastReorder<object> reorder)
		{
			int chunk = HostBroadcastReorder<object>.MaxPendingBytes / 4;
			for (ulong sequence = 2; sequence <= 5; sequence++)
				reorder.Accept(new SequencedRelay<object>
				{
					SenderId = Sender, Generation = Generation,
					Domain = HostBroadcastPacket.RelayDomain.MustExecute,
					Sequence = sequence, Bytes = chunk, Value = new DummyCommandRelay(),
				}, 0f);
			reorder.Accept(new SequencedRelay<object>
			{
				SenderId = Sender, Generation = Generation,
				Domain = HostBroadcastPacket.RelayDomain.MustExecute,
				Sequence = 6, Bytes = 1, Value = new DummyCommandRelay(),
			}, 0f);
		}

		private static void Timeout(HostBroadcastReorder<object> reorder)
		{
			reorder.Accept(Relay(new DummyCommandRelay(), 2), 0f);
			reorder.CheckTimeouts(2f, 1f);
		}

		private static void DispatchFailure(HostBroadcastReorder<object> reorder)
		{
			reorder.Accept(Relay(new DummyCommandRelay(), 2), 0f);
			reorder.Accept(Relay(new DummyCommandRelay(), 1), 0f);
		}
	}
}
#endif
