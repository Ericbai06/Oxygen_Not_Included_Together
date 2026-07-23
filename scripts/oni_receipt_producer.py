from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import cast, final

from .oni_coverage_gate import canonical_coverage_digest
from .oni_execution_receipts import JsonValue


@final
class ObservedExecutionReceiptProducer:
    def __init__(
        self,
        *,
        run_id: str,
        scenario: str,
        inventory_digest: str,
        coverage_digest: str,
        dll_hash: str,
        pdb_hash: str,
        records: list[dict[str, JsonValue]],
        logs: dict[str, str],
        verdict: dict[str, JsonValue],
        evidence_root: Path,
        control_driver: str,
    ) -> None:
        if not run_id or Path(run_id).name != run_id or run_id in {".", ".."}:
            raise ValueError("receipt producer runId is not path-safe")
        self._run_id = run_id
        self._scenario = scenario
        self._inventory_digest = inventory_digest
        self._coverage_digest = coverage_digest
        self._dll_hash = _binary_digest(dll_hash, "dllHash")
        self._pdb_hash = _binary_digest(pdb_hash, "pdbHash")
        self._records = records
        self._logs = logs
        self._verdict = verdict
        self._evidence_root = evidence_root
        self._control_driver = control_driver
        self._completed = False

    def complete(self) -> list[dict[str, JsonValue]]:
        if self._completed:
            raise ValueError("receipt producer already completed")
        self._completed = True
        entry_ids = self._observed_entry_ids()
        artifact = self._write_artifact(entry_ids)
        receipt: dict[str, JsonValue] = {
            "schemaVersion": 1,
            "runId": self._run_id,
            "inventoryDigest": self._inventory_digest,
            "coverageDigest": self._coverage_digest,
            "dllHash": self._dll_hash,
            "pdbHash": self._pdb_hash,
            "testId": f"real:{self._scenario}",
            "tier": "real",
            "scenarioId": self._scenario,
            "polarity": "positive",
            "executedEntryIds": list[JsonValue](entry_ids),
            "absentEntryIds": list[JsonValue](),
            "registrationWitnesses": list[JsonValue](),
            "artifact": artifact,
        }
        return [receipt]

    def _observed_entry_ids(self) -> list[str]:
        observed: set[str] = set()
        for record in self._records:
            if record.get("runId") != self._run_id:
                raise ValueError("typed record belongs to another run")
            entry_id = record.get("entryId")
            if not isinstance(entry_id, str) or not entry_id.startswith("sync:"):
                raise ValueError("typed record has invalid observed entryId")
            observed.add(entry_id)
        if not observed:
            raise ValueError("receipt producer observed no entry IDs")
        return sorted(observed)

    def _write_artifact(self, entry_ids: list[str]) -> dict[str, JsonValue]:
        relative = Path(".runtime-artifacts") / self._run_id
        evidence_root = self._evidence_root.resolve()
        artifact_root = (evidence_root / relative).resolve()
        if not artifact_root.is_relative_to(evidence_root):
            raise ValueError("runtime artifact escaped the evidence root")
        artifact_root.mkdir(parents=True, exist_ok=False)
        log_path = artifact_root / "runtime.json"
        result_path = artifact_root / "result.json"
        manifest_path = artifact_root / "manifest.json"
        _write_json(log_path, self._logs)
        _write_json(result_path, self._verdict)
        _write_json(manifest_path, {
            "schemaVersion": 1,
            "runId": self._run_id,
            "testId": f"real:{self._scenario}",
            "scenarioId": self._scenario,
            "tier": "real",
            "executedEntryIds": list[JsonValue](entry_ids),
            "controlPath": {"driver": self._control_driver},
            "log": _hashed_file(log_path),
            "result": _hashed_file(result_path),
        })
        return {
            "kind": "real-run",
            "path": str(relative / "manifest.json"),
            "sha256": _digest(manifest_path),
        }


def _write_json(path: Path, value: object) -> None:
    _ = path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n",
                        encoding="utf-8")


def _hashed_file(path: Path) -> dict[str, str]:
    return {"path": path.name, "sha256": _digest(path)}


def _digest(path: Path) -> str:
    return "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest()


def _binary_digest(value: str, subject: str) -> str:
    digest = value.removeprefix("sha256:")
    if len(digest) != 64 or any(character not in "0123456789abcdef"
                                for character in digest):
        raise ValueError(f"receipt producer {subject} is invalid")
    return digest


def read_execution_digests(
    inventory_path: Path,
    coverage_path: Path,
) -> tuple[str, str]:
    inventory = cast(JsonValue, json.loads(inventory_path.read_text(encoding="utf-8")))
    coverage = cast(JsonValue, json.loads(coverage_path.read_text(encoding="utf-8")))
    if not isinstance(inventory, dict) or not isinstance(coverage, dict):
        raise ValueError("execution catalogs must be JSON objects")
    inventory_digest = inventory.get("digest")
    if not isinstance(inventory_digest, str) or not inventory_digest.strip():
        raise ValueError("inventory lacks its internal digest")
    return inventory_digest, canonical_coverage_digest(coverage)
