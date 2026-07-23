using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ONI_Together.Networking;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class IntegrationPreflightStatusTests
	{
		private const string ExpectedGameBuild = "U59-740622-S";
		private const string SteamLobbyId = "109775241058992817";

		[UnitTest(name: "Integration preflight: exposes exact machine identity", category: "Integration")]
		public static UnitTestResult ExactMachineIdentityIsRequired()
		{
			string[] dlcIds = { "EXPANSION1_ID", "DLC3_ID", "EXPANSION1_ID" };
			var facts = ValidSteamFacts(dlcIds);
			if (!IntegrationPreflightStatus.TryCreate(facts, out var status, out string error))
				return UnitTestResult.Fail("Valid preflight facts were rejected: " + error);

			string expectedDlc = CanonicalDlcFingerprint(dlcIds);
			string actual = status.Format();
			bool valid = ContainsField(actual, "build", ExpectedGameBuild)
			             && ContainsField(actual, "sessionIdentity", "steam:" + SteamLobbyId)
			             && ContainsField(actual, "dlcFingerprint", expectedDlc)
			             && ContainsField(actual, "protocol",
				                 ProtocolCompatibility.CurrentProtocolVersion.ToString())
			             && ContainsField(actual, "role", "host")
			             && !ContainsField(actual, "session", "1");
			return valid
				? UnitTestResult.Pass("Preflight reports build, lobby identity, DLC fingerprint, protocol, and role")
				: UnitTestResult.Fail("Preflight omitted or weakened a machine identity field: " + actual);
		}

		[UnitTest(name: "Integration preflight: Steam identity fails closed", category: "Integration")]
		public static UnitTestResult SteamIdentityFailsClosed()
		{
			var noTransport = ValidSteamFacts();
			noTransport.TransportReady = false;
			var noLobbyState = ValidSteamFacts();
			noLobbyState.InSteamLobby = false;
			var emptyLobby = ValidSteamFacts();
			emptyLobby.SteamLobbyId = string.Empty;
			var zeroLobby = ValidSteamFacts();
			zeroLobby.SteamLobbyId = "0";
			var malformedLobby = ValidSteamFacts();
			malformedLobby.SteamLobbyId = "not-a-lobby";

			bool valid = Rejects(noTransport, "transport")
			             && Rejects(noLobbyState, "lobby")
			             && Rejects(emptyLobby, "lobby")
			             && Rejects(zeroLobby, "lobby")
			             && Rejects(malformedLobby, "lobby");
			return valid
				? UnitTestResult.Pass("Steam preflight rejects absent transport and invalid lobby identity")
				: UnitTestResult.Fail("Steam preflight accepted an unverifiable session identity");
		}

		[UnitTest(name: "Integration preflight: non-Steam identity is explicit", category: "Integration")]
		public static UnitTestResult NonSteamIdentityMustBeExplicit()
		{
			var missing = ValidNonSteamFacts(string.Empty);
			var present = ValidNonSteamFacts("lan:10.0.0.8:7777/session-42");
			bool rejected = Rejects(missing, "session");
			bool accepted = IntegrationPreflightStatus.TryCreate(
				present, out var status, out string error);
			string actual = accepted ? status.Format() : error;
			return rejected && accepted
			       && ContainsField(actual, "sessionIdentity", "lan:10.0.0.8:7777/session-42")
				? UnitTestResult.Pass("Non-Steam preflight uses a concrete session identity")
				: UnitTestResult.Fail("Non-Steam preflight fell back to a boolean session flag: " + actual);
		}

		[UnitTest(name: "Integration preflight: status fields are lossless and delimited", category: "Integration")]
		public static UnitTestResult ExistingStatusFieldsArePreservedAndEscaped()
		{
			var facts = ValidSteamFacts();
			facts.StatusFields = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["repairCut"] = "29",
				["repairJournal"] = "2",
				["txCalls"] = "41",
				["txBytes"] = "8192",
				["steamQueueUsec"] = "1200",
				["state"] = "Started;spoof=1\r\nnext"
			};
			if (!IntegrationPreflightStatus.TryCreate(facts, out var status, out string error))
				return UnitTestResult.Fail("Valid status fields were rejected: " + error);

			string actual = status.Format();
			string[] fields = actual.Split(';');
			bool valid = ContainsField(actual, "repairCut", "29")
			             && ContainsField(actual, "repairJournal", "2")
			             && ContainsField(actual, "txCalls", "41")
			             && ContainsField(actual, "txBytes", "8192")
			             && ContainsField(actual, "steamQueueUsec", "1200")
			             && ContainsField(actual, "state", "Started%3Bspoof%3D1%0D%0Anext")
			             && actual.IndexOfAny(new[] { '\r', '\n' }) < 0
			             && fields.All(field => field.Count(character => character == '=') == 1);
			return valid
				? UnitTestResult.Pass("Repair and native transport fields remain grep-safe")
				: UnitTestResult.Fail("Status formatting lost fields or allowed delimiter injection: " + actual);
		}

		private static IntegrationPreflightFacts ValidSteamFacts(IEnumerable<string> dlcIds = null)
			=> new IntegrationPreflightFacts
			{
				GameBuild = ExpectedGameBuild,
				Transport = IntegrationTransportKind.Steam,
				TransportReady = true,
				InSteamLobby = true,
				SteamLobbyId = SteamLobbyId,
				ActiveDlcIds = dlcIds ?? new[] { "EXPANSION1_ID" },
				Protocol = ProtocolCompatibility.CurrentProtocolVersion,
				Role = "host"
			};

		private static IntegrationPreflightFacts ValidNonSteamFacts(string sessionIdentity)
			=> new IntegrationPreflightFacts
			{
				GameBuild = ExpectedGameBuild,
				Transport = IntegrationTransportKind.NonSteam,
				TransportReady = true,
				NonSteamSessionIdentity = sessionIdentity,
				ActiveDlcIds = new[] { "EXPANSION1_ID" },
				Protocol = ProtocolCompatibility.CurrentProtocolVersion,
				Role = "client"
			};

		private static bool Rejects(IntegrationPreflightFacts facts, string expectedReason)
			=> !IntegrationPreflightStatus.TryCreate(facts, out _, out string error)
			   && error.IndexOf(expectedReason, StringComparison.OrdinalIgnoreCase) >= 0;

		private static bool ContainsField(string text, string key, string expectedValue)
			=> text.Split(';').Contains(key + "=" + expectedValue, StringComparer.Ordinal);

		private static string CanonicalDlcFingerprint(IEnumerable<string> dlcIds)
		{
			string canonical = string.Join("\n", dlcIds
				.Where(id => !string.IsNullOrEmpty(id))
				.Distinct(StringComparer.Ordinal)
				.OrderBy(id => id, StringComparer.Ordinal));
			using SHA256 sha = SHA256.Create();
			byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
			return "sha256:" + string.Concat(digest.Select(value => value.ToString("x2")));
		}
	}
}
