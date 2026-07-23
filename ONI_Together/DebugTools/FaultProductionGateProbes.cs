#if DEBUG
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Tools;
using ONI_Together.Networking.Packets.Tools.Build;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Packets.World.Buildings;
using System;

namespace ONI_Together.DebugTools
{
	internal static class FaultWorkProbe
	{
		internal static FaultProbeResult Execute(string caseId)
		{
			if (caseId != "work.revision-stale")
				throw new InvalidOperationException("Unsupported work fault " + caseId);
			const string domain = "fault-injection:work-stale";
			const int netId = -987654;
			NetworkIdentityRegistry.ResetStateRevisionDomain(domain);
			bool seeded = NetworkIdentityRegistry.TryAcceptStateRevision(netId, domain, 5);
			bool rejected = seeded
			                && !NetworkIdentityRegistry.TryAcceptStateRevision(netId, domain, 4);
			bool invariant = NetworkIdentityRegistry.GetLastStateRevision(netId, domain) == 5;
			NetworkIdentityRegistry.ResetStateRevisionDomain(domain);
			bool reset = NetworkIdentityRegistry.GetLastStateRevision(netId, domain) == 0;
			bool clean = NetworkIdentityRegistry.TryAcceptStateRevision(netId, domain, 1);
			NetworkIdentityRegistry.ResetStateRevisionDomain(domain);
			return new FaultProbeResult(rejected, invariant, reset, clean,
				"inject:revision=current-1",
				"production-gate:NetworkIdentityRegistry.TryAcceptStateRevision",
				"reset:ResetStateRevisionDomain", "clean:newer-revision-accepted");
		}

		internal static FaultProbeResult Stateless(
			string caseId, bool oracle, bool clean, string productionSymbol)
			=> new FaultProbeResult(oracle, oracle, true, clean,
				"inject:" + caseId, "production-gate:" + productionSymbol,
				"reset:stateless-gate", "clean:valid-input-accepted");
	}

	internal static class FaultBuildingProbe
	{
		internal static FaultProbeResult Execute(string caseId)
		{
			switch (caseId)
			{
				case "building.complete-before-queued":
					return CompleteBeforeQueue(caseId);
				case "building.finish-duplicate":
					return DuplicateFinish(caseId);
				case "building.net-id-collision":
					return NetIdCollision(caseId);
				default: throw new InvalidOperationException("Unsupported building fault " + caseId);
			}
		}

		private static FaultProbeResult CompleteBeforeQueue(string id)
		{
			bool rejected = !BuildLifecycleAdmission.CanComplete(
				true, true, false, true, true);
			bool clean = BuildLifecycleAdmission.CanComplete(
				true, true, true, true, true);
			return FaultWorkProbe.Stateless(id, rejected, clean,
				"BuildLifecycleAdmission.CanComplete");
		}

		private static FaultProbeResult DuplicateFinish(string id)
		{
			bool rejected = !NetworkIdentityRegistry.ShouldAcceptLifecycleRevision(7, 7)
			                && BuildCompletePacket.IsAlreadyAppliedLifecycle(7, 7, true, true);
			bool clean = NetworkIdentityRegistry.ShouldAcceptLifecycleRevision(7, 8);
			return FaultWorkProbe.Stateless(id, rejected, clean,
				"NetworkIdentityRegistry.ShouldAcceptLifecycleRevision");
		}

		private static FaultProbeResult NetIdCollision(string id)
		{
			bool rejected = !NetworkIdentityRegistry.CanRegisterExisting(71, true, false);
			bool clean = NetworkIdentityRegistry.CanRegisterExisting(71, true, true)
			             && NetworkIdentityRegistry.CanRegisterExisting(71, false, false);
			return FaultWorkProbe.Stateless(id, rejected, clean,
				"NetworkIdentityRegistry.CanRegisterExisting");
		}
	}

