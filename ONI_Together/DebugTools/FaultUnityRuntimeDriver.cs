#if DEBUG
using System;
using System.Linq;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Social;
using ONI_Together.Networking.Packets.Tools.Build;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Patches.Bionics;
using ONI_Together.Patches.DLC.Aquatic;
using ONI_Together.Patches.DLC.Bionic;
using ONI_Together.Patches.DLC.Common;
using ONI_Together.Patches.DLC.Frosty;
using ONI_Together.Patches.DLC.Prehistoric;
using ONI_Together.Patches.DLC.SpacedOut;
using ONI_Together.Patches.Duplicant;
using ONI_Together.Patches.GamePatches;
using ONI_Together.Patches.ToolPatches.Build;
using ONI_Together.Patches.World;
using ONI_Together.Scripts.Duplicants;
using UnityEngine;

namespace ONI_Together.DebugTools
{
	internal static class FaultUnityRuntimeStages
	{
		internal static FaultRuntimeTargetContext Setup(FaultUnityProductionBinding binding)
		{
			object target = FaultUnityTargetSelector.Resolve(binding.CaseId);
			if (target == null && binding.CaseId != "work.client-native-start")
				throw new InvalidOperationException("runtime-target-required:" + binding.CaseId);
			var context = new FaultRuntimeTargetContext
			{
				CaseId = binding.CaseId, Target = target,
				TargetId = FaultUnityTargetSelector.Identity(binding.CaseId, target),
			};
			if (context.CaseId == "building.destroy-deferred")
			{
				FaultDeferredDestroyRuntime.CreateDisposableFixture(context);
				FaultDeferredDestroyRuntime.DisposableFixtureIdentity(context);
				FaultDeferredDestroyRuntime.EnsureOriginalLifecycleIdentity(context);
			}
			else
				FaultUnityTargetSelector.Configure(context);
			context.Setup = context;
			context.ExecutionTier = FaultInjectionRegistry.Cases.Single(
				value => value.Id == binding.CaseId).ExecutionTier;
			FaultUnityTargetSelector.CaptureBaseline(context);
			context.BaselineHash = FaultUnityTargetSelector.Hash(context);
			FaultInjectionReceipt armed = context.CaseId == "building.destroy-deferred"
				? FaultInjectionUnitySeams.Arm(
					context.CaseId, context.ExecutionTier, targetId: context.TargetId,
					runtimeTarget: context.DeferredOriginal)
				: FaultInjectionUnitySeams.Arm(
					context.CaseId, context.ExecutionTier, targetId: context.TargetId,
					runtimeTarget: target);
			if (!armed.Succeeded) throw new InvalidOperationException(armed.Detail);
			return context;
		}

		internal static FaultRuntimeTargetContext SetupClean(
			FaultUnityProductionBinding binding)
		{
			FaultRuntimeTargetContext context = Setup(binding);
			FaultInjectionUnitySeams.Clean(context.CaseId);
			FaultInjectionReceipt armed = FaultInjectionUnitySeams.Arm(
				context.CaseId, context.ExecutionTier, cleanControl: true,
				targetId: context.TargetId,
				runtimeTarget: context.DeferredReplacement);
			if (!armed.Succeeded) throw new InvalidOperationException(armed.Detail);
			context.CleanControl = true;
			return context;
		}

