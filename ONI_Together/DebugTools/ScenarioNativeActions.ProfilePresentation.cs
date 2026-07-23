using System.Linq;
using Klei.AI;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using UnityEngine;

#if DEBUG
namespace ONI_Together.DebugTools
{
	internal static partial class ScenarioNativeActions
	{
		internal static DebugCommandOutcome Effect(ScenarioActionCommand command)
		{
			if (!RequireActionProfile(command, out DebugCommandOutcome failure)
			    || !ScenarioActionTargets.RequireHostWorld(command, out failure)
			    || !ScenarioActionTargets.TryIdentity(
				    command, "netId", out NetworkIdentity identity, out failure))
				return failure;
			Effects effects = identity.GetComponent<Effects>();
			EffectActionMutation mutation = EffectActionFlow.ExecuteHost(command);
			if (mutation == null)
				return ScenarioActionTargets.Fail(
					command, "effects-target-or-integration-effect-required");
			return ScenarioActionTargets.Arm(command, cleanup =>
				EffectActionFlow.ExecuteCleanup(mutation) != null
					? ScenarioActionTargets.Restored(cleanup)
					: ScenarioActionTargets.Fail(cleanup, "effect-restore-failed"));
		}

		internal static DebugCommandOutcome Animation(ScenarioActionCommand command)
		{
			if (!RequireActionProfile(command, out DebugCommandOutcome failure)
			    || !ScenarioActionTargets.RequireHostWorld(command, out failure))
				return failure;
			if (!command.TryGetInt("cell", out int cell) || !Grid.IsValidCell(cell)
			    || !TryPrimaryMinionAtCell(cell, out KBatchedAnimController target))
				return ScenarioActionTargets.Fail(command, "primary-minion-at-cell-required");
			var working = new HashedString("working_loop");
			if (!target.HasAnimation(working) || target.CurrentAnim == null)
				return ScenarioActionTargets.Fail(command, "working-loop-animation-required");
			AnimationActionMutation mutation = AnimationActionFlow.ExecuteHost(command);
			if (mutation == null)
				return ScenarioActionTargets.Fail(command, "networked-animation-target-required");
			return ScenarioActionTargets.Arm(command, cleanup =>
				AnimationActionFlow.ExecuteCleanup(mutation) != null
					? ScenarioActionTargets.Restored(cleanup)
					: ScenarioActionTargets.Fail(cleanup, "animation-restore-failed"));
		}

		internal static DebugCommandOutcome Motion(ScenarioActionCommand command)
		{
			if (!RequireActionProfile(command, out DebugCommandOutcome failure)
			    || !ScenarioActionTargets.RequireHostWorld(command, out failure)
			    || !ScenarioActionTargets.TryIdentity(
				    command, "netId", out NetworkIdentity identity, out failure))
				return failure;
			RemoteMotionPresenter target = identity.GetComponent<RemoteMotionPresenter>();
			if (target == null)
				return ScenarioActionTargets.Fail(command, "motion-presenter-target-required");
			MotionActionMutation mutation = MotionActionFlow.ExecuteHost(command);
			if (mutation == null)
				return ScenarioActionTargets.Fail(command, "motion-transport-target-required");
			return ScenarioActionTargets.Arm(command, cleanup =>
				MotionActionFlow.ExecuteCleanup(mutation) != null
					? ScenarioActionTargets.Restored(cleanup)
					: ScenarioActionTargets.Fail(cleanup, "motion-restore-failed"));
		}

		private static bool TryPrimaryMinionAtCell(
			int cell,
			out KBatchedAnimController controller)
		{
			controller = global::Components.LiveMinionIdentities?.Items
				.Where(value => value != null && !value.IsNullOrDestroyed())
				.Select(value => value.GetComponent<KBatchedAnimController>())
				.FirstOrDefault(value => value != null && Grid.PosToCell(value.gameObject) == cell);
			return controller != null;
		}
	}
}
#endif
