using System.Reflection;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class TransactionalSendPathMutationTests
	{
		[UnitTest(name: "Transactional send: valid single host is accepted", category: "StaticContract")]
		public static UnitTestResult ValidSingleHostIsAccepted()
			=> Accept(SingleHost("ValidHost"), "single host");

		[UnitTest(name: "Transactional send: pre-send host evidence is rejected", category: "StaticContract")]
		public static UnitTestResult PreSendHostEvidenceIsRejected()
			=> Reject(SingleHost("EvidenceBeforeHost"), "evidence");

		[UnitTest(name: "Transactional send: ignored rollback state is rejected", category: "StaticContract")]
		public static UnitTestResult IgnoredRollbackStateIsRejected()
			=> Reject(SingleHost("IgnoredRestoreHost"), "result is ignored");

		[UnitTest(name: "Transactional send: valid cleanup is accepted", category: "StaticContract")]
		public static UnitTestResult ValidCleanupIsAccepted()
			=> Accept(SingleCleanup("ValidCleanup"), "cleanup");

		[UnitTest(name: "Transactional send: ignored cleanup send is rejected", category: "StaticContract")]
		public static UnitTestResult IgnoredCleanupSendIsRejected()
			=> Reject(SingleCleanup("IgnoredCleanupSend"), "conditionally consumed");

		[UnitTest(name: "Transactional send: pre-send cleanup evidence is rejected", category: "StaticContract")]
		public static UnitTestResult PreSendCleanupEvidenceIsRejected()
			=> Reject(SingleCleanup("EvidenceBeforeCleanupSend"), "evidence");

		[UnitTest(name: "Transactional send: ignored cleanup restore is rejected", category: "StaticContract")]
		public static UnitTestResult IgnoredCleanupRestoreIsRejected()
			=> Reject(SingleCleanup("IgnoredRestoreCleanup"), "restore result");

		[UnitTest(name: "Transactional send: valid pickup two-packet path is accepted", category: "StaticContract")]
		public static UnitTestResult ValidPickupIsAccepted()
			=> Accept(Pickup("ValidPickupHost"), "pickup");

		[UnitTest(name: "Transactional send: ignored pickup second packet is rejected", category: "StaticContract")]
		public static UnitTestResult IgnoredPickupSecondIsRejected()
			=> Reject(Pickup("PickupIgnoresSecond"), "conditionally consumed");

		[UnitTest(name: "Transactional send: missing pickup compensation is rejected", category: "StaticContract")]
		public static UnitTestResult MissingPickupCompensationIsRejected()
			=> Reject(Pickup("PickupOmitsCompensation"), "compensation");

		private static string SingleHost(string method)
			=> TransactionalSendPathValidator.ValidateSingleHost(
				M(method), M("SendFirst"), M("ObserveHost"), M("Restore"));

		private static string SingleCleanup(string method)
			=> TransactionalSendPathValidator.ValidateSingleCleanup(
				M(method), M("Restore"), M("SendFirst"), M("ObserveCleanup"));

		private static string Pickup(string method)
			=> TransactionalSendPathValidator.ValidatePickup(M(method),
				new[] { M("SendFirst"), M("SendSecond") }, M("ObserveHost"),
				M("Restore"), M("Compensate"));

		private static UnitTestResult Accept(string failure, string name)
			=> failure == null ? UnitTestResult.Pass(name + " accepted")
				: UnitTestResult.Fail(failure);

		private static UnitTestResult Reject(string failure, string expected)
			=> failure?.Contains(expected) == true
				? UnitTestResult.Pass("rejected: " + failure)
				: UnitTestResult.Fail("expected " + expected + ", actual=" + failure);

		private static MethodInfo M(string name) => TransactionalSendPathFixtures.M(name);
	}
}
