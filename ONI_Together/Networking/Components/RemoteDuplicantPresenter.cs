using ONI_Together.Networking.Packets.DuplicantActions;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
	internal sealed class RemoteDuplicantPresenter : KMonoBehaviour
	{
		[MyCmpGet] private KBatchedAnimController _animController;
		[MyCmpGet] private NetworkIdentity _identity;

		private GameObject _toolObject;
		private int _progressTargetNetId;
		internal DuplicantActionState ActionState { get; private set; }
		internal bool IsWorking { get; private set; }
		internal int VisualTargetNetId { get; private set; }
		internal DuplicantToolVisual ToolVisual { get; private set; }
		internal DuplicantFacing Facing { get; private set; }

		public override void OnSpawn()
		{
			base.OnSpawn();
			enabled = MultiplayerSession.IsClient && _animController != null;
		}

		public override void OnCleanUp()
		{
			ClearProgress();
			DestroyTool();
			base.OnCleanUp();
		}

		internal void ApplySnapshot(DuplicantPresentationEntry entry)
		{
			if (!MultiplayerSession.IsClient || entry == null) return;
			ActionState = entry.ActionState;
			IsWorking = entry.IsWorking;
			VisualTargetNetId = entry.VisualTargetNetId;
			ApplyFacing(entry.Facing);
			ApplyToolVisual(entry.ToolVisual);
			ApplyProgress(entry);
#if DEBUG
			ONI_Together.DebugTools.DebugConsole.Log(
				$"[RemoteDuplicantPresenter][CLIENT_APPLY] revision={entry.Revision} " +
				$"netId={entry.NetId} action={entry.ActionState} targetCell={entry.TargetCell}");
#endif
			if (_animController == null || entry.AnimHash == 0) return;
			AnimReconciliationHelper.Reconcile(
				_animController, new HashedString(entry.AnimHash),
				(KAnim.PlayMode)entry.PlayMode, entry.AnimSpeed,
				ProjectElapsed(entry, PresentationTickClock.CurrentTick),
				nameof(DuplicantPresentationBatchPacket));
		}

		internal static float ProjectElapsed(
			DuplicantPresentationEntry entry, long currentTick)
		{
			long elapsedTicks = System.Math.Max(0, currentTick - entry.StartSimTick);
			float elapsed = entry.AnimElapsedAtStart
			                + elapsedTicks / (float)PresentationTickClock.TicksPerSecond
			                * entry.AnimSpeed;
			float duration = entry.DurationTicks / (float)PresentationTickClock.TicksPerSecond;
			return (KAnim.PlayMode)entry.PlayMode == KAnim.PlayMode.Loop
				? elapsed % duration : System.Math.Min(elapsed, duration);
		}

		private void ApplyFacing(DuplicantFacing facing)
		{
			Facing = facing;
			if (_animController == null || facing == DuplicantFacing.Unspecified) return;
			_animController.FlipX = facing == DuplicantFacing.Left;
		}

		private void ApplyProgress(DuplicantPresentationEntry entry)
		{
			if (_progressTargetNetId != 0
			    && (_progressTargetNetId != entry.VisualTargetNetId || !entry.ShowProgress))
				ClearProgress();
			if (!entry.ShowProgress || entry.VisualTargetNetId == 0) return;
			_progressTargetNetId = entry.VisualTargetNetId;
			RemoteProgressRegistry.SetProgress(
				_progressTargetNetId, RemoteProgressKind.WorkablePercent,
				entry.ProgressPercent, true, entry.WorkTimeRemaining, entry.WorkTimeTotal);
		}

		private void ClearProgress()
		{
			if (_progressTargetNetId == 0) return;
			RemoteProgressRegistry.Clear(
				_progressTargetNetId, RemoteProgressKind.WorkablePercent, hideTarget: false);
			_progressTargetNetId = 0;
		}

		private void ApplyToolVisual(DuplicantToolVisual visual)
		{
			if (ToolVisual == visual) return;
			ToolVisual = visual;
			DestroyTool();
			if (visual == DuplicantToolVisual.None) return;
			Transform hand = ToolEquipPacket.FindHandTransform(transform);
			string animFile = ToolAnimFile(visual);
			KAnimFile asset = Assets.GetAnim(animFile);
			if (hand == null || asset == null) return;
			_toolObject = new GameObject($"{_identity?.NetId ?? 0}_PresentationTool");
			_toolObject.transform.SetParent(hand, false);
			var controller = _toolObject.AddComponent<KBatchedAnimController>();
			controller.AnimFiles = [asset];
			_toolObject.SetActive(true);
		}

		private void DestroyTool()
		{
			if (_toolObject == null) return;
			Object.Destroy(_toolObject);
			_toolObject = null;
		}

		private static string ToolAnimFile(DuplicantToolVisual visual)
			=> visual switch
			{
				DuplicantToolVisual.Build => "construct_beam_kanim",
				DuplicantToolVisual.Disinfect => "plant_spray_beam_kanim",
				_ => "laser_kanim",
			};
	}
}
