#if DEBUG
using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

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
		public TypedEvidenceEnvelope HostState { get; set; }
		public TypedEvidenceEnvelope ClientState { get; set; }
		public TypedEvidenceEnvelope PostReconnectHostState { get; set; }
		public TypedEvidenceEnvelope PostReconnectClientState { get; set; }
	}

	public readonly struct RevisionProbeResult
	{
		internal RevisionProbeResult(
			int revision, bool accepted, bool duplicate, bool outOfOrder, bool applied)
		{
			Revision = revision; Accepted = accepted; Duplicate = duplicate;
			OutOfOrder = outOfOrder; Applied = applied;
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
		private static readonly JsonSerializerSettings CompareSettings = new JsonSerializerSettings
		{
			ContractResolver = new CamelCasePropertyNamesContractResolver(),
			Formatting = Formatting.None,
			NullValueHandling = NullValueHandling.Include,
		};

		public static RevisionProbeResult ProbeRevision(int currentRevision, int incomingRevision)
		{
			if (incomingRevision > currentRevision)
				return new RevisionProbeResult(incomingRevision, true, false, false, true);
			if (incomingRevision == currentRevision)
				return new RevisionProbeResult(incomingRevision, false, true, false, false);
			return new RevisionProbeResult(incomingRevision, false, false, true, false);
		}

		public static void Log(TypedEvidenceEnvelope evidence)
			=> DebugConsole.Log(TypedEvidenceLogCodec.Serialize(evidence));

		public static bool Validate(IntegrationScenarioEvidence evidence)
		{
			if (!HasCausalFacts(evidence) || !HasRevisionFacts(evidence)) return false;
			if (!ValidFinal(evidence.HostState, evidence.Scenario, "host")
			    || !ValidFinal(evidence.ClientState, evidence.Scenario, "client")) return false;
			if (!SameCausalIdentity(evidence.HostState, evidence.ClientState)
			    || !SameTargetAndState(evidence.HostState, evidence.ClientState)) return false;
			return evidence.Scenario != ReconnectScenario || ReconnectMatches(evidence);
		}

		private static bool HasCausalFacts(IntegrationScenarioEvidence evidence)
			=> evidence != null && !string.IsNullOrEmpty(evidence.Scenario)
			   && evidence.HostSubmitObserved && evidence.ClientApplyObserved
			   && evidence.ClientOriginalBlocked && evidence.HostSubmitRevision > 0
			   && evidence.ClientApplyRevision == evidence.HostSubmitRevision;

		private static bool HasRevisionFacts(IntegrationScenarioEvidence evidence)
			=> IsAccepted(evidence.Accepted, evidence.HostSubmitRevision)
			   && IsDuplicate(evidence.Duplicate, evidence.HostSubmitRevision)
			   && IsOutOfOrder(evidence.OutOfOrder, evidence.HostSubmitRevision);

		private static bool ValidFinal(TypedEvidenceEnvelope value, string scenario, string role)
			=> value != null && value.Scenario == scenario && value.Role == role
			   && value.Phase == "final-state" && TypedEvidenceContract.Validate(value).Count == 0;

		private static bool SameCausalIdentity(TypedEvidenceEnvelope left, TypedEvidenceEnvelope right)
			=> left.RunId == right.RunId && left.DllHash == right.DllHash
			   && left.SessionEpoch == right.SessionEpoch
			   && left.ConnectionGeneration == right.ConnectionGeneration
			   && left.SnapshotGeneration == right.SnapshotGeneration
			   && left.RevisionDomain == right.RevisionDomain && left.Revision == right.Revision;

		private static bool SameTargetAndState(TypedEvidenceEnvelope left, TypedEvidenceEnvelope right)
			=> left.Target.GetType() == right.Target.GetType()
			   && left.State.GetType() == right.State.GetType()
			   && Canonical(left.Target) == Canonical(right.Target)
			   && Canonical(left.State) == Canonical(right.State)
			   && left.StateHash == right.StateHash;

		private static bool ReconnectMatches(IntegrationScenarioEvidence evidence)
		{
			TypedEvidenceEnvelope host = evidence.PostReconnectHostState;
			TypedEvidenceEnvelope client = evidence.PostReconnectClientState;
			return ValidReconnect(host, "host") && ValidReconnect(client, "client")
			       && SameCausalIdentity(host, client) && SameTargetAndState(host, client)
			       && SameTargetAndState(evidence.HostState, host)
			       && SameTargetAndState(evidence.ClientState, client);
		}

		private static bool ValidReconnect(TypedEvidenceEnvelope value, string role)
			=> value != null && value.Scenario == ReconnectScenario && value.Role == role
			   && value.Phase == "post-reconnect-state"
			   && TypedEvidenceContract.Validate(value).Count == 0;

		private static bool IsAccepted(RevisionProbeResult result, int revision)
			=> result.Accepted && result.Applied && !result.Duplicate && !result.OutOfOrder
			   && result.Revision == revision;
		private static bool IsDuplicate(RevisionProbeResult result, int revision)
			=> !result.Accepted && !result.Applied && result.Duplicate && !result.OutOfOrder
			   && result.Revision == revision;
		private static bool IsOutOfOrder(RevisionProbeResult result, int revision)
			=> !result.Accepted && !result.Applied && !result.Duplicate && result.OutOfOrder
			   && result.Revision < revision;
		private static string Canonical(object value)
			=> JsonConvert.SerializeObject(value, value.GetType(), CompareSettings);
	}
}
#endif
