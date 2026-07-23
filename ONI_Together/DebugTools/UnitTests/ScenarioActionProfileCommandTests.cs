using System;
using System.Collections.Generic;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class ScenarioActionProfileCommandTests
	{
		private static readonly IReadOnlyDictionary<string, string> Commands =
			new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["building-config"] = "scenario-action:building-config:kind=netId:netId=41:profile=toggle-checkbox",
				["uproot"] = "scenario-action:uproot:kind=netId:netId=41:profile=mark-and-cancel-uproot",
				["inventory"] = "scenario-action:inventory:kind=inventory:profile=add-remove-sand-1000g",
				["pickup"] = "scenario-action:pickup:itemNetId=42:kind=pickup:profile=primary-duplicant-pickup-drop:targetCell=95028",
				["effect"] = "scenario-action:effect:kind=netId:netId=41:profile=toggle-integration-effect",
				["animation"] = "scenario-action:animation:cell=95028:kind=cell:profile=primary-minion-working-loop",
				["motion"] = "scenario-action:motion:kind=netId:netId=41:profile=offset-one-cell-one-tick",
				["entity-lifecycle"] = "scenario-action:entity-lifecycle:kind=netId:netId=41:profile=deactivate-reactivate-same-prefab",
				["dlc-runtime"] = "scenario-action:dlc-runtime:dlcFamily=SpacedOut:identity=rover-7:kind=dlc:prefab=ScoutRover:profile=next-admissible-state-restore",
				["rocket"] = "scenario-action:rocket:kind=rocket:padNetId=72:profile=next-reachable-boarding-restore:rocketNetId=71",
			};

		[UnitTest(name: "Scenario action profiles: canonical commands parse separately from targets",
			category: "Integration")]
		public static UnitTestResult CanonicalProfilesParseSeparatelyFromTargets()
		{
			foreach (KeyValuePair<string, string> entry in Commands)
			{
				if (!ScenarioActionCommand.TryParse(entry.Value, out ScenarioActionCommand command))
					return UnitTestResult.Fail("Profile command did not parse: " + entry.Value);
				if (command.Scenario != entry.Key || string.IsNullOrEmpty(command.ActionProfile)
				    || command.Selector.ContainsKey("profile"))
					return UnitTestResult.Fail("Profile leaked into target selector: " + entry.Key);
				if (!ScenarioActionProfileRegistry.IsAllowed(entry.Key, command.ActionProfile))
					return UnitTestResult.Fail("Profile is not allowlisted: " + entry.Key);
			}
			return UnitTestResult.Pass("Fixed action profiles are parsed apart from target identity");
		}

		[UnitTest(name: "Scenario action profiles: mismatched and arbitrary values fail closed",
			category: "Integration")]
		public static UnitTestResult InvalidProfilesFailClosed()
		{
			string valid = Commands["motion"];
			string[] invalid =
			{
				valid.Replace("profile=offset-one-cell-one-tick", "profile=arbitrary"),
				valid.Replace("profile=offset-one-cell-one-tick",
					"profile=toggle-checkbox"),
				valid + ":profile=offset-one-cell-one-tick",
			};
			foreach (string command in invalid)
				if (ScenarioActionCommand.TryParse(command, out _))
					return UnitTestResult.Fail("Invalid profile command parsed: " + command);
			return UnitTestResult.Pass("Only the scenario's fixed profile is accepted");
		}

		[UnitTest(name: "Scenario action profiles: target-only commands remain negative probes",
			category: "Integration")]
		public static UnitTestResult TargetOnlyCommandsRemainNegativeProbes()
		{
			const string command = "scenario-action:motion:kind=netId:netId=41";
			if (!ScenarioActionCommand.TryParse(command, out ScenarioActionCommand parsed)
			    || parsed.ActionProfile != null)
				return UnitTestResult.Fail("Existing target-only command grammar regressed");
			DebugCommandOutcome outcome = ScenarioActionHandlerRegistry.Dispatch(parsed);
			return !outcome.Success && outcome.Reason == "motion-action-profile-required"
				? UnitTestResult.Pass("Target-only commands fail before Unity mutation")
				: UnitTestResult.Fail("Target-only command did not fail on its missing profile");
		}

		internal static IReadOnlyDictionary<string, string> ProfileCommands => Commands;
	}
}
