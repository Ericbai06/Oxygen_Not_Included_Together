import copy
import unittest
from unittest import mock

import scripts.oni_integration as oni_integration
import scripts.test_typed_scenario_evidence as typed_evidence
from scripts import oni_target


PASSIVE_DRIVER_IDS = {
    "arbitrary-json",
    "json",
    "log-observer",
    "mcp-call",
    "native-game-event",
    "typed-evidence",
}

EXPECTED_SELECTOR_KIND = {
    "remote-dig": "cell",
    "animation": "cell",
    "motion": "netId",
    "effect": "netId",
    "building-lifecycle": "cell",
    "priority": "netId",
    "building-config": "netId",
    "door": "netId",
    "uproot": "netId",
    "toggle": "netId",
    "research": "tech",
    "schedule": "schedule",
    "inventory": "inventory",
    "storage": "storage",
    "pickup": "pickup",
    "deconstruct": "deconstruct",
    "chat": "sender",
    "cursor": "player",
    "entity-lifecycle": "netId",
    "dlc-runtime": "dlc",
    "rocket": "rocket",
    "reconnect-world-state": "session",
}


def production_contract_api(name):
    return getattr(oni_target, name)


def production_runner_api(name):
    return getattr(oni_integration, name)


class ScenarioDriverCatalogTests(unittest.TestCase):
    def test_all_22_specs_bind_callable_production_drivers(self):
        specs = production_contract_api("SCENARIO_EXECUTION_SPECS")
        registry = production_runner_api("DRIVER_REGISTRY")

        self.assertEqual(set(typed_evidence.SCENARIOS), set(specs))
        self.assertEqual({spec["driver"] for spec in specs.values()}, set(registry))
        self.assertTrue(all(callable(executor) for executor in registry.values()))
        self.assertFalse(set(registry) & PASSIVE_DRIVER_IDS)

    def test_every_spec_declares_an_executable_lifecycle(self):
        specs = production_contract_api("SCENARIO_EXECUTION_SPECS")
        required = {
            "driver",
            "trigger",
            "targetSelectorKind",
            "completionBarrierPredicate",
            "observationWindowSeconds",
            "cleanup",
        }
        missing = {
            scenario: sorted(required - set(spec))
            for scenario, spec in specs.items()
            if required - set(spec)
        }

        self.assertEqual([], production_contract_api("validate_execution_specs")(specs))
        self.assertEqual({}, missing)
        for scenario, spec in specs.items():
            with self.subTest(scenario=scenario):
                self.assertEqual(
                    EXPECTED_SELECTOR_KIND[scenario],
                    spec["targetSelectorKind"],
                )
                self.assertIn(spec["trigger"]["type"], {"debug-command", "native-action"})
                self.assertTrue(spec["trigger"].get("commandBuilder"))
                self.assertTrue(spec["completionBarrierPredicate"])
                self.assertGreater(spec["observationWindowSeconds"], 0)
                self.assertIn(spec["cleanup"]["type"], {"callable", "debug-command"})
                self.assertTrue(spec["cleanup"].get("executor"))

    def test_validation_rejects_passive_or_arbitrary_acceptance(self):
        specs = copy.deepcopy(production_contract_api("SCENARIO_EXECUTION_SPECS"))
        specs["door"]["trigger"] = {
            "type": "mcp-call",
            "commandBuilder": "arbitrary-json",
        }

        failures = production_contract_api("validate_execution_specs")(specs)

        self.assertTrue(any("door" in failure for failure in failures))

    def test_validation_rejects_invalid_debug_command_builder(self):
        specs = copy.deepcopy(production_contract_api("SCENARIO_EXECUTION_SPECS"))
        specs["reconnect-world-state"]["trigger"] = {
            "type": "debug-command",
            "commandBuilder": "arbitrary-json",
        }

        failures = production_contract_api("validate_execution_specs")(specs)

        self.assertTrue(
            any("reconnect-world-state" in failure for failure in failures)
        )

    def test_validation_rejects_generic_cleanup_string(self):
        specs = copy.deepcopy(production_contract_api("SCENARIO_EXECUTION_SPECS"))
        specs["door"]["cleanup"] = "cleanup"

        failures = production_contract_api("validate_execution_specs")(specs)

        self.assertTrue(any("door" in failure for failure in failures))


class ScenarioRunnerTests(unittest.TestCase):
    def execution_spec(self):
        return {
            "driver": "test-debug-driver",
            "trigger": {
                "type": "debug-command",
                "commandBuilder": "test-command",
            },
            "targetSelectorKind": "netId",
            "completionBarrierPredicate": "typed-final-state:door",
            "observationWindowSeconds": 9,
            "cleanup": {"type": "callable", "executor": "test-cleanup"},
        }

    def test_runner_triggers_waits_for_typed_barrier_then_cleans_up(self):
        events = []
        driver = mock.Mock(side_effect=lambda spec, runtime: events.append("trigger"))
        cleanup = mock.Mock(side_effect=lambda spec, runtime: events.append("cleanup"))
        runtime = mock.Mock()
        runtime.wait_for_typed_barrier.side_effect = lambda predicate, timeout: (
            events.append("barrier") or {"passed": True}
        )

        with (
            mock.patch.dict(
                production_runner_api("DRIVER_REGISTRY"),
                {"test-debug-driver": driver},
                clear=True,
            ),
            mock.patch.dict(
                production_runner_api("CLEANUP_REGISTRY"),
                {"test-cleanup": cleanup},
                clear=True,
            ),
        ):
            result = production_runner_api("run_scenario_execution")(
                self.execution_spec(), runtime
            )

        self.assertEqual({"passed": True}, result)
        self.assertEqual(["trigger", "barrier", "cleanup"], events)

    def test_runner_cleans_up_when_typed_barrier_fails(self):
        events = []
        driver = mock.Mock(side_effect=lambda spec, runtime: events.append("trigger"))
        cleanup = mock.Mock(side_effect=lambda spec, runtime: events.append("cleanup"))
        runtime = mock.Mock()
        runtime.wait_for_typed_barrier.side_effect = RuntimeError("barrier failed")

        with (
            mock.patch.dict(
                production_runner_api("DRIVER_REGISTRY"),
                {"test-debug-driver": driver},
                clear=True,
            ),
            mock.patch.dict(
                production_runner_api("CLEANUP_REGISTRY"),
                {"test-cleanup": cleanup},
                clear=True,
            ),
            self.assertRaisesRegex(RuntimeError, "barrier failed"),
        ):
            production_runner_api("run_scenario_execution")(
                self.execution_spec(), runtime
            )

        self.assertEqual(["trigger", "cleanup"], events)

    def test_bare_log_observer_cannot_run_a_scenario(self):
        spec = self.execution_spec()
        spec["driver"] = "log-observer"
        runtime = mock.Mock()

        with self.assertRaises((KeyError, ValueError)):
            production_runner_api("run_scenario_execution")(spec, runtime)


if __name__ == "__main__":
    unittest.main()
