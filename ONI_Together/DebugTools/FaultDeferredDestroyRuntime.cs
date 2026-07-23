#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Tools.Build;
using UnityEngine;

namespace ONI_Together.DebugTools
{
	internal static partial class FaultDeferredDestroyRuntime
	{
		private static readonly object Sync = new object();
		private static FaultRuntimeTargetContext pending;
		private static FaultRuntimeTargetContext uncommitted;
		private const string FixturePrefix = "ONI_Together_FaultFixture_";

		internal static void CreateDisposableFixture(FaultRuntimeTargetContext context)
		{
			var template = context.Target as BuildingComplete;
			BuildingDef def = template?.Def;
			Tag material = template?.GetComponent<PrimaryElement>()?.Element?.tag ?? Tag.Invalid;
			if (def == null || !material.IsValid || !TryFindFixtureCell(def, out int cell))
				throw new InvalidOperationException("disposable-building-fixture-location-required");
			float temperature = template.GetComponent<PrimaryElement>()?.Temperature
			                    ?? def.Temperature;
			GameObject fixture = def.Build(
				cell, Orientation.Neutral, null, new List<Tag> { material }, temperature,
				BuildRequestValidator.DefaultFacade, playsound: false, GameClock.Instance.GetTime());
			TrackUncommittedFixture(context, fixture);
			BuildingComplete complete = fixture?.GetComponent<BuildingComplete>();
			if (complete == null)
				throw new InvalidOperationException("disposable-building-fixture-create-failed");
			fixture.name = FixturePrefix + def.PrefabID + "_" + cell;
			context.Target = complete;
			context.TargetId = "building:fixture:" + def.PrefabID + ":" + cell;
			FaultUnityTargetSelector.Configure(context);
		}

		internal static void TrackUncommittedFixture(
			FaultRuntimeTargetContext context, GameObject fixture)
		{
			context.DeferredOriginal = fixture;
			ClaimUncommittedOwnership(context);
			lock (Sync) uncommitted = context;
			if (fixture != null) fixture.name = FixturePrefix + "pending";
		}

		internal static void ClaimUncommittedOwnership(FaultRuntimeTargetContext context)
		{
			context.DeferredDestroyEvidence ??= new DeferredDestroyResetEvidence();
			context.DeferredDestroyEvidence.DisposableFixtureOwned = true;
		}

		internal static void DisposableFixtureIdentity(FaultRuntimeTargetContext context)
		{
			var complete = context.Target as BuildingComplete;
			GameObject original = complete?.gameObject;
			if (original == null || complete.Def == null)
				throw new InvalidOperationException("disposable-building-fixture-required");
			NetworkIdentity identity = original.AddOrGet<NetworkIdentity>();
			identity.RegisterIdentity();
			context.DeferredOriginal = original;
			context.DeferredOriginalNetId = identity?.NetId ?? 0;
			context.DeferredOriginalLifecycle = identity?.LifecycleRevision ?? 0;
			if (context.DeferredOriginalNetId != 0)
				context.DeferredLifecycleState = NetworkIdentityRegistry
					.CaptureLifecycleRevisionState(context.DeferredOriginalNetId);
			context.DeferredTemperature = original.GetComponent<PrimaryElement>()?.Temperature
			                              ?? complete.Def.Temperature;
			context.DeferredDestroyEvidence = new DeferredDestroyResetEvidence
			{
				FixtureIdentity = context.TargetId,
				DisposableFixtureOwned = original.name.StartsWith(
					FixturePrefix, StringComparison.Ordinal),
				LogicalTargetIdBefore = context.TargetId,
				LogicalTargetIdAfter = context.TargetId,
				OriginalInstanceId = original.GetInstanceID(),
			};
		}

		internal static void RecordDestroyRequest(FaultRuntimeTargetContext context)
		{
			context.DeferredDestroyRequestedFrame = Time.frameCount;
			context.DeferredDestroyEvidence.DestroyRequestedFrame = Time.frameCount;
		}

		internal static void StorePending(FaultRuntimeTargetContext context)
		{
			lock (Sync)
			{
				if (pending != null)
					throw new InvalidOperationException("deferred-destroy-reset-already-pending");
				pending = context;
				if (ReferenceEquals(uncommitted, context)) uncommitted = null;
			}
		}

		internal static FaultRuntimeTargetContext Pending()
		{
			lock (Sync)
				return pending ?? throw new InvalidOperationException(
					"deferred-destroy-fault-phase-required");
		}

		internal static void ClearPending(FaultRuntimeTargetContext context)
		{
			lock (Sync)
				if (ReferenceEquals(pending, context)) pending = null;
		}

		internal static void DeferredDestroyBarrier(FaultRuntimeTargetContext context)
		{
			long frame = Time.frameCount;
			bool destroyed = context.DeferredOriginal == null;
			context.DeferredDestroyEvidence.DestroyBarrierFrame = frame;
			context.DeferredDestroyEvidence.OriginalDestroyed = destroyed;
			if (!destroyed || frame <= context.DeferredDestroyRequestedFrame)
				throw new InvalidOperationException("deferred-destroy-frame-barrier-not-reached");
		}

