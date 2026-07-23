using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using UnityEngine;

#if DEBUG
namespace ONI_Together.DebugTools
{
	internal static partial class ScenarioNativeActions
	{
		internal static DebugCommandOutcome BuildingConfig(ScenarioActionCommand command)
		{
			if (!RequireActionProfile(command, out DebugCommandOutcome failure)
			    || !ScenarioActionTargets.RequireHostWorld(command, out failure)
			    || !ScenarioActionTargets.TryIdentity(
				    command, "netId", out NetworkIdentity identity, out failure))
				return failure;
			LogicSwitch target = identity.GetComponent<LogicSwitch>();
			if (target == null)
				return ScenarioActionTargets.Fail(command, "logic-switch-target-required");
			BuildingConfigProfileMutation mutation = BuildingConfigActionFlow.ExecuteHost(command);
			if (mutation == null)
				return ScenarioActionTargets.Fail(command, "checkbox-toggle-not-applied");
			return ScenarioActionTargets.Arm(command, cleanup =>
			{
				if (mutation.RuntimeTarget == null || mutation.RuntimeTarget.IsNullOrDestroyed())
					return ScenarioActionTargets.Fail(cleanup, "logic-switch-target-destroyed");
				return BuildingConfigActionFlow.ExecuteCleanup(mutation) != null
					? ScenarioActionTargets.Restored(cleanup)
					: ScenarioActionTargets.Fail(cleanup, "checkbox-restore-failed");
			});
		}

		internal static DebugCommandOutcome Uproot(ScenarioActionCommand command)
		{
			if (!RequireActionProfile(command, out DebugCommandOutcome failure)
			    || !ScenarioActionTargets.RequireHostWorld(command, out failure)
			    || !ScenarioActionTargets.TryIdentity(
				    command, "netId", out NetworkIdentity identity, out failure))
				return failure;
			Uprootable target = identity.GetComponent<Uprootable>();
			if (target == null || !target.CanUproot())
				return ScenarioActionTargets.Fail(command, "uprootable-target-required");
			if (target.IsMarkedForUproot)
				return ScenarioActionTargets.Fail(command, "unmarked-uprootable-required");
			UprootProfileMutation mutation = UprootActionFlow.ExecuteHost(command);
			if (mutation == null)
				return ScenarioActionTargets.Fail(command, "uproot-mark-failed");
			return ScenarioActionTargets.Arm(command, cleanup =>
			{
				if (mutation.RuntimeTarget == null || mutation.RuntimeTarget.IsNullOrDestroyed())
					return ScenarioActionTargets.Fail(cleanup, "uproot-target-destroyed");
				return UprootActionFlow.ExecuteCleanup(mutation) != null
					? ScenarioActionTargets.Restored(cleanup)
					: ScenarioActionTargets.Fail(cleanup, "uproot-restore-failed");
			});
		}

		internal static DebugCommandOutcome Inventory(ScenarioActionCommand command)
		{
			if (!RequireActionProfile(command, out DebugCommandOutcome failure)
			    || !ScenarioActionTargets.RequireHostWorld(command, out failure))
				return failure;
			InventoryActionMutation mutation = InventoryActionFlow.ExecuteHost(command);
			if (mutation == null)
				return ScenarioActionTargets.Fail(command, "active-world-sand-spawn-failed");
			return ScenarioActionTargets.Arm(command, cleanup =>
				InventoryActionFlow.ExecuteCleanup(mutation) != null
					? ScenarioActionTargets.Restored(cleanup)
					: ScenarioActionTargets.Fail(cleanup, "sand-remove-failed"));
		}

		internal static DebugCommandOutcome Pickup(ScenarioActionCommand command)
		{
			if (!RequireActionProfile(command, out DebugCommandOutcome failure)
			    || !ScenarioActionTargets.RequireHostWorld(command, out failure)
			    || !ScenarioActionTargets.TryIdentity(
				    command, "itemNetId", out NetworkIdentity identity, out failure))
				return failure;
			if (!command.TryGetInt("targetCell", out int cell) || !Grid.IsValidCell(cell))
				return ScenarioActionTargets.Fail(command, "target-cell-invalid");
			PickupActionMutation mutation = PickupActionFlow.ExecuteHost(command);
			if (mutation == null)
				return ScenarioActionTargets.Fail(
					command, "primary-duplicant-or-pickup-target-required");
			return ScenarioActionTargets.Arm(command, cleanup =>
				PickupActionFlow.ExecuteCleanup(mutation) != null
					? ScenarioActionTargets.Restored(cleanup)
					: ScenarioActionTargets.Fail(cleanup, "pickup-restore-failed"));
		}
	}
}
#endif
