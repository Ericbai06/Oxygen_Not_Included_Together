using System;
using System.Collections.Generic;

namespace ONI_Together.DebugTools.UnitTests
{
	internal sealed class ExpectedFaultCase
	{
		internal ExpectedFaultCase(string id, string domain, string seam, string value,
			string oracle, string invariant, string reset, string testId, string scenarioId,
			string tier)
		{
			Id = id;
			Domain = domain;
			InjectionSeam = seam;
			InjectionValue = value;
			ExpectedOracle = oracle;
			StateInvariant = invariant;
			Reset = reset;
			TestId = testId;
			ScenarioId = scenarioId;
			ExecutionTier = tier;
		}

		internal string Id { get; }
		internal string Domain { get; }
		internal string InjectionSeam { get; }
		internal string InjectionValue { get; }
		internal string ExpectedOracle { get; }
		internal string StateInvariant { get; }
		internal string Reset { get; }
		internal string CleanControl => "rerun same seam without injection";
		internal string TestId { get; }
		internal string ScenarioId { get; }
		internal string ExecutionTier { get; }
	}

	internal static class FaultInjectionMatrixFixture
	{
		internal static readonly IReadOnlyList<ExpectedFaultCase> Cases = new[]
		{
			Case("duplicant.personality-missing", "duplicant-spawn", "MinionIdentity.OnSpawn personality lookup", "personality=missing", "spawn rejected without NullReferenceException", "preview and live rosters unchanged", "restore personality resource", "ingame:fault:duplicant-personality-missing", "fault-duplicant-personality-missing", "ingame"),
			Case("duplicant.set-minion-before-controller", "duplicant-spawn", "Immigration SetMinion/SetController order", "SetMinion first", "operation deferred until controller exists", "no partially initialized minion identity", "restore controller-first order", "ingame:fault:duplicant-set-order", "fault-duplicant-set-order", "ingame"),
			Case("duplicant.preview-flatulence", "duplicant-spawn", "MinionSelectPreview Flatulence emit", "preview emits", "preview emit is ignored safely", "preview creates no gameplay gas or packet", "destroy preview and clear hook", "ingame:fault:duplicant-preview-flatulence", "fault-duplicant-preview-flatulence", "ingame"),
			Case("duplicant.destroyed-add-component", "duplicant-spawn", "MinionMultiplayerInitializer component attach", "gameObject=destroyed", "attach is rejected without exception", "no orphan network component or identity", "clear destroyed fixture", "ingame:fault:duplicant-destroyed-add-component", "fault-duplicant-destroyed-add-component", "ingame"),

			Case("work.workable-unregistered", "work", "Workable registry lookup", "workable=unregistered", "work packet rejected", "worker and workable remain idle", "restore workable registry", "ingame:fault:work-unregistered", "fault-work-unregistered", "ingame"),
			Case("work.target-missing", "work", "work target resolution", "target=null", "work packet rejected", "no chore or progress mutation", "restore target resolver", "ingame:fault:work-target-missing", "fault-work-target-missing", "ingame"),
			Case("work.original-dig-element-null", "work", "Diggable originalDigElement", "originalDigElement=null", "dig start rejected safely", "cell element and progress unchanged", "restore original element", "ingame:fault:work-null-dig-element", "fault-work-null-dig-element", "ingame"),
			Case("work.revision-stale", "work", "work revision gate", "revision=current-1", "stale update rejected", "current work revision and state unchanged", "reset revision ledger", "headless:fault:work-stale-revision", "fault-work-stale-revision", "headless"),
			Case("work.client-native-start", "work", "client Workable.StartWork guard", "native StartWork=true", "client native start blocked", "only authoritative remote work may start", "restore client work guard", "ingame:fault:work-client-native-start", "fault-work-client-native-start", "ingame"),

			Case("building.selected-elements-null", "building", "construction SelectedElementsTags", "selectedElementsTags=null", "queue request rejected safely", "no construction site or resource debit", "restore selected element tags", "ingame:fault:building-null-elements", "fault-building-null-elements", "ingame"),
			Case("building.complete-before-queued", "building", "building lifecycle gate", "Complete before Queued", "completion rejected", "lifecycle remains absent", "reset lifecycle ledger", "headless:fault:building-complete-before-queued", "fault-building-complete-before-queued", "headless"),
			Case("building.finish-duplicate", "building", "FinishConstruction idempotency gate", "FinishConstruction twice", "duplicate ignored", "single completion and revision increment", "reset lifecycle ledger", "headless:fault:building-duplicate-finish", "fault-building-duplicate-finish", "headless"),
			Case("building.net-id-collision", "building", "building NetId admission", "NetId already owned", "second identity rejected", "original identity mapping unchanged", "reset identity registry", "headless:fault:building-netid-collision", "fault-building-netid-collision", "headless"),
			Case("building.destroy-deferred", "building", "Unity Object.Destroy lifecycle", "destroy deferred one frame", "tombstone waits for destroyed object", "no live lookup after tombstone", "advance frame and clear object", "ingame:fault:building-deferred-destroy", "fault-building-deferred-destroy", "ingame"),

			Case("inventory.storage-missing", "inventory", "inventory storage resolver", "storage=null", "delta rejected", "resource totals unchanged", "reset inventory ledger", "headless:fault:inventory-storage-missing", "fault-inventory-storage-missing", "headless"),
			Case("inventory.item-missing", "inventory", "inventory item resolver", "item=null", "delta rejected", "membership and totals unchanged", "reset inventory ledger", "headless:fault:inventory-item-missing", "fault-inventory-item-missing", "headless"),
			Case("inventory.membership-wrong", "inventory", "storage membership gate", "item belongs to other storage", "delta rejected", "both storage memberships unchanged", "reset inventory ledger", "headless:fault:inventory-membership", "fault-inventory-membership", "headless"),
			Case("inventory.mass-zero", "inventory", "inventory quantity gate", "mass=0", "zero delta ignored", "totals and revision unchanged", "reset inventory ledger", "headless:fault:inventory-zero-mass", "fault-inventory-zero-mass", "headless"),
			Case("inventory.delta-duplicate", "inventory", "inventory sequence gate", "sequence repeated", "duplicate delta ignored", "quantity applied exactly once", "reset inventory sequence", "headless:fault:inventory-duplicate-delta", "fault-inventory-duplicate-delta", "headless"),
			Case("inventory.delta-out-of-order", "inventory", "inventory sequence gate", "sequence current-1", "out-of-order delta rejected", "quantity and revision unchanged", "reset inventory sequence", "headless:fault:inventory-out-of-order", "fault-inventory-out-of-order", "headless"),

			Case("entity.state-before-identity", "entity", "entity domain-state admission", "domain state before identity", "state deferred", "no anonymous entity state applied", "reset entity ledger", "headless:fault:entity-state-before-identity", "fault-entity-state-before-identity", "headless"),
			Case("entity.despawn-before-spawn", "entity", "entity lifecycle gate", "despawn before spawn", "despawn recorded as tombstone", "later stale spawn cannot activate", "reset entity ledger", "headless:fault:entity-despawn-before-spawn", "fault-entity-despawn-before-spawn", "headless"),
			Case("entity.spawn-after-tombstone", "entity", "entity lifecycle revision gate", "old spawn after tombstone", "old spawn rejected", "tombstone revision remains authoritative", "reset entity ledger", "headless:fault:entity-old-spawn", "fault-entity-old-spawn", "headless"),
			Case("entity.prefab-null", "entity", "entity spawn admission", "prefab=null", "spawn rejected", "identity table and lifecycle unchanged", "reset entity ledger", "headless:fault:entity-null-prefab", "fault-entity-null-prefab", "headless"),

			Case("dlc.fingerprint-mismatch", "dlc", "DLC admission fingerprint", "remote fingerprint differs", "session admission rejected", "no DLC entity admitted", "reset admission generation", "headless:fault:dlc-fingerprint", "fault-dlc-fingerprint", "headless"),
			Case("dlc.prefab-missing", "dlc", "Assets prefab resolver", "prefab=missing", "runtime spawn rejected safely", "no identity or state-machine record", "restore prefab fixture", "ingame:fault:dlc-prefab-missing", "fault-dlc-prefab-missing", "ingame"),
			Case("dlc.state-before-start-sm", "dlc", "DLC state-machine admission", "state before StartSM", "state deferred", "machine is not transitioned before start", "clear deferred state and restart", "ingame:fault:dlc-state-before-startsm", "fault-dlc-state-before-startsm", "ingame"),
			Case("dlc.family-aquatic", "dlc", "Aquatic runtime prefab/state hook", "family=Aquatic", "family handler reaches typed state", "identity and state-machine generation agree", "despawn fixture and reset family hook", "real:fault:dlc-aquatic", "fault-dlc-aquatic", "real"),
			Case("dlc.family-bionic", "dlc", "Bionic runtime prefab/state hook", "family=Bionic", "family handler reaches typed state", "identity and state-machine generation agree", "despawn fixture and reset family hook", "real:fault:dlc-bionic", "fault-dlc-bionic", "real"),
			Case("dlc.family-frosty", "dlc", "Frosty runtime prefab/state hook", "family=Frosty", "family handler reaches typed state", "identity and state-machine generation agree", "despawn fixture and reset family hook", "real:fault:dlc-frosty", "fault-dlc-frosty", "real"),
			Case("dlc.family-prehistoric", "dlc", "Prehistoric runtime prefab/state hook", "family=Prehistoric", "family handler reaches typed state", "identity and state-machine generation agree", "despawn fixture and reset family hook", "real:fault:dlc-prehistoric", "fault-dlc-prehistoric", "real"),
			Case("dlc.family-spaced-out", "dlc", "SpacedOut runtime prefab/state hook", "family=SpacedOut", "family handler reaches typed state", "identity and state-machine generation agree", "despawn fixture and reset family hook", "real:fault:dlc-spaced-out", "fault-dlc-spaced-out", "real"),
			Case("dlc.family-common", "dlc", "Common runtime prefab/state hook", "family=Common", "family handler reaches typed state", "identity and state-machine generation agree", "despawn fixture and reset family hook", "real:fault:dlc-common", "fault-dlc-common", "real"),

			Case("reconnect.session-stale", "reconnect", "session epoch gate", "sessionEpoch=current-1", "record rejected", "current snapshot unchanged", "reset reconnect ledger", "headless:fault:reconnect-stale-session", "fault-reconnect-stale-session", "headless"),
			Case("reconnect.connection-stale", "reconnect", "connection generation gate", "connectionGeneration=current-1", "record rejected", "current snapshot unchanged", "reset reconnect ledger", "headless:fault:reconnect-stale-connection", "fault-reconnect-stale-connection", "headless"),
			Case("reconnect.snapshot-stale", "reconnect", "snapshot generation gate", "snapshotGeneration=current-1", "record rejected", "current snapshot unchanged", "reset reconnect ledger", "headless:fault:reconnect-stale-snapshot", "fault-reconnect-stale-snapshot", "headless"),
			Case("reconnect.batch-missing", "reconnect", "snapshot batch completion gate", "batch index missing", "snapshot commit rejected", "prior committed state remains visible", "reset batch accumulator", "headless:fault:reconnect-missing-batch", "fault-reconnect-missing-batch", "headless"),
			Case("reconnect.batch-duplicate", "reconnect", "snapshot batch sequence gate", "batch index repeated", "duplicate batch ignored", "batch contents applied exactly once", "reset batch accumulator", "headless:fault:reconnect-duplicate-batch", "fault-reconnect-duplicate-batch", "headless"),
			Case("reconnect.ack-lost", "reconnect", "snapshot ACK ledger", "ACK dropped", "sender retains retry state", "snapshot is not falsely acknowledged", "reset ACK ledger", "headless:fault:reconnect-lost-ack", "fault-reconnect-lost-ack", "headless"),
			Case("reconnect.disconnect-mid-apply", "reconnect", "transactional snapshot apply", "disconnect during apply", "partial apply rolled back", "last committed state remains visible", "reset apply transaction", "headless:fault:reconnect-disconnect-mid-apply", "fault-reconnect-disconnect-mid-apply", "headless"),
		};

		private static ExpectedFaultCase Case(string id, string domain, string seam, string value,
			string oracle, string invariant, string reset, string testId, string scenarioId, string tier)
			=> new ExpectedFaultCase(id, domain, seam, value, oracle, invariant, reset, testId,
				scenarioId, tier);
	}
}
