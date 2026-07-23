using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Tools.Prioritize;
using UnityEngine;

#if DEBUG
namespace ONI_Together.DebugTools
{
	internal static partial class ScenarioNativeActions
	{
		internal static DebugCommandOutcome RemoteDig(ScenarioActionCommand command)
		{
			if (!ScenarioActionTargets.RequireHostWorld(command, out DebugCommandOutcome failure))
				return failure;
			if (!command.TryGetInt("cell", out int cell) || !Grid.IsValidCell(cell))
				return ScenarioActionTargets.Fail(command, "target-cell-invalid");
			GameObject marker = DigTool.PlaceDig(cell, 0);
			if (marker == null)
				return ScenarioActionTargets.Fail(command, "dig-marker-not-created");
			return ScenarioActionTargets.Arm(command, cleanup =>
			{
				if (marker == null || marker.IsNullOrDestroyed())
					return ScenarioActionTargets.Fail(cleanup, "dig-marker-destroyed-before-cleanup");
				marker.Trigger((int)GameHashes.Cancel, null);
				return ScenarioActionTargets.Restored(cleanup);
			});
		}

		internal static DebugCommandOutcome Priority(ScenarioActionCommand command)
		{
			if (!ScenarioActionTargets.RequireHostWorld(command, out DebugCommandOutcome failure)
			    || !ScenarioActionTargets.TryIdentity(
				    command, "netId", out NetworkIdentity identity, out failure))
				return failure;
			Prioritizable target = identity.GetComponent<Prioritizable>();
			if (target == null || !target.IsPrioritizable())
				return ScenarioActionTargets.Fail(command, "prioritizable-component-required");
			PrioritySetting previous = target.GetMasterPriority();
			int nextValue = previous.priority_class == PriorityScreen.PriorityClass.basic
			                && previous.priority_value < 9 ? previous.priority_value + 1 : 5;
			var next = new PrioritySetting(PriorityScreen.PriorityClass.basic, nextValue);
			if (!PriorityAuthority.IsValidClientPriority(next) || next.Equals(previous))
				return ScenarioActionTargets.Fail(command, "distinct-valid-priority-unavailable");
			target.SetMasterPriority(next);
			return ScenarioActionTargets.Arm(command, cleanup =>
				RestorePriority(cleanup, target, previous));
		}

		private static DebugCommandOutcome RestorePriority(
			ScenarioActionCommand command,
			Prioritizable target,
			PrioritySetting previous)
		{
			if (target == null || target.IsNullOrDestroyed())
				return ScenarioActionTargets.Fail(command, "priority-target-destroyed");
			target.SetMasterPriority(previous);
			return ScenarioActionTargets.Restored(command);
		}

		internal static DebugCommandOutcome Deconstruct(ScenarioActionCommand command)
		{
			if (!ScenarioActionTargets.RequireHostWorld(command, out DebugCommandOutcome failure)
			    || !ScenarioActionTargets.TryIdentity(
				    command, "buildingNetId", out NetworkIdentity identity, out failure))
				return failure;
			if (!command.TryGetInt("targetCell", out int expectedCell)
			    || Grid.PosToCell(identity.gameObject) != expectedCell)
				return ScenarioActionTargets.Fail(command, "target-cell-mismatch");
			Deconstructable target = identity.GetComponent<Deconstructable>();
			if (target == null)
				return ScenarioActionTargets.Fail(command, "deconstructable-component-required");
			target.QueueDeconstruction(userTriggered: true);
			return ScenarioActionTargets.Arm(command, cleanup =>
				RestoreDeconstruct(cleanup, target));
		}

		private static DebugCommandOutcome RestoreDeconstruct(
			ScenarioActionCommand command,
			Deconstructable target)
		{
			if (target == null || target.IsNullOrDestroyed())
				return ScenarioActionTargets.Fail(command, "deconstruct-target-destroyed");
			target.CancelDeconstruction();
			return ScenarioActionTargets.Restored(command);
		}
	}
}
#endif