	internal static class FaultInventoryProbe
	{
		internal static FaultProbeResult Execute(string caseId)
		{
			bool rejected;
			string symbol = "StorageItemPacket.CanApplyResolvedTransfer";
			switch (caseId)
			{
				case "inventory.storage-missing":
					rejected = !StorageItemPacket.CanApplyResolvedTransfer(false, true, true, 2); break;
				case "inventory.item-missing":
					rejected = !StorageItemPacket.CanApplyResolvedTransfer(true, false, true, 2); break;
				case "inventory.membership-wrong":
					rejected = !StorageItemPacket.CanApplyResolvedTransfer(true, true, false, 2); break;
				case "inventory.mass-zero":
					rejected = !StorageItemPacket.CanApplyResolvedTransfer(true, true, true, 0); break;
				case "inventory.delta-duplicate":
					rejected = !RevisionAccepted(6, 6); symbol = "StorageItemPacket.ShouldApplyRevision"; break;
				case "inventory.delta-out-of-order":
					rejected = !RevisionAccepted(6, 5); symbol = "StorageItemPacket.ShouldApplyRevision"; break;
				default: throw new InvalidOperationException("Unsupported inventory fault " + caseId);
			}
			bool clean = caseId.StartsWith("inventory.delta-", StringComparison.Ordinal)
				? RevisionAccepted(6, 7)
				: StorageItemPacket.CanApplyResolvedTransfer(true, true, true, 2);
			return FaultWorkProbe.Stateless(caseId, rejected, clean, symbol);
		}

		private static bool RevisionAccepted(ulong current, ulong incoming)
			=> StorageItemPacket.ShouldApplyRevision(current, current, current, current, incoming);
	}

	internal static class FaultEntityProbe
	{
		internal static FaultProbeResult Execute(string caseId)
		{
			switch (caseId)
			{
				case "entity.state-before-identity":
					return LifecycleState(caseId);
				case "entity.despawn-before-spawn":
					return DespawnBeforeSpawn(caseId);
				case "entity.spawn-after-tombstone":
					return SpawnAfterTombstone(caseId);
				case "entity.prefab-null":
					return NullPrefab(caseId);
				default: throw new InvalidOperationException("Unsupported entity fault " + caseId);
			}
		}

		private static FaultProbeResult LifecycleState(string id)
		{
			bool rejected = !NetworkIdentityRegistry.CanApplyDomainState(
				false, false, 0, false, 1);
			bool clean = NetworkIdentityRegistry.CanApplyDomainState(
				true, true, 1, false, 1);
			return FaultWorkProbe.Stateless(id, rejected, clean,
				"NetworkIdentityRegistry.CanApplyDomainState");
		}

		private static FaultProbeResult DespawnBeforeSpawn(string id)
		{
			const int netId = -987653;
			NetworkIdentityRegistry.LifecycleRevisionState original =
				NetworkIdentityRegistry.CaptureLifecycleRevisionState(netId);
			bool accepted = NetworkIdentityRegistry.TryAcceptLifecycleRevision(netId, 2, true);
			bool invariant = accepted && NetworkIdentityRegistry.IsLifecycleTombstoned(netId)
			                 && !NetworkIdentityRegistry.ShouldAcceptLifecycleRevision(2, 1);
			NetworkIdentityRegistry.RestoreLifecycleRevisionState(netId, original);
			bool reset = NetworkIdentityRegistry.GetLastLifecycleRevision(netId)
			             == (original.HasRevision ? original.Revision : 0);
			bool clean = NetworkIdentityRegistry.TryAcceptLifecycleRevision(netId, 3, false);
			NetworkIdentityRegistry.RestoreLifecycleRevisionState(netId, original);
			return new FaultProbeResult(accepted, invariant, reset, clean,
				"inject:despawn-before-spawn",
				"production-gate:NetworkIdentityRegistry.TryAcceptLifecycleRevision",
				"reset:RestoreLifecycleRevisionState", "clean:new-lifecycle-accepted");
		}

		private static FaultProbeResult SpawnAfterTombstone(string id)
		{
			bool rejected = !NetworkIdentityRegistry.ShouldAcceptLifecycleRevision(3, 2);
			bool clean = NetworkIdentityRegistry.ShouldAcceptLifecycleRevision(3, 4);
			return FaultWorkProbe.Stateless(id, rejected, clean, "SpawnPrefabPacket.ShouldApply");
		}

		private static FaultProbeResult NullPrefab(string id)
		{
			bool rejected = !NetworkIdentityRegistry.CanAdmitPrefab(0, false, false);
			bool clean = NetworkIdentityRegistry.CanAdmitPrefab(1234, true, false);
			return FaultWorkProbe.Stateless(id, rejected, clean,
				"NetworkIdentityRegistry.CanAdmitPrefab");
		}
	}
}
#endif
