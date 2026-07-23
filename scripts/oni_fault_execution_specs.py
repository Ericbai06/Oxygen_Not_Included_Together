from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass
from types import MappingProxyType
from typing import NotRequired, Protocol, TypedDict, override


class FaultExecutionSpec(TypedDict):
    caseId: str
    tier: str
    triggerMethod: str
    oracleMethod: str
    snapshotMethod: str
    faultCommand: str
    faultReceiptPredicate: str
    cleanCommand: str
    cleanReceiptPredicate: str
    observationWindowSeconds: int


class FaultReceipt(TypedDict):
    receiptId: str
    targetId: str
    consumed: bool
    passed: bool
    stage: NotRequired[str]
    detail: NotRequired[str]
    fixtureDisposeRequested: NotRequired[bool]
    fixtureDisposeRequestedFrame: NotRequired[int]
    fixtureDisposeObservedFrame: NotRequired[int]
    fixtureAbsent: NotRequired[bool]


class NativeFaultReceipt(TypedDict):
    receiptId: str
    caseId: str
    targetId: str
    consumed: bool
    passed: bool
    stage: str
    detail: str
    fixtureDisposeRequested: bool
    fixtureDisposeRequestedFrame: int
    fixtureDisposeObservedFrame: int
    fixtureAbsent: bool


class FaultSetup(TypedDict):
    targetId: str
    baselineHash: str


class DlcRuntimeEvidence(TypedDict):
    scenario: str
    dlcFamily: str
    prefab: str
    identity: str
    stateMachineState: str
    admissionGeneration: int


class DestroyedMinionEvidence(TypedDict):
    componentCountBefore: int
    componentCountAfter: int
    identityPresentBefore: bool
    identityPresentAfter: bool
    exceptionCount: int


class FaultSnapshot(TypedDict):
    schemaVersion: int
    caseId: str
    targetId: str
    stateHash: str
    preserved: bool
    dlcRuntime: DlcRuntimeEvidence | None
    destroyedMinion: DestroyedMinionEvidence | None


class FaultOracle(TypedDict, total=False):
    passed: bool
    observedTargetId: str
    beforeHash: str
    afterHash: str
    invariantPreserved: bool


class FaultExecutionResult(TypedDict):
    passed: bool


class FaultRuntime(Protocol):
    def setup_fault_case(self, spec: FaultExecutionSpec) -> FaultSetup: ...
    def run_fault_command(self, command: str, target_id: str) -> None: ...
    def wait_for_fault_receipt(
        self, predicate: str, target_id: str, timeout: int
    ) -> FaultReceipt: ...
    def capture_fault_invariant(
        self, spec: FaultExecutionSpec, target_id: str, phase: str
    ) -> FaultSnapshot: ...
    def evaluate_fault_oracle(
        self,
        spec: FaultExecutionSpec,
        receipt: FaultReceipt,
        setup: FaultSetup,
        snapshot: FaultSnapshot,
        phase: str,
    ) -> FaultOracle: ...
    def reset_fault_case(self, spec: FaultExecutionSpec, target_id: str) -> None: ...
    def cleanup_fault_case(self, spec: FaultExecutionSpec, target_id: str) -> None: ...


@dataclass(frozen=True, slots=True)
class FaultExecutionContractError(RuntimeError):
    stage: str
    detail: str

    @override
    def __str__(self) -> str:
        return self.stage + ": " + self.detail


@dataclass(frozen=True, slots=True)
class FaultMethodNames:
    trigger: str
    snapshot: str
    oracle: str


def _fault_spec(case_id: str, tier: str) -> FaultExecutionSpec:
    slug = case_id.replace(".", "-")
    methods = FaultMethodNames(
        trigger="trigger-" + slug,
        snapshot="capture-" + slug + "-invariant",
        oracle="evaluate-" + slug + "-oracle",
    )
    return {
        "caseId": case_id,
        "tier": tier,
        "triggerMethod": methods.trigger,
        "oracleMethod": methods.oracle,
        "snapshotMethod": methods.snapshot,
        "faultCommand": "fault-inject:" + case_id,
        "faultReceiptPredicate": "fault-receipt:" + case_id,
        "cleanCommand": "fault-clean:" + case_id,
        "cleanReceiptPredicate": "fault-clean-receipt:" + case_id,
        "observationWindowSeconds": 60 if tier == "real" else 30,
    }


_INGAME_FAULT_CASES = (
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
)

_REAL_FAULT_CASES = (
    "dlc.family-aquatic",
    "dlc.family-bionic",
    "dlc.family-frosty",
    "dlc.family-prehistoric",
    "dlc.family-spaced-out",
    "dlc.family-common",
)

FAULT_EXECUTION_SPECS: Mapping[str, FaultExecutionSpec] = MappingProxyType(
    {
        **{case_id: _fault_spec(case_id, "ingame") for case_id in _INGAME_FAULT_CASES},
        **{case_id: _fault_spec(case_id, "real") for case_id in _REAL_FAULT_CASES},
    }
)
