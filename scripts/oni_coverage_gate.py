from collections.abc import Iterable, Mapping, Sequence
from dataclasses import dataclass
import hashlib
import json
from pathlib import Path
import re
from typing import cast

from .oni_execution_receipts import (
    ExecutionReceipt,
    JsonValue,
    TIERS,
    choice,
    exact_mapping,
    mapping,
    optional_string,
    parse_execution_receipts,
    schema_version,
    sequence,
    string,
    strings,
)


ENTRY_STATUSES = frozenset({
    "Active", "RegisteredDisabled", "Vendor", "TestOnly",
})
INVENTORY_ROOT_FIELDS = frozenset({
    "schemaVersion", "digest", "entries", "errors",
})
INVENTORY_ENTRY_FIELDS = frozenset({
    "id", "kind", "fullyQualifiedSymbol", "resolvedTargetSignature",
    "bootstrap", "variants", "status",
})
COVERAGE_ENTRY_FIELDS = frozenset({
    "id", "domain", "testIds", "negativeTestIds", "scenarioIds",
    "variants", "status",
})


@dataclass(frozen=True, slots=True)
class ExecutionCoverageError:
    code: str
    subject: str


@dataclass(frozen=True, slots=True)
class TestDefinition:
    test_id: str
    tier: str
    scenario_id: str | None


@dataclass(frozen=True, slots=True)
class InventoryEntry:
    entry_id: str
    kind: str
    fully_qualified_symbol: str
    status: str


def validate_execution_coverage(
    values: Mapping[str, JsonValue],
    *,
    evidence_root: Path | None = None,
) -> list[ExecutionCoverageError]:
    root = exact_mapping(
        values,
        frozenset({"inventory", "coverage", "testRegistry", "evidenceBundle"}),
        "execution coverage input",
    )
    inventory = mapping(root.get("inventory"), "inventory")
    coverage = mapping(root.get("coverage"), "coverage")
    evidence_bundle = mapping(root.get("evidenceBundle"), "evidenceBundle")
    receipts = parse_execution_receipts(evidence_bundle)
    registry = _parse_registry(root.get("testRegistry"))
    inventory_entries = _inventory_entries(inventory)
    errors: list[ExecutionCoverageError] = []
    bundle_run_id = string(evidence_bundle.get("runId"), "bundle runId")
    errors.extend(ExecutionCoverageError("execution_run_id_mismatch", receipt.test_id)
                  for receipt in receipts if receipt.run_id != bundle_run_id)
    _validate_receipts(
        receipts, registry, inventory_entries, inventory, coverage,
        evidence_bundle, errors)
    coverage_entries = _coverage_entries(coverage)
    coverage_map: dict[str, dict[str, JsonValue]] = {}
    for entry in coverage_entries:
        entry_id = string(entry.get("id"), "coverage entry id")
        if entry_id in coverage_map:
            errors.append(ExecutionCoverageError(
                "manifest_duplicate_entry", entry_id))
        else:
            coverage_map[entry_id] = entry
    for entry_id in inventory_entries.keys() - coverage_map.keys():
        errors.append(ExecutionCoverageError("manifest_missing_entry", entry_id))
    _validate_receipt_ownership(receipts, coverage_map, errors)
    for entry in coverage_entries:
        _validate_entry(entry, receipts, inventory_entries, evidence_root, errors)
    return list(dict.fromkeys(errors))


def _parse_registry(value: JsonValue) -> dict[str, TestDefinition]:
    definitions: dict[str, TestDefinition] = {}
    for item in sequence(value, "testRegistry"):
        row = exact_mapping(
            item, frozenset({"id", "tier", "scenarioId"}), "test definition")
        test_id = string(row.get("id"), "test id")
        tier = choice(row.get("tier"), TIERS, "test tier")
        if not test_id.startswith(tier + ":") or test_id == tier + ":":
            raise ValueError("test id does not match its tier")
        if test_id in definitions:
            raise ValueError("duplicate test id")
        definitions[test_id] = TestDefinition(
            test_id, tier, optional_string(row.get("scenarioId"), "scenarioId"))
    if {definition.tier for definition in definitions.values()} != set(TIERS):
        raise ValueError("test registry requires all four tiers")
    return definitions


