import hashlib
import json
from typing import Any, cast
import unittest

from scripts import oni_target


ENTRY_ID = "sync:door-dispatch"
OTHER_ENTRY_ID = "sync:other-entry"
HEADLESS_TEST_ID = "headless:packet-roundtrip"
INGAME_TEST_ID = "ingame:door-harmony"
PYTHON_TEST_ID = "python:evidence-parser"
REAL_TEST_ID = "real:door-dispatch"
NEGATIVE_TEST_ID = "headless:disabled-hook-negative"


def production_api(name):
    return getattr(oni_target, name)


def registry():
    return [
        {"id": HEADLESS_TEST_ID, "tier": "headless", "scenarioId": None},
        {"id": INGAME_TEST_ID, "tier": "ingame", "scenarioId": "door"},
        {"id": PYTHON_TEST_ID, "tier": "python", "scenarioId": None},
        {"id": REAL_TEST_ID, "tier": "real", "scenarioId": "door"},
        {"id": NEGATIVE_TEST_ID, "tier": "headless", "scenarioId": None},
    ]


def inventory_entry(status) -> dict[str, Any]:
    return {
        "id": ENTRY_ID,
        "kind": "PacketDispatch",
        "fullyQualifiedSymbol": "DoorPacket.OnDispatched()",
        "resolvedTargetSignature": "DoorPacket.OnDispatched()",
        "bootstrap": "packet.OnDispatched()",
        "variants": ["Debug/OS_MAC"],
        "status": status,
    }


def inventory_digest(status):
    content = {"entries": [inventory_entry(status)], "errors": []}
    canonical = json.dumps(content, separators=(",", ":"), ensure_ascii=True)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def inventory(status="Active") -> dict[str, Any]:
    entries = [inventory_entry(status)]
    return {
        "schemaVersion": 1,
        "digest": inventory_digest(status),
        "entries": entries,
        "errors": [],
    }


def active_coverage() -> dict[str, Any]:
    return {
        "schemaVersion": 1,
        "inventoryDigest": INVENTORY_DIGEST,
        "entries": [{
            "id": ENTRY_ID,
            "domain": "door",
            "testIds": [HEADLESS_TEST_ID],
            "negativeTestIds": [],
            "scenarioIds": [],
            "variants": ["Debug/OS_MAC"],
            "status": "Active",
        }],
    }


def coverage_entry(value: dict[str, Any]) -> dict[str, Any]:
    return cast(dict[str, Any], value["entries"][0])


def unity_coverage() -> dict[str, Any]:
    value = active_coverage()
    row = coverage_entry(value)
    row["testIds"] = [INGAME_TEST_ID]
    row["scenarioIds"] = ["door"]
    row["headlessUnsupportedReason"] = "requires ONI GameObject"
    return value


def disabled_coverage() -> dict[str, Any]:
    value = active_coverage()
    row = coverage_entry(value)
    value["inventoryDigest"] = DISABLED_INVENTORY_DIGEST
    row["testIds"] = []
    row["negativeTestIds"] = [NEGATIVE_TEST_ID]
    row["status"] = "RegisteredDisabled"
    return value


INVENTORY_DIGEST = inventory_digest("Active")
DISABLED_INVENTORY_DIGEST = inventory_digest("RegisteredDisabled")
COVERAGE_DIGEST = production_api("canonical_coverage_digest")(active_coverage())
UNITY_COVERAGE_DIGEST = production_api("canonical_coverage_digest")(unity_coverage())
DISABLED_COVERAGE_DIGEST = production_api(
    "canonical_coverage_digest")(disabled_coverage())


def receipt(
    test_id=HEADLESS_TEST_ID,
    tier="headless",
    scenario_id=None,
) -> dict[str, Any]:
    artifact_kind = {
        "headless": "headless-log",
        "ingame": "ingame-result",
        "python": "python-log",
        "real": "real-run",
    }[tier]
    disabled = test_id == NEGATIVE_TEST_ID
    coverage_digest = (UNITY_COVERAGE_DIGEST if test_id == INGAME_TEST_ID
                       else DISABLED_COVERAGE_DIGEST if disabled
                       else COVERAGE_DIGEST)
    return {
        "schemaVersion": 1,
        "runId": "run-receipt-001",
        "inventoryDigest": (DISABLED_INVENTORY_DIGEST
                            if disabled else INVENTORY_DIGEST),
        "coverageDigest": coverage_digest,
        "dllHash": "d" * 64,
        "pdbHash": "e" * 64,
        "testId": test_id,
        "tier": tier,
        "scenarioId": scenario_id,
        "polarity": "positive",
        "executedEntryIds": [ENTRY_ID],
        "absentEntryIds": [],
        "registrationWitnesses": [],
        "artifact": {
            "kind": artifact_kind,
            "path": "artifacts/execution.log",
            "sha256": "sha256:" + "3" * 64,
        },
    }


def bundle(receipts) -> dict[str, Any]:
    coverage_digest = (receipts[0]["coverageDigest"]
                       if receipts else COVERAGE_DIGEST)
    return {
        "schemaVersion": 1,
        "runId": "run-receipt-001",
        "inventoryDigest": (receipts[0]["inventoryDigest"]
                            if receipts else INVENTORY_DIGEST),
        "coverageDigest": coverage_digest,
        "dllHash": "sha256:" + "4" * 64,
        "endpointLogs": {"host": {}, "client": {}},
        "typedRecords": [],
        "results": [],
        "failureFlow": [],
        "executionReceipts": receipts,
    }


