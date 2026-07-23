from collections.abc import Mapping
from dataclasses import dataclass
import re
from typing import Final, TypeAlias


JsonScalar: TypeAlias = str | int | float | bool | None
JsonValue: TypeAlias = JsonScalar | list["JsonValue"] | dict[str, "JsonValue"]

TIERS: Final = frozenset({"headless", "ingame", "python", "real"})
POLARITIES: Final = frozenset({"positive", "negative"})
RECEIPT_FIELDS: Final = frozenset({
    "schemaVersion", "runId", "inventoryDigest", "coverageDigest", "testId",
    "tier", "scenarioId", "polarity", "executedEntryIds", "artifact",
    "dllHash", "pdbHash",
})
PROVENANCE_FIELDS: Final = frozenset({
    "absentEntryIds", "registrationWitnesses",
})
WITNESS_FIELDS: Final = frozenset({"entryId", "registrationEntryId"})
ARTIFACT_FIELDS: Final = frozenset({"kind", "path", "sha256"})
BUNDLE_FIELDS: Final = frozenset({
    "schemaVersion", "runId", "inventoryDigest", "coverageDigest", "dllHash",
    "endpointLogs", "typedRecords", "results", "failureFlow",
    "executionReceipts",
})
HEX_DIGEST = re.compile(r"[0-9a-f]{64}\Z")
PREFIXED_DIGEST = re.compile(r"sha256:[0-9a-f]{64}\Z")


@dataclass(frozen=True, slots=True)
class ExecutionArtifact:
    kind: str
    path: str
    sha256: str


@dataclass(frozen=True, slots=True)
class RegistrationWitness:
    entry_id: str
    registration_entry_id: str


@dataclass(frozen=True, slots=True)
class ExecutionReceipt:
    schema_version: int
    run_id: str
    inventory_digest: str
    coverage_digest: str
    dll_hash: str
    pdb_hash: str
    test_id: str
    tier: str
    scenario_id: str | None
    polarity: str
    executed_entry_ids: tuple[str, ...]
    absent_entry_ids: tuple[str, ...]
    registration_witnesses: tuple[RegistrationWitness, ...]
    artifact: ExecutionArtifact | None


def parse_execution_receipts(
    evidence_bundle: Mapping[str, JsonValue],
) -> tuple[ExecutionReceipt, ...]:
    bundle = exact_mapping(evidence_bundle, BUNDLE_FIELDS, "evidence bundle")
    _ = schema_version(bundle, "evidence bundle")
    return tuple(_parse_receipt(value) for value in
                 sequence(bundle.get("executionReceipts"), "executionReceipts"))


def _parse_receipt(value: JsonValue) -> ExecutionReceipt:
    receipt = mapping(value, "execution receipt")
    fields = frozenset(receipt)
    if fields != RECEIPT_FIELDS | PROVENANCE_FIELDS:
        raise ValueError("execution receipt fields are not exact")
    version = schema_version(receipt, "execution receipt")
    inventory_digest = string(receipt.get("inventoryDigest"), "inventoryDigest")
    coverage_digest = string(receipt.get("coverageDigest"), "coverageDigest")
    if HEX_DIGEST.fullmatch(inventory_digest) is None:
        raise ValueError("execution receipt inventoryDigest is invalid")
    if PREFIXED_DIGEST.fullmatch(coverage_digest) is None:
        raise ValueError("execution receipt coverageDigest is invalid")
    dll_hash = string(receipt.get("dllHash"), "dllHash")
    pdb_hash = string(receipt.get("pdbHash"), "pdbHash")
    if HEX_DIGEST.fullmatch(dll_hash) is None:
        raise ValueError("execution receipt dllHash is invalid")
    if HEX_DIGEST.fullmatch(pdb_hash) is None:
        raise ValueError("execution receipt pdbHash is invalid")
    tier = choice(receipt.get("tier"), TIERS, "tier")
    polarity = choice(receipt.get("polarity"), POLARITIES, "polarity")
    entry_ids = tuple(string(item, "executedEntryIds item") for item in
                      sequence(receipt.get("executedEntryIds"), "executedEntryIds"))
    if not entry_ids or len(set(entry_ids)) != len(entry_ids):
        raise ValueError("executedEntryIds must be nonempty and unique")
    absent_entry_ids, witnesses = _parse_absence_provenance(
        receipt, polarity, entry_ids)
    return ExecutionReceipt(
        version, string(receipt.get("runId"), "runId"),
        inventory_digest, coverage_digest, dll_hash, pdb_hash,
        string(receipt.get("testId"), "testId"), tier,
        optional_string(receipt.get("scenarioId"), "scenarioId"), polarity,
        entry_ids, absent_entry_ids, witnesses,
        _parse_artifact(receipt.get("artifact")),
    )


