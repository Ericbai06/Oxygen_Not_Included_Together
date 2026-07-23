using ONI_Together.Networking;
using ONI_Together.Networking.Components;

#if DEBUG
namespace ONI_Together.DebugTools
{
	internal static class ScenarioActionTargets
	{
		internal static bool RequireHostWorld(
			ScenarioActionCommand command,
			out DebugCommandOutcome failure)
		{
			if (Game.Instance != null && MultiplayerSession.IsHostInSession)
			{
				failure = default;
				return true;
			}
			failure = DebugCommandOutcome.Fail(
				command.RawCommand, command.Scenario + "-active-host-world-required");
			return false;
		}

		internal static bool TryIdentity(
			ScenarioActionCommand command,
			string key,
			out NetworkIdentity identity,
			out DebugCommandOutcome failure)
		{
			identity = null;
			if (!command.TryGetInt(key, out int netId)
			    || !NetworkIdentityRegistry.TryGet(netId, out identity))
			{
				failure = DebugCommandOutcome.Fail(
					command.RawCommand, command.Scenario + "-target-net-id-not-found");
				return false;
			}
			failure = default;
			return true;
		}

		internal static DebugCommandOutcome Applied(ScenarioActionCommand command)
			=> DebugCommandOutcome.Ok(
				command.RawCommand, command.Scenario + "-native-action-applied");

		internal static DebugCommandOutcome Arm(
			ScenarioActionCommand command,
			System.Func<ScenarioActionCommand, DebugCommandOutcome> cleanup)
		{
			if (ScenarioActionReceiptStore.TryArm(command, cleanup))
				return Applied(command);
			cleanup(command);
			return Fail(command, "cleanup-receipt-already-armed");
		}

		internal static DebugCommandOutcome Restored(ScenarioActionCommand command)
			=> DebugCommandOutcome.Ok(
				command.RawCommand, command.Scenario + "-native-state-restored");

		internal static DebugCommandOutcome Fail(
			ScenarioActionCommand command,
			string reason)
			=> DebugCommandOutcome.Fail(command.RawCommand, command.Scenario + "-" + reason);
	}
}
#endif
