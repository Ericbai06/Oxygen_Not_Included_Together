using System;
using System.Collections.Generic;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.DuplicantActions;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
	public sealed class DuplicantStateSender : KMonoBehaviour, IRender200ms, IRender1000ms
	{
		private const int VisibilityMargin = 2;
		private static readonly Dictionary<int, DuplicantPresentationEntry> PendingEntries = [];
		private static readonly HashSet<ulong> VisibleRecipients = [];
		private static readonly Dictionary<ulong, List<DuplicantPresentationEntry>> RecipientEntries = [];

		[MyCmpGet] private NetworkIdentity _identity;
		[MyCmpGet] private KAnimControllerBase _animController;
		[MyCmpGet] private ChoreDriver _choreDriver;
		[MyCmpGet] private Navigator _navigator;

		private DuplicantPresentationEntry _lastQueued;
		private int _heartbeatSeconds;

		public override void OnSpawn()
		{
			using var _ = Profiler.Scope();
			base.OnSpawn();
			if (_identity == null || MultiplayerSession.IsClient)
			{
				enabled = false;
				if (_identity == null)
					DebugConsole.LogWarning($"[DuplicantStateSender] {gameObject.name} missing NetworkIdentity");
#if DEBUG
				else
					IntegrationScenarioEvidenceCore.Log(
						"animation", "client-original-blocked", 0, false,
						$"netId={_identity.NetId},senderEnabled=0");
#endif
			}
		}

		public override void OnCleanUp()
		{
			if (_identity != null)
			{
				PendingEntries.Remove(_identity.NetId);
				DuplicantPresentationBatchPacket.ForgetNetId(_identity.NetId);
			}
			base.OnCleanUp();
		}

		public void Render200ms(float dt) => QueueSnapshot(heartbeat: false);

		public void Render1000ms(float dt)
		{
			_heartbeatSeconds++;
			if (_heartbeatSeconds < 2) return;
			_heartbeatSeconds = 0;
			QueueSnapshot(heartbeat: true);
		}

		internal static void FlushPending()
		{
			if (!MultiplayerSession.IsHostInSession || PendingEntries.Count == 0)
				return;
			if (WorldStateSyncer.Instance == null)
			{
				PendingEntries.Clear();
				return;
			}

			RecipientEntries.Clear();
			foreach (DuplicantPresentationEntry entry in PendingEntries.Values)
				CollectRecipients(entry);
			PendingEntries.Clear();

			foreach (var recipient in RecipientEntries)
			{
				foreach (DuplicantPresentationBatchPacket batch in
				         DuplicantPresentationBatchPacket.CreateBatches(recipient.Value))
				{
#if DEBUG
					foreach (DuplicantPresentationEntry entry in batch.Entries)
					{
						DebugConsole.Log(
							$"[DuplicantPresentationBatch][HOST_SEND] revision={entry.Revision} " +
							$"netId={entry.NetId} action={entry.ActionState} targetCell={entry.TargetCell}");
						LogHostEvidence("animation", entry);
						if (entry.ActionState == DuplicantActionState.Digging)
							LogHostEvidence("remote-dig", entry);
					}
#endif
					PacketSender.SendToPlayer(recipient.Key, batch, PacketSendMode.Unreliable);
				}
			}
		}

		internal static void ResetSessionState()
		{
			PendingEntries.Clear();
			RecipientEntries.Clear();
			VisibleRecipients.Clear();
		}

		private void QueueSnapshot(bool heartbeat)
		{
			if (!MultiplayerSession.IsHostInSession || !TryCapture(out DuplicantPresentationEntry entry))
				return;
			if (!heartbeat && entry.HasSameVisualState(_lastQueued)) return;
			_lastQueued = entry;
			PendingEntries[entry.NetId] = entry;
		}

		private bool TryCapture(out DuplicantPresentationEntry entry)
		{
			entry = null;
			try
			{
				if (_identity.NetId == 0) _identity.RegisterIdentity();
				if (_identity.NetId == 0) return false;
				Chore chore = _choreDriver?.GetCurrentChore();
				GameObject target = chore?.gameObject;
				entry = BuildSnapshot(DetermineAction(chore), chore, target);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private DuplicantPresentationEntry BuildSnapshot(
			DuplicantActionState action, Chore chore, GameObject target)
		{
			int targetNetId = target?.GetComponent<NetworkIdentity>()?.NetId ?? 0;
			bool isWorking = chore != null && !(_navigator?.IsMoving() ?? false);
			var progress = CaptureProgress(isWorking ? target : null, targetNetId);
			return new DuplicantPresentationEntry
			{
				NetId = _identity.NetId,
				Revision = NetworkIdentityRegistry.NextAuthorityRevision(),
				StartSimTick = PresentationTickClock.CurrentTick,
				DurationTicks = PresentationTickClock.DurationTicks(
					_animController?.CurrentAnim?.totalTime ?? 0f),
				ActionState = action,
				AnimHash = _animController?.CurrentAnim == null ? 0 : _animController.currentAnim.hash,
				PlayMode = (byte)(_animController?.mode ?? KAnim.PlayMode.Loop),
				AnimSpeed = _animController?.playSpeed ?? 1f,
				AnimElapsedAtStart = _animController?.GetElapsedTime() ?? 0f,
				IsWorking = isWorking,
				WorkVisual = WorkVisualFor(action),
				TargetCell = target == null ? -1 : Grid.PosToCell(target),
				VisualTargetNetId = targetNetId,
				ToolVisual = ToolVisualFor(action),
				Facing = _animController == null
					? DuplicantFacing.Unspecified
					: _animController.FlipX ? DuplicantFacing.Left : DuplicantFacing.Right,
				ShowProgress = progress.Show,
				ProgressPercent = progress.Percent,
				WorkTimeRemaining = progress.Remaining,
				WorkTimeTotal = progress.Total,
			};
		}

		private static (bool Show, float Percent, float Remaining, float Total)
			CaptureProgress(GameObject target, int targetNetId)
		{
			Workable workable = target?.GetComponent<Workable>();
			if (targetNetId == 0 || workable == null || workable.IsNullOrDestroyed())
				return default;
			float total = workable.GetWorkTime();
			float remaining = workable.WorkTimeRemaining;
			if (total <= 0f || remaining < 0f || remaining > total)
				return default;
			return (true, Mathf.Clamp01(workable.GetPercentComplete()), remaining, total);
		}

		private DuplicantActionState DetermineAction(Chore chore)
		{
			if (chore == null)
				return MovingActionOr(DuplicantActionState.Idle);
			string id = chore.choreType?.Id;
			if (string.IsNullOrEmpty(id)) return DuplicantActionState.Other;
			return KnownAction(id) ?? MovingActionOr(DuplicantActionState.Working);
		}

		private static DuplicantActionState? KnownAction(string id)
		{
			if (Contains(id, "Build", "Construct")) return DuplicantActionState.Building;
			if (Contains(id, "Dig", "Uproot")) return DuplicantActionState.Digging;
			if (Contains(id, "Eat", "Food")) return DuplicantActionState.Eating;
			if (id.Contains("Sleep")) return DuplicantActionState.Sleeping;
			if (Contains(id, "Fetch", "Deliver", "Storage")) return DuplicantActionState.Carrying;
			return null;
		}

		private DuplicantActionState MovingActionOr(DuplicantActionState fallback)
		{
			return _navigator != null && _navigator.IsMoving()
				? NavigationAction(_navigator.CurrentNavType) : fallback;
		}

		private static bool Contains(string value, params string[] candidates)
		{
			foreach (string candidate in candidates)
				if (value.Contains(candidate)) return true;
			return false;
		}

		private static void CollectRecipients(DuplicantPresentationEntry entry)
		{
			if (!NetworkIdentityRegistry.TryGet(entry.NetId, out NetworkIdentity identity)) return;
			WorldStateSyncer.Instance.GetClientsViewingCell(
				Grid.PosToCell(identity.gameObject), VisibleRecipients, VisibilityMargin);
			foreach (ulong recipient in VisibleRecipients)
			{
				if (!RecipientEntries.TryGetValue(
					    recipient, out List<DuplicantPresentationEntry> entries))
					RecipientEntries[recipient] = entries = [];
				entries.Add(entry);
			}
		}

		private static DuplicantActionState NavigationAction(NavType navType)
			=> navType == NavType.Ladder || navType == NavType.Pole
				? DuplicantActionState.Climbing
				: navType == NavType.Swim ? DuplicantActionState.Swimming : DuplicantActionState.Walking;

		private static DuplicantWorkVisual WorkVisualFor(DuplicantActionState action)
			=> action switch
			{
				DuplicantActionState.Building => DuplicantWorkVisual.Building,
				DuplicantActionState.Digging => DuplicantWorkVisual.Digging,
				DuplicantActionState.Disinfecting => DuplicantWorkVisual.Disinfecting,
				DuplicantActionState.Working => DuplicantWorkVisual.Working,
				_ => DuplicantWorkVisual.None,
			};

		private static DuplicantToolVisual ToolVisualFor(DuplicantActionState action)
			=> action switch
			{
				DuplicantActionState.Building => DuplicantToolVisual.Build,
				DuplicantActionState.Digging => DuplicantToolVisual.Dig,
				DuplicantActionState.Disinfecting => DuplicantToolVisual.Disinfect,
				_ => DuplicantToolVisual.None,
			};

#if DEBUG
		private static void LogHostEvidence(
			string scenario, DuplicantPresentationEntry entry)
		{
			string state = DuplicantPresentationBatchPacket.EvidenceState(entry);
			long revision = (long)entry.Revision;
			IntegrationScenarioEvidenceCore.Log(
				scenario, "host-submit", revision, true, state);
			IntegrationScenarioEvidenceCore.Log(
				scenario, "final-state", revision, true, state);
		}
#endif
	}
}