def _validate_receipts(
    receipts: Sequence[ExecutionReceipt],
    registry: Mapping[str, TestDefinition],
    inventory_entries: Mapping[str, InventoryEntry],
    inventory: Mapping[str, JsonValue],
    coverage: Mapping[str, JsonValue],
    evidence_bundle: Mapping[str, JsonValue],
    errors: list[ExecutionCoverageError],
) -> None:
    for test_id in _duplicates(receipt.test_id for receipt in receipts):
        errors.append(ExecutionCoverageError("execution_duplicate_receipt", test_id))
    inventory_digest = string(inventory.get("digest"), "inventory digest")
    if _inventory_digest(inventory) != inventory_digest:
        errors.append(ExecutionCoverageError(
            "inventory_digest_mismatch", "inventory"))
    coverage_digest = _coverage_digest(coverage)
    declared_inventory = string(coverage.get("inventoryDigest"), "coverage inventoryDigest")
    if declared_inventory != inventory_digest:
        errors.append(ExecutionCoverageError(
            "coverage_inventory_digest_mismatch", "coverage"))
    envelope_inventory = string(evidence_bundle.get("inventoryDigest"), "bundle inventoryDigest")
    envelope_coverage = string(evidence_bundle.get("coverageDigest"), "bundle coverageDigest")
    if envelope_inventory != inventory_digest:
        errors.append(ExecutionCoverageError(
            "execution_envelope_inventory_digest_mismatch", "evidenceBundle"))
    if envelope_coverage != coverage_digest:
        errors.append(ExecutionCoverageError(
            "execution_envelope_coverage_digest_mismatch", "evidenceBundle"))
    for receipt in receipts:
        definition = registry.get(receipt.test_id)
        if definition is None:
            errors.append(ExecutionCoverageError(
                "execution_unknown_test_receipt", receipt.test_id))
        else:
            _validate_definition(receipt, definition, errors)
        if receipt.inventory_digest != inventory_digest:
            errors.append(ExecutionCoverageError(
                "execution_inventory_digest_mismatch", receipt.test_id))
        if receipt.coverage_digest != coverage_digest:
            errors.append(ExecutionCoverageError(
                "execution_coverage_digest_mismatch", receipt.test_id))
        for entry_id in receipt.executed_entry_ids:
            if entry_id not in inventory_entries:
                errors.append(ExecutionCoverageError(
                    "execution_unknown_entry_receipt", entry_id))
        for entry_id in receipt.absent_entry_ids:
            if entry_id not in inventory_entries:
                errors.append(ExecutionCoverageError(
                    "execution_unknown_absent_entry_receipt", entry_id))


def _validate_definition(
    receipt: ExecutionReceipt,
    definition: TestDefinition,
    errors: list[ExecutionCoverageError],
) -> None:
    if receipt.tier != definition.tier:
        errors.append(ExecutionCoverageError(
            "execution_tier_mismatch", receipt.test_id))
    if receipt.scenario_id != definition.scenario_id:
        errors.append(ExecutionCoverageError(
            "execution_scenario_mismatch", receipt.test_id))


