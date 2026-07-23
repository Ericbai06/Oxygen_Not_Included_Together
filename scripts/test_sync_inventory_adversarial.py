import hashlib
import json
from typing import cast
import unittest

from scripts import oni_target
from scripts.oni_execution_receipts import JsonValue
from scripts.test_sync_coverage_receipts import (
    active_coverage,
    bundle,
    error_codes,
    inventory,
    inventory_digest,
    receipt,
    registry,
)


def validate(
    inventory_value: dict[str, JsonValue],
    coverage_value: dict[str, JsonValue],
    receipt_value: dict[str, JsonValue],
):
    evidence_bundle = cast(dict[str, JsonValue], bundle([receipt_value]))
    evidence_bundle["inventoryDigest"] = receipt_value["inventoryDigest"]
    evidence_bundle["coverageDigest"] = receipt_value["coverageDigest"]
    values: dict[str, JsonValue] = {
        "inventory": inventory_value,
        "coverage": coverage_value,
        "testRegistry": cast(JsonValue, registry()),
        "evidenceBundle": evidence_bundle,
    }
    return oni_target.validate_execution_coverage(values)


def bind_receipt(
    inventory_value: dict[str, JsonValue],
    coverage_value: dict[str, JsonValue],
) -> dict[str, JsonValue]:
    value = cast(dict[str, JsonValue], receipt())
    value["inventoryDigest"] = inventory_value["digest"]
    value["coverageDigest"] = oni_target.canonical_coverage_digest(coverage_value)
    return value


def actual_inventory_document() -> dict[str, JsonValue]:
    entry: dict[str, JsonValue] = {
        "id": "sync:door-dispatch",
        "kind": "PacketDispatch",
        "fullyQualifiedSymbol": "DoorPacket.OnDispatched()",
        "resolvedTargetSignature": "DoorPacket.OnDispatched()",
        "bootstrap": "packet.OnDispatched()",
        "variants": list[JsonValue](["Debug/OS_MAC"]),
        "status": "Active",
    }
    content: dict[str, JsonValue] = {
        "entries": list[JsonValue]([entry]),
        "errors": list[JsonValue](),
    }
    canonical = json.dumps(content, separators=(",", ":"), ensure_ascii=True)
    digest = hashlib.sha256(canonical.encode("utf-8")).hexdigest()
    return {"schemaVersion": 1, "digest": digest, **content}


def first_entry(document: dict[str, JsonValue]) -> dict[str, JsonValue]:
    entries = document.get("entries")
    if not isinstance(entries, list) or not entries or not isinstance(entries[0], dict):
        raise AssertionError("fixture document lacks its first entry")
    return entries[0]


class InventoryIdentityAdversarialTests(unittest.TestCase):
    def test_invalid_inventory_status_fails_closed(self):
        inventory_value = cast(dict[str, JsonValue], inventory())
        first_entry(inventory_value)["status"] = "BogusStatus"
        inventory_value["digest"] = inventory_digest("BogusStatus")
        coverage_value = cast(dict[str, JsonValue], active_coverage())

        with self.assertRaisesRegex(ValueError, "invalid inventory status"):
            validate(
                inventory_value, coverage_value,
                bind_receipt(inventory_value, coverage_value),
            )

    def test_inventory_and_coverage_status_mismatch_is_reported(self):
        inventory_value = cast(dict[str, JsonValue], inventory("Active"))
        coverage_value = cast(dict[str, JsonValue], active_coverage())
        first_entry(coverage_value)["status"] = "RegisteredDisabled"

        errors = validate(
            inventory_value, coverage_value,
            bind_receipt(inventory_value, coverage_value),
        )

        self.assertIn("manifest_status_mismatch", error_codes(errors))

    def test_inventory_content_mutation_cannot_retain_old_digest(self):
        inventory_value = actual_inventory_document()
        coverage_value = cast(dict[str, JsonValue], active_coverage())
        coverage_value["inventoryDigest"] = inventory_value["digest"]
        value = bind_receipt(inventory_value, coverage_value)
        first_entry(inventory_value)["bootstrap"] = "tampered()"

        errors = validate(inventory_value, coverage_value, value)

        self.assertIn("inventory_digest_mismatch", error_codes(errors))


if __name__ == "__main__":
    unittest.main()
