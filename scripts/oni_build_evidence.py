from __future__ import annotations

import json
import re
from typing import Any


PREFIX = "[BuildAuthorityEvidence] "
PHASES = {
    "host-request",
    "host-commit",
    "host-rejected",
    "client-apply",
    "save-state",
    "reload-state",
}
FORBIDDEN_PATTERNS = (
    re.compile(r"\breconnect\b", re.IGNORECASE),
    re.compile(r"NullReferenceException", re.IGNORECASE),
    re.compile(r"duplicate\s+NetId", re.IGNORECASE),
    re.compile(r"phantom\s+(?:conduit|utility)\s+(?:edge|connection)", re.IGNORECASE),
    re.compile(r"generic\s+SpawnPrefab\s+duplicate", re.IGNORECASE),
)


def parse_line(line: str) -> dict[str, Any] | None:
    if not line.startswith(PREFIX):
        return None
    try:
        value = json.loads(line[len(PREFIX) :])
    except (TypeError, json.JSONDecodeError):
        return None
    return value if isinstance(value, dict) else None


def parse_logs(logs: dict[str, str]) -> tuple[list[dict[str, Any]], list[str]]:
    records: list[dict[str, Any]] = []
    malformed: list[str] = []
    for role, text in logs.items():
        for raw in text.splitlines():
            if not raw.startswith(PREFIX):
                continue
            record = parse_line(raw)
            if record is None:
                malformed.append(f"{role}: malformed evidence line")
                continue
            record = dict(record)
            record["_role"] = role
            records.append(record)
    return records, malformed


def _same_identity(records: list[dict[str, Any]]) -> list[str]:
    failures: list[str] = []
    for field in ("operationId", "prefabId", "geometry", "materialTags", "facadeId", "priority"):
        values = {json.dumps(record.get(field), sort_keys=True) for record in records}
        if len(values) != 1:
            failures.append(f"{field} differs across causal records")
    return failures


def _accepted_cells(commit: dict[str, Any]) -> set[int]:
    placements = commit.get("placements")
    if not isinstance(placements, list):
        return set()
    return {
        item["cell"]
        for item in placements
        if isinstance(item, dict)
        and isinstance(item.get("cell"), int)
        and item.get("status") in {"queued", "completed", "replacement", "accepted"}
    }


def _connection_failures(commit: dict[str, Any]) -> list[str]:
    accepted = _accepted_cells(commit)
    connections = commit.get("connections", [])
    if not isinstance(connections, list):
        return ["host commit connections is not a list"]
    failures: list[str] = []
    geometry = commit.get("geometry")
    path = geometry.get("cells") if isinstance(geometry, dict) else None
    path_index = {cell: index for index, cell in enumerate(path)} if isinstance(path, list) else {}
    for edge in connections:
        if not isinstance(edge, dict) or not isinstance(edge.get("from"), int) or not isinstance(edge.get("to"), int):
            failures.append("connection edge has invalid endpoints")
            continue
        if edge["from"] not in accepted or edge["to"] not in accepted:
            failures.append("connection edge references a cell absent from placements")
        elif path_index and abs(path_index.get(edge["from"], -1) - path_index.get(edge["to"], -1)) != 1:
            failures.append("connection edge crosses a failed path cell")
    return failures


def evaluate(logs: dict[str, str], *, require_rejection: bool = False,
             require_save_reload: bool = False) -> dict[str, Any]:
    records, failures = parse_logs(logs)
    if not records:
        failures.append("no BuildAuthorityEvidence records")
    for role, text in logs.items():
        for raw in text.splitlines():
            if raw.startswith(PREFIX):
                continue
            for pattern in FORBIDDEN_PATTERNS:
                if pattern.search(raw):
                    failures.append(f"{role}: forbidden runtime signal {pattern.pattern}")

    by_phase = {phase: [record for record in records if record.get("phase") == phase]
                for phase in PHASES}
    for phase in ("host-request", "host-commit", "client-apply"):
        if not by_phase[phase]:
            failures.append(f"missing {phase} evidence")
    if by_phase["host-request"] and by_phase["host-commit"] and by_phase["client-apply"]:
        causal = [by_phase[phase][0] for phase in ("host-request", "host-commit", "client-apply")]
        failures.extend(_same_identity(causal))
        request, commit, apply = causal
        if request.get("_role") != "host" or commit.get("_role") != "host":
            failures.append("request and commit must be observed on host")
        if apply.get("_role") != "client":
            failures.append("commit apply must be observed on client")
        if commit.get("publisherCount") != 1:
            failures.append("host commit must have exactly one lifecycle publisher")
        if commit.get("stateHash") != apply.get("stateHash"):
            failures.append("host/client final state hashes differ")
        failures.extend(_connection_failures(commit))

    if require_rejection:
        rejected = by_phase["host-rejected"]
        if not rejected:
            failures.append("missing host-rejected evidence")
        else:
            if any(record.get("disconnect") for record in rejected):
                failures.append("normal rejection caused disconnect")
            if any(record.get("reconnect") for record in rejected):
                failures.append("normal rejection caused reconnect")

    if require_save_reload:
        if not by_phase["save-state"] or not by_phase["reload-state"]:
            failures.append("missing save/reload evidence")
        else:
            save = by_phase["save-state"][0]
            reload = by_phase["reload-state"][0]
            if save.get("stateHash") != reload.get("stateHash"):
                failures.append("save/reload state hashes differ")

    return {"passed": not failures, "failures": failures, "records": records}