def _validate_entry(
    entry: Mapping[str, JsonValue],
    receipts: Sequence[ExecutionReceipt],
    inventory_entries: Mapping[str, InventoryEntry],
    evidence_root: Path | None,
    errors: list[ExecutionCoverageError],
) -> None:
    entry_id = string(entry.get("id"), "coverage entry id")
    test_ids = set(strings(entry.get("testIds"), "testIds"))
    negative_ids = set(strings(entry.get("negativeTestIds"), "negativeTestIds"))
    mapped = [receipt for receipt in receipts
              if ((receipt.polarity == "positive" and receipt.test_id in test_ids)
                  or (receipt.polarity == "negative"
                      and receipt.test_id in negative_ids))
              and entry_id in receipt.executed_entry_ids]
    inventory_entry = inventory_entries.get(entry_id)
    coverage_status = choice(
        entry.get("status"), ENTRY_STATUSES, "coverage entry status")
    if inventory_entry is not None and coverage_status != inventory_entry.status:
        errors.append(ExecutionCoverageError(
            "manifest_status_mismatch", entry_id))
    if inventory_entry is not None and inventory_entry.status == "RegisteredDisabled":
        absence_receipts = [receipt for receipt in receipts
                            if receipt.polarity == "negative"
                            and receipt.test_id in negative_ids
                            and entry_id in receipt.absent_entry_ids]
        if not absence_receipts:
            errors.append(ExecutionCoverageError(
                "registered_disabled_missing_negative_receipt", entry_id))
        elif not any(_has_same_owner_registration(
                receipt, inventory_entry, inventory_entries)
                for receipt in absence_receipts):
            errors.append(ExecutionCoverageError(
                "registered_disabled_missing_same_owner_registration", entry_id))
    elif not any(receipt.polarity == "positive" for receipt in mapped):
        errors.append(ExecutionCoverageError("execution_missing_entry_receipt", entry_id))
    reason = entry.get("headlessUnsupportedReason")
    if reason is not None and not any(
            _runtime_artifact(receipt, evidence_root, errors) for receipt in mapped):
        errors.append(ExecutionCoverageError(
            "unity_only_missing_runtime_artifact", entry_id))


def _runtime_artifact(
    receipt: ExecutionReceipt,
    evidence_root: Path | None,
    errors: list[ExecutionCoverageError],
) -> bool:
    if receipt.artifact is None:
        return False
    expected = {"ingame": "ingame-result", "real": "real-run"}.get(receipt.tier)
    if expected is None or receipt.artifact.kind != expected:
        return False
    if evidence_root is None:
        errors.append(ExecutionCoverageError(
            "runtime_artifact_root_missing", receipt.test_id))
        return False
    root = evidence_root.resolve()
    raw_path = Path(receipt.artifact.path)
    candidate = raw_path.resolve() if raw_path.is_absolute() else (root / raw_path).resolve()
    if not candidate.is_relative_to(root):
        errors.append(ExecutionCoverageError(
            "runtime_artifact_outside_root", receipt.test_id))
        return False
    if not candidate.is_file():
        errors.append(ExecutionCoverageError("runtime_artifact_missing", receipt.test_id))
        return False
    if _file_digest(candidate) != receipt.artifact.sha256:
        errors.append(ExecutionCoverageError(
            "runtime_artifact_hash_mismatch", receipt.test_id))
        return False
    return _validate_runtime_manifest(candidate, root, receipt, errors)


def _has_same_owner_registration(
    receipt: ExecutionReceipt,
    disabled: InventoryEntry,
    inventory: Mapping[str, InventoryEntry],
) -> bool:
    for witness in receipt.registration_witnesses:
        registration = inventory.get(witness.registration_entry_id)
        if witness.entry_id != disabled.entry_id or registration is None:
            continue
        if registration.kind != "PacketRegistration":
            continue
        if registration.entry_id not in receipt.executed_entry_ids:
            continue
        if _entry_owner(registration) == _entry_owner(disabled):
            return True
    return False


def _entry_owner(entry: InventoryEntry) -> str:
    symbol = entry.fully_qualified_symbol
    if entry.kind == "PacketRegistration" and "(" not in symbol:
        return symbol
    member = symbol.split("(", maxsplit=1)[0]
    return member.rsplit(".", maxsplit=1)[0] if "." in member else member


def canonical_coverage_digest(coverage: Mapping[str, JsonValue]) -> str:
    payload = json.dumps(coverage, sort_keys=True, separators=(",", ":"))
    return "sha256:" + hashlib.sha256(payload.encode("utf-8")).hexdigest()


def _coverage_digest(coverage: Mapping[str, JsonValue]) -> str:
    if "digest" in coverage or "coverageDigest" in coverage:
        raise ValueError("coverage must not declare a synthetic digest")
    return canonical_coverage_digest(coverage)


def _file_digest(path: Path) -> str:
    return "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest()


