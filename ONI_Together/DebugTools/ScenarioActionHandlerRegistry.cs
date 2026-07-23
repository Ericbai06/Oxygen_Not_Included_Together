using System;
using System.Collections.Generic;
using System.Linq;

#if DEBUG
namespace ONI_Together.DebugTools
{
	internal delegate DebugCommandOutcome ScenarioActionHandler(ScenarioActionCommand command);

	internal static class ScenarioActionHandlerRegistry
	{
		private static readonly IReadOnlyDictionary<string, ScenarioActionHandler> Actions =
			new Dictionary<string, ScenarioActionHandler>(StringComparer.Ordinal)
			{
				["remote-dig"] = ScenarioNativeActions.RemoteDig,
				["priority"] = ScenarioNativeActions.Priority,
				["building-config"] = ScenarioNativeActions.BuildingConfig,
				["door"] = ScenarioNativeActions.Door,
				["uproot"] = ScenarioNativeActions.Uproot,
				["toggle"] = ScenarioNativeActions.Toggle,
				["research"] = ScenarioNativeActions.Research,
				["schedule"] = ScenarioNativeActions.Schedule,
				["inventory"] = ScenarioNativeActions.Inventory,
				["storage"] = ScenarioNativeActions.Storage,
				["pickup"] = ScenarioNativeActions.Pickup,
				["deconstruct"] = ScenarioNativeActions.Deconstruct,
				["effect"] = ScenarioNativeActions.Effect,
				["chat"] = ScenarioNativeActions.Chat,
				["cursor"] = ScenarioNativeActions.Cursor,
				["animation"] = ScenarioNativeActions.Animation,
				["motion"] = ScenarioNativeActions.Motion,
				["entity-lifecycle"] = ScenarioNativeActions.EntityLifecycle,
				["dlc-runtime"] = ScenarioNativeActions.DlcRuntime,
				["rocket"] = ScenarioNativeActions.Rocket,
			};

		private static readonly IReadOnlyDictionary<string, ScenarioActionHandler> Cleanups =
			CreateCleanupHandlers();

		internal static IReadOnlyCollection<string> NativeScenarios =>
			Actions.Keys.ToArray();

		internal static bool TryResolve(string scenario, out ScenarioActionHandler handler)
			=> Actions.TryGetValue(scenario, out handler);

		internal static bool TryResolveCleanup(
			string scenario,
			out ScenarioActionHandler handler)
			=> Cleanups.TryGetValue(scenario, out handler);

		internal static DebugCommandOutcome Dispatch(ScenarioActionCommand command)
		{
			IReadOnlyDictionary<string, ScenarioActionHandler> handlers =
				command.IsCleanup ? Cleanups : Actions;
			return handlers.TryGetValue(command.Scenario, out ScenarioActionHandler handler)
				? handler(command)
				: DebugCommandOutcome.Fail(command.RawCommand, "scenario-handler-missing");
		}

		private static IReadOnlyDictionary<string, ScenarioActionHandler>
			CreateCleanupHandlers()
		{
			var cleanups = new Dictionary<string, ScenarioActionHandler>(
				StringComparer.Ordinal);
			foreach (string scenario in Actions.Keys)
				cleanups.Add(scenario, ScenarioNativeActions.Cleanup);
			cleanups.Add("building-lifecycle", ScenarioNativeActions.BuildingLifecycleCleanup);
			cleanups.Add("reconnect-world-state", ScenarioNativeActions.ReconnectCleanup);
			return cleanups;
		}
	}

	internal static class ScenarioActionReceiptStore
	{
		private static readonly Dictionary<string,
			Func<ScenarioActionCommand, DebugCommandOutcome>> Receipts =
			new Dictionary<string, Func<ScenarioActionCommand, DebugCommandOutcome>>(
				StringComparer.Ordinal);

		internal static bool TryArm(
			ScenarioActionCommand command,
			Func<ScenarioActionCommand, DebugCommandOutcome> cleanup)
		{
			string key = Key(command);
			if (Receipts.ContainsKey(key))
				return false;
			Receipts.Add(key, cleanup);
			return true;
		}

		internal static DebugCommandOutcome Cleanup(ScenarioActionCommand command)
		{
			string key = Key(command);
			if (!Receipts.TryGetValue(
				    key, out Func<ScenarioActionCommand, DebugCommandOutcome> cleanup))
				return DebugCommandOutcome.Fail(
					command.RawCommand, command.Scenario + "-cleanup-not-armed");
			DebugCommandOutcome outcome = cleanup(command);
			if (outcome.Success)
				Receipts.Remove(key);
			return outcome;
		}

		internal static void Discard(ScenarioActionCommand command)
			=> Receipts.Remove(Key(command));

		private static string Key(ScenarioActionCommand command)
		{
			var parts = new List<string> { command.Scenario };
			foreach (KeyValuePair<string, string> pair in command.Selector.OrderBy(
				         value => value.Key, StringComparer.Ordinal))
				parts.Add(pair.Key + "=" + pair.Value);
			return string.Join(":", parts);
		}
	}
}
#endif
