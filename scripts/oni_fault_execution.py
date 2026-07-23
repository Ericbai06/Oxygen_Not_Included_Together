from __future__ import annotations

from .oni_fault_execution_specs import (
    FaultExecutionContractError,
    FaultExecutionResult,
    FaultExecutionSpec,
    FaultOracle,
    FaultReceipt,
    FaultRuntime,
    FaultSetup,
    FaultSnapshot,
)


def _require(condition: bool, stage: str, detail: str) -> None:
    if not condition:
        raise FaultExecutionContractError(stage, detail)


def _valid_hash(value: str) -> bool:
    return (
        len(value) == 71
        and value.startswith("sha256:")
        and all(character in "0123456789abcdef" for character in value[7:])
    )


def _validate_setup(setup: FaultSetup) -> None:
    _require(bool(setup["targetId"]), "setup", "target identity is required")
    _require(_valid_hash(setup["baselineHash"]), "setup", "baseline hash is invalid")


def _validate_spec(spec: FaultExecutionSpec) -> None:
    case_id = spec["caseId"]
    _require(
        spec["faultReceiptPredicate"] == "fault-receipt:" + case_id,
        "spec",
        "fault receipt predicate does not match case",
    )
    _require(
        spec["cleanReceiptPredicate"] == "fault-clean-receipt:" + case_id,
        "spec",
        "clean receipt predicate does not match case",
    )


def _validate_receipt(expected_id: str, target_id: str, receipt: FaultReceipt) -> None:
    _validate_receipt_identity(expected_id, target_id, receipt)
    _require(receipt["passed"], "barrier", "runtime receipt failed")


def _validate_receipt_identity(
    expected_id: str, target_id: str, receipt: FaultReceipt
) -> None:
    _require(receipt["receiptId"] == expected_id, "barrier", "wrong receipt id")
    _require(receipt["targetId"] == target_id, "barrier", "receipt target drift")
    _require(receipt["consumed"], "barrier", "fault input was not consumed")


def _validate_deferred_cleanup(receipt: FaultReceipt) -> None:
    requested = receipt.get("fixtureDisposeRequested")
    requested_frame = receipt.get("fixtureDisposeRequestedFrame")
    observed_frame = receipt.get("fixtureDisposeObservedFrame")
    _require(requested is True, "clean-barrier", "fixture disposal was not requested")
    _require(
        isinstance(requested_frame, int) and isinstance(observed_frame, int),
        "clean-barrier",
        "fixture disposal frames are required",
    )
    assert isinstance(requested_frame, int) and isinstance(observed_frame, int)
    _require(
        observed_frame > requested_frame,
        "clean-barrier",
        "fixture absence was not observed on a later frame",
    )
    _require(receipt.get("fixtureAbsent") is True, "clean-barrier", "fixture remains")


def _wait_for_clean_receipt(
    spec: FaultExecutionSpec, runtime: FaultRuntime, target_id: str
) -> FaultReceipt:
    deferred = spec["caseId"] == "building.destroy-deferred"
    attempts = max(1, spec["observationWindowSeconds"] if deferred else 1)
    for attempt in range(attempts):
        runtime.run_fault_command(spec["cleanCommand"], target_id)
        receipt = runtime.wait_for_fault_receipt(
            spec["cleanReceiptPredicate"], target_id, 1 if deferred else attempts
        )
        _validate_receipt_identity(spec["cleanReceiptPredicate"], target_id, receipt)
        if deferred and receipt.get("stage") == "cleanup-pending":
            if attempt + 1 == attempts:
                detail = receipt.get("detail", "fixture absence was not observed")
                raise FaultExecutionContractError("cleanup-pending", detail)
            continue
        _require(receipt["passed"], "barrier", "runtime receipt failed")
        if deferred:
            _validate_deferred_cleanup(receipt)
        return receipt
    raise FaultExecutionContractError("cleanup-pending", "cleanup retry budget exhausted")


def _validate_snapshot(
    spec: FaultExecutionSpec, setup: FaultSetup, snapshot: FaultSnapshot
) -> None:
    _require(snapshot["schemaVersion"] == 1, "invariant", "unknown schema")
    _require(snapshot["caseId"] == spec["caseId"], "invariant", "case drift")
    _require(snapshot["targetId"] == setup["targetId"], "invariant", "target drift")
    _require(_valid_hash(snapshot["stateHash"]), "invariant", "state hash is invalid")
    _require(snapshot["preserved"], "invariant", "state invariant changed")


