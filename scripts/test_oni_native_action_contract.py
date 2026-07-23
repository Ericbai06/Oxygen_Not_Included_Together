import copy
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from scripts import oni_integration, oni_scenario_execution_specs, oni_target


NATIVE_SELECTORS = {
    "remote-dig": {"kind": "cell", "cell": 95028},
    "priority": {"kind": "netId", "netId": 41},
    "building-config": {"kind": "netId", "netId": 41},
    "door": {"kind": "netId", "netId": 41},
    "uproot": {"kind": "netId", "netId": 41},
    "toggle": {"kind": "netId", "netId": 41},
    "research": {"kind": "tech", "techId": "AdvancedResearch"},
    "schedule": {"kind": "schedule", "scheduleId": "schedule-3"},
    "inventory": {"kind": "inventory"},
    "storage": {"kind": "storage", "storageNetId": 41, "itemNetId": 42},
    "pickup": {"kind": "pickup", "itemNetId": 42, "targetCell": 95028},
    "deconstruct": {
        "kind": "deconstruct", "buildingNetId": 43, "targetCell": 95028,
    },
    "effect": {"kind": "netId", "netId": 41},
    "chat": {"kind": "sender", "sender": "76561198000000000"},
    "cursor": {"kind": "player", "playerId": "76561198000000000"},
    "animation": {"kind": "cell", "cell": 95028},
    "motion": {"kind": "netId", "netId": 41},
    "entity-lifecycle": {"kind": "netId", "netId": 41},
    "dlc-runtime": {
        "kind": "dlc", "dlcFamily": "SpacedOut",
        "prefab": "ScoutRover", "identity": "rover-7",
    },
    "rocket": {"kind": "rocket", "rocketNetId": 71, "padNetId": 72},
}

EXPECTED_ACTION_COMMANDS = {
    scenario: "scenario-action:" + scenario + ":" + ":".join(
        f"{key}={value}" for key, value in sorted({
            **selector,
            **({"profile": oni_scenario_execution_specs.ACTION_PROFILES[scenario]}
               if scenario in oni_scenario_execution_specs.ACTION_PROFILES else {}),
        }.items())
    )
    for scenario, selector in NATIVE_SELECTORS.items()
}


class NativeActionCommandCatalogTests(unittest.TestCase):
    def test_all_native_scenarios_build_concrete_allowlisted_commands(self):
        build = getattr(oni_target, "build_native_action_command")

        self.assertEqual(
            {f"{scenario}-action" for scenario in NATIVE_SELECTORS},
            set(oni_scenario_execution_specs.NATIVE_ACTION_BUILDERS),
        )
        for scenario, selector in NATIVE_SELECTORS.items():
            with self.subTest(scenario=scenario):
                self.assertEqual(
                    [], oni_target.validate_target_selector(scenario, selector),
                )
                builder = f"{scenario}-action"
                profile = oni_scenario_execution_specs.ACTION_PROFILES.get(scenario)
                command = (build(builder, selector) if profile is None
                           else build(builder, selector, profile))
                self.assertEqual(EXPECTED_ACTION_COMMANDS[scenario], command)
                self.assertNotEqual(builder, command)
                self.assertTrue(oni_target.valid_debug_command(command))
                self.assertFalse(oni_target.valid_debug_command(builder))

    def test_cleanup_commands_are_concrete_and_allowlisted(self):
        build = getattr(oni_target, "build_scenario_cleanup_command")

        for scenario, selector in NATIVE_SELECTORS.items():
            with self.subTest(scenario=scenario):
                profile = oni_scenario_execution_specs.ACTION_PROFILES.get(scenario)
                command = (build(scenario, selector) if profile is None
                           else build(scenario, selector, profile))
                self.assertEqual(
                    EXPECTED_ACTION_COMMANDS[scenario].replace(
                        "scenario-action:", "scenario-cleanup:", 1),
                    command,
                )
                self.assertTrue(oni_target.valid_debug_command(command))


class TargetAdapterNativeActionTests(unittest.TestCase):
    def test_mac_adapter_delivers_action_through_command_file(self):
        adapter = oni_target.MacTargetAdapter("local")
        selector = NATIVE_SELECTORS["door"]

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with (
                mock.patch.object(adapter, "game_data_root", return_value=root),
                mock.patch.object(adapter, "game_running", return_value=True),
                mock.patch.object(adapter, "_activate_game"),
            ):
                getattr(adapter, "invoke_native_action")("door-action", selector)

            command_file = root / "oni_together_debug_command.txt"
            self.assertEqual(
                "scenario-action:door:kind=netId:netId=41\n",
                command_file.read_text(),
            )

    def test_windows_adapter_delivers_action_through_command_file(self):
        adapter = oni_target.WindowsTargetAdapter("alienware")
        powershell_calls = []

        def powershell(script):
            powershell_calls.append(script)
            return "1" if "Get-Process" in script else ""

        with mock.patch.object(adapter, "_powershell", side_effect=powershell):
            getattr(adapter, "invoke_native_action")(
                "door-action", NATIVE_SELECTORS["door"])

        self.assertTrue(any("oni_together_debug_command.txt" in call
                            for call in powershell_calls))
        self.assertTrue(any("scenario-action:door:kind=netId:netId=41" in call
                            for call in powershell_calls))


class NativeActionCleanupLifecycleTests(unittest.TestCase):
    def test_concrete_cleanup_is_attempted_after_barrier_failure(self):
        spec = copy.deepcopy(oni_target.SCENARIO_EXECUTION_SPECS["door"])
        state = {"scenario": "door", "selector": NATIVE_SELECTORS["door"]}
        host = mock.Mock(spec=oni_target.MacTargetAdapter)
        client = mock.Mock(spec=oni_target.WindowsTargetAdapter)
        runtime = oni_integration.ScenarioRuntime(state, host, client)
        runtime.wait_for_typed_barrier = mock.Mock(
            side_effect=RuntimeError("barrier failed"))

        with self.assertRaisesRegex(RuntimeError, "barrier failed"):
            oni_integration.run_scenario_execution(spec, runtime)

        getattr(host, "invoke_native_action").assert_called_once_with(
            "door-action", NATIVE_SELECTORS["door"])
        getattr(host, "cleanup_scenario").assert_called_once_with(
            "door", NATIVE_SELECTORS["door"])
        getattr(client, "cleanup_scenario").assert_called_once_with(
            "door", NATIVE_SELECTORS["door"])

    def test_profiled_runtime_uses_three_argument_action_and_cleanup(self):
        scenario = "motion"
        selector = NATIVE_SELECTORS[scenario]
        profile = oni_scenario_execution_specs.ACTION_PROFILES[scenario]
        spec = copy.deepcopy(oni_target.SCENARIO_EXECUTION_SPECS[scenario])
        host = mock.Mock(spec=oni_target.MacTargetAdapter)
        client = mock.Mock(spec=oni_target.WindowsTargetAdapter)
        runtime = oni_integration.ScenarioRuntime(
            {"scenario": scenario, "selector": selector}, host, client)
        runtime.wait_for_typed_barrier = mock.Mock(
            side_effect=RuntimeError("barrier failed"))

        with self.assertRaisesRegex(RuntimeError, "barrier failed"):
            oni_integration.run_scenario_execution(spec, runtime)

        getattr(host, "invoke_native_action").assert_called_once_with(
            "motion-action", selector, profile)
        getattr(host, "cleanup_scenario").assert_called_once_with(
            scenario, selector, profile)
        getattr(client, "cleanup_scenario").assert_called_once_with(
            scenario, selector, profile)


if __name__ == "__main__":
    unittest.main()