		internal static void Trigger(FaultRuntimeTargetContext context)
		{
			switch (context.CaseId)
			{
				case "duplicant.personality-missing": ((ImmigrantOptionEntry)context.Target).ToGameDeliverable(); break;
				case "duplicant.set-minion-before-controller": ImmigrantScreenPatch.ApplyOptionsToScreen((ImmigrantScreen)context.Target); break;
				case "duplicant.preview-flatulence": Flatulence_Patch.Flatulence_Emit_Patch.Prefix((Flatulence)context.Target); break;
				case "duplicant.destroyed-add-component": ((MinionMultiplayerInitializer)context.Target).FinalizeInit(); break;
				case "work.workable-unregistered":
				case "work.target-missing": ((WorkableProgressPacket)context.Target).TryApplyWorkableProgress(); break;
				case "work.original-dig-element-null": DiggableStartWorkFaultPatch.Prefix((Diggable)context.Target); break;
				case "work.client-native-start": WorkableStartWorkAuthorityPatch.Prefix(); break;
				case "building.selected-elements-null": global::ConstructablePatch.Capture((Constructable)context.Target); break;
				case "building.destroy-deferred":
					OniBuildRuntimeAdapter.TryGetReplacement(context.BuildingDef, context.Cell,
						Orientation.Neutral, context.Materials, out _);
					FaultDeferredDestroyRuntime.RecordDestroyRequest(context); break;
				case "dlc.prefab-missing": ((SpawnPrefabPacket)context.Target).CreateAuthoritativeObject(); break;
				case "dlc.state-before-start-sm": BionicPatches.BionicOilMonitor_Instance_StartSM_Patch.Prefix((BionicOilMonitor.Instance)context.Target); break;
				case "dlc.family-aquatic": MinnowPoiPrefabIdentityPatch.Postfix((GameObject)context.Target); break;
				case "dlc.family-bionic": ElectrobankSim1000Patch.Prefix((Electrobank)context.Target); break;
				case "dlc.family-frosty": GeothermalControllerButtonPatch.Prefix((GeothermalController.StatesInstance)context.Target); break;
				case "dlc.family-prehistoric": LargeImpactorStartSmPatch.Postfix((LargeImpactorStatus.Instance)context.Target); break;
				case "dlc.family-spaced-out": ArtifactPoiIdentityPatch.Postfix((GameObject)context.Target); break;
				case "dlc.family-common": PoiTechSync.CompleteWorkPatch.Postfix((POITechItemUnlockWorkable)context.Target); break;
				default: throw new InvalidOperationException("unknown-unity-fault:" + context.CaseId);
			}
		}

		internal static FaultRuntimeReceipt ReceiptBarrier(FaultRuntimeTargetContext context)
		{
			if (!FaultInjectionUnitySeams.TryTakeReceipt(context.CaseId,
				    out FaultInjectionReceipt receipt, out string targetId))
				throw new InvalidOperationException("runtime-receipt-timeout:" + context.CaseId);
			bool clean = receipt.Detail == "fault-clean-receipt:" + context.CaseId;
			context.Receipt = new FaultRuntimeReceipt
			{
				ReceiptId = (clean ? "fault-clean-receipt:" : "fault-receipt:") + context.CaseId,
				TargetId = targetId, Consumed = true, Succeeded = receipt.Succeeded,
			};
			return context.Receipt;
		}

		internal static FaultRuntimeSnapshot Snapshot(FaultRuntimeTargetContext context)
		{
			context.Snapshot = FaultUnityTargetSelector.Snapshot(context);
			return context.Snapshot;
		}

		internal static TypedEvidenceEnvelope Oracle(
			FaultRuntimeTargetContext context, FaultRuntimeSnapshot snapshot)
		{
			context.Oracle = FaultUnityTargetSelector.Oracle(context, snapshot);
			return context.Oracle;
		}

		internal static bool Validate(FaultRuntimeTargetContext input)
		{
			if (input?.Setup == null || input.Receipt == null || input.Snapshot == null
			    || input.Oracle == null)
				return false;
			string expected = (input.CleanControl ? "fault-clean-receipt:" : "fault-receipt:")
			                  + input.CaseId;
			bool restored = !input.CleanControl
			                || input.Snapshot.StateHash == input.Setup.BaselineHash;
			bool destroyed = input.CaseId != "duplicant.destroyed-add-component"
			                 || input.Snapshot.ComponentCountAfter <= input.Snapshot.ComponentCountBefore
			                 && input.Snapshot.IdentityPresentAfter == input.Snapshot.IdentityPresentBefore
			                 && input.Snapshot.ExceptionCount == 0;
			bool dlc = !input.CaseId.StartsWith("dlc.family-", StringComparison.Ordinal)
			           || ValidDlc(input.Snapshot.Evidence);
			bool deferred = input.CaseId != "building.destroy-deferred"
			                || (input.CleanControl
				                ? FaultDeferredDestroyRuntime.Validate(input.DeferredDestroyEvidence)
				                : FaultDeferredDestroyRuntime.ValidateFaultPhase(
					                input.DeferredDestroyEvidence));
			return input.Receipt.ReceiptId == expected && input.Receipt.Consumed
			       && input.Receipt.Succeeded && input.Receipt.TargetId == input.Setup.TargetId
			       && input.Snapshot.TargetId == input.Setup.TargetId
			       && input.Oracle.ObservedTargetId == input.Setup.TargetId
			       && input.Oracle.BeforeHash == input.Setup.BaselineHash
			       && input.Oracle.AfterHash == input.Snapshot.StateHash
			       && input.Oracle.Passed && input.Oracle.InvariantPreserved
			       && input.Snapshot.InvariantPreserved && restored && destroyed && dlc && deferred;
		}

