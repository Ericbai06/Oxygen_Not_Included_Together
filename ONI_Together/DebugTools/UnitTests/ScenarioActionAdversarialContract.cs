namespace ONI_Together.DebugTools.UnitTests
{
	internal sealed class ScenarioActionAdversarialTrace
	{
		internal bool Tombstoned { get; set; }
		internal bool BindExistingOnly { get; set; }
		internal bool Materialized { get; set; }
		internal bool ReplicationSuppressed { get; set; }
		internal bool ExtraMembershipSend { get; set; }
		internal bool ExtraCarrySend { get; set; }
		internal long ExpectedGeneration { get; set; }
		internal string ExpectedCorrelation { get; set; }
		internal ScenarioActionAdmissionResult Admission { get; set; }
		internal bool EvidenceObserved { get; set; }
		internal long EvidenceGeneration { get; set; }
		internal string EvidenceCorrelation { get; set; }
		internal long EvidenceSequence { get; set; }
		internal bool MutationCreated { get; set; }
		internal bool HostObserved { get; set; }
		internal bool PacketSent { get; set; }
		internal bool DownstreamFailed { get; set; }
		internal bool RolledBack { get; set; }
		internal bool FailureOutcomeConsumed { get; set; }
		internal string FailureDomain { get; set; }
		internal int CleanupHostTargetCell { get; set; }
		internal int CleanupWireTargetCell { get; set; }
		internal int CleanupClientTargetCell { get; set; }
	}

	internal static class ScenarioActionAdversarialContract
	{
		internal static string Validate(ScenarioActionAdversarialTrace trace)
		{
			if (trace.Tombstoned && (trace.BindExistingOnly || !trace.Materialized))
				return "tombstoned pickup was not rematerialized";
			if (!trace.ReplicationSuppressed
			    || trace.ExtraMembershipSend || trace.ExtraCarrySend)
				return "mutation emitted transport outside the exact packet sequence";
			if (trace.Admission?.Accepted != true && trace.EvidenceObserved)
				return "evidence was synthesized outside accepted receiver delivery";
			if (trace.Admission?.Accepted == true
			    && (trace.Admission.Generation != trace.ExpectedGeneration
			        || trace.Admission.Correlation != trace.ExpectedCorrelation))
				return "accepted receiver admission does not match armed action";
			if (trace.EvidenceObserved
			    && (trace.EvidenceGeneration != trace.Admission.Generation
			        || trace.EvidenceCorrelation != trace.Admission.Correlation
			        || trace.EvidenceSequence != trace.Admission.Sequence))
				return "evidence provenance does not match receiver admission";
			if (!trace.MutationCreated && (trace.HostObserved || trace.PacketSent))
				return "null mutation produced host side effects";
			if (trace.MutationCreated && trace.DownstreamFailed && !trace.RolledBack)
				return "downstream failure left host mutation applied";
			if (trace.DownstreamFailed && !trace.FailureOutcomeConsumed)
				return trace.FailureDomain + " downstream failure outcome was ignored";
			if (trace.CleanupHostTargetCell == 0 || trace.CleanupWireTargetCell == 0
			    || trace.CleanupClientTargetCell == 0)
				return "pickup cleanup target cell is missing";
			if (trace.CleanupHostTargetCell != trace.CleanupWireTargetCell
			    || trace.CleanupHostTargetCell != trace.CleanupClientTargetCell)
				return "pickup cleanup canonical target drifted across host wire client";
			return null;
		}
	}
}
