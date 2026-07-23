#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Social;
using ONI_Together.Networking.Packets.DLC.Bionic;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Patches.DLC.Bionic;
using ONI_Together.Patches.GamePatches;
using ONI_Together.Scripts.Duplicants;
using UnityEngine;

namespace ONI_Together.DebugTools
{
	internal static class FaultUnityTargetSelector
	{
		internal static object Resolve(string caseId)
		{
			switch (caseId)
			{
				case "duplicant.personality-missing": return ImmigrantScreenPatch.AvailableOptions?
					.Where(value => value.EntryType == 0).OrderBy(value => value.GetId()).FirstOrDefault();
				case "duplicant.set-minion-before-controller": return First<ImmigrantScreen>();
				case "duplicant.preview-flatulence": return First<Flatulence>();
				case "duplicant.destroyed-add-component": return All<MinionMultiplayerInitializer>()
					.FirstOrDefault(value => !value.IsInitialized);
				case "work.workable-unregistered": return WorkPacket(requireIdentity: true);
				case "work.target-missing": return WorkPacket(requireIdentity: false);
				case "work.original-dig-element-null": return First<Diggable>();
				case "work.client-native-start": return null;
				case "building.selected-elements-null": return First<Constructable>();
				case "building.destroy-deferred": return All<BuildingComplete>()
					.FirstOrDefault(value => value.Def != null && value.Def.Replaceable
					                         && value.Def.ReplacementLayer != ObjectLayer.NumLayers
					                         && value.GetComponent<PrimaryElement>()?.Element != null);
				case "dlc.prefab-missing": return SpawnPacket();
				case "dlc.state-before-start-sm": return FirstSmi<BionicOilMonitor.Instance>();
				case "dlc.family-aquatic": return PrefabContaining("MinnowImperativePOI");
				case "dlc.family-bionic": return First<Electrobank>();
				case "dlc.family-frosty": return First<GeothermalController>()?.smi;
				case "dlc.family-prehistoric": return FirstSmi<LargeImpactorStatus.Instance>();
				case "dlc.family-spaced-out": return PrefabContaining("ArtifactPOI");
				case "dlc.family-common": return First<POITechItemUnlockWorkable>();
				default: return null;
			}
		}

		internal static void Configure(FaultRuntimeTargetContext context)
		{
			if (context.CaseId.StartsWith("dlc.family-", StringComparison.Ordinal))
			{
				GameObject gameObject = GameObjectOf(context.Target);
				NetworkIdentity identity = gameObject?.GetComponent<NetworkIdentity>();
				if (identity == null || identity.NetId == 0)
					throw new InvalidOperationException(
						"concrete-runtime-identity-required:" + context.CaseId);
			}
			if (context.CaseId != "building.destroy-deferred") return;
			var complete = (BuildingComplete)context.Target;
			context.BuildingDef = complete.Def;
			context.Cell = Grid.PosToCell(complete);
			Tag material = complete.GetComponent<PrimaryElement>()?.Element?.tag ?? Tag.Invalid;
			context.Materials = new[] { material };
		}

		internal static string Identity(string caseId, object target)
		{
			if (target is string explicitIdentity) return explicitIdentity;
			if (target is ImmigrantOptionEntry option)
				return "immigrant-option:" + option.GetId();
			if (target is WorkableProgressPacket work)
				return "net:" + work.RuntimeTargetNetId;
			if (target is SpawnPrefabPacket spawn)
				return "prefab:" + spawn.Hash;
			GameObject gameObject = GameObjectOf(target);
			int netId = gameObject?.GetComponent<NetworkIdentity>()?.NetId ?? 0;
			if (netId != 0) return "net:" + netId;
			if (target is UnityEngine.Object unity && unity != null)
				return "unity:" + unity.GetInstanceID();
			if (target != null) return "runtime:" + target.GetType().FullName;
			return "runtime:" + caseId;
		}

