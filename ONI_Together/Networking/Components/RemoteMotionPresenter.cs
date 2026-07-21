using System.Collections.Generic;
using System.Threading;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Core;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
	internal sealed class RemoteMotionPresenter : KMonoBehaviour, IRender1000ms
	{
		private const float CorrectionSeconds = 0.2f;
		private const float CorrectionSnapDistance = 1.5f;
		private const int VisibilityMargin = 2;
		private static readonly Dictionary<int, EntityMotionState> PendingStates = [];
		private static readonly HashSet<ulong> VisibleRecipients = [];
		private static readonly Dictionary<ulong, List<EntityMotionState>> RecipientStates = [];
		private static long _nextHostSequence;
#if DEBUG
		private static bool _checkpointFrozen;
#endif

		[MyCmpGet] internal KBatchedAnimController AnimController;
		[MyCmpGet] internal Navigator Navigator;
		[MyCmpGet] private NetworkIdentity _identity;

		private EntityMotionState _activeState;
		internal Vector3 AuthoritativePosition { get; private set; }
		internal ulong AuthoritativeRevision { get; private set; }
		internal bool AuthoritativeFlipX { get; private set; }
		internal bool AuthoritativeFlipY { get; private set; }
		internal NavType AuthoritativeNavType { get; private set; }

		public override void OnSpawn()
		{
			base.OnSpawn();
			AuthoritativePosition = transform.position;
			AuthoritativeFlipX = AnimController != null && AnimController.FlipX;
			AuthoritativeFlipY = AnimController != null && AnimController.FlipY;
			AuthoritativeNavType = CurrentNavType(Navigator);
			enabled = _identity != null;
		}

		public override void OnCleanUp()
		{
			if (_identity != null)
			{
				PendingStates.Remove(_identity.NetId);
				EntityMotionBatchPacket.ForgetNetId(_identity.NetId);
			}
			base.OnCleanUp();
		}

		private void Update()
		{
			if (!MultiplayerSession.IsClient || _activeState == null)
				return;
			long tick = PresentationTickClock.CurrentTick;
			transform.SetPosition(EvaluatePosition(_activeState, tick));
			ApplyOrientation();
		}

		public void Render1000ms(float dt)
		{
			if (!MultiplayerSession.IsHostInSession || _identity == null || _identity.NetId == 0)
				return;
#if DEBUG
			if (_checkpointFrozen) return;
#endif
			EntityMotionKind kind = HeartbeatKind(
				Navigator != null, Navigator != null && Navigator.IsMoving());
			QueueState(CaptureCurrent(kind, CorrectionSeconds));
		}

		internal void ApplySnapshot(EntityMotionState incoming)
		{
			long currentTick = PresentationTickClock.CurrentTick;
			AuthoritativePosition = incoming.Target;
			AuthoritativeRevision = incoming.Revision;
			AuthoritativeFlipX = incoming.FlipX;
			AuthoritativeFlipY = incoming.FlipY;
			AuthoritativeNavType = incoming.EndNavType;
			if (incoming.Kind == EntityMotionKind.Correction
			    && ShouldSnapCorrection(transform.position, incoming.Target))
			{
				_activeState = null;
				transform.SetPosition(incoming.Target);
				ApplyOrientation();
				return;
			}
			_activeState = incoming.Kind == EntityMotionKind.Transition
				? incoming
				: RebaseCorrection(incoming, transform.position, currentTick);
			transform.SetPosition(EvaluatePosition(_activeState, currentTick));
			ApplyOrientation();
		}

		internal void ApplyAuthoritativeSnapshot(EntityMotionState state)
		{
			AuthoritativePosition = state.Target;
			AuthoritativeRevision = state.Revision;
			AuthoritativeFlipX = state.FlipX;
			AuthoritativeFlipY = state.FlipY;
			AuthoritativeNavType = state.EndNavType;
			_activeState = null;
			transform.SetPosition(state.Target);
			ApplyOrientation();
		}

		internal static void PublishTransition(Navigator navigator, NavGrid.Transition transition)
		{
			if (!TryGetHostPresenter(navigator, out RemoteMotionPresenter presenter)) return;
			Vector3 source = navigator.transform.position;
			Vector3 target = source + new Vector3(transition.x, transition.y, 0f);
			var active = navigator.transitionDriver?.GetTransition;
			float speed = active?.speed ?? 0f;
			float seconds = speed > 0f ? Vector3.Distance(source, target) / speed : CorrectionSeconds;
			QueueState(presenter.Capture(new EntityMotionState
			{
				Kind = EntityMotionKind.Transition,
				Source = source,
				Target = target,
				StartNavType = transition.start,
				EndNavType = transition.end,
			}, seconds));
		}

		internal static void PublishStop(Navigator navigator)
		{
			if (!TryGetHostPresenter(navigator, out RemoteMotionPresenter presenter)) return;
			QueueState(presenter.CaptureCurrent(EntityMotionKind.Stop, CorrectionSeconds));
		}

		internal static void FlushPending()
		{
			if (!MultiplayerSession.IsHostInSession || PendingStates.Count == 0
			    || WorldStateSyncer.Instance == null)
				return;
			RecipientStates.Clear();
			foreach (EntityMotionState state in PendingStates.Values)
				CollectRecipients(state);
			PendingStates.Clear();
			foreach (var recipient in RecipientStates)
			{
				foreach (EntityMotionBatchPacket batch in EntityMotionBatchPacket.CreateBatches(
					         recipient.Value))
				{
#if DEBUG
					foreach (EntityMotionState state in batch.States)
					{
						string evidenceState = EntityMotionBatchPacket.EvidenceState(state);
						long revision = (long)state.Revision;
						IntegrationScenarioEvidenceCore.Log(
							"motion", "host-submit", revision, true, evidenceState);
						IntegrationScenarioEvidenceCore.Log(
							"motion", "final-state", revision, true, evidenceState);
					}
#endif
					PacketSender.SendToPlayer(recipient.Key, batch, PacketSendMode.Unreliable);
				}
			}
		}

		internal static Vector3 EvaluatePosition(EntityMotionState state, long tick)
		{
			if (tick <= state.StartSimTick) return state.Source;
			long delta = tick - state.StartSimTick;
			float progress = Mathf.Clamp01(delta / (float)state.DurationTicks);
			return Vector3.Lerp(state.Source, state.Target, progress);
		}

		internal static Vector3 SelectHashPosition(
			bool localIsClient, Vector3 rendered,
			(ulong Revision, Vector3 Position) authoritative)
			=> localIsClient && authoritative.Revision > 0
				? authoritative.Position : rendered;

		internal static bool ShouldSnapCorrection(Vector3 rendered, Vector3 authoritative)
			=> Vector3.Distance(rendered, authoritative) > CorrectionSnapDistance;

		internal static EntityMotionKind HeartbeatKindForTests(
			bool hasNavigator, bool isMoving) => HeartbeatKind(hasNavigator, isMoving);

		internal static long NextHostSequence()
		{
			long sequence = Interlocked.Increment(ref _nextHostSequence);
			if (sequence > 0) return sequence;
			Interlocked.Exchange(ref _nextHostSequence, 1);
			return 1;
		}

		internal static void ResetSessionState()
		{
			PendingStates.Clear();
			EntityMotionBatchPacket.ResetSessionState();
			Interlocked.Exchange(ref _nextHostSequence, 0);
#if DEBUG
			_checkpointFrozen = false;
#endif
		}

#if DEBUG
		internal static void SetCheckpointFrozen(bool frozen) => _checkpointFrozen = frozen;
		internal static bool CheckpointFrozen => _checkpointFrozen;
#endif

		private EntityMotionState CaptureCurrent(EntityMotionKind kind, float seconds)
		{
			Vector3 position = transform.position;
			NavType navType = CurrentNavType(Navigator);
			return Capture(new EntityMotionState
			{
				Kind = kind,
				Source = position,
				Target = position,
				StartNavType = navType,
				EndNavType = navType,
			}, seconds);
		}

		private EntityMotionState Capture(EntityMotionState state, float seconds)
		{
			state.NetId = _identity.NetId;
			state.Revision = NetworkIdentityRegistry.NextAuthorityRevision();
			state.StartSimTick = PresentationTickClock.CurrentTick;
			state.DurationTicks = PresentationTickClock.DurationTicks(seconds);
			state.Flags = CaptureFlags(AnimController);
			return state;
		}

		private void ApplyOrientation()
		{
			if (AnimController != null)
			{
				AnimController.FlipX = AuthoritativeFlipX;
				AnimController.FlipY = AuthoritativeFlipY;
			}
		}

		private static EntityMotionState RebaseCorrection(
			EntityMotionState state, Vector3 source, long startTick)
		{
			return new EntityMotionState
			{
				NetId = state.NetId,
				Revision = state.Revision,
				Kind = state.Kind,
				StartSimTick = startTick,
				Source = source,
				Target = state.Target,
				DurationTicks = state.DurationTicks,
				StartNavType = state.StartNavType,
				EndNavType = state.EndNavType,
				Flags = state.Flags,
			};
		}

		private static void QueueState(EntityMotionState state)
		{
			if (state == null || state.NetId == 0) return;
			PendingStates.TryGetValue(state.NetId, out EntityMotionState current);
			PendingStates[state.NetId] = SelectPending(current, state);
		}

		internal static EntityMotionState SelectPendingForTests(
			EntityMotionState current, EntityMotionState incoming)
			=> SelectPending(current, incoming);

		private static EntityMotionState SelectPending(
			EntityMotionState current, EntityMotionState incoming)
		{
			if (incoming == null || incoming.NetId == 0) return current;
			if (current == null) return incoming;
			if (incoming.NetId != current.NetId || incoming.Revision <= current.Revision)
				return current;
			if (incoming.Kind == EntityMotionKind.Transition) return incoming;
			if (current.Kind == EntityMotionKind.Stop) return current;
			if (incoming.Kind == EntityMotionKind.Correction
			    && current.Kind == EntityMotionKind.Transition) return current;
			return incoming;
		}

		private static void CollectRecipients(EntityMotionState state)
		{
			VisibleRecipients.Clear();
			WorldStateSyncer.Instance.GetClientsViewingCell(
				Grid.PosToCell(state.Target), VisibleRecipients, VisibilityMargin);
			foreach (ulong recipient in VisibleRecipients)
			{
				if (!RecipientStates.TryGetValue(recipient, out List<EntityMotionState> states))
					RecipientStates[recipient] = states = [];
				states.Add(state);
			}
		}

		private static bool TryGetHostPresenter(
			Navigator navigator, out RemoteMotionPresenter presenter)
		{
			presenter = null;
			if (!MultiplayerSession.IsHostInSession || navigator == null
			    || !navigator.TryGetComponent(out NetworkIdentity identity) || identity.NetId == 0)
				return false;
			presenter = navigator.gameObject.AddOrGet<RemoteMotionPresenter>();
			return presenter != null;
		}

		private static NavType CurrentNavType(Navigator navigator)
			=> navigator != null && navigator.CurrentNavType != NavType.NumNavTypes
				? navigator.CurrentNavType : NavType.Floor;

		private static EntityMotionKind HeartbeatKind(bool hasNavigator, bool isMoving)
			=> hasNavigator && !isMoving ? EntityMotionKind.Stop : EntityMotionKind.Correction;

		private static EntityMotionFlags CaptureFlags(KBatchedAnimController controller)
		{
			EntityMotionFlags flags = EntityMotionFlags.None;
			if (controller?.FlipX == true) flags |= EntityMotionFlags.FlipX;
			if (controller?.FlipY == true) flags |= EntityMotionFlags.FlipY;
			return flags;
		}
	}
}
