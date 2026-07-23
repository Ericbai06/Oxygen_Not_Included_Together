#if DEBUG
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ONI_Together.DebugTools
{
	public interface ITypedEvidenceTarget { }
	public interface ITypedEvidenceState { }

	public sealed class TypedEvidenceEnvelope
	{
		[JsonIgnore] public bool Passed { get; set; }
		[JsonIgnore] public string ObservedTargetId { get; set; }
		[JsonIgnore] public string BeforeHash { get; set; }
		[JsonIgnore] public string AfterHash { get; set; }
		[JsonIgnore] public bool InvariantPreserved { get; set; }
		[JsonIgnore] public bool NoNewComponents { get; set; }
		[JsonIgnore] public bool NoNewIdentity { get; set; }
		[JsonIgnore] public bool ExceptionFree { get; set; }
		[JsonProperty("schemaVersion", Order = 1)] public int SchemaVersion { get; set; }
		[JsonProperty("runId", Order = 2)] public string RunId { get; set; }
		[JsonProperty("dllHash", Order = 3)] public string DllHash { get; set; }
		[JsonProperty("scenario", Order = 4)] public string Scenario { get; set; }
		[JsonProperty("entryId", Order = 5)] public string EntryId { get; set; }
		[JsonProperty("role", Order = 6)] public string Role { get; set; }
		[JsonProperty("sessionEpoch", Order = 7)] public long SessionEpoch { get; set; }
		[JsonProperty("connectionGeneration", Order = 8)] public long ConnectionGeneration { get; set; }
		[JsonProperty("snapshotGeneration", Order = 9)] public long SnapshotGeneration { get; set; }
		[JsonProperty("phase", Order = 10)] public string Phase { get; set; }
		[JsonProperty("revisionDomain", Order = 11)] public string RevisionDomain { get; set; }
		[JsonProperty("revision", Order = 12)] public long Revision { get; set; }
		[JsonProperty("sequence", Order = 13)] public long Sequence { get; set; }
		[JsonProperty("actionGeneration", Order = 14)] public long ActionGeneration { get; set; }
		[JsonProperty("actionCorrelation", Order = 15)] public string ActionCorrelation { get; set; } = string.Empty;
		[JsonProperty("actionSequence", Order = 16)] public long ActionSequence { get; set; }
		[JsonProperty("target", Order = 17)] public ITypedEvidenceTarget Target { get; set; }
		[JsonProperty("state", Order = 18)] public ITypedEvidenceState State { get; set; }
		[JsonProperty("stateHash", Order = 19)] public string StateHash { get; set; }

		public bool ShouldSerializeActionGeneration() => HasActionAdmission();
		public bool ShouldSerializeActionCorrelation() => HasActionAdmission();
		public bool ShouldSerializeActionSequence() => HasActionAdmission();

		private bool HasActionAdmission()
			=> ActionGeneration != 0 || ActionSequence != 0
			   || !string.IsNullOrEmpty(ActionCorrelation);
	}

	public sealed class RemoteDigTarget : ITypedEvidenceTarget { public long MinionNetId { get; set; } public long TargetNetId { get; set; } public int TargetCell { get; set; } }
	public sealed class AnimationTarget : ITypedEvidenceTarget { public long MinionNetId { get; set; } public long TargetNetId { get; set; } public int TargetCell { get; set; } }
	public sealed class MotionTarget : ITypedEvidenceTarget { public long EntityNetId { get; set; } }
	public sealed class EffectTarget : ITypedEvidenceTarget { public long MinionNetId { get; set; } }
	public sealed class BuildingLifecycleTarget : ITypedEvidenceTarget { public string Prefab { get; set; } public int Cell { get; set; } public long NetId { get; set; } }
	public sealed class PriorityTarget : ITypedEvidenceTarget { public long TargetNetId { get; set; } }
	public sealed class BuildingConfigTarget : ITypedEvidenceTarget { public long TargetNetId { get; set; } }
	public sealed class DoorTarget : ITypedEvidenceTarget { public long TargetNetId { get; set; } }
	public sealed class UprootTarget : ITypedEvidenceTarget { public long TargetNetId { get; set; } }
	public sealed class ToggleTarget : ITypedEvidenceTarget { public long TargetNetId { get; set; } }
	public sealed class ResearchTarget : ITypedEvidenceTarget { public string TechId { get; set; } }
	public sealed class ScheduleTarget : ITypedEvidenceTarget { public string ScheduleId { get; set; } }
	public sealed class InventoryTarget : ITypedEvidenceTarget { }
	public sealed class StorageTarget : ITypedEvidenceTarget { public long StorageNetId { get; set; } public long ItemNetId { get; set; } }
	public sealed class PickupTarget : ITypedEvidenceTarget { public long ItemNetId { get; set; } public int TargetCell { get; set; } }
	public sealed class DeconstructTarget : ITypedEvidenceTarget { public long BuildingNetId { get; set; } public int TargetCell { get; set; } }
	public sealed class ChatTarget : ITypedEvidenceTarget { public string Sender { get; set; } }
	public sealed class CursorTarget : ITypedEvidenceTarget { public string PlayerId { get; set; } }
	public sealed class EntityLifecycleTarget : ITypedEvidenceTarget { public long NetId { get; set; } public string Prefab { get; set; } public int WorldId { get; set; } }
	public sealed class DlcRuntimeTarget : ITypedEvidenceTarget { public string DlcFamily { get; set; } public string Prefab { get; set; } public string Identity { get; set; } }
	public sealed class RocketTarget : ITypedEvidenceTarget { public long RocketNetId { get; set; } public long PadNetId { get; set; } }
	public sealed class ReconnectWorldStateTarget : ITypedEvidenceTarget { public string PeerId { get; set; } }

	public sealed class RemoteDigState : ITypedEvidenceState
	{
		[JsonProperty("action", Order = 1)] public string Action { get; set; }
		[JsonProperty("animation", Order = 2)] public string Animation { get; set; }
		[JsonProperty("tool", Order = 4)] public string Tool { get; set; }
		[JsonProperty("progress", Order = 3)] public double Progress { get; set; }
	}
	public sealed class AnimationState : ITypedEvidenceState { public string Action { get; set; } public string Animation { get; set; } public string Tool { get; set; } public double Progress { get; set; } }
	public sealed class MotionState : ITypedEvidenceState { public long Tick { get; set; } public double[] StartPosition { get; set; } public double[] EndPosition { get; set; } public string NavigationState { get; set; } public long MotionRevision { get; set; } }
	public sealed class EffectState : ITypedEvidenceState { public string EffectHash { get; set; } public bool Active { get; set; } }
	public sealed class BuildingLifecycleState : ITypedEvidenceState { public long LifecycleRevision { get; set; } public bool Queued { get; set; } public bool Completed { get; set; } }
	public sealed class PriorityState : ITypedEvidenceState { public long LifecycleRevision { get; set; } public long BaseRevision { get; set; } public long StateRevision { get; set; } public int Priority { get; set; } }
	public sealed class BuildingConfigState : ITypedEvidenceState { public long LifecycleRevision { get; set; } public long BaseRevision { get; set; } public long StateRevision { get; set; } public string ConfigKind { get; set; } public string ConfigValue { get; set; } }
	public sealed class DoorState : ITypedEvidenceState { public long LifecycleRevision { get; set; } public long StateRevision { get; set; } public string Control { get; set; } }
	public sealed class UprootState : ITypedEvidenceState { public long LifecycleRevision { get; set; } public long StateRevision { get; set; } public bool Uprooted { get; set; } }
	public sealed class ToggleState : ITypedEvidenceState { public long LifecycleRevision { get; set; } public long StateRevision { get; set; } public bool Toggled { get; set; } }
	public sealed class ResearchState : ITypedEvidenceState { public long Revision { get; set; } public bool Completed { get; set; } public double Progress { get; set; } }
	public sealed class ScheduleState : ITypedEvidenceState { public long Revision { get; set; } public List<ScheduleBlockState> Blocks { get; set; } }
	public sealed class ScheduleBlockState { public int Start { get; set; } public string Group { get; set; } }
	public sealed class InventoryState : ITypedEvidenceState { public List<InventoryResourceState> Resources { get; set; } }
	public sealed class InventoryResourceState { public string Tag { get; set; } public double Amount { get; set; } }
	public sealed class StorageState : ITypedEvidenceState { public bool Membership { get; set; } public double Amount { get; set; } }
	public sealed class PickupState : ITypedEvidenceState { public string Action { get; set; } public bool Tombstone { get; set; } }
	public sealed class DeconstructState : ITypedEvidenceState { public string Action { get; set; } public bool Tombstone { get; set; } }
	public sealed class ChatState : ITypedEvidenceState { public long Sequence { get; set; } public long Timestamp { get; set; } public string MessageHash { get; set; } }
	public sealed class CursorEvidenceState : ITypedEvidenceState { public long ConnectionGeneration { get; set; } public double[] WorldPosition { get; set; } public double[] ViewPosition { get; set; } public string DragState { get; set; } public string BuildState { get; set; } }
	public sealed class EntityLifecycleState : ITypedEvidenceState { public long LifecycleRevision { get; set; } public bool Active { get; set; } public bool Tombstone { get; set; } }
	public sealed class DlcRuntimeState : ITypedEvidenceState { public string StateMachineState { get; set; } public long AdmissionGeneration { get; set; } }
	public sealed class RocketState : ITypedEvidenceState { public string Destination { get; set; } public string CraftPhase { get; set; } public long SettingsRevision { get; set; } }
	public sealed class ReconnectWorldStateState : ITypedEvidenceState
	{
		public long ConnectionGeneration { get; set; }
		public long SnapshotGeneration { get; set; }
		public ReconnectDomainRecord Grid { get; set; }
		public ReconnectDomainRecord Entity { get; set; }
		public ReconnectDomainRecord World { get; set; }
		public ReconnectDomainRecord Storage { get; set; }
		public ReconnectDomainRecord ClusterRocket { get; set; }
	}
	public sealed class ReconnectDomainRecord { public int Count { get; set; } public string Hash { get; set; } }
}
#endif
