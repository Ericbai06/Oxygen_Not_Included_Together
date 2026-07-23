using Klei.AI;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Patches.Duplicant;
using UnityEngine;

#if DEBUG
namespace ONI_Together.Networking
{
	internal sealed class EffectProfileMutation
	{
		internal Effects Effects;
		internal string EffectId;
		internal bool WasActive;
		internal bool ShouldSave;
		internal float TimeRemaining;
	}

	internal static class EffectProfileRuntime
	{
		private const string IntegrationEffectId = "WellFed";

		internal static EffectProfileMutation Toggle(Effects effects)
		{
			Effect definition = Db.Get()?.effects?.TryGet(IntegrationEffectId);
			if (effects == null || definition == null)
				return null;
			EffectInstance current = effects.Get(definition);
			var mutation = new EffectProfileMutation
			{
				Effects = effects,
				EffectId = definition.Id,
				WasActive = current != null,
				ShouldSave = current?.shouldSave ?? false,
				TimeRemaining = current?.timeRemaining ?? 0f,
			};
			if (mutation.WasActive)
				EffectsPatch.RemoveEffect(effects, definition.IdHash);
			else
				EffectsPatch.AddEffect(effects, definition.Id, false, 0f);
			if ((effects.Get(definition) != null) == mutation.WasActive)
				return null;
			return mutation;
		}

		internal static bool Restore(EffectProfileMutation mutation)
		{
			if (mutation?.Effects == null || string.IsNullOrEmpty(mutation.EffectId))
				return false;
			Effect definition = Db.Get()?.effects?.TryGet(mutation.EffectId);
			if (definition == null)
				return false;
			EffectInstance current = mutation.Effects.Get(definition);
			if (mutation.WasActive && current == null)
			{
				current = EffectsPatch.AddEffect(
					mutation.Effects, definition.Id, mutation.ShouldSave, mutation.TimeRemaining);
				if (current != null)
					current.timeRemaining = mutation.TimeRemaining;
			}
			else if (!mutation.WasActive && current != null)
				EffectsPatch.RemoveEffect(mutation.Effects, definition.IdHash);
			bool restored = (mutation.Effects.Get(definition) != null) == mutation.WasActive;
			return restored;
		}
	}

	internal sealed class AnimationProfileMutation
	{
		internal KBatchedAnimController Controller;
		internal NetworkIdentity Identity;
		internal HashedString PreviousAnim;
		internal KAnim.PlayMode PreviousMode;
		internal float PreviousSpeed;
		internal float PreviousElapsed;
	}

	internal static class AnimationProfileRuntime
	{
		private static readonly HashedString WorkingLoop = new("working_loop");

		internal static AnimationProfileMutation PlayWorkingLoop(
			KBatchedAnimController controller)
		{
			NetworkIdentity identity = controller?.GetComponent<NetworkIdentity>();
			if (identity == null || identity.NetId == 0 || controller.CurrentAnim == null
			    || !controller.HasAnimation(WorkingLoop))
				return null;
			var mutation = Capture(controller, identity);
			controller.Play(WorkingLoop, KAnim.PlayMode.Loop, 1f, 0f);
			return controller.currentAnim == WorkingLoop ? mutation : null;
		}

		internal static bool Restore(AnimationProfileMutation mutation)
		{
			if (mutation?.Controller == null || mutation.Controller.IsNullOrDestroyed()
			    || mutation.Identity == null || mutation.Identity.IsNullOrDestroyed())
				return false;
			mutation.Controller.Play(
				mutation.PreviousAnim, mutation.PreviousMode,
				mutation.PreviousSpeed, mutation.PreviousElapsed);
			return mutation.Controller.currentAnim == mutation.PreviousAnim;
		}

		private static AnimationProfileMutation Capture(
			KBatchedAnimController controller, NetworkIdentity identity)
			=> new()
			{
				Controller = controller,
				Identity = identity,
				PreviousAnim = controller.currentAnim,
				PreviousMode = controller.GetMode(),
				PreviousSpeed = controller.GetPlaySpeed(),
				PreviousElapsed = controller.GetElapsedTime(),
			};

		private static void Publish(
			int netId, HashedString animation, KAnim.PlayMode mode,
			float speed, float elapsed)
			=> PacketSender.SendToAllClients(
				new PlayAnimPacket(netId, [animation], false, mode, speed, elapsed));
	}

	internal static class MotionProfileRuntime
	{
		internal static Vector3? OffsetOneCellOneTick(
			RemoteMotionPresenter presenter,
			NetworkIdentity identity)
		{
			if (!MultiplayerSession.IsHostInSession || WorldStateSyncer.Instance == null)
				return null;
			Vector3 previous = presenter.transform.position;
			Apply(presenter, identity, previous, previous + Vector3.right);
			return previous;
		}

		internal static bool Restore(
			RemoteMotionPresenter presenter,
			NetworkIdentity identity,
			Vector3 previous)
		{
			if (presenter == null || presenter.IsNullOrDestroyed()
			    || identity == null || identity.IsNullOrDestroyed())
				return false;
			Apply(presenter, identity, presenter.transform.position, previous);
			return presenter.transform.position == previous;
		}

		private static void Apply(
			RemoteMotionPresenter presenter,
			NetworkIdentity identity,
			Vector3 source,
			Vector3 target)
		{
			var state = new Packets.Core.EntityMotionState
			{
				NetId = identity.NetId,
				Revision = NetworkIdentityRegistry.NextAuthorityRevision(),
				Kind = Packets.Core.EntityMotionKind.Transition,
				StartSimTick = PresentationTickClock.CurrentTick,
				Source = source,
				Target = target,
				DurationTicks = 1,
				StartNavType = presenter.AuthoritativeNavType,
				EndNavType = presenter.AuthoritativeNavType,
			};
			presenter.ApplyProfileState(state);
			RemoteMotionPresenter.FlushPending();
		}
	}
}
#endif
