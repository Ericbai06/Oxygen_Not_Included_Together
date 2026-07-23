import copy
import unittest
from unittest import mock

from scripts import oni_integration, oni_scenario_execution_specs, oni_target


ACTION_PROFILES = {
    "building-config": "toggle-checkbox",
    "uproot": "mark-and-cancel-uproot",
    "inventory": "add-remove-sand-1000g",
    "pickup": "primary-duplicant-pickup-drop",
    "effect": "toggle-integration-effect",
    "animation": "primary-minion-working-loop",
    "motion": "offset-one-cell-one-tick",
    "entity-lifecycle": "deactivate-reactivate-same-prefab",
    "dlc-runtime": "next-admissible-state-restore",
    "rocket": "next-reachable-boarding-restore",
}

PREPARED_SELECTORS = {
    "building-config": {"kind": "netId", "netId": 41},
    "uproot": {"kind": "netId", "netId": 41},
    "inventory": {"kind": "inventory"},
    "pickup": {"kind": "pickup", "itemNetId": 42, "targetCell": 95028},
    "effect": {"kind": "netId", "netId": 41},
    "animation": {"kind": "cell", "cell": 95028},
    "motion": {"kind": "netId", "netId": 41},
    "entity-lifecycle": {"kind": "netId", "netId": 41},
    "dlc-runtime": {
        "kind": "dlc", "dlcFamily": "SpacedOut",
        "prefab": "ScoutRover", "identity": "rover-7",
    },
    "rocket": {"kind": "rocket", "rocketNetId": 71, "padNetId": 72},
}


def production_api(name):
    return getattr(oni_target, name)


class ScenarioActionProfileCatalogTests(unittest.TestCase):
    def test_specs_bind_exact_fixed_profiles_separately_from_selectors(self):
        registered = getattr(oni_scenario_execution_specs, "ACTION_PROFILES")
        specs = production_api("SCENARIO_EXECUTION_SPECS")

        self.assertEqual(ACTION_PROFILES, registered)
        for scenario, profile in ACTION_PROFILES.items():
            with self.subTest(scenario=scenario):
                trigger = specs[scenario]["trigger"]
                self.assertEqual(profile, trigger["actionProfile"])
                self.assertNotIn("actionProfile", PREPARED_SELECTORS[scenario])
                self.assertEqual(
                    [], oni_target.validate_target_selector(
                        scenario, PREPARED_SELECTORS[scenario]),
                )

    def test_profile_commands_are_canonical_and_allowlisted(self):
        build = production_api("build_native_action_command")

        for scenario, profile in ACTION_PROFILES.items():
            with self.subTest(scenario=scenario):
                selector = PREPARED_SELECTORS[scenario]
                command = build(f"{scenario}-action", selector, profile)
                fields = {**selector, "profile": profile}
                expected = f"scenario-action:{scenario}:" + ":".join(
                    f"{key}={value}" for key, value in sorted(fields.items())
                )
                self.assertEqual(expected, command)
                self.assertTrue(oni_target.valid_debug_command(command))
                self.assertFalse(oni_target.valid_debug_command(
                    command.replace(f"profile={profile}", "profile=arbitrary")))

    def test_execution_spec_validation_rejects_missing_or_wrong_profile(self):
        specs = copy.deepcopy(production_api("SCENARIO_EXECUTION_SPECS"))
        del specs["motion"]["trigger"]["actionProfile"]
        missing = production_api("validate_execution_specs")(specs)
        specs = copy.deepcopy(production_api("SCENARIO_EXECUTION_SPECS"))
        specs["motion"]["trigger"]["actionProfile"] = "arbitrary"
        wrong = production_api("validate_execution_specs")(specs)

        self.assertTrue(any("motion" in failure and "profile" in failure
                            for failure in missing))
        self.assertTrue(any("motion" in failure and "profile" in failure
                            for failure in wrong))


class ScenarioActionProfileLifecycleTests(unittest.TestCase):
    def test_driver_forwards_profile_and_cleanup_after_barrier_failure(self):
        scenario = "motion"
        profile = ACTION_PROFILES[scenario]
        spec = copy.deepcopy(production_api("SCENARIO_EXECUTION_SPECS")[scenario])
        host = mock.Mock()
        client = mock.Mock()
        state = {"scenario": scenario, "selector": PREPARED_SELECTORS[scenario]}
        runtime = oni_integration.ScenarioRuntime(state, host, client)
        runtime.wait_for_typed_barrier = mock.Mock(
            side_effect=RuntimeError("barrier failed"))

        with self.assertRaisesRegex(RuntimeError, "barrier failed"):
            oni_integration.run_scenario_execution(spec, runtime)

        host.invoke_native_action.assert_called_once_with(
            "motion-action", PREPARED_SELECTORS[scenario], profile)
        host.cleanup_scenario.assert_called_once_with(
            scenario, PREPARED_SELECTORS[scenario], profile)
        client.cleanup_scenario.assert_called_once_with(
            scenario, PREPARED_SELECTORS[scenario], profile)


if __name__ == "__main__":
    unittest.main()
