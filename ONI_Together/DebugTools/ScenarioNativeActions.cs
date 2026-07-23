using ONI_Together.Networking;

#if DEBUG
namespace ONI_Together.DebugTools
{
	internal static partial class ScenarioNativeActions
	{
		internal static DebugCommandOutcome Cleanup(ScenarioActionCommand command)
			=> ScenarioActionReceiptStore.Cleanup(command);

		private static bool RequireActionProfile(
			ScenarioActionCommand command,
			out DebugCommandOutcome failure)
		{
			if (!string.IsNullOrEmpty(command.ActionProfile)
			    && ScenarioActionProfileRegistry.IsAllowed(
				    command.Scenario, command.ActionProfile))
			{
				if (!ScenarioActionReceiverGate.IsArmed(command.Scenario))
				{
					failure = ScenarioActionTargets.Fail(
						command, "scenario-action-admission-not-armed");
					return false;
				}
				failure = default;
				return true;
			}
			failure = ScenarioActionTargets.Fail(command, "action-profile-required");
			return false;
		}

		internal static DebugCommandOutcome ReconnectCleanup(ScenarioActionCommand command)
		{
			if (!ReconnectScenarioEvidence.CancelAutomationArm())
				return ScenarioActionTargets.Fail(command, "evidence-not-armed");
			return ScenarioActionTargets.Restored(command);
		}

		internal static DebugCommandOutcome BuildingLifecycleCleanup(
			ScenarioActionCommand command)
			=> ScenarioBuildingLifecycleCleanup.Execute(command);
	}
}
#endif
