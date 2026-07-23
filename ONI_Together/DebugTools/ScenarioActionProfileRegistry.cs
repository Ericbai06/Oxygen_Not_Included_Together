using System;
using System.Collections.Generic;

#if DEBUG
namespace ONI_Together.DebugTools
{
	internal static class ScenarioActionProfileRegistry
	{
		private static readonly IReadOnlyDictionary<string, string> Profiles =
			new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["building-config"] = "toggle-checkbox",
				["uproot"] = "mark-and-cancel-uproot",
				["inventory"] = "add-remove-sand-1000g",
				["pickup"] = "primary-duplicant-pickup-drop",
				["effect"] = "toggle-integration-effect",
				["animation"] = "primary-minion-working-loop",
				["motion"] = "offset-one-cell-one-tick",
				["entity-lifecycle"] = "deactivate-reactivate-same-prefab",
				["dlc-runtime"] = "next-admissible-state-restore",
				["rocket"] = "next-reachable-boarding-restore",
			};

		internal static bool IsAllowed(string scenario, string profile)
			=> scenario != null && profile != null
			   && Profiles.TryGetValue(scenario, out string expected)
			   && string.Equals(profile, expected, StringComparison.Ordinal);

		internal static bool RequiresProfile(string scenario)
			=> scenario != null && Profiles.ContainsKey(scenario);
	}
}
#endif