		internal static string Hash(FaultRuntimeTargetContext context)
		{
			GameObject gameObject = GameObjectOf(context.Target);
			int components = gameObject == null ? 0 : gameObject.GetComponents<Component>().Length;
			bool identity = gameObject?.GetComponent<NetworkIdentity>() != null;
			NetworkIdentity network = gameObject?.GetComponent<NetworkIdentity>();
			string domainState = context.CaseId.StartsWith("dlc.family-", StringComparison.Ordinal)
				? StateName(context.Target) : string.Empty;
			string value = context.CaseId + "|" + context.TargetId + "|" + components
			               + "|" + identity + "|" + (gameObject?.activeSelf ?? false)
			               + "|" + (gameObject == null ? -1 : Grid.PosToCell(gameObject))
			               + "|" + (network?.NetId ?? 0) + "|"
			               + (network?.LifecycleRevision ?? 0) + "|" + domainState;
			using (SHA256 sha = SHA256.Create())
			{
				byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
				return "sha256:" + string.Concat(digest.Select(item => item.ToString("x2")));
			}
		}

		internal static void CaptureBaseline(FaultRuntimeTargetContext context)
		{
			GameObject gameObject = GameObjectOf(context.Target);
			context.BaselineComponentCount = gameObject == null
				? 0 : gameObject.GetComponents<Component>().Length;
			context.BaselineIdentityPresent = gameObject?.GetComponent<NetworkIdentity>() != null;
		}

		internal static FaultRuntimeSnapshot Snapshot(FaultRuntimeTargetContext context)
		{
			GameObject gameObject = GameObjectOf(context.Target);
			int components = gameObject == null ? 0 : gameObject.GetComponents<Component>().Length;
			bool identity = gameObject?.GetComponent<NetworkIdentity>() != null;
			string stateHash = Hash(context);
			var snapshot = new FaultRuntimeSnapshot
			{
				TargetId = context.TargetId, StateHash = stateHash,
				InvariantPreserved = stateHash == context.BaselineHash,
				ComponentCount = components, ComponentCountBefore = context.BaselineComponentCount,
				ComponentCountAfter = components, IdentityPresent = identity,
				IdentityPresentBefore = context.BaselineIdentityPresent,
				IdentityPresentAfter = identity, ExceptionCount = context.ExceptionCount,
			};
			if (context.CaseId.StartsWith("dlc.family-", StringComparison.Ordinal))
				snapshot.Evidence = DlcEvidence(context);
			return snapshot;
		}

		internal static TypedEvidenceEnvelope Oracle(
			FaultRuntimeTargetContext context, FaultRuntimeSnapshot snapshot)
		{
			TypedEvidenceEnvelope result = snapshot.Evidence ?? new TypedEvidenceEnvelope();
			result.Passed = snapshot.InvariantPreserved;
			result.ObservedTargetId = context.TargetId;
			result.BeforeHash = context.BaselineHash;
			result.AfterHash = snapshot.StateHash;
			result.InvariantPreserved = snapshot.InvariantPreserved;
			result.NoNewComponents = snapshot.ComponentCountAfter <= snapshot.ComponentCountBefore;
			result.NoNewIdentity = snapshot.IdentityPresentAfter == snapshot.IdentityPresentBefore;
			result.ExceptionFree = snapshot.ExceptionCount == 0;
			return result;
		}

		private static TypedEvidenceEnvelope DlcEvidence(FaultRuntimeTargetContext context)
		{
			string family = context.CaseId.Substring("dlc.family-".Length);
			GameObject gameObject = GameObjectOf(context.Target);
			long admission = MultiplayerSession.LocalPlayer?.ConnectionGeneration ?? 0;
			if (admission <= 0)
				throw new InvalidOperationException(
					"active-admission-generation-required:" + context.CaseId);
			string prefab = gameObject?.GetComponent<KPrefabID>()?.PrefabTag.Name;
			if (string.IsNullOrEmpty(prefab))
				throw new InvalidOperationException(
					"concrete-runtime-prefab-required:" + context.CaseId);
			var target = new DlcRuntimeTarget
			{
				DlcFamily = FamilyName(family),
				Prefab = prefab,
				Identity = context.TargetId,
			};
			var state = new DlcRuntimeState
			{
				StateMachineState = StateName(context.Target),
				AdmissionGeneration = admission,
			};
			return TypedEvidenceRuntimeContext.Create(
				"dlc-runtime", "final-state", admission, target, state,
				"sync:fault:" + context.CaseId, "dlc-runtime",
				admission, ReadyManager.ClientSnapshotGeneration);
		}

