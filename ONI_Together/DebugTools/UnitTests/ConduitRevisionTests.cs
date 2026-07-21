#if DEBUG
using System.IO;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.World;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class ConduitRevisionTests
	{
		[UnitTest(name: "Conduit revision roundtrips and rejects zero", category: "Sync")]
		public static UnitTestResult RevisionWireSafety()
		{
			var source = new ConduitContentsPacket();
			source.Updates.Add(Update(123, ConduitContentsPacket.CONDUIT_GAS, 17));
			ConduitContentsPacket copy = Roundtrip(source);
			if (1 != copy.Updates.Count || 17UL != copy.Updates[0].Revision)
				return UnitTestResult.Fail("Conduit revision did not roundtrip exactly");
			var zero = new ConduitContentsPacket();
			zero.Updates.Add(Update(123, ConduitContentsPacket.CONDUIT_GAS, 0));
			return RejectsSerialization(zero)
				? UnitTestResult.Pass("Conduit wire carries non-zero authority revision")
				: UnitTestResult.Fail("Conduit wire admitted revision zero");
		}

		[UnitTest(name: "Conduit revision is ordered per cell and type", category: "Sync")]
		public static UnitTestResult RevisionOrderingIsKeyedByType()
		{
			ConduitContentsPacket.ResetClientRevisionState();
			const int cell = 456;
			bool gasTwo = ConduitContentsPacket.TryAcceptRevision(
				cell, ConduitContentsPacket.CONDUIT_GAS, 2);
			bool gasOne = ConduitContentsPacket.TryAcceptRevision(
				cell, ConduitContentsPacket.CONDUIT_GAS, 1);
			bool gasDuplicate = ConduitContentsPacket.TryAcceptRevision(
				cell, ConduitContentsPacket.CONDUIT_GAS, 2);
			bool liquidOne = ConduitContentsPacket.TryAcceptRevision(
				cell, ConduitContentsPacket.CONDUIT_LIQUID, 1);
			bool liquidZero = ConduitContentsPacket.TryAcceptRevision(
				cell, ConduitContentsPacket.CONDUIT_LIQUID, 0);
			ConduitContentsPacket.ResetClientRevisionState();
			return gasTwo && !gasOne && !gasDuplicate && liquidOne && !liquidZero
				? UnitTestResult.Pass("Gas and liquid keep isolated monotonic revision cuts")
				: UnitTestResult.Fail("Conduit gate admitted zero/stale/duplicate or mixed gas and liquid");
		}

		[UnitTest(name: "Reliable conduit fast path rejects late unreliable state", category: "Sync")]
		public static UnitTestResult ReliableFastPathWinsReordering()
		{
			ConduitContentsPacket.ResetClientRevisionState();
			const int cell = 789;
			bool reliableTwo = ConduitContentsPacket.TryAcceptRevision(
				cell, ConduitContentsPacket.CONDUIT_GAS, 2);
			bool lateUnreliableOne = ConduitContentsPacket.TryAcceptRevision(
				cell, ConduitContentsPacket.CONDUIT_GAS, 1);
			ConduitContentsPacket.ResetClientRevisionState();
			return reliableTwo && !lateUnreliableOne
				? UnitTestResult.Pass("Late unreliable sweep cannot overwrite reliable fast-path state")
				: UnitTestResult.Fail("Late unreliable conduit state crossed the revision cut");
		}

		[UnitTest(name: "Conduit revisions reset for baseline and session", category: "Sync")]
		public static UnitTestResult RevisionStateResets()
		{
			ConduitContentsPacket.ResetClientRevisionState();
			if (!ConduitContentsPacket.TryAcceptRevision(
				    901, ConduitContentsPacket.CONDUIT_GAS, 9))
				return UnitTestResult.Fail("Conduit baseline setup rejected revision nine");
			bool baselineReset = SessionStateReset.ResetPresentationForBaseline(100, 1);
			bool baselineOne = ConduitContentsPacket.TryAcceptRevision(
				901, ConduitContentsPacket.CONDUIT_GAS, 1);
			ConduitContentsPacket.TryAcceptRevision(
				901, ConduitContentsPacket.CONDUIT_GAS, 9);
			SessionStateReset.Reset();
			bool sessionOne = ConduitContentsPacket.TryAcceptRevision(
				901, ConduitContentsPacket.CONDUIT_GAS, 1);
			ConduitContentsPacket.ResetClientRevisionState();
			return baselineReset && baselineOne && sessionOne
				? UnitTestResult.Pass("New baseline and session accept conduit revision one")
				: UnitTestResult.Fail("Conduit revision cache survived baseline or session reset");
		}

		private static ConduitCellUpdate Update(int cell, byte type, ulong revision)
			=> new()
			{
				Cell = cell,
				ConduitType = type,
				Revision = revision,
				Element = (int)SimHashes.Oxygen,
				Mass = 1f,
				Temperature = 300f
			};

		private static ConduitContentsPacket Roundtrip(ConduitContentsPacket source)
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
				source.Serialize(writer);
			stream.Position = 0;
			var copy = new ConduitContentsPacket();
			using var reader = new BinaryReader(stream);
			copy.Deserialize(reader);
			if (stream.Position != stream.Length)
				throw new InvalidDataException("Conduit packet left unread bytes");
			return copy;
		}

		private static bool RejectsSerialization(ConduitContentsPacket packet)
		{
			try
			{
				using var stream = new MemoryStream();
				using var writer = new BinaryWriter(stream);
				packet.Serialize(writer);
				return false;
			}
			catch (InvalidDataException) { return true; }
		}
	}
}
#endif