		internal static void Reset(FaultRuntimeTargetContext context)
		{
			FaultInjectionUnitySeams.Clean(context.CaseId);
			if (context.CaseId == "building.destroy-deferred")
			{
				FaultDeferredDestroyRuntime.DeferredDestroyBarrier(context);
				FaultDeferredDestroyRuntime.ReplacementRecreate(context);
				FaultDeferredDestroyRuntime.ReplacementRebind(context);
				FaultDeferredDestroyRuntime.BaselineRestore(context);
			}
			context.CleanControl = true;
		}

		internal static void CleanControlTrigger(FaultRuntimeTargetContext context)
		{
			if (context.CaseId == "building.destroy-deferred")
			{
				FaultDeferredDestroyRuntime.ReplacementCleanControl(context);
				return;
			}
			FaultInjectionReceipt armed = FaultInjectionUnitySeams.Arm(
				context.CaseId, context.ExecutionTier, cleanControl: true,
				targetId: context.TargetId, runtimeTarget: context.Target);
			if (!armed.Succeeded)
				throw new InvalidOperationException(armed.Detail);
			Trigger(context);
		}

		private static bool ValidDlc(TypedEvidenceEnvelope evidence)
			=> evidence?.Target is DlcRuntimeTarget target && evidence.State is DlcRuntimeState state
			   && evidence.Scenario == "dlc-runtime" && !string.IsNullOrEmpty(target.DlcFamily)
			   && !string.IsNullOrEmpty(target.Prefab) && !string.IsNullOrEmpty(target.Identity)
			   && !string.IsNullOrEmpty(state.StateMachineState) && state.AdmissionGeneration > 0;
	}

	internal static partial class FaultUnityRuntimeDriver
	{
		internal static FaultInjectionReceipt ExecuteFault(FaultUnityProductionBinding binding)
		{
			try
			{
				return ExecuteLifecycle(binding);
			}
			catch (Exception failure)
			{
				if (binding.CaseId == "building.destroy-deferred")
					FaultDeferredDestroyRuntime.AbortUncommitted();
				FaultInjectionUnitySeams.Clean(binding.CaseId);
				return FaultInjectionReceipt.Fail("runtime-exception",
					binding.CaseId + ":" + failure.GetType().Name + ":" + failure.Message);
			}
		}

		internal static FaultInjectionReceipt ExecuteCleanControl(FaultUnityProductionBinding binding)
			=> ExecuteCleanPhase(binding);

		internal static FaultInjectionReceipt ExecuteLifecycle(
			FaultUnityProductionBinding binding)
		{
			FaultRuntimeTargetContext context = FaultUnityRuntimeStages.Setup(binding);
			FaultUnityRuntimeStages.Trigger(context);
			FaultRuntimeReceipt receipt = FaultUnityRuntimeStages.ReceiptBarrier(context);
			FaultRuntimeSnapshot snapshot = FaultUnityRuntimeStages.Snapshot(context);
			TypedEvidenceEnvelope oracle = FaultUnityRuntimeStages.Oracle(context, snapshot);
			if (!FaultUnityRuntimeStages.Validate(context))
			{
				if (binding.CaseId == "building.destroy-deferred")
					FaultDeferredDestroyRuntime.Abort(context);
				return FaultInjectionReceipt.Fail("runtime-oracle",
					"runtime-oracle-unsatisfied:" + binding.CaseId,
					context: context);
			}
			if (binding.CaseId == "building.destroy-deferred")
				FaultDeferredDestroyRuntime.StorePending(context);
			return FaultInjectionReceipt.Pass(
				"runtime", binding.ReceiptId, context: context);
		}

	}
}
#endif
