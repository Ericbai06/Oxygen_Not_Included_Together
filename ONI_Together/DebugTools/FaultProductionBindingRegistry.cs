#if DEBUG
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Tools.Build;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Packets.World.Buildings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ONI_Together.DebugTools
{
	public sealed class FaultProductionBinding
	{
		internal FaultProductionBinding(string caseId, MethodInfo gate, MethodInfo runtimeCallsite)
		{
			CaseId = caseId;
			GateMethod = gate;
			RuntimeCallsite = runtimeCallsite;
		}

		public string CaseId { get; }
		public MethodInfo GateMethod { get; }
		public MethodInfo RuntimeCallsite { get; }
	}

	public static class FaultProductionBindingRegistry
	{
		public static readonly IReadOnlyList<FaultProductionBinding> Bindings = Build();

		public static FaultProductionBinding Resolve(string caseId)
			=> Bindings.Single(binding => binding.CaseId == caseId);

		internal static FaultProbeResult ExecuteHeadless(string caseId)
		{
			Resolve(caseId);
			if (caseId.StartsWith("work.", StringComparison.Ordinal)) return FaultWorkProbe.Execute(caseId);
			if (caseId.StartsWith("building.", StringComparison.Ordinal)) return FaultBuildingProbe.Execute(caseId);
			if (caseId.StartsWith("inventory.", StringComparison.Ordinal)) return FaultInventoryProbe.Execute(caseId);
			if (caseId.StartsWith("entity.", StringComparison.Ordinal)) return FaultEntityProbe.Execute(caseId);
			if (caseId.StartsWith("dlc.", StringComparison.Ordinal)) return FaultDlcProbe.Execute(caseId);
			if (caseId.StartsWith("reconnect.", StringComparison.Ordinal)) return FaultReconnectProbe.Execute(caseId);
			throw new InvalidOperationException("No production binding executor for " + caseId);
		}

		private static IReadOnlyList<FaultProductionBinding> Build()
		{
			var result = new List<FaultProductionBinding>();
			BindWork(result);
			BindBuilding(result);
			BindInventory(result);
			BindEntity(result);
			BindDlc(result);
			BindReconnect(result);
			return result;
		}

		private static void BindWork(ICollection<FaultProductionBinding> result)
			=> Add(result, "work.revision-stale",
				One(typeof(NetworkIdentityRegistry), "TryAcceptStateRevision"),
				One(typeof(WorkableProgressPacket), "OnDispatched"));

		private static void BindBuilding(ICollection<FaultProductionBinding> result)
		{
			Add(result, "building.complete-before-queued",
				One(typeof(BuildLifecycleAdmission), "CanComplete"),
				One(typeof(BuildCompletePacket), "TryResolveTarget"));
			Add(result, "building.finish-duplicate",
				One(typeof(NetworkIdentityRegistry), "ShouldAcceptLifecycleRevision"),
				One(typeof(NetworkIdentityRegistry), "TryAcceptLifecycleRevision"));
			Add(result, "building.net-id-collision",
				One(typeof(NetworkIdentityRegistry), "CanRegisterExisting"),
				One(typeof(NetworkIdentityRegistry), "RegisterExisting"));
		}

		private static void BindInventory(ICollection<FaultProductionBinding> result)
		{
			MethodInfo resolved = One(typeof(StorageItemPacket), "CanApplyResolvedTransfer");
			MethodInfo apply = One(typeof(StorageItemPacket), "ApplyTransfer");
			foreach (string id in new[]
			         {
				"inventory.storage-missing", "inventory.item-missing",
				"inventory.membership-wrong", "inventory.mass-zero",
			         })
				Add(result, id, resolved, apply);
			MethodInfo revision = One(typeof(StorageItemPacket), "ShouldApplyRevision");
			MethodInfo dispatch = One(typeof(StorageItemPacket), "OnDispatched");
			Add(result, "inventory.delta-duplicate", revision, dispatch);
			Add(result, "inventory.delta-out-of-order", revision, dispatch);
		}

		private static void BindEntity(ICollection<FaultProductionBinding> result)
		{
			Add(result, "entity.state-before-identity",
				One(typeof(NetworkIdentityRegistry), "CanApplyDomainState"),
				One(typeof(OperationalStatePacket), "OnDispatched"));
			Add(result, "entity.despawn-before-spawn",
				One(typeof(NetworkIdentityRegistry), "TryAcceptLifecycleRevision"),
				One(typeof(GroundItemPickedUpPacket), "OnDispatched"));
			Add(result, "entity.spawn-after-tombstone",
				One(typeof(NetworkIdentityRegistry), "ShouldAcceptLifecycleRevision"),
				One(typeof(NetworkIdentityRegistry), "TryAcceptLifecycleRevision"));
			Add(result, "entity.prefab-null",
				One(typeof(NetworkIdentityRegistry), "CanAdmitPrefab"),
				One(typeof(SpawnPrefabPacket), "GetSnapshotApplicabilityFailure"));
		}

		private static void BindDlc(ICollection<FaultProductionBinding> result)
			=> Add(result, "dlc.fingerprint-mismatch",
				One(typeof(ProtocolCompatibility), "MatchesValues"),
				One(typeof(ProtocolCompatibility), "Matches"));

		private static void BindReconnect(ICollection<FaultProductionBinding> result)
		{
			MethodInfo acceptBatch = One(typeof(ReadyReplayAssembly), "AcceptBatch");
			MethodInfo receiveBatch = One(typeof(GameClient), "TryAcceptReadyReplayBatch");
			foreach (string id in new[]
			         {
				"reconnect.session-stale", "reconnect.connection-stale",
				"reconnect.batch-duplicate",
			         })
				Add(result, id, acceptBatch, receiveBatch);
			Add(result, "reconnect.snapshot-stale", One(typeof(ReadyReplayAssembly), "AcceptCommit"),
				One(typeof(GameClient), "TryAcceptReadyReplayCommit"));
			Add(result, "reconnect.batch-missing", One(typeof(ReadyReplayAssembly), "TryBeginApply"),
				One(typeof(GameClient), "ApplyReadyReplay"));
			Add(result, "reconnect.ack-lost", One(typeof(ReadyManager), "ShouldRetryReadyAcceptance"),
				One(typeof(ReadyManager), "SendReadyAccepted"));
			Add(result, "reconnect.disconnect-mid-apply", One(typeof(ReadyReplayAssembly), "ShouldRollbackApply"),
				One(typeof(ReadyReplayAssembly), "CompleteApply"));
		}

		private static void Add(ICollection<FaultProductionBinding> result, string id,
			MethodInfo gate, MethodInfo runtimeCallsite)
			=> result.Add(new FaultProductionBinding(id, gate, runtimeCallsite));

		private static MethodInfo One(Type type, string name)
			=> type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
			                   BindingFlags.Static | BindingFlags.Instance)
				.Single(method => method.Name == name);

	}
}
#endif
