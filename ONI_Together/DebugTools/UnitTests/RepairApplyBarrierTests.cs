#if DEBUG
using System.Collections;
using System.Collections.Generic;
using ONI_Together.Misc.World;
using ONI_Together.Networking.Packets.World;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class RepairApplyBarrierTests
	{
		[UnitTest(name: "Repair apply barrier crosses a Unity frame", category: "Networking")]
		public static UnitTestResult CrossesUnityFrame()
		{
			WorldUpdatePacket.ResetRevisionState();
			try
			{
				WorldUpdatePacket packet = RepairPacket();
				if (!WorldUpdateRepairObservability.Track(packet, packet.Updates))
					return UnitTestResult.Fail("Repair observation was rejected");
				IEnumerator work = WorldUpdateRepairObservability.RunNextUnityFrameForTests(
					WorldUpdateRepairObservability.EpochForTests);
				if (!work.MoveNext() || WorldUpdatePacket.ClientResolvedRepairSequence != 0)
					return UnitTestResult.Fail("Repair resolved before the Unity frame boundary");
				if (work.MoveNext() || WorldUpdatePacket.ClientResolvedRepairSequence != 1)
					return UnitTestResult.Fail("Repair did not resolve after the Unity frame boundary");
				return UnitTestResult.Pass("Repair ACK eligibility begins after one rendered frame");
			}
			finally { WorldUpdatePacket.ResetRevisionState(); }
		}

		[UnitTest(name: "Stale repair coroutine cannot consume a new session", category: "Networking")]
		public static UnitTestResult StaleCoroutineCannotConsumeNewSession()
		{
			WorldUpdatePacket.ResetRevisionState();
			long staleEpoch = WorldUpdateRepairObservability.EpochForTests;
			WorldUpdatePacket.ResetRevisionState();
			try
			{
				WorldUpdatePacket packet = RepairPacket();
				if (!WorldUpdateRepairObservability.Track(packet, packet.Updates))
					return UnitTestResult.Fail("New-session repair observation was rejected");
				IEnumerator stale = WorldUpdateRepairObservability.RunNextUnityFrameForTests(staleEpoch);
				stale.MoveNext();
				stale.MoveNext();
				return WorldUpdateRepairObservability.PendingCount == 1
				       && WorldUpdatePacket.ClientResolvedRepairSequence == 0
					? UnitTestResult.Pass("Old coroutine left the new epoch untouched")
					: UnitTestResult.Fail("Old coroutine resolved or removed a new-session repair");
			}
			finally { WorldUpdatePacket.ResetRevisionState(); }
		}

		private static WorldUpdatePacket RepairPacket()
			=> new()
			{
				Revision = 10,
				RepairSequence = 1,
				Updates = new List<WorldUpdatePacket.CellUpdate>
				{
					new() { Cell = 7, ReplaceType = SimMessages.ReplaceType.Replace },
				},
			};
	}
}
#endif
