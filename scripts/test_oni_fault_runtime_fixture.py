from dataclasses import dataclass
from typing import cast

from scripts.oni_fault_execution_specs import (
    DestroyedMinionEvidence,
    DlcRuntimeEvidence,
    FaultExecutionSpec,
    FaultOracle,
    FaultReceipt,
    FaultSetup,
    FaultSnapshot,
)


FaultSpec = FaultExecutionSpec


class FaultStageError(RuntimeError):
    def __init__(self, stage: str) -> None:
        self.stage: str = stage
        super().__init__(stage + " failed")


class FaultCleanupError(RuntimeError):
    def __init__(self) -> None:
        super().__init__("cleanup failed")


@dataclass(frozen=True, slots=True)
class FaultRuntimeMutation:
    fail_at: str | None = None
    cleanup_fails: bool = False
    receipt_consumed: bool = True
    receipt_target_drift: bool = False
    receipt_only_oracle: bool = False
    clean_restored: bool = True
    dlc_missing_prefab: bool = False
    dlc_missing_identity: bool = False
    dlc_missing_state: bool = False
    dlc_missing_admission: bool = False
    destroyed_component_added: bool = False
    destroyed_identity_added: bool = False
    destroyed_exception: bool = False
    cleanup_pending_receipts: int = 0
    cleanup_dispose_requested: bool = True
    cleanup_absent_on_success: bool = True


class RecordingFaultRuntime:
    def __init__(self, mutation: FaultRuntimeMutation | None = None) -> None:
        self.events: list[str] = []
        self.target_ids: list[str] = []
        self.receipt_predicates: list[str] = []
        self.commands: list[tuple[str, str]] = []
        self.clean_receipts: int = 0
        self.mutation: FaultRuntimeMutation = mutation or FaultRuntimeMutation()

    def _record(self, stage: str, target_id: str | None = None) -> None:
        self.events.append(stage)
        if target_id is not None:
            self.target_ids.append(target_id)
        if self.mutation.fail_at == stage:
            raise FaultStageError(stage)

    def setup_fault_case(self, spec: FaultSpec) -> FaultSetup:
        _ = spec
        self._record("setup")
        return {"targetId": "target:minion:7", "baselineHash": "sha256:" + "1" * 64}

    def run_fault_command(self, command: str, target_id: str) -> None:
        stage = "clean" if command.startswith("fault-clean:") else "fault"
        self.commands.append((command, target_id))
        self._record(stage, target_id)

    def wait_for_fault_receipt(
        self,
        predicate: str,
        target_id: str,
        timeout: int,
    ) -> FaultReceipt:
        _ = timeout
        self.receipt_predicates.append(predicate)
        clean = predicate.startswith("fault-clean-receipt:")
        self._record("clean-barrier" if clean else "barrier", target_id)
        if clean:
            self.clean_receipts += 1
            if self.clean_receipts <= self.mutation.cleanup_pending_receipts:
                return self._cleanup_receipt(predicate, target_id, pending=True)
            return self._cleanup_receipt(predicate, target_id, pending=False)
        observed = "target:other:99" if self.mutation.receipt_target_drift else target_id
        return {
            "receiptId": predicate,
            "targetId": observed,
            "consumed": self.mutation.receipt_consumed,
            "passed": True,
        }

    def _cleanup_receipt(
        self, predicate: str, target_id: str, pending: bool
    ) -> FaultReceipt:
        requested = self.mutation.cleanup_dispose_requested
        absent = not pending and self.mutation.cleanup_absent_on_success
        observed_frame = 8 if absent else 7
        return cast(
            FaultReceipt,
            cast(object, {
                "receiptId": predicate,
                "targetId": target_id,
                "consumed": True,
                "passed": not pending,
                "stage": "cleanup-pending" if pending else "runtime",
                "detail": (
                    "fixture-absence-not-observed" if pending
                    else predicate
                ),
                "fixtureDisposeRequested": requested,
                "fixtureDisposeRequestedFrame": 7,
                "fixtureDisposeObservedFrame": observed_frame,
                "fixtureAbsent": absent,
            }),
        )

    def capture_fault_invariant(
        self,
        spec: FaultSpec,
        target_id: str,
        phase: str,
    ) -> FaultSnapshot:
        self._record("clean-invariant" if phase == "clean" else "invariant", target_id)
        restored = phase == "clean" and self.mutation.clean_restored
        state_hash = "sha256:" + ("1" if restored else "2") * 64
        dlc = self._dlc_evidence(spec) if spec["caseId"].startswith("dlc.family-") else None
        destroyed = (
            self._destroyed_evidence()
            if spec["caseId"] == "duplicant.destroyed-add-component"
            else None
        )
        return {
            "schemaVersion": 1,
            "caseId": spec["caseId"],
            "targetId": target_id,
            "stateHash": state_hash,
            "preserved": True,
            "dlcRuntime": dlc,
            "destroyedMinion": destroyed,
        }

    def evaluate_fault_oracle(
        self,
        spec: FaultSpec,
        receipt: FaultReceipt,
        setup: FaultSetup,
        snapshot: FaultSnapshot,
        phase: str,
    ) -> FaultOracle:
        _ = spec
        self._record("clean-oracle" if phase == "clean" else "oracle", snapshot["targetId"])
        if self.mutation.receipt_only_oracle:
            return {"passed": True}
        return {
            "passed": receipt["passed"],
            "observedTargetId": snapshot["targetId"],
            "beforeHash": setup["baselineHash"],
            "afterHash": snapshot["stateHash"],
            "invariantPreserved": snapshot["preserved"],
        }

    def reset_fault_case(self, spec: FaultSpec, target_id: str) -> None:
        _ = spec
        self._record("reset", target_id)

    def cleanup_fault_case(self, spec: FaultSpec, target_id: str) -> None:
        _ = spec
        self._record("cleanup", target_id)
        if self.mutation.cleanup_fails:
            raise FaultCleanupError()

    def _dlc_evidence(self, spec: FaultSpec) -> DlcRuntimeEvidence:
        family = spec["caseId"].removeprefix("dlc.family-")
        return {
            "scenario": "dlc-runtime",
            "dlcFamily": family,
            "prefab": (
                "" if self.mutation.dlc_missing_prefab
                else "MinnowImperativePOIAConfig"
            ),
            "identity": "" if self.mutation.dlc_missing_identity else "net:dlc:17",
            "stateMachineState": "" if self.mutation.dlc_missing_state else "idle",
            "admissionGeneration": 0 if self.mutation.dlc_missing_admission else 4,
        }

    def _destroyed_evidence(self) -> DestroyedMinionEvidence:
        return {
            "componentCountBefore": 8,
            "componentCountAfter": 9 if self.mutation.destroyed_component_added else 8,
            "identityPresentBefore": False,
            "identityPresentAfter": self.mutation.destroyed_identity_added,
            "exceptionCount": 1 if self.mutation.destroyed_exception else 0,
        }


def execution_spec(
    case_id: str = "duplicant.destroyed-add-component",
    tier: str = "ingame",
) -> FaultSpec:
    return {
        "caseId": case_id,
        "tier": tier,
        "triggerMethod": "production-trigger",
        "oracleMethod": "typed-snapshot-oracle",
        "snapshotMethod": "capture-target-invariant",
        "faultCommand": "fault-inject:" + case_id,
        "faultReceiptPredicate": "fault-receipt:" + case_id,
        "cleanCommand": "fault-clean:" + case_id,
        "cleanReceiptPredicate": "fault-clean-receipt:" + case_id,
        "observationWindowSeconds": 30,
    }