def _parse_absence_provenance(
    receipt: Mapping[str, JsonValue],
    polarity: str,
    executed: tuple[str, ...],
) -> tuple[tuple[str, ...], tuple[RegistrationWitness, ...]]:
    absent = tuple(string(item, "absentEntryIds item") for item in
                   sequence(receipt.get("absentEntryIds"), "absentEntryIds"))
    if len(set(absent)) != len(absent):
        raise ValueError("absentEntryIds must be unique")
    witnesses = tuple(_parse_witness(value) for value in sequence(
        receipt.get("registrationWitnesses"), "registrationWitnesses"))
    pairs = {(item.entry_id, item.registration_entry_id) for item in witnesses}
    if len(pairs) != len(witnesses):
        raise ValueError("registrationWitnesses must be unique")
    if polarity == "positive" and (absent or witnesses):
        raise ValueError("positive receipt cannot declare absence provenance")
    if polarity == "negative" and (not absent or not witnesses):
        raise ValueError("negative receipt requires absence provenance")
    if set(absent) & set(executed):
        raise ValueError("absentEntryIds cannot overlap executedEntryIds")
    if {item.entry_id for item in witnesses} != set(absent):
        raise ValueError("registrationWitnesses must exactly cover absentEntryIds")
    if any(item.registration_entry_id not in executed for item in witnesses):
        raise ValueError("registration witness must reference an executed entry")
    return absent, witnesses


def _parse_witness(value: JsonValue) -> RegistrationWitness:
    witness = exact_mapping(value, WITNESS_FIELDS, "registration witness")
    return RegistrationWitness(
        string(witness.get("entryId"), "registration witness entryId"),
        string(witness.get("registrationEntryId"),
               "registration witness registrationEntryId"),
    )


def _parse_artifact(value: JsonValue) -> ExecutionArtifact | None:
    if value is None:
        return None
    artifact = exact_mapping(value, ARTIFACT_FIELDS, "execution artifact")
    digest = string(artifact.get("sha256"), "artifact sha256")
    if PREFIXED_DIGEST.fullmatch(digest) is None:
        raise ValueError("execution artifact sha256 is invalid")
    return ExecutionArtifact(
        string(artifact.get("kind"), "artifact kind"),
        string(artifact.get("path"), "artifact path"), digest)


def exact_mapping(
    value: JsonValue | Mapping[str, JsonValue],
    fields: frozenset[str],
    subject: str,
) -> dict[str, JsonValue]:
    result = mapping(value, subject)
    if frozenset(result) != fields:
        raise ValueError(f"{subject} fields are not exact")
    return result


def mapping(
    value: JsonValue | Mapping[str, JsonValue], subject: str,
) -> dict[str, JsonValue]:
    if not isinstance(value, dict):
        raise ValueError(f"{subject} must be an object")
    return value


def sequence(value: JsonValue, subject: str) -> list[JsonValue]:
    if not isinstance(value, list):
        raise ValueError(f"{subject} must be an array")
    return value


def string(value: JsonValue, subject: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{subject} must be a nonempty string")
    return value


def optional_string(value: JsonValue, subject: str) -> str | None:
    if value is None:
        return None
    return string(value, subject)


def choice(value: JsonValue, choices: frozenset[str], subject: str) -> str:
    result = string(value, subject)
    if result not in choices:
        raise ValueError(f"{subject} is invalid")
    return result


def strings(value: JsonValue, subject: str) -> tuple[str, ...]:
    return tuple(string(item, subject) for item in sequence(value, subject))


def schema_version(document: Mapping[str, JsonValue], subject: str) -> int:
    value = document.get("schemaVersion")
    if not isinstance(value, int) or isinstance(value, bool) or value != 1:
        raise ValueError(f"{subject} schemaVersion must be 1")
    return value
