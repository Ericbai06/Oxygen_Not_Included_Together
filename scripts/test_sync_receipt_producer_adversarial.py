import hashlib
import json
from dataclasses import dataclass
from pathlib import Path
import tempfile
from typing import Final
import unittest

from scripts import oni_run_evidence
from scripts.oni_coverage_gate import canonical_coverage_digest
from scripts.oni_execution_receipts import JsonValue
from scripts.oni_receipt_producer import ObservedExecutionReceiptProducer
from scripts.test_typed_scenario_evidence import TypedEvidenceRecord, envelope, line


RUN_ID: Final = "run-receipt-001"
ENTRY_ID: Final = "sync:canonical-entry"


def inventory_document() -> dict[str, JsonValue]:
    entry: dict[str, JsonValue] = {
        "id": ENTRY_ID,
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


def coverage_document(inventory_digest: str) -> dict[str, JsonValue]:
    return {
        "schemaVersion": 1,
        "inventoryDigest": inventory_digest,
        "entries": list[JsonValue]([{
            "id": ENTRY_ID,
            "domain": "door",
            "testIds": list[JsonValue](["real:door"]),
            "negativeTestIds": list[JsonValue](),
            "scenarioIds": list[JsonValue](["door"]),
            "variants": list[JsonValue](["Debug/OS_MAC"]),
            "status": "Active",
            "headlessUnsupportedReason": "requires real runtime",
        }]),
    }


INVENTORY_DOCUMENT: Final = inventory_document()
INVENTORY_DIGEST: Final = str(INVENTORY_DOCUMENT["digest"])
COVERAGE_DOCUMENT: Final = coverage_document(INVENTORY_DIGEST)
COVERAGE_DIGEST: Final = canonical_coverage_digest(COVERAGE_DOCUMENT)


@dataclass(frozen=True, slots=True)
class ProducerFixture:
    inventory_path: Path
    coverage_path: Path
    evidence_root: Path
    logs: dict[str, str]
    records: list[dict[str, JsonValue]]
    verdict: dict[str, JsonValue]


@dataclass(frozen=True, slots=True)
class ReceiptIdentity:
    run_id: str
    inventory_digest: str
    coverage_digest: str


VALID_IDENTITY: Final = ReceiptIdentity(
    RUN_ID, INVENTORY_DIGEST, COVERAGE_DIGEST)


def create_fixture(root: Path, *, observed: bool) -> ProducerFixture:
    record: TypedEvidenceRecord = envelope("door")
    record["runId"] = RUN_ID
    record["entryId"] = ENTRY_ID
    logs = {"host": line(record), "client": ""} if observed else {
        "host": "", "client": ""}
    inventory_path = root / "inventory.json"
    coverage_path = root / "coverage.json"
    inventory_path.write_text(json.dumps(INVENTORY_DOCUMENT), encoding="utf-8")
    coverage_path.write_text(json.dumps(COVERAGE_DOCUMENT), encoding="utf-8")
    return ProducerFixture(
        inventory_path=inventory_path,
        coverage_path=coverage_path,
        evidence_root=root / "evidence",
        logs=logs,
        records=[json_record(record)] if observed else [],
        verdict={"passed": True, "failures": [], "forbidden": []},
    )


def json_record(record: TypedEvidenceRecord) -> dict[str, JsonValue]:
    return {
        "schemaVersion": record["schemaVersion"],
        "runId": record["runId"],
        "dllHash": record["dllHash"],
        "scenario": record["scenario"],
        "entryId": record["entryId"],
        "role": record["role"],
        "sessionEpoch": record["sessionEpoch"],
        "connectionGeneration": record["connectionGeneration"],
        "snapshotGeneration": record["snapshotGeneration"],
        "phase": record["phase"],
        "revisionDomain": record["revisionDomain"],
        "revision": record["revision"],
        "sequence": record["sequence"],
        "target": record["target"],
        "state": record["state"],
        "stateHash": record["stateHash"],
    }


def persist(fixture: ProducerFixture, producer) -> Path:
    return Path(oni_run_evidence.persist_run_evidence({
        "state": {
            "runId": RUN_ID,
            "scenario": "door",
            "offsets": {"host": 0, "client": 0},
        },
        "logs": fixture.logs,
        "verdict": fixture.verdict,
        "dllHash": "sha256:" + "4" * 64,
        "inventory": fixture.inventory_path,
        "coverage": fixture.coverage_path,
        "outputRoot": fixture.evidence_root,
        "receiptProducer": producer,
    }))


def observed_producer(
    fixture: ProducerFixture,
    identity: ReceiptIdentity = VALID_IDENTITY,
    records: list[dict[str, JsonValue]] | None = None,
) -> ObservedExecutionReceiptProducer:
    return ObservedExecutionReceiptProducer(
        run_id=identity.run_id,
        scenario="door",
        inventory_digest=identity.inventory_digest,
        coverage_digest=identity.coverage_digest,
        dll_hash="sha256:" + "4" * 64,
        pdb_hash="sha256:" + "5" * 64,
        records=fixture.records if records is None else records,
        logs=fixture.logs,
        verdict=fixture.verdict,
        evidence_root=fixture.evidence_root,
        control_driver="door-driver",
    )


class PersistedProducerReceiptTests(unittest.TestCase):
    def test_persist_requires_observed_producer_and_canonical_mapping(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            fixture = create_fixture(Path(temporary), observed=True)
            producer = observed_producer(fixture)

            persisted = json.loads(persist(fixture, producer).read_text(
                encoding="utf-8"))

        self.assertEqual(
            [ENTRY_ID], persisted["executionReceipts"][0]["executedEntryIds"])
        self.assertEqual(ENTRY_ID, persisted["typedRecords"][0]["entryId"])
        self.assertEqual(INVENTORY_DIGEST, persisted["inventoryDigest"])
        self.assertEqual(COVERAGE_DIGEST, persisted["coverageDigest"])

    def test_arbitrary_producer_id_not_observed_fails(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            fixture = create_fixture(Path(temporary), observed=True)
            records = [dict(fixture.records[0])]
            records[0]["entryId"] = "sync:unobserved"

            with self.assertRaisesRegex(ValueError, "differ from observed"):
                persist(fixture, observed_producer(fixture, records=records))

    def test_no_observed_typed_record_fails(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            fixture = create_fixture(Path(temporary), observed=False)

            with self.assertRaisesRegex(ValueError, "no entry IDs"):
                persist(fixture, observed_producer(fixture, records=[]))

    def test_stale_producer_identity_fails(self) -> None:
        mutations = (
            ReceiptIdentity("run-stale", INVENTORY_DIGEST, COVERAGE_DIGEST),
            ReceiptIdentity(RUN_ID, "9" * 64, COVERAGE_DIGEST),
            ReceiptIdentity(RUN_ID, INVENTORY_DIGEST, "sha256:" + "8" * 64),
        )
        for identity in mutations:
            with self.subTest(identity=identity), tempfile.TemporaryDirectory() as temporary:
                fixture = create_fixture(Path(temporary), observed=True)

                with self.assertRaisesRegex(
                        ValueError, "another run|identity differs"):
                    persist(fixture, observed_producer(fixture, identity))
                self.assertFalse(any(fixture.evidence_root.glob("*.json")),
                                 "stale producer identity was rewritten and persisted")

    def test_raw_caller_supplied_receipts_fail(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            fixture = create_fixture(Path(temporary), observed=True)
            producer_receipts = observed_producer(fixture).complete()
            context = {
                "state": {
                    "runId": RUN_ID,
                    "scenario": "door",
                    "offsets": {"host": 0, "client": 0},
                },
                "logs": fixture.logs,
                "verdict": fixture.verdict,
                "dllHash": "sha256:" + "4" * 64,
                "inventory": fixture.inventory_path,
                "coverage": fixture.coverage_path,
                "outputRoot": fixture.evidence_root,
                "executionReceipts": producer_receipts,
            }

            with self.assertRaisesRegex(ValueError, "receipt producer"):
                oni_run_evidence.persist_run_evidence(context)


if __name__ == "__main__":
    unittest.main()
