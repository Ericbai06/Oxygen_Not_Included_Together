import json
from dataclasses import dataclass
from pathlib import Path
import tempfile
from typing import cast
import unittest

from scripts.oni_execution_receipts import JsonValue
from scripts.test_sync_coverage_adversarial import file_digest, validate
from scripts.test_sync_coverage_receipts import (
    ENTRY_ID,
    INGAME_TEST_ID,
    active_coverage,
    error_codes,
    receipt,
    unity_coverage,
)


@dataclass(frozen=True, slots=True)
class RuntimeArtifactFixture:
    receipt: dict[str, JsonValue]
    log_path: Path
    result_path: Path


def create_artifact(root: Path, *, include_control: bool) -> RuntimeArtifactFixture:
    log_path = root / "runtime.json"
    result_path = root / "result.json"
    manifest_path = root / "artifact.json"
    log_path.write_text('{"event":"door-open"}\n', encoding="utf-8")
    result_path.write_text('{"passed":true}\n', encoding="utf-8")
    manifest: dict[str, JsonValue] = {
        "schemaVersion": 1,
        "runId": "run-receipt-001",
        "testId": INGAME_TEST_ID,
        "scenarioId": "door",
        "tier": "ingame",
        "executedEntryIds": [ENTRY_ID],
        "log": {"path": log_path.name, "sha256": file_digest(log_path)},
        "result": {"path": result_path.name, "sha256": file_digest(result_path)},
    }
    if include_control:
        manifest["controlPath"] = {"driver": "door-driver"}
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    value = cast(dict[str, JsonValue], receipt(INGAME_TEST_ID, "ingame", "door"))
    value["artifact"] = {
        "kind": "ingame-result",
        "path": manifest_path.name,
        "sha256": file_digest(manifest_path),
    }
    return RuntimeArtifactFixture(value, log_path, result_path)


class RuntimeArtifactStrictGateTests(unittest.TestCase):
    def test_runtime_artifact_requires_evidence_root(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            fixture = create_artifact(Path(temporary), include_control=True)

            errors = validate(unity_coverage(), [fixture.receipt])

        self.assertIn("runtime_artifact_root_missing", error_codes(errors))

    def test_runtime_artifact_requires_control_path(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            fixture = create_artifact(root, include_control=False)

            errors = validate(
                unity_coverage(), [fixture.receipt], evidence_root=root)

        self.assertIn("runtime_control_path_mismatch", error_codes(errors))

    def test_nested_log_and_result_hash_mutations_fail(self) -> None:
        for payload, code in (
            ("log", "runtime_log_hash_mismatch"),
            ("result", "runtime_result_hash_mismatch"),
        ):
            with self.subTest(payload=payload), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                fixture = create_artifact(root, include_control=True)
                target = fixture.log_path if payload == "log" else fixture.result_path
                target.write_text("tampered\n", encoding="utf-8")

                errors = validate(
                    unity_coverage(), [fixture.receipt], evidence_root=root)

                self.assertIn(code, error_codes(errors))

    def test_runtime_manifest_identity_is_bound_to_receipt(self) -> None:
        mutations: dict[str, JsonValue] = {
            "runId": "run-other",
            "testId": "ingame:other",
            "scenarioId": "toggle",
            "tier": "real",
            "executedEntryIds": list[JsonValue](["sync:other-entry"]),
        }
        for field, value in mutations.items():
            with self.subTest(field=field), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                fixture = create_artifact(root, include_control=True)
                artifact_path = receipt_artifact_path(root, fixture.receipt)
                manifest = cast(dict[str, JsonValue], json.loads(
                    artifact_path.read_text(encoding="utf-8")))
                manifest[field] = value
                artifact_path.write_text(json.dumps(manifest), encoding="utf-8")
                set_receipt_artifact_digest(fixture.receipt, artifact_path)

                errors = validate(
                    unity_coverage(), [fixture.receipt], evidence_root=root)

                self.assertIn(
                    "runtime_artifact_identity_mismatch", error_codes(errors))

    def test_runtime_result_must_report_passed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            fixture = create_artifact(root, include_control=True)
            fixture.result_path.write_text('{"passed":false}\n', encoding="utf-8")
            artifact_path = receipt_artifact_path(root, fixture.receipt)
            manifest = cast(dict[str, JsonValue], json.loads(
                artifact_path.read_text(encoding="utf-8")))
            result = manifest["result"]
            if not isinstance(result, dict):
                raise AssertionError("fixture manifest lacks result payload")
            result["sha256"] = file_digest(fixture.result_path)
            artifact_path.write_text(json.dumps(manifest), encoding="utf-8")
            set_receipt_artifact_digest(fixture.receipt, artifact_path)

            errors = validate(
                unity_coverage(), [fixture.receipt], evidence_root=root)

        self.assertIn("runtime_result_failed", error_codes(errors))


class CoverageInventoryDigestTests(unittest.TestCase):
    def test_coverage_inventory_digest_mismatch_fails(self) -> None:
        coverage = active_coverage()
        coverage["inventoryDigest"] = "9" * 64

        errors = validate(coverage, [receipt()])

        self.assertIn("coverage_inventory_digest_mismatch", error_codes(errors))


def receipt_artifact_path(root: Path, value: dict[str, JsonValue]) -> Path:
    artifact = value["artifact"]
    if not isinstance(artifact, dict):
        raise AssertionError("fixture receipt lacks artifact path")
    raw_path = artifact.get("path")
    if not isinstance(raw_path, str):
        raise AssertionError("fixture receipt lacks artifact path")
    return root / raw_path


def set_receipt_artifact_digest(
    value: dict[str, JsonValue],
    path: Path,
) -> None:
    artifact = value["artifact"]
    if not isinstance(artifact, dict):
        raise AssertionError("fixture receipt lacks artifact")
    artifact["sha256"] = file_digest(path)


if __name__ == "__main__":
    unittest.main()
