using System;
using System.Collections.Generic;
using System.Linq;

namespace ONI_Together.DebugTools.UnitTests
{
	public static class ScenarioActionCommandTests
	{
		private static readonly IReadOnlyDictionary<string, string> Commands =
			new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["remote-dig"] = "scenario-action:remote-dig:cell=95028:kind=cell",
				["priority"] = "scenario-action:priority:kind=netId:netId=41",
				["building-config"] = "scenario-action:building-config:kind=netId:netId=41",
				["door"] = "scenario-action:door:kind=netId:netId=41",
				["uproot"] = "scenario-action:uproot:kind=netId:netId=41",
				["toggle"] = "scenario-action:toggle:kind=netId:netId=41",
				["research"] = "scenario-action:research:kind=tech:techId=AdvancedResearch",
				["schedule"] = "scenario-action:schedule:kind=schedule:scheduleId=schedule-3",
				["inventory"] = "scenario-action:inventory:kind=inventory",
				["storage"] = "scenario-action:storage:itemNetId=42:kind=storage:storageNetId=41",
				["pickup"] = "scenario-action:pickup:itemNetId=42:kind=pickup:targetCell=95028",
				["deconstruct"] = "scenario-action:deconstruct:buildingNetId=43:kind=deconstruct:targetCell=95028",
				["effect"] = "scenario-action:effect:kind=netId:netId=41",
				["chat"] = "scenario-action:chat:kind=sender:sender=76561198000000000",
				["cursor"] = "scenario-action:cursor:kind=player:playerId=76561198000000000",
				["animation"] = "scenario-action:animation:cell=95028:kind=cell",
				["motion"] = "scenario-action:motion:kind=netId:netId=41",
				["entity-lifecycle"] = "scenario-action:entity-lifecycle:kind=netId:netId=41",
				["dlc-runtime"] = "scenario-action:dlc-runtime:dlcFamily=SpacedOut:identity=rover-7:kind=dlc:prefab=ScoutRover",
				["rocket"] = "scenario-action:rocket:kind=rocket:padNetId=72:rocketNetId=71",
			};

		[UnitTest(name: "Scenario actions: exact native catalog resolves concrete handlers",
			category: "Integration")]
		public static UnitTestResult NativeCatalogResolvesConcreteHandlers()
		{
			if (!Commands.Keys.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(
				    ScenarioActionHandlerRegistry.NativeScenarios.OrderBy(
					    value => value, StringComparer.Ordinal)))
				return UnitTestResult.Fail("Native scenario handler catalog is incomplete");

			foreach (KeyValuePair<string, string> entry in Commands)
			{
				if (!ScenarioActionCommand.TryParse(entry.Value, out ScenarioActionCommand request)
				    || request.Scenario != entry.Key || request.IsCleanup)
					return UnitTestResult.Fail($"Concrete command did not parse: {entry.Value}");
				if (!ScenarioActionHandlerRegistry.TryResolve(
					    request.Scenario, out ScenarioActionHandler handler) || handler == null)
					return UnitTestResult.Fail($"No concrete handler delegate: {request.Scenario}");
			}

			return UnitTestResult.Pass("All native scenario commands resolve concrete handler delegates");
		}

		[UnitTest(name: "Scenario actions: automation dispatch reaches concrete handlers",
			category: "Integration")]
		public static UnitTestResult AutomationDispatchIsConcrete()
		{
			foreach (string command in Commands.Values)
			{
				DebugCommandOutcome outcome = DebugMenu.ExecuteAutomationCommandForTests(command);
				if (outcome.Command != command || outcome.Reason == "unknown-command"
				    || outcome.Reason == "invalid-scenario-action"
				    || outcome.Reason == "scenario-handler-missing")
					return UnitTestResult.Fail(
						$"Scenario command did not reach its concrete handler: {command}; {outcome.Reason}");
			}

			return UnitTestResult.Pass(
				"Automation dispatch reaches native handlers; Unity preconditions remain game-only");
		}

		[UnitTest(name: "Scenario actions: malformed commands fail closed", category: "Integration")]
		public static UnitTestResult MalformedCommandsFailClosed()
		{
			string[] malformed =
			{
				"scenario-action:door:kind=netId",
				"scenario-action:door:kind=netId:netId=41:extra",
				"scenario-action:door:kind=netId:netId=../41",
				"scenario-action:unknown:kind=netId:netId=41",
				"scenario-cleanup:door:cell=41:kind=cell",
			};
			foreach (string command in malformed)
			{
				if (ScenarioActionCommand.TryParse(command, out _))
					return UnitTestResult.Fail($"Malformed command parsed: {command}");
				DebugCommandOutcome outcome = DebugMenu.ExecuteAutomationCommandForTests(command);
				if (outcome.Success || outcome.Reason != "invalid-scenario-action")
					return UnitTestResult.Fail(
						$"Malformed scenario command was not rejected precisely: {command}; {outcome.Reason}");
			}

			return UnitTestResult.Pass("Malformed scenario commands fail closed before handler dispatch");
		}

		[UnitTest(name: "Scenario cleanup: commands preserve typed target", category: "Integration")]
		public static UnitTestResult CleanupCommandsPreserveTypedTarget()
		{
			foreach (KeyValuePair<string, string> entry in Commands)
			{
				string cleanup = entry.Value.Replace("scenario-action:", "scenario-cleanup:");
				if (!ScenarioActionCommand.TryParse(cleanup, out ScenarioActionCommand request)
				    || !request.IsCleanup || request.Scenario != entry.Key)
					return UnitTestResult.Fail($"Concrete cleanup did not parse: {cleanup}");
				if (!ScenarioActionHandlerRegistry.TryResolveCleanup(
					    request.Scenario, out ScenarioActionHandler handler) || handler == null)
					return UnitTestResult.Fail($"No concrete cleanup handler: {request.Scenario}");
			}

			return UnitTestResult.Pass("Every native scenario has typed cleanup routing");
		}
	}
}
