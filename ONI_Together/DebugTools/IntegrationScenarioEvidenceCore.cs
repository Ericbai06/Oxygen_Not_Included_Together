#if DEBUG
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ONI_Together.DebugTools
{
	public sealed class IntegrationScenarioEvidence
	{
		public string Scenario { get; set; }
		public bool HostSubmitObserved { get; set; }
		public int HostSubmitRevision { get; set; }
		public bool ClientApplyObserved { get; set; }
		public int ClientApplyRevision { get; set; }
		public bool ClientOriginalBlocked { get; set; }
		public RevisionProbeResult Accepted { get; set; }
		public RevisionProbeResult Duplicate { get; set; }
		public RevisionProbeResult OutOfOrder { get; set; }
		public string HostState { get; set; }
		public string ClientState { get; set; }
		public string HostHash { get; set; }
		public string ClientHash { get; set; }
		public string PostReconnectHostState { get; set; }
		public string PostReconnectClientState { get; set; }
		public string PostReconnectHostHash { get; set; }
		public string PostReconnectClientHash { get; set; }
	}

	public readonly struct RevisionProbeResult
	{
		internal RevisionProbeResult(
			int revision, bool accepted, bool duplicate, bool outOfOrder, bool applied)
		{
			Revision = revision;
			Accepted = accepted;
			Duplicate = duplicate;
			OutOfOrder = outOfOrder;
			Applied = applied;
		}

		public int Revision { get; }
		public bool Accepted { get; }
		public bool Duplicate { get; }
		public bool OutOfOrder { get; }
		public bool Applied { get; }
	}

	public static class IntegrationScenarioEvidenceCore
	{
		private const string ReconnectScenario = "reconnect-world-state";
		private static readonly string[] ScenarioCatalog =
		{
			"remote-dig", "building-lifecycle", "research", "priority", "schedule",
			"building-config", "door", "uproot", "toggle", "inventory", "storage",
			"pickup", "deconstruct", "effect", "chat", "cursor", "animation", "motion",
			"entity-lifecycle", "dlc-runtime", "rocket", ReconnectScenario,
		};
		private static readonly HashSet<string> ScenarioSet =
			new HashSet<string>(ScenarioCatalog, StringComparer.Ordinal);
		private static readonly HashSet<string> PhaseSet = new HashSet<string>(StringComparer.Ordinal)
		{
			"host-submit", "client-apply", "client-original-blocked", "revision-accepted",
			"revision-duplicate", "revision-out-of-order", "final-state", "post-reconnect-state",
		};

		public static IReadOnlyList<string> Scenarios { get; } =
			new ReadOnlyCollection<string>(ScenarioCatalog);

		public static RevisionProbeResult ProbeRevision(int currentRevision, int incomingRevision)
		{
			if (incomingRevision > currentRevision)
				return new RevisionProbeResult(incomingRevision, true, false, false, true);
			if (incomingRevision == currentRevision)
				return new RevisionProbeResult(incomingRevision, false, true, false, false);
			return new RevisionProbeResult(incomingRevision, false, false, true, false);
		}

		public static void Log(
			string scenario, string phase, long revision, bool applied, string state)
		{
			if (!ScenarioSet.Contains(scenario))
				throw new ArgumentException("Unknown integration scenario.", nameof(scenario));
			if (!PhaseSet.Contains(phase))
				throw new ArgumentException("Unknown integration evidence phase.", nameof(phase));
			DebugConsole.Log(EvidenceLogCodec.Serialize(
				scenario, phase, revision, applied, state, HashState(state)));
		}

		public static string HashState(string state)
		{
			if (state == null)
				throw new ArgumentNullException(nameof(state));
			using (SHA256 sha256 = SHA256.Create())
			{
				byte[] digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(state));
				var result = new StringBuilder("sha256:", 71);
				foreach (byte value in digest)
					result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
				return result.ToString();
			}
		}

		public static bool Validate(IntegrationScenarioEvidence evidence)
		{
			if (evidence == null || !ScenarioSet.Contains(evidence.Scenario)
			    || !evidence.HostSubmitObserved || !evidence.ClientApplyObserved
			    || !evidence.ClientOriginalBlocked || evidence.HostSubmitRevision <= 0
			    || evidence.ClientApplyRevision != evidence.HostSubmitRevision)
				return false;

			if (!IsAccepted(evidence.Accepted, evidence.HostSubmitRevision)
			    || !IsDuplicate(evidence.Duplicate, evidence.HostSubmitRevision)
			    || !IsOutOfOrder(evidence.OutOfOrder, evidence.HostSubmitRevision))
				return false;

			if (!Matches(evidence.HostState, evidence.ClientState)
			    || !Matches(evidence.HostHash, evidence.ClientHash))
				return false;

			return evidence.Scenario != ReconnectScenario || ReconnectMatches(evidence);
		}

		private static bool IsAccepted(RevisionProbeResult result, int revision)
			=> result.Accepted && result.Applied && !result.Duplicate && !result.OutOfOrder
			   && result.Revision == revision;

		private static bool IsDuplicate(RevisionProbeResult result, int revision)
			=> !result.Accepted && !result.Applied && result.Duplicate && !result.OutOfOrder
			   && result.Revision == revision;

		private static bool IsOutOfOrder(RevisionProbeResult result, int revision)
			=> !result.Accepted && !result.Applied && !result.Duplicate && result.OutOfOrder
			   && result.Revision < revision;

		private static bool ReconnectMatches(IntegrationScenarioEvidence evidence)
			=> Matches(evidence.HostState, evidence.PostReconnectHostState)
			   && Matches(evidence.ClientState, evidence.PostReconnectClientState)
			   && Matches(evidence.HostHash, evidence.PostReconnectHostHash)
			   && Matches(evidence.ClientHash, evidence.PostReconnectClientHash)
			   && Matches(evidence.PostReconnectHostState, evidence.PostReconnectClientState)
			   && Matches(evidence.PostReconnectHostHash, evidence.PostReconnectClientHash);

		private static bool Matches(string expected, string actual)
			=> !string.IsNullOrEmpty(expected)
			   && string.Equals(expected, actual, StringComparison.Ordinal);
	}

	public sealed class EvidenceLogEntry
	{
		internal EvidenceLogEntry(
			string scenario, string phase, long revision, bool applied, string state, string hash)
		{
			Scenario = scenario;
			Phase = phase;
			Revision = revision;
			Applied = applied;
			State = state;
			Hash = hash;
		}

		public string Scenario { get; }
		public string Phase { get; }
		public long Revision { get; }
		public bool Applied { get; }
		public string State { get; }
		public string Hash { get; }
	}

	public static class EvidenceLogCodec
	{
		private const string Prefix = "[IntegrationEvidence] ";
		private static readonly string[] Keys =
		{
			"scenario", "phase", "revision", "applied", "state", "hash",
		};

		public static string Serialize(
			string scenario, string phase, long revision, bool applied, string state, string hash)
		{
			RequireValue(scenario, nameof(scenario));
			RequireValue(phase, nameof(phase));
			RequireValue(state, nameof(state));
			RequireValue(hash, nameof(hash));
			if (revision < 0)
				throw new ArgumentOutOfRangeException(nameof(revision));

			return Prefix + "scenario=" + scenario + ";phase=" + phase
			       + ";revision=" + revision.ToString(CultureInfo.InvariantCulture)
			       + ";applied=" + (applied ? "1" : "0")
			       + ";state=" + state + ";hash=" + hash;
		}

		public static EvidenceLogEntry Parse(string line)
		{
			if (line == null)
				throw new ArgumentNullException(nameof(line));
			if (!line.StartsWith(Prefix, StringComparison.Ordinal))
				throw new FormatException("Integration evidence prefix is invalid.");

			string[] fields = line.Substring(Prefix.Length).Split(';');
			if (fields.Length != Keys.Length)
				throw new FormatException("Integration evidence field count is invalid.");

			string[] values = new string[Keys.Length];
			for (int index = 0; index < fields.Length; index++)
				values[index] = ParseField(fields[index], Keys[index]);

			if (!long.TryParse(values[2], NumberStyles.None, CultureInfo.InvariantCulture,
				    out long revision))
				throw new FormatException("Integration evidence revision is invalid.");
			if (values[3] != "0" && values[3] != "1")
				throw new FormatException("Integration evidence applied flag is invalid.");

			return new EvidenceLogEntry(
				values[0], values[1], revision, values[3] == "1", values[4], values[5]);
		}

		private static string ParseField(string field, string expectedKey)
		{
			string prefix = expectedKey + "=";
			if (!field.StartsWith(prefix, StringComparison.Ordinal) || field.Length == prefix.Length)
				throw new FormatException("Integration evidence field is invalid: " + expectedKey);
			return field.Substring(prefix.Length);
		}

		private static void RequireValue(string value, string parameterName)
		{
			if (string.IsNullOrEmpty(value))
				throw new ArgumentException("Integration evidence values cannot be empty.", parameterName);
			if (value.IndexOfAny(new[] { ';', '\r', '\n' }) >= 0)
				throw new ArgumentException("Integration evidence values contain a delimiter.", parameterName);
		}
	}
}
#endif
