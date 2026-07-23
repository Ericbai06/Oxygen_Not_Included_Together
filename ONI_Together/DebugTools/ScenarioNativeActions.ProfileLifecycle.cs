using System;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.DLC.SpacedOut;

#if DEBUG
namespace ONI_Together.DebugTools
{
	internal static partial class ScenarioNativeActions
	{
		internal static DebugCommandOutcome EntityLifecycle(ScenarioActionCommand command)
		{
			if (!RequireActionProfile(command, out DebugCommandOutcome failure)
			    || !ScenarioActionTargets.RequireHostWorld(command, out failure)
			    || !ScenarioActionTargets.TryIdentity(
				    command, "netId", out NetworkIdentity identity, out failure))
				return failure;
			if (!identity.gameObject.activeSelf)
				return ScenarioActionTargets.Fail(command, "active-entity-target-required");
			EntityLifecycleActionMutation mutation = EntityLifecycleActionFlow.ExecuteHost(command);
			if (mutation == null)
				return ScenarioActionTargets.Fail(command, "entity-deactivation-failed");
			return ScenarioActionTargets.Arm(command, cleanup =>
				EntityLifecycleActionFlow.ExecuteCleanup(mutation) != null
					? ScenarioActionTargets.Restored(cleanup)
					: ScenarioActionTargets.Fail(cleanup, "entity-reactivation-failed"));
		}

		internal static DebugCommandOutcome DlcRuntime(ScenarioActionCommand command)
		{
			if (!RequireActionProfile(command, out DebugCommandOutcome failure)
			    || !ScenarioActionTargets.RequireHostWorld(command, out failure))
				return failure;
			string family = command.Selector["dlcFamily"];
			if (!string.Equals(family, "SpacedOut", StringComparison.Ordinal)
			    || !DlcManager.IsExpansion1Active())
				return ScenarioActionTargets.Fail(command, "spaced-out-runtime-required");
			DlcActionMutation mutation = DlcRuntimeActionFlow.ExecuteHost(command);
			if (mutation == null)
				return ScenarioActionTargets.Fail(
					command, "prepared-dlc-safe-transition-required");
			return ScenarioActionTargets.Arm(command, cleanup =>
				DlcRuntimeActionFlow.ExecuteCleanup(mutation) != null
					? ScenarioActionTargets.Restored(cleanup)
					: ScenarioActionTargets.Fail(cleanup, "dlc-state-restore-failed"));
		}

		internal static DebugCommandOutcome Rocket(ScenarioActionCommand command)
		{
			if (!RequireActionProfile(command, out DebugCommandOutcome failure)
			    || !ScenarioActionTargets.RequireHostWorld(command, out failure))
				return failure;
			if (!command.TryGetInt("rocketNetId", out int rocketNetId)
			    || !command.TryGetInt("padNetId", out int padNetId))
				return ScenarioActionTargets.Fail(command, "rocket-and-pad-net-ids-required");
			RocketActionMutation mutation = RocketActionFlow.ExecuteHost(command);
			if (mutation == null)
				return ScenarioActionTargets.Fail(
					command, "reachable-rocket-and-pad-fixture-required");
			return ScenarioActionTargets.Arm(command, cleanup =>
				RocketActionFlow.ExecuteCleanup(mutation) != null
					? ScenarioActionTargets.Restored(cleanup)
					: ScenarioActionTargets.Fail(cleanup, "rocket-settings-restore-failed"));
		}
	}
}
#endif
