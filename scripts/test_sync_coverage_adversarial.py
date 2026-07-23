import hashlib
import json
from pathlib import Path
import tempfile
from typing import Any, cast
import unittest

from scripts import oni_target
from scripts.test_sync_coverage_receipts import (
    ENTRY_ID,
    INGAME_TEST_ID,
    NEGATIVE_TEST_ID,
    active_coverage,
    bundle,
    error_codes,
    inventory,
    receipt,
    registry,
    unity_coverage,
)


CANONICAL_DIGEST = (
    "sha256:5032d24bd3f34b3f984737f4fbb99b8eb317923ae89d7b3f01e394e6413b22d7"
)


def production_api(name):
    return getattr(oni_target, name)


def validate(coverage, receipts, *, evidence_bundle=None, evidence_root=None):
    values = {
        "inventory": inventory(),
        "coverage": coverage,
        "testRegistry": registry(),
        "evidenceBundle": evidence_bundle or bundle(receipts),
    }
    if evidence_root is None:
        return production_api("validate_execution_coverage")(values)
    return production_api("validate_execution_coverage")(
        values, evidence_root=evidence_root)


def canonical_fixture():
    return {
        "schemaVersion": 1,
        "inventoryDigest": "a" * 64,
        "entries": [{
            "id": "sync:canonical-entry",
            "domain": "door",
            "testIds": ["headless:canonical"],
            "negativeTestIds": [],
            "scenarioIds": [],
            "variants": ["Debug/OS_MAC"],
            "status": "Active",
        }],
    }


def file_digest(path):
    return "sha256:" + hashlib.sha256(Path(path).read_bytes()).hexdigest()


class CoverageDigestParityTests(unittest.TestCase):
    def test_missing_root_digest_uses_stable_canonical_digest(self):
        first = canonical_fixture()
        second = {key: first[key] for key in reversed(tuple(first))}

        first_digest = production_api("canonical_coverage_digest")(first)
        second_digest = production_api("canonical_coverage_digest")(second)

        self.assertEqual(CANONICAL_DIGEST, first_digest)
        self.assertEqual(CANONICAL_DIGEST, second_digest)

    def test_real_coverage_without_synthetic_digest_is_accepted(self):
        coverage = json.loads(Path("sync-entry-coverage.json").read_text())

        digest = production_api("canonical_coverage_digest")(coverage)

        self.assertRegex(digest, r"\Asha256:[0-9a-f]{64}\Z")

    def test_declared_synthetic_digest_cannot_override_content(self):
        for field in ("digest", "coverageDigest"):
            coverage = active_coverage()
            coverage[field] = "sha256:" + "9" * 64

            with self.subTest(field=field), self.assertRaisesRegex(
                    ValueError, "synthetic digest"):
                validate(coverage, [receipt()])


class MappingAndEnvelopeAdversarialTests(unittest.TestCase):
    def test_duplicate_coverage_entry_id_fails_closed(self):
        coverage = active_coverage()
        coverage["entries"].append(dict(coverage["entries"][0]))
        coverage_digest = production_api("canonical_coverage_digest")(coverage)
        value = receipt()
        value["coverageDigest"] = coverage_digest

        errors = validate(coverage, [value])

        self.assertIn("manifest_duplicate_entry", error_codes(errors))

    def test_registry_must_contain_all_and_only_four_tiers(self):
        values = {
            "inventory": inventory(),
            "coverage": active_coverage(),
            "testRegistry": [registry()[0]],
            "evidenceBundle": bundle([receipt()]),
        }

        with self.assertRaisesRegex(ValueError, "four tiers"):
            production_api("validate_execution_coverage")(values)

    def test_missing_coverage_row_and_orphan_known_receipt_fail(self):
        missing = active_coverage()
        missing["entries"] = []

        absent_errors = validate(missing, [])
        orphan_errors = validate(missing, [receipt()])

        self.assertIn("manifest_missing_entry", error_codes(absent_errors))
        self.assertIn("execution_unmapped_entry_receipt", error_codes(orphan_errors))

    def test_active_positive_proof_cannot_use_negative_mapping(self):
        coverage = cast(dict[str, Any], active_coverage())
        coverage["entries"][0]["testIds"] = []
        coverage["entries"][0]["negativeTestIds"] = [NEGATIVE_TEST_ID]

        errors = validate(coverage, [receipt(NEGATIVE_TEST_ID)])

        self.assertIn("execution_missing_entry_receipt", error_codes(errors))

    def test_receipt_is_bound_to_run_and_bundle_envelope(self):
        cross_run = receipt()
        cross_run["runId"] = "run-stale"
        drift = bundle([receipt()])
        drift["inventoryDigest"] = "9" * 64
        drift["coverageDigest"] = "sha256:" + "8" * 64

        run_errors = validate(active_coverage(), [cross_run])
        envelope_errors = validate(
            active_coverage(), [receipt()], evidence_bundle=drift)

        self.assertIn("execution_run_id_mismatch", error_codes(run_errors))
        self.assertIn(
            "execution_envelope_inventory_digest_mismatch",
            error_codes(envelope_errors),
        )
        self.assertIn(
            "execution_envelope_coverage_digest_mismatch",
            error_codes(envelope_errors),
        )


class RuntimeArtifactAdversarialTests(unittest.TestCase):
    def test_runtime_artifact_must_exist_under_root_and_match_hash(self):
        value = receipt(INGAME_TEST_ID, "ingame", "door")
        value["artifact"] = {
            "kind": "ingame-result",
            "path": "missing/artifact.json",
            "sha256": "sha256:" + "7" * 64,
        }
        with tempfile.TemporaryDirectory() as root:
            missing_errors = validate(
                unity_coverage(), [value], evidence_root=Path(root))

            inside = Path(root) / "artifact.json"
            inside.write_text("{}")
            value["artifact"]["path"] = "artifact.json"
            hash_errors = validate(
                unity_coverage(), [value], evidence_root=Path(root))

            outside = Path(root).parent / "outside-artifact.json"
            outside.write_text("{}")
            try:
                value["artifact"]["path"] = str(outside)
                value["artifact"]["sha256"] = file_digest(outside)
                outside_errors = validate(
                    unity_coverage(), [value], evidence_root=Path(root))
            finally:
                outside.unlink()

        self.assertIn("runtime_artifact_missing", error_codes(missing_errors))
        self.assertIn("runtime_artifact_hash_mismatch", error_codes(hash_errors))
        self.assertIn("runtime_artifact_outside_root", error_codes(outside_errors))

    def test_runtime_artifact_control_path_cannot_be_manual_observe(self):
        with tempfile.TemporaryDirectory() as root:
            artifact = Path(root) / "artifact.json"
            artifact.write_text(json.dumps({
                "schemaVersion": 1,
                "runId": "run-receipt-001",
                "testId": INGAME_TEST_ID,
                "scenarioId": "door",
                "tier": "ingame",
                "executedEntryIds": [ENTRY_ID],
                "controlPath": {"driver": "manual-observe"},
            }))
            value = receipt(INGAME_TEST_ID, "ingame", "door")
            value["artifact"] = {
                "kind": "ingame-result",
                "path": "artifact.json",
                "sha256": file_digest(artifact),
            }

            errors = validate(
                unity_coverage(), [value], evidence_root=Path(root))

        self.assertIn("runtime_control_path_mismatch", error_codes(errors))


if __name__ == "__main__":
    unittest.main()
