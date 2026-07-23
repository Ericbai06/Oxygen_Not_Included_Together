import json
from pathlib import Path
from typing import cast

from .oni_scenario_contracts import parse_scenario_evidence
from .oni_coverage_gate import canonical_coverage_digest, validate_execution_coverage
from .oni_execution_receipts import JsonValue
from .oni_target_contracts import build_evidence_bundle, write_evidence_bundle_atomic


def persist_run_evidence(context):
    if "executionReceipts" in context or "receiptProducer" not in context:
        raise ValueError("receipt producer is required")
    state = context["state"]
    logs = context["logs"]
    evidence = parse_scenario_evidence(state["scenario"], logs)
    records = [] if evidence is None else [record for _, record in evidence["records"]]
    windows = {
        role: {
            "startOffset": state["offsets"][role],
            "endOffset": state["offsets"][role] + len(text.encode("utf-8")),
            "text": text,
        }
        for role, text in logs.items()
    }
    verdict = context["verdict"]
    inventory = json.loads(Path(context["inventory"]).read_text(encoding="utf-8"))
    coverage = json.loads(Path(context["coverage"]).read_text(encoding="utf-8"))
    inventory_digest = inventory["digest"]
    coverage_digest = canonical_coverage_digest(coverage)
    receipts = [_bind_receipt(item, state["runId"], inventory_digest,
                              coverage_digest)
                for item in context["receiptProducer"].complete()]
    _validate_observed_receipts(receipts, records)
    failure_flow = list(verdict.get("failures", [])) + list(verdict.get("forbidden", []))
    bundle = build_evidence_bundle(
        run_id=state["runId"], inventory_digest=inventory_digest,
        coverage_digest=coverage_digest, dll_hash=context["dllHash"],
        endpoint_logs=windows, typed_records=records,
        results=[{"scenario": state["scenario"], "passed": verdict["passed"]}],
        failure_flow=failure_flow, execution_receipts=receipts,
    )
    gate_input: dict[str, JsonValue] = {
        "inventory": cast(JsonValue, inventory),
        "coverage": cast(JsonValue, coverage),
        "testRegistry": cast(JsonValue, _receipt_registry(
            str(state["scenario"]), receipts)),
        "evidenceBundle": cast(JsonValue, bundle),
    }
    gate_errors = validate_execution_coverage(
        gate_input, evidence_root=Path(context["outputRoot"]))
    if gate_errors:
        codes = ", ".join(sorted({error.code for error in gate_errors}))
        raise ValueError(f"execution coverage gate failed: {codes}")
    return write_evidence_bundle_atomic(context["outputRoot"], bundle)


def _bind_receipt(receipt, run_id, inventory_digest, coverage_digest):
    if not isinstance(receipt, dict):
        raise ValueError("receipt producer returned a non-object receipt")
    bound = dict(receipt)
    expected = {
        "runId": run_id,
        "inventoryDigest": inventory_digest,
        "coverageDigest": coverage_digest,
    }
    if any(bound.get(field) != value for field, value in expected.items()):
        raise ValueError("receipt producer identity differs from evidence envelope")
    return bound


def _validate_observed_receipts(receipts, records):
    observed = {record["entryId"] for record in records}
    if not observed:
        raise ValueError("receipt producer observed no typed entry IDs")
    claimed = {entry_id for receipt in receipts
               for entry_id in receipt.get("executedEntryIds", [])}
    if claimed != observed:
        raise ValueError("receipt producer IDs differ from observed typed records")


def _receipt_registry(scenario: str, receipts) -> list[dict[str, JsonValue]]:
    definitions: dict[str, dict[str, JsonValue]] = {
        "headless:execution-coverage-gate": {
            "id": "headless:execution-coverage-gate",
            "tier": "headless", "scenarioId": None,
        },
        f"ingame:{scenario}": {
            "id": f"ingame:{scenario}",
            "tier": "ingame", "scenarioId": scenario,
        },
        "python:typed-evidence-parser": {
            "id": "python:typed-evidence-parser",
            "tier": "python", "scenarioId": None,
        },
        f"real:{scenario}": {
            "id": f"real:{scenario}",
            "tier": "real", "scenarioId": scenario,
        },
    }
    for receipt in receipts:
        test_id = receipt.get("testId")
        if not isinstance(test_id, str):
            raise ValueError("receipt producer returned an invalid test ID")
        definition = {
            "id": test_id,
            "tier": receipt.get("tier"),
            "scenarioId": receipt.get("scenarioId"),
        }
        if test_id in definitions and definitions[test_id] != definition:
            raise ValueError("receipt producer returned conflicting test definitions")
        definitions[test_id] = definition
    return list(definitions.values())
