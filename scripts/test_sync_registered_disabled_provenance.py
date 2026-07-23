import hashlib
import json
from typing import cast
import unittest

from scripts import oni_target
from scripts.oni_execution_receipts import JsonValue
from scripts.test_sync_coverage_receipts import (
    HEADLESS_TEST_ID,
    NEGATIVE_TEST_ID,
    bundle,
    error_codes,
    receipt,
    registry,
)


REGISTRATION_ID = "sync:disabled-owner-registration"
DISABLED_SEND_ID = "sync:disabled-owner-send"


def inventory_document(owner: str) -> dict[str, JsonValue]:
    entries = list[JsonValue]([
        inventory_entry(
            REGISTRATION_ID, "PacketRegistration", owner, "Active"),
        inventory_entry(
            DISABLED_SEND_ID, "PacketSend", "Fixture.DisabledOwner.Send()",
            "RegisteredDisabled"),
    ])
    content: dict[str, JsonValue] = {
        "entries": entries,
        "errors": list[JsonValue](),
    }
    canonical = json.dumps(content, separators=(",", ":"), ensure_ascii=True)
    digest = hashlib.sha256(canonical.encode("utf-8")).hexdigest()
    return {"schemaVersion": 1, "digest": digest, **content}


def inventory_entry(
    entry_id: str,
    kind: str,
    symbol: str,
    status: str,
) -> dict[str, JsonValue]:
    return {
        "id": entry_id,
        "kind": kind,
        "fullyQualifiedSymbol": symbol,
        "resolvedTargetSignature": symbol,
        "bootstrap": symbol,
        "variants": list[JsonValue](["Debug/OS_MAC"]),
        "status": status,
    }


def coverage_document(inventory_digest: str) -> dict[str, JsonValue]:
    return {
        "schemaVersion": 1,
        "inventoryDigest": inventory_digest,
        "entries": list[JsonValue]([
            {
                "id": REGISTRATION_ID,
                "domain": "packet-registration",
                "testIds": list[JsonValue]([HEADLESS_TEST_ID]),
                "negativeTestIds": list[JsonValue]([NEGATIVE_TEST_ID]),
                "scenarioIds": list[JsonValue](),
                "variants": list[JsonValue](["Debug/OS_MAC"]),
                "status": "Active",
            },
            {
                "id": DISABLED_SEND_ID,
                "domain": "packet-send",
                "testIds": list[JsonValue](),
                "negativeTestIds": list[JsonValue]([NEGATIVE_TEST_ID]),
                "scenarioIds": list[JsonValue](),
                "variants": list[JsonValue](["Debug/OS_MAC"]),
                "status": "RegisteredDisabled",
            },
        ]),
    }


def receipts(
    inventory_digest: str,
    coverage_digest: str,
) -> list[dict[str, JsonValue]]:
    positive = cast(dict[str, JsonValue], receipt())
    positive["inventoryDigest"] = inventory_digest
    positive["coverageDigest"] = coverage_digest
    positive["executedEntryIds"] = list[JsonValue]([REGISTRATION_ID])
    negative = cast(dict[str, JsonValue], receipt(NEGATIVE_TEST_ID))
    negative["inventoryDigest"] = inventory_digest
    negative["coverageDigest"] = coverage_digest
    negative["polarity"] = "negative"
    negative["executedEntryIds"] = list[JsonValue]([REGISTRATION_ID])
    negative["absentEntryIds"] = list[JsonValue]([DISABLED_SEND_ID])
    negative["registrationWitnesses"] = list[JsonValue]([{
        "entryId": DISABLED_SEND_ID,
        "registrationEntryId": REGISTRATION_ID,
    }])
    return [positive, negative]


def validate(owner: str):
    inventory_value = inventory_document(owner)
    inventory_digest = cast(str, inventory_value["digest"])
    coverage_value = coverage_document(inventory_digest)
    coverage_digest = oni_target.canonical_coverage_digest(coverage_value)
    receipt_values = receipts(inventory_digest, coverage_digest)
    evidence_bundle = cast(dict[str, JsonValue], bundle(receipt_values))
    evidence_bundle["inventoryDigest"] = inventory_digest
    evidence_bundle["coverageDigest"] = coverage_digest
    values: dict[str, JsonValue] = {
        "inventory": inventory_value,
        "coverage": coverage_value,
        "testRegistry": cast(JsonValue, registry()),
        "evidenceBundle": evidence_bundle,
    }
    return oni_target.validate_execution_coverage(values)


class RegisteredDisabledProvenanceTests(unittest.TestCase):
    def test_same_owner_registration_and_explicit_absence_prove_disabled_send(self):
        self.assertEqual([], validate("Fixture.DisabledOwner"))

    def test_wrong_owner_registration_cannot_prove_disabled_send_absence(self):
        errors = validate("Fixture.OtherOwner")

        self.assertIn(
            "registered_disabled_missing_same_owner_registration",
            error_codes(errors),
        )


if __name__ == "__main__":
    unittest.main()