		internal static void ReplacementRecreate(FaultRuntimeTargetContext context)
		{
			if (context.DeferredReplacement != null)
				return;
			IList<Tag> materials = context.Materials.ToList();
			GameObject replacement = context.BuildingDef.Build(
				context.Cell, Orientation.Neutral, null, materials,
				context.DeferredTemperature, BuildRequestValidator.DefaultFacade,
				playsound: false, GameClock.Instance.GetTime());
			if (replacement == null || replacement.GetInstanceID()
			    == context.DeferredDestroyEvidence.OriginalInstanceId)
				throw new InvalidOperationException("fresh-building-replacement-required");
			context.DeferredReplacement = replacement;
			replacement.name = FixturePrefix + context.BuildingDef.PrefabID + "_" + context.Cell;
			context.Target = replacement.GetComponent<BuildingComplete>();
			context.DeferredDestroyEvidence.ReplacementInstanceId = replacement.GetInstanceID();
		}

		internal static void ReplacementRebind(FaultRuntimeTargetContext context)
		{
			EnsureOriginalLifecycleIdentity(context);
			bool bound = RebindLifecycle(context);
			context.DeferredDestroyEvidence.ReplacementBound = bound;
			if (!bound) throw new InvalidOperationException("replacement-lifecycle-rebind-failed");
		}

		internal static void EnsureOriginalLifecycleIdentity(
			FaultRuntimeTargetContext context)
		{
			if (context == null || context.DeferredOriginalNetId == 0
			    || context.DeferredOriginalLifecycle == 0)
				throw new InvalidOperationException("original-lifecycle-identity-required");
		}

		internal static void BaselineRestore(FaultRuntimeTargetContext context)
		{
			context.DeferredDestroyEvidence.BaselineHashBefore = context.BaselineHash;
			context.DeferredDestroyEvidence.BaselineHashAfter = FaultUnityTargetSelector.Hash(context);
			if (!ValidatePartial(context.DeferredDestroyEvidence, requireClean: false))
				throw new InvalidOperationException("replacement-baseline-not-restored");
		}

		internal static void ReplacementCleanControl(FaultRuntimeTargetContext context)
		{
			GameObject replacement = context.DeferredReplacement;
			if (replacement == null || context.Target == null)
				throw new InvalidOperationException("replacement-clean-target-required");
			context.DeferredDestroyEvidence.CleanControlInstanceId = replacement.GetInstanceID();
			context.DeferredDestroyEvidence.CleanControlTargetId = context.TargetId;
			FaultInjectionReceipt armed = FaultInjectionUnitySeams.Arm(
				context.CaseId, context.ExecutionTier, cleanControl: true,
				targetId: context.TargetId,
				runtimeTarget: context.DeferredReplacement);
			if (!armed.Succeeded) throw new InvalidOperationException(armed.Detail);
			FaultUnityRuntimeStages.Trigger(context);
		}

		internal static bool Validate(DeferredDestroyResetEvidence evidence)
			=> ValidatePartial(evidence, requireClean: true);

		internal static bool ValidateFaultPhase(DeferredDestroyResetEvidence evidence)
			=> evidence != null && evidence.DisposableFixtureOwned
			   && !string.IsNullOrEmpty(evidence.FixtureIdentity)
			   && evidence.FixtureIdentity == evidence.LogicalTargetIdBefore
			   && evidence.LogicalTargetIdAfter == evidence.LogicalTargetIdBefore
			   && evidence.OriginalInstanceId != 0
			   && evidence.ReplacementInstanceId == 0
			   && evidence.DestroyBarrierFrame == 0
			   && !evidence.OriginalDestroyed && !evidence.ReplacementBound
			   && evidence.BaselineHashBefore == null && evidence.BaselineHashAfter == null
			   && evidence.CleanControlInstanceId == 0
			   && evidence.CleanControlTargetId == null;

		private static bool ValidatePartial(
			DeferredDestroyResetEvidence evidence, bool requireClean)
		{
			if (evidence == null || !evidence.DisposableFixtureOwned
			    || evidence.FixtureIdentity != evidence.LogicalTargetIdBefore
			    || string.IsNullOrEmpty(evidence.LogicalTargetIdBefore)
			    || evidence.LogicalTargetIdAfter != evidence.LogicalTargetIdBefore
			    || evidence.OriginalInstanceId == 0
			    || evidence.ReplacementInstanceId == evidence.OriginalInstanceId
			    || evidence.DestroyBarrierFrame <= evidence.DestroyRequestedFrame
			    || !evidence.OriginalDestroyed || !evidence.ReplacementBound
			    || string.IsNullOrEmpty(evidence.BaselineHashBefore)
			    || evidence.BaselineHashAfter != evidence.BaselineHashBefore)
				return false;
			return !requireClean
			       || evidence.CleanControlInstanceId == evidence.ReplacementInstanceId
			       && evidence.CleanControlTargetId == evidence.LogicalTargetIdBefore;
		}

	}
}
#endif
