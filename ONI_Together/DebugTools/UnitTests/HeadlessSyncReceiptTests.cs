#if DEBUG
using ONI_Together.Networking.Packets.Core;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class HeadlessSyncReceiptTests
	{
		[UnitTest(
			name: "Headless cursor relay executes production sync callsites",
			category: "Networking")]
		public static UnitTestResult CursorRelayExecutesProductionCallsites()
		{
			var packet = new PlayerCursorPacket
			{
				PlayerID = 1,
				SenderConnectionGeneration = 1,
				Revision = 1,
				BuildingPrefabId = string.Empty,
				HasUtilityPath = true,
				UtilityPathData = new uint[] { 1, 2 },
			};
			return HostBroadcastPacket.TryFitUnreliableRelay(packet)
			       && packet.HasUtilityPath
			       && packet.UtilityPathData?.Length == 2
				? UnitTestResult.Pass(
					"Production cursor relay preserved a valid bounded utility path")
				: UnitTestResult.Fail(
					"Production cursor relay rejected or mutated a valid bounded utility path");
		}
	}
}
#endif
