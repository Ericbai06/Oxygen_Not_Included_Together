using ONI_Together.DebugTools;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class NativeTransportMetricsTests
	{
		[UnitTest(name: "Native metrics: calls, bytes, failures and presentation classes", category: "Networking")]
		public static UnitTestResult NativeMetricsClassifyActualSendAttempts()
		{
			SyncStats.ResetNativeTransportForTests();
			SyncStats.RecordNativeSend("EntityMotionBatchPacket", 120, true);
			SyncStats.RecordNativeSend("AnimSyncBatchPacket", 80, true);
			SyncStats.RecordNativeSend("DuplicantPresentationBatchPacket", 60, true);
			SyncStats.RecordNativeSend("PlayerCursorPacket", 40, true);
			SyncStats.RecordNativeSend("EntityMotionBatchPacket", 20, false);

			SyncStats.NativeTransportSnapshot snapshot = SyncStats.GetNativeTransportSnapshot();
			bool valid = snapshot.TxCalls == 5 && snapshot.TxBytes == 300
			             && snapshot.TxFailures == 1
			             && snapshot.MotionCalls == 2 && snapshot.MotionBytes == 120
			             && snapshot.AnimationCalls == 2 && snapshot.AnimationBytes == 140
			             && snapshot.CursorCalls == 1 && snapshot.CursorBytes == 40;
			SyncStats.ResetNativeTransportForTests();
			return valid
				? UnitTestResult.Pass("Native send metrics preserve traffic class and failures")
				: UnitTestResult.Fail("Native send metrics lost calls, bytes, failures, or class totals");
		}

		[UnitTest(name: "Native metrics: session reset clears baseline", category: "Networking")]
		public static UnitTestResult NativeMetricsResetClearsBaseline()
		{
			SyncStats.RecordNativeSend("PlayerCursorPacket", 40, true);
			SyncStats.ResetNativeTransportForTests();
			SyncStats.NativeTransportSnapshot snapshot = SyncStats.GetNativeTransportSnapshot();
			return snapshot.TxCalls == 0 && snapshot.TxBytes == 0 && snapshot.TxFailures == 0
				? UnitTestResult.Pass("Native traffic baseline resets with the session")
				: UnitTestResult.Fail("Native traffic counters survived reset");
		}
	}
}
