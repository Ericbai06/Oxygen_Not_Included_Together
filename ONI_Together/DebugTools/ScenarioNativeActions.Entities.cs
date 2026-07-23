using System;
using ONI_Together.Networking.Components;
using UnityEngine;

#if DEBUG
namespace ONI_Together.DebugTools
{
	internal static partial class ScenarioNativeActions
	{
		internal static DebugCommandOutcome Door(ScenarioActionCommand command)
		{
			if (!TryBuildingTarget(command, out NetworkIdentity identity,
				    out DebugCommandOutcome failure))
				return failure;
			Door target = identity.GetComponent<Door>();
			if (target == null)
				return ScenarioActionTargets.Fail(command, "door-component-required");
			Door.ControlState previous = target.RequestedState;
			if (!TryDifferentDoorState(previous, out Door.ControlState next))
				return ScenarioActionTargets.Fail(command, "alternate-door-state-unavailable");
			target.QueueStateChange(next);
			return ScenarioActionTargets.Arm(command, cleanup =>
				RestoreDoor(cleanup, target, previous));
		}

		private static bool TryDifferentDoorState(
			Door.ControlState current,
			out Door.ControlState next)
		{
			foreach (Door.ControlState candidate in Enum.GetValues(typeof(Door.ControlState)))
				if (candidate != current)
				{
					next = candidate;
					return true;
				}
			next = current;
			return false;
		}

		private static DebugCommandOutcome RestoreDoor(
			ScenarioActionCommand command,
			Door target,
			Door.ControlState previous)
		{
			if (target == null || target.IsNullOrDestroyed())
				return ScenarioActionTargets.Fail(command, "door-target-destroyed");
			target.QueueStateChange(previous);
			return ScenarioActionTargets.Restored(command);
		}

		internal static DebugCommandOutcome Toggle(ScenarioActionCommand command)
		{
			if (!TryBuildingTarget(command, out NetworkIdentity identity,
				    out DebugCommandOutcome failure))
				return failure;
			Toggleable target = identity.GetComponent<Toggleable>();
			if (target == null || target.targets == null || target.targets.Count == 0)
				return ScenarioActionTargets.Fail(command, "toggleable-target-zero-required");
			bool previous = target.IsToggleQueued(0);
			target.Toggle(0);
			if (target.IsToggleQueued(0) == previous)
				return ScenarioActionTargets.Fail(command, "toggle-state-did-not-change");
			return ScenarioActionTargets.Arm(command, cleanup =>
				RestoreToggle(cleanup, target, previous));
		}

		private static DebugCommandOutcome RestoreToggle(
			ScenarioActionCommand command,
			Toggleable target,
			bool previous)
		{
			if (target == null || target.IsNullOrDestroyed())
				return ScenarioActionTargets.Fail(command, "toggle-target-destroyed");
			if (target.IsToggleQueued(0) != previous)
				target.Toggle(0);
			return target.IsToggleQueued(0) == previous
				? ScenarioActionTargets.Restored(command)
				: ScenarioActionTargets.Fail(command, "toggle-state-restore-failed");
		}

		internal static DebugCommandOutcome Storage(ScenarioActionCommand command)
		{
			if (!TryStorageTargets(command, out Storage storage, out GameObject item,
				    out DebugCommandOutcome failure))
				return failure;
			if (!storage.items.Contains(item))
				return ScenarioActionTargets.Fail(command, "item-current-membership-required");
			storage.Remove(item, do_disease_transfer: false);
			if (storage.items.Contains(item))
				return ScenarioActionTargets.Fail(command, "storage-remove-did-not-apply");
			return ScenarioActionTargets.Arm(command, cleanup =>
				RestoreStorage(cleanup, storage, item));
		}

		private static DebugCommandOutcome RestoreStorage(
			ScenarioActionCommand command,
			Storage storage,
			GameObject item)
		{
			if (storage == null || storage.IsNullOrDestroyed()
			    || item == null || item.IsNullOrDestroyed())
				return ScenarioActionTargets.Fail(command, "storage-or-item-destroyed");
			if (!storage.items.Contains(item))
				storage.Store(item, hide_popups: true, block_events: false,
					do_disease_transfer: false, is_deserializing: false);
			return storage.items.Contains(item)
				? ScenarioActionTargets.Restored(command)
				: ScenarioActionTargets.Fail(command, "storage-membership-restore-failed");
		}

		private static bool TryBuildingTarget(
			ScenarioActionCommand command,
			out NetworkIdentity identity,
			out DebugCommandOutcome failure)
		{
			identity = null;
			return ScenarioActionTargets.RequireHostWorld(command, out failure)
			       && ScenarioActionTargets.TryIdentity(
				       command, "netId", out identity, out failure);
		}

		private static bool TryStorageTargets(
			ScenarioActionCommand command,
			out Storage storage,
			out GameObject item,
			out DebugCommandOutcome failure)
		{
			storage = null;
			item = null;
			if (!ScenarioActionTargets.RequireHostWorld(command, out failure)
			    || !ScenarioActionTargets.TryIdentity(
				    command, "storageNetId", out NetworkIdentity owner, out failure)
			    || !ScenarioActionTargets.TryIdentity(
				    command, "itemNetId", out NetworkIdentity itemIdentity, out failure))
				return false;
			storage = owner.GetComponent<Storage>();
			item = itemIdentity.gameObject;
			if (storage != null)
				return true;
			failure = ScenarioActionTargets.Fail(command, "storage-component-required");
			return false;
		}
	}
}
#endif