def error_codes(errors):
    return {error.code for error in errors}


class ReceiptParserTests(unittest.TestCase):
    def test_evidence_bundle_persists_execution_receipts(self):
        values = {
            "run_id": "run-receipt-001",
            "inventory_digest": INVENTORY_DIGEST,
            "coverage_digest": COVERAGE_DIGEST,
            "dll_hash": "sha256:" + "4" * 64,
            "endpoint_logs": {"host": {}, "client": {}},
            "typed_records": [],
            "results": [],
            "failure_flow": [],
            "execution_receipts": [receipt()],
        }

        result = production_api("build_evidence_bundle")(**values)

        self.assertEqual(
            [ENTRY_ID], result["executionReceipts"][0]["executedEntryIds"])

    def test_parses_actual_executed_entry_ids_from_exact_schema(self):
        parsed = production_api("parse_execution_receipts")(bundle([receipt()]))

        self.assertEqual((ENTRY_ID,), parsed[0].executed_entry_ids)
        self.assertEqual(HEADLESS_TEST_ID, parsed[0].test_id)

    def test_rejects_source_class_count_and_missing_execution_proof(self):
        fake_fields = ("classNames", "sourceMatches", "matchedCount")
        for field in fake_fields:
            fake = receipt()
            fake[field] = ["PacketSender"] if field != "matchedCount" else 1
            with self.subTest(field=field), self.assertRaises(ValueError):
                production_api("parse_execution_receipts")(bundle([fake]))

        missing = receipt()
        del missing["executedEntryIds"]
        with self.assertRaises(ValueError):
            production_api("parse_execution_receipts")(bundle([missing]))


class CoverageReceiptGateTests(unittest.TestCase):
    def validate(self, inventory_value, coverage_value, receipts):
        return production_api("validate_execution_coverage")({
            "inventory": inventory_value,
            "coverage": coverage_value,
            "testRegistry": registry(),
            "evidenceBundle": bundle(receipts),
        })

    def test_accepts_receipt_that_executes_the_mapped_entry(self):
        errors = self.validate(inventory(), active_coverage(), [receipt()])

        self.assertEqual([], errors)

    def test_rejects_manifest_declaration_without_execution_receipt(self):
        errors = self.validate(inventory(), active_coverage(), [])

        self.assertIn("execution_missing_entry_receipt", error_codes(errors))

    def test_rejects_unknown_receipt_and_unknown_entry(self):
        unknown_test = receipt("headless:not-registered")
        unknown_entry = receipt()
        unknown_entry["executedEntryIds"] = [OTHER_ENTRY_ID]

        errors = self.validate(
            inventory(), active_coverage(), [unknown_test, unknown_entry])

        self.assertIn("execution_unknown_test_receipt", error_codes(errors))
        self.assertIn("execution_unknown_entry_receipt", error_codes(errors))

    def test_rejects_duplicate_receipt(self):
        value = receipt()

        errors = self.validate(inventory(), active_coverage(), [value, value])

        self.assertIn("execution_duplicate_receipt", error_codes(errors))

    def test_rejects_cross_inventory_and_coverage_digest(self):
        wrong_inventory = receipt()
        wrong_inventory["inventoryDigest"] = "9" * 64
        wrong_coverage = receipt()
        wrong_coverage["coverageDigest"] = "sha256:" + "8" * 64

        inventory_errors = self.validate(
            inventory(), active_coverage(), [wrong_inventory])
        coverage_errors = self.validate(
            inventory(), active_coverage(), [wrong_coverage])

        self.assertIn(
            "execution_inventory_digest_mismatch", error_codes(inventory_errors))
        self.assertIn(
            "execution_coverage_digest_mismatch", error_codes(coverage_errors))

    def test_rejects_scenario_and_tier_mismatch(self):
        wrong_scenario = receipt(INGAME_TEST_ID, "ingame", "toggle")
        wrong_tier = receipt(INGAME_TEST_ID, "real", "door")

        scenario_errors = self.validate(
            inventory(), unity_coverage(), [wrong_scenario])
        tier_errors = self.validate(inventory(), unity_coverage(), [wrong_tier])

        self.assertIn("execution_scenario_mismatch", error_codes(scenario_errors))
        self.assertIn("execution_tier_mismatch", error_codes(tier_errors))

    def test_rejects_unity_only_receipt_without_runtime_artifact(self):
        missing = receipt(INGAME_TEST_ID, "ingame", "door")
        missing["artifact"] = None
        wrong_kind = receipt(INGAME_TEST_ID, "ingame", "door")
        wrong_kind["artifact"]["kind"] = "headless-log"

        missing_errors = self.validate(inventory(), unity_coverage(), [missing])
        kind_errors = self.validate(inventory(), unity_coverage(), [wrong_kind])

        self.assertIn(
            "unity_only_missing_runtime_artifact", error_codes(missing_errors))
        self.assertIn(
            "unity_only_missing_runtime_artifact", error_codes(kind_errors))

    def test_registered_disabled_requires_negative_execution_receipt(self):
        positive = receipt(NEGATIVE_TEST_ID)
        positive_errors = self.validate(
            inventory("RegisteredDisabled"), disabled_coverage(), [positive])

        self.assertIn(
            "registered_disabled_missing_negative_receipt",
            error_codes(positive_errors),
        )


if __name__ == "__main__":
    unittest.main()
