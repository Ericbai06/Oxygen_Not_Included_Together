import unittest

from scripts.oni_fault_execution import run_fault_execution
from scripts.oni_fault_execution_specs import (
    FAULT_EXECUTION_SPECS,
    FaultExecutionResult,
)
from scripts.test_oni_fault_runtime_fixture import (
    FaultRuntimeMutation,
    FaultSpec,
    RecordingFaultRuntime,
    execution_spec,
)


UNITY_FAULT_IDS = {
    "duplicant.personality-missing",
    "duplicant.set-minion-before-controller",
    "duplicant.preview-flatulence",
    "duplicant.destroyed-add-component",
    "work.workable-unregistered",
    "work.target-missing",
    "work.original-dig-element-null",
    "work.client-native-start",
    "building.selected-elements-null",
    "building.destroy-deferred",
    "dlc.prefab-missing",
    "dlc.state-before-start-sm",
    "dlc.family-aquatic",
    "dlc.family-bionic",
    "dlc.family-frosty",
    "dlc.family-prehistoric",
    "dlc.family-spaced-out",
    "dlc.family-common",
}


class FaultRuntimeDriverTests(unittest.TestCase):
    def run_fault(
        self,
        spec: FaultSpec,
        runtime: RecordingFaultRuntime,
    ) -> FaultExecutionResult:
        return run_fault_execution(spec, runtime)

    def test_runner_executes_full_fault_and_clean_control_lifecycle(self):
        runtime = RecordingFaultRuntime()

        result = self.run_fault(execution_spec(), runtime)

        self.assertTrue(result["passed"])
        self.assertEqual(
            [
                "setup",
                "fault",
                "barrier",
                "invariant",
                "oracle",
                "reset",
                "clean",
                "clean-barrier",
                "clean-invariant",
                "clean-oracle",
                "cleanup",
            ],
            runtime.events,
        )
        self.assertEqual({"target:minion:7"}, set(runtime.target_ids))
        self.assertEqual(
            [
                "fault-receipt:duplicant.destroyed-add-component",
                "fault-clean-receipt:duplicant.destroyed-add-component",
            ],
            runtime.receipt_predicates,
        )

    def test_deferred_destroy_retries_same_clean_command_until_absence(self):
        runtime = RecordingFaultRuntime(
            FaultRuntimeMutation(cleanup_pending_receipts=1)
        )
        spec = execution_spec("building.destroy-deferred")

        result = self.run_fault(spec, runtime)

        self.assertTrue(result["passed"])
        clean_calls = [call for call in runtime.commands if call[0] == spec["cleanCommand"]]
        self.assertEqual(
            [(spec["cleanCommand"], "target:minion:7")] * 2,
            clean_calls,
        )
        self.assertEqual(2, runtime.events.count("clean-barrier"))
        self.assertLess(
            runtime.events.index("clean", runtime.events.index("clean") + 1),
            runtime.events.index("clean-invariant"),
        )

    def test_deferred_destroy_pending_timeout_is_bounded_and_evidenced(self):
        runtime = RecordingFaultRuntime(
            FaultRuntimeMutation(
                cleanup_pending_receipts=100,
                cleanup_dispose_requested=False,
            )
        )
        spec = execution_spec("building.destroy-deferred")
        spec["observationWindowSeconds"] = 3

        failure = ""
        try:
            _ = self.run_fault(spec, runtime)
        except RuntimeError as error:
            failure = str(error)

        self.assertIn("cleanup-pending", failure)

        clean_calls = [call for call in runtime.commands if call[0] == spec["cleanCommand"]]
        self.assertEqual(3, len(clean_calls))
        self.assertEqual({"target:minion:7"}, {target for _, target in clean_calls})
        self.assertEqual(3, runtime.events.count("clean-barrier"))
        self.assertEqual("cleanup", runtime.events[-1])

    def test_deferred_destroy_rejects_pass_without_observed_absence(self):
        runtime = RecordingFaultRuntime(
            FaultRuntimeMutation(cleanup_absent_on_success=False)
        )

        with self.assertRaises(RuntimeError):
            _ = self.run_fault(execution_spec("building.destroy-deferred"), runtime)

        self.assertEqual(1, runtime.events.count("clean-barrier"))
        self.assertEqual("cleanup", runtime.events[-1])

    def test_runner_rejects_wrong_case_receipt_predicates(self):
        for field in ("faultReceiptPredicate", "cleanReceiptPredicate"):
            with self.subTest(field=field):
                spec = execution_spec()
                spec[field] = (
                    "fault-clean-receipt:wrong-case"
                    if field == "cleanReceiptPredicate"
                    else "fault-receipt:wrong-case"
                )
                with self.assertRaises(RuntimeError):
                    _ = self.run_fault(spec, RecordingFaultRuntime())

    def test_runner_cleans_up_after_failure_at_every_runtime_stage(self):
        stages = (
            "setup",
            "fault",
            "barrier",
            "invariant",
            "oracle",
            "reset",
            "clean",
            "clean-barrier",
            "clean-invariant",
            "clean-oracle",
        )
        for stage in stages:
            with self.subTest(stage=stage):
                mutation = FaultRuntimeMutation(fail_at=stage)
                runtime = RecordingFaultRuntime(mutation)
                with self.assertRaisesRegex(RuntimeError, stage + " failed"):
                    _ = self.run_fault(execution_spec(), runtime)
                self.assertEqual("cleanup", runtime.events[-1])
                self.assertEqual(1, runtime.events.count("cleanup"))

    def test_cleanup_failure_is_chained_without_hiding_primary_failure(self):
        mutation = FaultRuntimeMutation(fail_at="barrier", cleanup_fails=True)
        runtime = RecordingFaultRuntime(mutation)

        with self.assertRaisesRegex(RuntimeError, "barrier failed") as raised:
            _ = self.run_fault(execution_spec(), runtime)

        self.assertIsNotNone(raised.exception.__cause__)
        self.assertRegex(str(raised.exception.__cause__), "cleanup failed")

    def test_mutation_rejects_unconsumed_or_wrong_target_receipt(self):
        mutations = (
            FaultRuntimeMutation(receipt_consumed=False),
            FaultRuntimeMutation(receipt_target_drift=True),
        )
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                runtime = RecordingFaultRuntime(mutation)
                with self.assertRaises(RuntimeError):
                    _ = self.run_fault(execution_spec(), runtime)
                self.assertEqual("cleanup", runtime.events[-1])

    def test_mutation_rejects_receipt_only_oracle(self):
        runtime = RecordingFaultRuntime(FaultRuntimeMutation(receipt_only_oracle=True))

        with self.assertRaises(RuntimeError):
            _ = self.run_fault(execution_spec(), runtime)

        self.assertEqual("cleanup", runtime.events[-1])

    def test_mutation_rejects_destroyed_object_component_identity_or_exception(self):
        mutations = (
            FaultRuntimeMutation(destroyed_component_added=True),
            FaultRuntimeMutation(destroyed_identity_added=True),
            FaultRuntimeMutation(destroyed_exception=True),
        )
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                runtime = RecordingFaultRuntime(mutation)
                with self.assertRaises(RuntimeError):
                    _ = self.run_fault(execution_spec(), runtime)
                self.assertEqual("cleanup", runtime.events[-1])

    def test_mutation_rejects_clean_control_without_state_restoration(self):
        runtime = RecordingFaultRuntime(FaultRuntimeMutation(clean_restored=False))

        with self.assertRaises(RuntimeError):
            _ = self.run_fault(execution_spec(), runtime)

        self.assertEqual({"target:minion:7"}, set(runtime.target_ids))
        self.assertEqual("cleanup", runtime.events[-1])

    def test_mutation_rejects_each_missing_dlc_runtime_field(self):
        mutations = (
            FaultRuntimeMutation(dlc_missing_prefab=True),
            FaultRuntimeMutation(dlc_missing_identity=True),
            FaultRuntimeMutation(dlc_missing_state=True),
            FaultRuntimeMutation(dlc_missing_admission=True),
        )
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                runtime = RecordingFaultRuntime(mutation)
                spec = execution_spec("dlc.family-aquatic", "real")
                with self.assertRaises(RuntimeError):
                    _ = self.run_fault(spec, runtime)
                self.assertEqual("cleanup", runtime.events[-1])

    def test_valid_dlc_oracle_accepts_identity_state_and_admission_generation(self):
        runtime = RecordingFaultRuntime()
        spec = execution_spec("dlc.family-aquatic", "real")

        result = self.run_fault(spec, runtime)

        self.assertTrue(result["passed"])
        self.assertEqual({"target:minion:7"}, set(runtime.target_ids))
        self.assertEqual("cleanup", runtime.events[-1])

    def test_catalog_exactly_covers_18_unity_cases_with_executable_metadata(self):
        self.assertEqual(UNITY_FAULT_IDS, set(FAULT_EXECUTION_SPECS))
        for case_id, spec in FAULT_EXECUTION_SPECS.items():
            with self.subTest(case_id=case_id):
                self.assertTrue(spec["triggerMethod"])
                self.assertTrue(spec["oracleMethod"])
                self.assertTrue(spec["snapshotMethod"])
                self.assertEqual("fault-inject:" + case_id, spec["faultCommand"])
                self.assertEqual("fault-receipt:" + case_id, spec["faultReceiptPredicate"])
                self.assertEqual("fault-clean:" + case_id, spec["cleanCommand"])
                self.assertEqual(
                    "fault-clean-receipt:" + case_id,
                    spec["cleanReceiptPredicate"],
                )


if __name__ == "__main__":
    _ = unittest.main()