def _validate_runtime_manifest(
    path: Path,
    evidence_root: Path,
    receipt: ExecutionReceipt,
    errors: list[ExecutionCoverageError],
) -> bool:
    try:
        value = cast(JsonValue, json.loads(path.read_text(encoding="utf-8")))
    except (OSError, json.JSONDecodeError):
        errors.append(ExecutionCoverageError(
            "runtime_artifact_invalid", receipt.test_id))
        return False
    if not isinstance(value, dict):
        errors.append(ExecutionCoverageError(
            "runtime_artifact_invalid", receipt.test_id))
        return False
    control: JsonValue = value.get("controlPath")
    driver = control.get("driver") if isinstance(control, dict) else None
    valid = isinstance(driver, str) and bool(driver.strip()) and driver != "manual-observe"
    if not valid:
        errors.append(ExecutionCoverageError(
            "runtime_control_path_mismatch", receipt.test_id))
    identity_valid = _runtime_identity_matches(value, receipt)
    if not identity_valid:
        errors.append(ExecutionCoverageError(
            "runtime_artifact_identity_mismatch", receipt.test_id))
    payloads = [_validate_payload(value, name, path.parent, evidence_root,
                                  receipt, errors)
                for name in ("log", "result")]
    result_valid = _runtime_result_passed(value, path.parent, evidence_root)
    if not result_valid:
        errors.append(ExecutionCoverageError(
            "runtime_result_failed", receipt.test_id))
    return valid and identity_valid and all(payloads) and result_valid


def _runtime_identity_matches(
    manifest: Mapping[str, JsonValue],
    receipt: ExecutionReceipt,
) -> bool:
    expected: dict[str, JsonValue] = {
        "runId": receipt.run_id,
        "testId": receipt.test_id,
        "scenarioId": receipt.scenario_id,
        "tier": receipt.tier,
        "executedEntryIds": list[JsonValue](receipt.executed_entry_ids),
    }
    return all(manifest.get(field) == value for field, value in expected.items())


def _runtime_result_passed(
    manifest: Mapping[str, JsonValue],
    artifact_root: Path,
    evidence_root: Path,
) -> bool:
    result = manifest.get("result")
    raw_path = result.get("path") if isinstance(result, dict) else None
    if not isinstance(raw_path, str):
        return False
    candidate = (artifact_root / raw_path).resolve()
    if Path(raw_path).is_absolute() or not candidate.is_relative_to(evidence_root):
        return False
    try:
        value = cast(JsonValue, json.loads(
            candidate.read_text(encoding="utf-8")))
    except (OSError, json.JSONDecodeError):
        return False
    return isinstance(value, dict) and value.get("passed") is True


def _validate_payload(
    manifest: Mapping[str, JsonValue],
    name: str,
    artifact_root: Path,
    evidence_root: Path,
    receipt: ExecutionReceipt,
    errors: list[ExecutionCoverageError],
) -> bool:
    value = manifest.get(name)
    if not isinstance(value, dict) or set(value) != {"path", "sha256"}:
        errors.append(ExecutionCoverageError(
            f"runtime_artifact_missing_{name}", receipt.test_id))
        return False
    raw_path, expected = value.get("path"), value.get("sha256")
    if not isinstance(raw_path, str) or not isinstance(expected, str):
        errors.append(ExecutionCoverageError(
            f"runtime_artifact_missing_{name}", receipt.test_id))
        return False
    candidate = (artifact_root / raw_path).resolve()
    if Path(raw_path).is_absolute() or not candidate.is_relative_to(evidence_root):
        errors.append(ExecutionCoverageError(
            "runtime_artifact_outside_root", receipt.test_id))
        return False
    if not candidate.is_file() or _file_digest(candidate) != expected:
        errors.append(ExecutionCoverageError(
            f"runtime_{name}_hash_mismatch", receipt.test_id))
        return False
    return True