		private static string FamilyName(string value)
			=> value == "spaced-out" ? "SpacedOut"
				: char.ToUpperInvariant(value[0]) + value.Substring(1);

		private static string StateName(object target)
		{
			if (target is GeothermalController.StatesInstance geothermal)
				return RequireState(geothermal.GetCurrentState()?.name);
			if (target is LargeImpactorStatus.Instance impactor)
				return RequireState(impactor.GetCurrentState()?.name);
			if (target is Electrobank bank
			    && BionicRuntimeSync.TryCapture(bank, out BionicElectrobankStatePacket state))
				return "health=" + state.CurrentHealth + ";charge=" + state.Charge
				       + ";powerAge=" + state.TimeSincePowerDrawn;
			GameObject gameObject = GameObjectOf(target);
			if (gameObject != null)
			{
				var minnow = gameObject.GetSMI<MinnowImperativePOIStates.Instance>();
				if (minnow != null) return RequireState(minnow.GetCurrentState()?.name);
				var poiTech = gameObject.GetSMI<POITechItemUnlocks.Instance>();
				if (poiTech != null) return RequireState(poiTech.GetCurrentState()?.name);
				NetworkIdentity identity = gameObject.GetComponent<NetworkIdentity>();
				if (identity != null && identity.NetId != 0)
					return "active=" + gameObject.activeSelf + ";lifecycle="
					       + identity.LifecycleRevision;
			}
			throw new InvalidOperationException("concrete-runtime-state-required");
		}

		private static string RequireState(string value)
			=> !string.IsNullOrEmpty(value) ? value
				: throw new InvalidOperationException("running-state-machine-state-required");

		private static WorkableProgressPacket WorkPacket(bool requireIdentity)
		{
			foreach (Workable workable in All<Workable>())
			{
				if (requireIdentity && workable.GetComponent<NetworkIdentity>()?.NetId <= 0)
					continue;
				if (WorkableProgressPacket.TryCreate(
					    workable, showProgressBar: false, out WorkableProgressPacket packet))
					return packet;
			}
			return null;
		}

		private static SpawnPrefabPacket SpawnPacket()
		{
			KPrefabID prefab = All<KPrefabID>().FirstOrDefault(value => value.PrefabTag.IsValid);
			return prefab == null ? null : new SpawnPrefabPacket
			{
				Hash = prefab.PrefabTag.GetHash(), Position = prefab.transform.position,
			};
		}

		private static GameObject PrefabContaining(string token)
			=> All<KPrefabID>().Where(value => value.PrefabTag.Name.IndexOf(
				token, StringComparison.OrdinalIgnoreCase) >= 0)
				.Select(value => value.gameObject).FirstOrDefault();

		private static GameObject GameObjectOf(object target)
		{
			if (target is GameObject gameObject) return gameObject;
			if (target is Component component) return component.gameObject;
			if (target is BionicOilMonitor.Instance bionic) return bionic.gameObject;
			if (target is GeothermalController.StatesInstance geothermal) return geothermal.gameObject;
			if (target is LargeImpactorStatus.Instance impactor) return impactor.gameObject;
			if (target is WorkableProgressPacket work
			    && NetworkIdentityRegistry.TryGet(work.RuntimeTargetNetId, out NetworkIdentity workIdentity))
				return workIdentity?.gameObject;
			if (target is SpawnPrefabPacket spawn) return Assets.GetPrefab(new Tag(spawn.Hash));
			return null;
		}

		private static T First<T>() where T : UnityEngine.Object => All<T>().FirstOrDefault();

		private static T FirstSmi<T>() where T : StateMachine.Instance
			=> All<KPrefabID>().Select(value => value.gameObject.GetSMI<T>())
				.FirstOrDefault(value => value != null);

		private static IEnumerable<T> All<T>() where T : UnityEngine.Object
			=> UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode.None)
				.Where(value => value != null).OrderBy(value => value.GetInstanceID());
	}
}
#endif
