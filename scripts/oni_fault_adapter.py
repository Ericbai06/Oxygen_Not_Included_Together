from __future__ import annotations

import re
import time
from typing import Protocol, cast, final

from scripts.oni_fault_execution_specs import (
    FaultExecutionSpec,
    FaultOracle,
    FaultReceipt,
    FaultSetup,
    FaultSnapshot,
    NativeFaultReceipt,
)


POLL_INTERVAL_SECONDS = 0.25
FAULT_COMMAND_OUTCOME = re.compile(
    r"\[DebugCommand\]\[(OK|FAIL)\] command=([^\s]+) " +
    r"receiptId=([^\s]+) caseId=([^\s]+) targetId=([^\s]+) " +
    r"consumed=(true|false) passed=(true|false) stage=([^\s]+) " +
    r"fixtureDisposeRequested=(true|false) fixtureDisposeRequestedFrame=(\d+) " +
    r"fixtureDisposeObservedFrame=(\d+) fixtureAbsent=(true|false) " +
    r"reason=([^\r\n]+)"
)


class FaultCommandTarget(Protocol):
    def player_log_path(self) -> str: ...
    def size(self, path: str) -> int: ...
    def submit(self, command: str) -> None: ...
    def read_text(self, path: str, offset: int = 0) -> str: ...


class FaultLifecycleBackend(Protocol):
    def setup_fault_case(self, spec: FaultExecutionSpec) -> FaultSetup: ...
    def capture_fault_invariant(
        self, spec: FaultExecutionSpec, target_id: str, phase: str
    ) -> FaultSnapshot: ...
    def evaluate_fault_oracle(
        self, spec: FaultExecutionSpec, receipt: FaultReceipt,
        setup: FaultSetup, snapshot: FaultSnapshot, phase: str,
    ) -> FaultOracle: ...
    def reset_fault_case(self, spec: FaultExecutionSpec, target_id: str) -> None: ...
    def cleanup_fault_case(self, spec: FaultExecutionSpec, target_id: str) -> None: ...


def parse_fault_command_receipt(
    text: str, command: str
) -> NativeFaultReceipt:
    matches = [
        match for match in FAULT_COMMAND_OUTCOME.finditer(text)
        if match.group(2) == command
    ]
    if not matches:
        raise RuntimeError(command + " structured fault outcome not found")
    match = matches[-1]
    passed = match.group(7) == "true"
    if passed != (match.group(1) == "OK"):
        raise RuntimeError(command + " fault outcome status drift")
    return {
        "receiptId": match.group(3),
        "caseId": match.group(4),
        "targetId": match.group(5),
        "consumed": match.group(6) == "true",
        "passed": passed,
        "stage": match.group(8),
        "fixtureDisposeRequested": match.group(9) == "true",
        "fixtureDisposeRequestedFrame": int(match.group(10)),
        "fixtureDisposeObservedFrame": int(match.group(11)),
        "fixtureAbsent": match.group(12) == "true",
        "detail": match.group(13),
    }


@final
class TargetFaultRuntime:
    def __init__(self, target: FaultCommandTarget) -> None:
        self._target = target
        self._command = ""
        self._path = ""
        self._offset = 0

    def run_fault_command(self, command: str, target_id: str) -> None:
        _ = target_id
        self._path = self._target.player_log_path()
        self._offset = self._target.size(self._path)
        self._command = command
        self._target.submit(command)

    def wait_for_fault_receipt(
        self, predicate: str, target_id: str, timeout: int
    ) -> FaultReceipt:
        _ = predicate, target_id
        deadline = time.monotonic() + timeout
        while time.monotonic() <= deadline:
            text = self._target.read_text(self._path, self._offset)
            try:
                return cast(
                    FaultReceipt,
                    cast(object, parse_fault_command_receipt(text, self._command)),
                )
            except RuntimeError:
                if time.monotonic() >= deadline:
                    raise
                time.sleep(POLL_INTERVAL_SECONDS)
        raise RuntimeError(self._command + " fault receipt timeout")


@final
class AdapterBackedFaultRuntime:
    def __init__(
        self, commands: TargetFaultRuntime, lifecycle: FaultLifecycleBackend
    ) -> None:
        self._commands = commands
        self._lifecycle = lifecycle

    def setup_fault_case(self, spec: FaultExecutionSpec) -> FaultSetup:
        return self._lifecycle.setup_fault_case(spec)

    def run_fault_command(self, command: str, target_id: str) -> None:
        self._commands.run_fault_command(command, target_id)

    def wait_for_fault_receipt(
        self, predicate: str, target_id: str, timeout: int
    ) -> FaultReceipt:
        return self._commands.wait_for_fault_receipt(predicate, target_id, timeout)

    def capture_fault_invariant(
        self, spec: FaultExecutionSpec, target_id: str, phase: str
    ) -> FaultSnapshot:
        return self._lifecycle.capture_fault_invariant(spec, target_id, phase)

    def evaluate_fault_oracle(
        self, spec: FaultExecutionSpec, receipt: FaultReceipt,
        setup: FaultSetup, snapshot: FaultSnapshot, phase: str,
    ) -> FaultOracle:
        return self._lifecycle.evaluate_fault_oracle(
            spec, receipt, setup, snapshot, phase)

    def reset_fault_case(self, spec: FaultExecutionSpec, target_id: str) -> None:
        self._lifecycle.reset_fault_case(spec, target_id)

    def cleanup_fault_case(self, spec: FaultExecutionSpec, target_id: str) -> None:
        self._lifecycle.cleanup_fault_case(spec, target_id)