def _validate_oracle(
    setup: FaultSetup, snapshot: FaultSnapshot, oracle: FaultOracle
) -> None:
    required = (
        oracle.get("passed"),
        oracle.get("observedTargetId"),
        oracle.get("beforeHash"),
        oracle.get("afterHash"),
        oracle.get("invariantPreserved"),
    )
    _require(None not in required, "oracle", "typed oracle fields are required")
    _require(oracle.get("passed") is True, "oracle", "oracle rejected runtime state")
    _require(oracle.get("observedTargetId") == setup["targetId"], "oracle", "target drift")
    _require(oracle.get("beforeHash") == setup["baselineHash"], "oracle", "baseline drift")
    _require(oracle.get("afterHash") == snapshot["stateHash"], "oracle", "snapshot drift")
    _require(oracle.get("invariantPreserved") is True, "oracle", "invariant was not proven")


def _validate_domain(spec: FaultExecutionSpec, snapshot: FaultSnapshot) -> None:
    if spec["caseId"].startswith("dlc.family-"):
        runtime = snapshot["dlcRuntime"]
        expected_family = spec["caseId"].removeprefix("dlc.family-")
        _require(runtime is not None, "oracle", "DLC runtime evidence is required")
        assert runtime is not None
        _require(runtime["scenario"] == "dlc-runtime", "oracle", "wrong DLC scenario")
        _require(runtime["dlcFamily"] == expected_family, "oracle", "DLC family drift")
        _require(bool(runtime["prefab"]), "oracle", "DLC prefab is required")
        _require(bool(runtime["identity"]), "oracle", "DLC identity is required")
        _require(bool(runtime["stateMachineState"]), "oracle", "DLC state is required")
        _require(runtime["admissionGeneration"] > 0, "oracle", "DLC admission missing")
    if spec["caseId"] == "duplicant.destroyed-add-component":
        destroyed = snapshot["destroyedMinion"]
        _require(destroyed is not None, "oracle", "destroyed minion evidence is required")
        assert destroyed is not None
        _require(
            destroyed["componentCountAfter"] <= destroyed["componentCountBefore"],
            "oracle",
            "component attached to destroyed minion",
        )
        _require(
            destroyed["identityPresentAfter"] == destroyed["identityPresentBefore"],
            "oracle",
            "identity attached to destroyed minion",
        )
        _require(destroyed["exceptionCount"] == 0, "oracle", "destroyed minion threw")


def _validate_clean_restoration(setup: FaultSetup, snapshot: FaultSnapshot) -> None:
    _require(
        snapshot["stateHash"] == setup["baselineHash"],
        "clean-oracle",
        "clean control did not restore baseline",
    )


def run_fault_execution(
    spec: FaultExecutionSpec, runtime: FaultRuntime
) -> FaultExecutionResult:
    target_id = ""
    try:
        _validate_spec(spec)
        setup = runtime.setup_fault_case(spec)
        _validate_setup(setup)
        target_id = setup["targetId"]
        runtime.run_fault_command(spec["faultCommand"], target_id)
        fault_receipt = runtime.wait_for_fault_receipt(
            spec["faultReceiptPredicate"], target_id, spec["observationWindowSeconds"]
        )
        _validate_receipt(spec["faultReceiptPredicate"], target_id, fault_receipt)
        fault_snapshot = runtime.capture_fault_invariant(spec, target_id, "fault")
        _validate_snapshot(spec, setup, fault_snapshot)
        _validate_domain(spec, fault_snapshot)
        fault_oracle = runtime.evaluate_fault_oracle(
            spec, fault_receipt, setup, fault_snapshot, "fault"
        )
        _validate_oracle(setup, fault_snapshot, fault_oracle)
        runtime.reset_fault_case(spec, target_id)
        clean_receipt = _wait_for_clean_receipt(spec, runtime, target_id)
        clean_snapshot = runtime.capture_fault_invariant(spec, target_id, "clean")
        _validate_snapshot(spec, setup, clean_snapshot)
        _validate_domain(spec, clean_snapshot)
        _validate_clean_restoration(setup, clean_snapshot)
        clean_oracle = runtime.evaluate_fault_oracle(
            spec, clean_receipt, setup, clean_snapshot, "clean"
        )
        _validate_oracle(setup, clean_snapshot, clean_oracle)
    except (KeyError, RuntimeError) as failure:
        try:
            runtime.cleanup_fault_case(spec, target_id)
        except RuntimeError as cleanup_failure:
            raise failure from cleanup_failure
        raise
    runtime.cleanup_fault_case(spec, target_id)
    return {"passed": True}
