#if DEBUG
using System.Collections.Generic;
using ONI_Together.Networking;
using UnityEngine;

namespace ONI_Together.DebugTools
{
	public sealed class FaultRuntimeTargetContext
	{
		public string CaseId { get; set; }
		public string TargetId { get; set; }
		public string BaselineHash { get; set; }
		public bool CleanControl { get; set; }
		public FaultRuntimeTargetContext Setup { get; set; }
		public FaultRuntimeReceipt Receipt { get; set; }
		public FaultRuntimeSnapshot Snapshot { get; set; }
		public TypedEvidenceEnvelope Oracle { get; set; }
		internal object Target { get; set; }
		internal BuildingDef BuildingDef { get; set; }
		internal int Cell { get; set; }
		internal IReadOnlyList<Tag> Materials { get; set; }
		internal string ExecutionTier { get; set; }
		internal int BaselineComponentCount { get; set; }
		internal bool BaselineIdentityPresent { get; set; }
		internal int ExceptionCount { get; set; }
		internal GameObject DeferredOriginal { get; set; }
		internal GameObject DeferredReplacement { get; set; }
		internal int DeferredOriginalNetId { get; set; }
		internal ulong DeferredOriginalLifecycle { get; set; }
		internal NetworkIdentityRegistry.LifecycleRevisionState DeferredLifecycleState { get; set; }
		internal float DeferredTemperature { get; set; }
		internal long DeferredDestroyRequestedFrame { get; set; }
		internal DeferredDestroyResetEvidence DeferredDestroyEvidence { get; set; }
	}

	public sealed class FaultRuntimeReceipt
	{
		public string ReceiptId { get; set; }
		public string TargetId { get; set; }
		public bool Consumed { get; set; }
		public bool Succeeded { get; set; }
	}

	public sealed class DeferredDestroyResetEvidence
	{
		public string FixtureIdentity { get; set; }
		public bool DisposableFixtureOwned { get; set; }
		public string LogicalTargetIdBefore { get; set; }
		public string LogicalTargetIdAfter { get; set; }
		public int OriginalInstanceId { get; set; }
		public int ReplacementInstanceId { get; set; }
		public long DestroyRequestedFrame { get; set; }
		public long DestroyBarrierFrame { get; set; }
		public bool OriginalDestroyed { get; set; }
		public bool ReplacementBound { get; set; }
		public string BaselineHashBefore { get; set; }
		public string BaselineHashAfter { get; set; }
		public int CleanControlInstanceId { get; set; }
		public string CleanControlTargetId { get; set; }
		public bool FixtureDisposed { get; set; }
		public bool PendingCleared { get; set; }
		public bool SeamCleaned { get; set; }
		public bool FixtureDisposeRequested { get; set; }
		public long FixtureDisposeRequestedFrame { get; set; }
		public long FixtureDisposeObservedFrame { get; set; }
		public bool FixtureAbsent { get; set; }
		public bool PendingPreservedForRetry { get; set; }
	}

	public sealed class FaultRuntimeSnapshot
	{
		public string TargetId { get; set; }
		public string StateHash { get; set; }
		public bool InvariantPreserved { get; set; }
		public int ComponentCount { get; set; }
		public int ComponentCountBefore { get; set; }
		public int ComponentCountAfter { get; set; }
		public bool IdentityPresent { get; set; }
		public bool IdentityPresentBefore { get; set; }
		public bool IdentityPresentAfter { get; set; }
		public int ExceptionCount { get; set; }
		public TypedEvidenceEnvelope Evidence { get; set; }
	}
}
#endif
