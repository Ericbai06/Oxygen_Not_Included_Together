using ONI_Together.Networking;
using UnityEngine;

#if DEBUG
namespace ONI_Together.DebugTools
{
	internal static class ScenarioBuildingLifecycleCleanup
	{
		internal static DebugCommandOutcome Execute(ScenarioActionCommand command)
		{
			if (!ScenarioActionTargets.RequireHostWorld(command, out DebugCommandOutcome failure))
				return failure;
			if (!command.TryGetInt("cell", out int cell) || !Grid.IsValidCell(cell))
				return ScenarioActionTargets.Fail(command, "target-cell-invalid");
			GameObject target = Grid.Objects[cell, (int)ObjectLayer.Building];
			if (target == null)
				return DebugCommandOutcome.Ok(
					command.RawCommand, "building-lifecycle-cleanup-completed");
			if (target.TryGetComponent(out Constructable constructable))
			{
				constructable.gameObject.Trigger(2127324410);
				return ObserveCompletion(command, cell, target);
			}
			if (target.TryGetComponent(out Deconstructable deconstructable))
			{
				deconstructable.QueueDeconstruction(userTriggered: true);
				return ObserveCompletion(command, cell, target);
			}
			return ScenarioActionTargets.Fail(
				command, "constructable-or-deconstructable-required");
		}

		private static DebugCommandOutcome ObserveCompletion(
			ScenarioActionCommand command,
			int cell,
			GameObject target)
		{
			GameObject remaining = Grid.Objects[cell, (int)ObjectLayer.Building];
			return target == null || target.IsNullOrDestroyed() || remaining != target
				? DebugCommandOutcome.Ok(
					command.RawCommand, "building-lifecycle-cleanup-completed")
				: ScenarioActionTargets.Fail(command, "cleanup-completion-not-observed");
		}
	}
}
#endif
