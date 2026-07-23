import unittest

from scripts import oni_integration
from scripts.oni_fault_execution import run_fault_execution
from scripts.test_oni_fault_runtime_fixture import (
    RecordingFaultRuntime,
    execution_spec,
)


FAULT_LINE = (
    "[DebugCommand][OK] command=fault-inject:building.destroy-deferred "
    "receiptId=fault-receipt:building.destroy-deferred "
    "caseId=building.destroy-deferred targetId=target:minion:7 "
    "consumed=true passed=true stage=runtime "
    "fixtureDisposeRequested=false fixtureDisposeRequestedFrame=0 "
    "fixtureDisposeObservedFrame=0 fixtureAbsent=false "
    "reason=fault-receipt:building.destroy-deferred\n"
)
PENDING_LINE = (
    "[DebugCommand][FAIL] command=fault-clean:building.destroy-deferred "
    "receiptId=fault-clean-receipt:building.destroy-deferred "
    "caseId=building.destroy-deferred targetId=target:minion:7 "
    "consumed=true passed=false stage=cleanup-pending "
    "fixtureDisposeRequested=true fixtureDisposeRequestedFrame=7 "
    "fixtureDisposeObservedFrame=7 fixtureAbsent=false "
    "reason=fixture-absence-not-observed:building.destroy-deferred\n"
)
FINAL_LINE = (
    "[DebugCommand][OK] command=fault-clean:building.destroy-deferred "
    "receiptId=fault-clean-receipt:building.destroy-deferred "
    "caseId=building.destroy-deferred targetId=target:minion:7 "
    "consumed=true passed=true stage=runtime "
    "fixtureDisposeRequested=true fixtureDisposeRequestedFrame=7 "
    "fixtureDisposeObservedFrame=8 fixtureAbsent=true "
    "reason=fault-clean-receipt:building.destroy-deferred\n"
)


class LogTargetAdapterFixture:
    def __init__(self, lines: list[str]) -> None:
        self.lines = list(lines)
        self.log = ""
        self.commands: list[str] = []

    def player_log_path(self) -> str:
        return "Player.log"

    def size(self, path: str) -> int:
        _ = path
        return len(self.log)

    def submit(self, command: str) -> None:
        self.commands.append(command)
        if self.lines:
            self.log += self.lines.pop(0)

    def read_text(self, path: str, offset: int = 0) -> str:
        _ = path
        return self.log[offset:]


class FaultAdapterContractTests(unittest.TestCase):
    def test_parser_reads_all_identity_fields_from_native_log(self):
        receipt = oni_integration.parse_fault_command_receipt(
            FINAL_LINE, "fault-clean:building.destroy-deferred"
        )

        self.assertEqual("fault-clean-receipt:building.destroy-deferred", receipt["receiptId"])
        self.assertEqual("building.destroy-deferred", receipt["caseId"])
        self.assertEqual("target:minion:7", receipt["targetId"])
        self.assertTrue(receipt["consumed"])
        self.assertTrue(receipt["passed"])

    def test_cleanup_preserves_stage_and_typed_absence(self):
        pending = oni_integration.parse_fault_command_receipt(
            PENDING_LINE, "fault-clean:building.destroy-deferred"
        )
        final = oni_integration.parse_fault_command_receipt(
            FINAL_LINE, "fault-clean:building.destroy-deferred"
        )

        self.assertEqual("fault-clean-receipt:building.destroy-deferred", pending["receiptId"])
        self.assertEqual("building.destroy-deferred", pending["caseId"])
        self.assertEqual("target:minion:7", pending["targetId"])
        self.assertTrue(pending["consumed"])
        self.assertFalse(pending["passed"])
        self.assertEqual("cleanup-pending", pending["stage"])
        self.assertFalse(pending["fixtureAbsent"])
        self.assertEqual(7, pending["fixtureDisposeObservedFrame"])
        self.assertTrue(final["passed"])
        self.assertEqual("runtime", final["stage"])
        self.assertTrue(final["fixtureAbsent"])
        self.assertEqual(7, final["fixtureDisposeRequestedFrame"])
        self.assertEqual(8, final["fixtureDisposeObservedFrame"])

    def test_parser_rejects_every_missing_native_field(self):
        fields = (
            "receiptId", "caseId", "targetId", "consumed", "passed", "stage",
            "fixtureDisposeRequested", "fixtureDisposeRequestedFrame",
            "fixtureDisposeObservedFrame", "fixtureAbsent",
        )
        for field in fields:
            with self.subTest(field=field):
                broken = FINAL_LINE.replace(field + "=", "missing=", 1)
                with self.assertRaises(RuntimeError):
                    _ = oni_integration.parse_fault_command_receipt(
                        broken, "fault-clean:building.destroy-deferred"
                    )

    def test_cli_exposes_fixed_fault_execution_spec(self):
        args = oni_integration.build_parser().parse_args(
            ["fault-execute", "host", "building.destroy-deferred"]
        )

        self.assertEqual("fault-execute", args.action)
        self.assertEqual("host", args.target)
        self.assertEqual("building.destroy-deferred", args.case_id)

    def test_runner_uses_adapter_commands_and_native_receipts(self):
        target = LogTargetAdapterFixture([FAULT_LINE, PENDING_LINE, FINAL_LINE])
        command_runtime = oni_integration.TargetFaultRuntime(target)
        runtime = oni_integration.AdapterBackedFaultRuntime(
            command_runtime, RecordingFaultRuntime()
        )

        result = run_fault_execution(
            execution_spec("building.destroy-deferred"), runtime
        )

        self.assertTrue(result["passed"])
        self.assertEqual(
            ["fault-inject:building.destroy-deferred",
             "fault-clean:building.destroy-deferred",
             "fault-clean:building.destroy-deferred"],
            target.commands,
        )

    def test_forged_native_identity_is_not_rederived_from_arguments(self):
        forged = FINAL_LINE.replace("target:minion:7", "target:forged:99")
        receipt = oni_integration.parse_fault_command_receipt(
            forged, "fault-clean:building.destroy-deferred"
        )

        self.assertEqual("target:forged:99", receipt["targetId"])


if __name__ == "__main__":
    _ = unittest.main()