def _validate_receipt_ownership(
    receipts: Sequence[ExecutionReceipt],
    coverage: Mapping[str, Mapping[str, JsonValue]],
    errors: list[ExecutionCoverageError],
) -> None:
    for receipt in receipts:
        for entry_id in receipt.executed_entry_ids:
            entry = coverage.get(entry_id)
            if entry is None or not _receipt_maps(entry, receipt):
                errors.append(ExecutionCoverageError(
                    "execution_unmapped_entry_receipt", entry_id))


def _receipt_maps(
    entry: Mapping[str, JsonValue], receipt: ExecutionReceipt,
) -> bool:
    field = "testIds" if receipt.polarity == "positive" else "negativeTestIds"
    return receipt.test_id in strings(entry.get(field), field)

def _inventory_entries(
    document: Mapping[str, JsonValue],
) -> dict[str, InventoryEntry]:
    root = exact_mapping(document, INVENTORY_ROOT_FIELDS, "inventory")
    _ = schema_version(root, "inventory")
    result: dict[str, InventoryEntry] = {}
    for value in sequence(root.get("entries"), "inventory entries"):
        row = exact_mapping(value, INVENTORY_ENTRY_FIELDS, "inventory entry")
        entry_id = string(row.get("id"), "inventory entry id")
        status = choice(
            row.get("status"), ENTRY_STATUSES, "invalid inventory status")
        _ = strings(row.get("variants"), "inventory entry variants")
        if entry_id in result:
            raise ValueError("duplicate inventory entry id")
        result[entry_id] = InventoryEntry(
            entry_id, string(row.get("kind"), "inventory entry kind"),
            string(row.get("fullyQualifiedSymbol"),
                   "inventory fullyQualifiedSymbol"),
            status,
        )
        _ = string(row.get("resolvedTargetSignature"),
                   "inventory resolvedTargetSignature")
        _ = string(row.get("bootstrap"), "inventory bootstrap")
    for value in sequence(root.get("errors"), "inventory errors"):
        error = exact_mapping(
            value, frozenset({"code", "subject"}), "inventory error")
        _ = string(error.get("code"), "inventory error code")
        _ = string(error.get("subject"), "inventory error subject")
    return result


def _inventory_digest(document: Mapping[str, JsonValue]) -> str:
    content = {
        "entries": document.get("entries"),
        "errors": document.get("errors"),
    }
    canonical = json.dumps(content, separators=(",", ":"), ensure_ascii=True)
    canonical = canonical.replace('\\"', "\\u0022")
    canonical = canonical.replace("+", "\\u002B")
    canonical = canonical.replace("<", "\\u003C")
    canonical = canonical.replace(">", "\\u003E")
    canonical = re.sub(
        r"\\u([0-9a-f]{4})",
        lambda match: "\\u" + match.group(1).upper(),
        canonical,
    )
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def _coverage_entries(document: Mapping[str, JsonValue]) -> list[dict[str, JsonValue]]:
    _ = schema_version(document, "coverage")
    return [_coverage_entry(value) for value in
            sequence(document.get("entries"), "coverage entries")]


def _coverage_entry(value: JsonValue) -> dict[str, JsonValue]:
    row = mapping(value, "coverage entry")
    fields = frozenset(row)
    allowed = (COVERAGE_ENTRY_FIELDS,
               COVERAGE_ENTRY_FIELDS | {"headlessUnsupportedReason"})
    if fields not in allowed:
        raise ValueError("coverage entry fields are not exact")
    _ = string(row.get("id"), "coverage entry id")
    _ = string(row.get("domain"), "coverage entry domain")
    _ = choice(row.get("status"), ENTRY_STATUSES, "coverage entry status")
    for field in ("testIds", "negativeTestIds", "scenarioIds", "variants"):
        _ = strings(row.get(field), f"coverage entry {field}")
    if "headlessUnsupportedReason" in row:
        _ = string(row.get("headlessUnsupportedReason"),
                   "coverage entry headlessUnsupportedReason")
    return row


def _duplicates(values: Iterable[str]) -> set[str]:
    seen: set[str] = set()
    duplicates: set[str] = set()
    for value in values:
        if value in seen:
            duplicates.add(value)
        seen.add(value)
    return duplicates
