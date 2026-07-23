using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ONI_Together.DebugTools.UnitTests
{
	internal static class TypedEvidenceTestFixture
	{
		private const string CanonicalRemoteDigState =
			"{\"action\":\"Digging\",\"animation\":\"dig_loop\",\"progress\":0.5,\"tool\":\"DigTool\"}";

		internal static TypedEvidenceEnvelope RemoteDigEnvelope()
		{
			var state = new RemoteDigState
			{
				Action = "Digging",
				Animation = "dig_loop",
				Tool = "DigTool",
				Progress = 0.5,
			};
			return new TypedEvidenceEnvelope
			{
				SchemaVersion = 1,
				RunId = "run:typed-evidence",
				DllHash = "sha256:" + new string('1', 64),
				Scenario = "remote-dig",
				EntryId = "sync:test:remote-dig",
				Role = "host",
				SessionEpoch = 8,
				ConnectionGeneration = 2,
				SnapshotGeneration = 3,
				Phase = "final-state",
				RevisionDomain = "remote-dig",
				Revision = 3,
				Sequence = 1,
				Target = new RemoteDigTarget
				{
					MinionNetId = 7,
					TargetNetId = 8,
					TargetCell = 42,
				},
				State = state,
				StateHash = Hash(CanonicalRemoteDigState),
			};
		}

		internal static string Hash(string canonicalJson)
		{
			using (SHA256 sha256 = SHA256.Create())
			{
				byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonicalJson));
				var result = new StringBuilder("sha256:", 71);
				foreach (byte value in bytes)
					result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
				return result.ToString();
			}
		}
	}
}
