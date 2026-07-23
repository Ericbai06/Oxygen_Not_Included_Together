import json
import os
from pathlib import Path
import re
import tempfile
from typing import Final

from .oni_scenario_contracts import SCENARIOS
from .oni_scenario_execution_specs import (
    REGISTERED_CLEANUPS,
    REGISTERED_DRIVERS,
    SCENARIO_EXECUTION_SPECS,
    validate_execution_specs,
)


STATIC_ID: Final = "ONI_Together"


def resolve_enabled_mod(mods_data, static_id):
    matches = [entry for entry in mods_data.get("mods", [])
               if entry.get("staticID") == static_id and entry.get("enabled") is True]
    if len(matches) != 1:
        raise ValueError(f"expected one enabled {static_id} entry, found {len(matches)}")
    return matches[0]


def resolve_loaded_mod_dll(mods_data, player_log, mods_root, exists=Path.exists):
    entry = resolve_enabled_mod(mods_data, STATIC_ID)
    label = entry.get("label")
    if not isinstance(label, dict):
        raise ValueError("enabled ONI_Together entry has no label")
    mod_id = label.get("id")
    platform = label.get("distribution_platform")
    if not isinstance(mod_id, str) or not mod_id:
        raise ValueError("enabled ONI_Together label id is invalid")
    locations = {0: ("Local", "Developer Build"), 1: ("Steam", "ONI ONLINE")}
    if platform not in locations:
        raise ValueError(f"unsupported ONI mod distribution platform: {platform}")
    folder, marker_name = locations[platform]
    marker = f"Loading mod content DLL [{marker_name}:{mod_id}] (provides DLL)"
    candidate = Path(mods_root) / folder / mod_id / "ONI_Together.dll"
    if marker not in player_log or not exists(candidate):
        raise ValueError("configured ONI_Together DLL is not loaded and present")
    return candidate


def validate_multiplayer_preflight(host, client):
    failures = []
    hash_pattern = re.compile(r"sha256:[0-9a-f]{64}\Z")
    for role, facts in (("host", host), ("client", client)):
        if facts.get("build") != "U59-740622-S":
            failures.append(f"{role}: build must be U59-740622-S")
        if facts.get("protocol") != "10":
            failures.append(f"{role}: protocol must be 10")
        if facts.get("role") != role:
            failures.append(f"{role}: endpoint role is invalid")
        if facts.get("steamSession") in {None, "", "0"}:
            failures.append(f"{role}: Steam session is invalid")
        for field in ("dllHash", "dlcFingerprint"):
            value = facts.get(field)
            if not isinstance(value, str) or hash_pattern.fullmatch(value) is None:
                failures.append(f"{role}: {field} is invalid")
    for field in ("dllHash", "dlcFingerprint", "steamSession"):
        if host.get(field) != client.get(field):
            failures.append(f"endpoint {field} values differ")
    return failures


SELECTOR_FIELDS: Final = {
    "cell": {"kind", "cell"}, "netId": {"kind", "netId"},
    "player": {"kind", "playerId"}, "session": {"kind", "sessionId"},
    "rocket": {"kind", "rocketNetId", "padNetId"},
    "tech": {"kind", "techId"}, "schedule": {"kind", "scheduleId"},
    "inventory": {"kind"},
    "storage": {"kind", "storageNetId", "itemNetId"},
    "pickup": {"kind", "itemNetId", "targetCell"},
    "deconstruct": {"kind", "buildingNetId", "targetCell"},
    "sender": {"kind", "sender"},
    "dlc": {"kind", "dlcFamily", "prefab", "identity"},
}
SCENARIO_SELECTOR_KIND: Final = {
    **{name: "cell" for name in ("remote-dig", "animation", "building-lifecycle")},
    **{name: "netId" for name in (
        "motion", "effect", "priority", "building-config", "door", "uproot",
        "toggle", "entity-lifecycle")},
    "research": "tech", "schedule": "schedule", "inventory": "inventory",
    "storage": "storage", "pickup": "pickup", "deconstruct": "deconstruct",
    "chat": "sender", "cursor": "player", "dlc-runtime": "dlc",
    "rocket": "rocket", "reconnect-world-state": "session",
}


def validate_target_selector(scenario, selector):
    expected_kind = SCENARIO_SELECTOR_KIND.get(scenario)
    kind = selector.get("kind") if isinstance(selector, dict) else None
    if expected_kind is None or not isinstance(kind, str) or kind != expected_kind:
        return [f"{scenario}: selector kind must be {expected_kind}"]
    failures = []
    fields = SELECTOR_FIELDS.get(kind)
    if fields is None:
        return [f"{scenario}: selector kind is unknown"]
    if set(selector) != fields:
        failures.append(f"{scenario}: selector fields are not exact")
    for field in fields - {"kind"}:
        value = selector.get(field)
        valid_string = isinstance(value, str) and bool(value)
        valid_integer = isinstance(value, int) and not isinstance(value, bool) and value >= 0
        if not valid_string and not valid_integer:
            failures.append(f"{scenario}: {field} is invalid")
    return failures


def build_evidence_bundle(**values):
    logs = values["endpoint_logs"]
    records = values["typed_records"]
    run_id = values["run_id"]
    if set(logs) != {"host", "client"}:
        raise ValueError("evidence requires exact host and client log windows")
    if any(record.get("runId") != run_id for record in records):
        raise ValueError("typed evidence contains a record from another run")
    bundle = {
        "schemaVersion": 1, "runId": run_id,
        "inventoryDigest": values["inventory_digest"],
        "coverageDigest": values["coverage_digest"], "dllHash": values["dll_hash"],
        "endpointLogs": logs, "typedRecords": records, "results": values["results"],
        "failureFlow": values["failure_flow"],
    }
    if "execution_receipts" in values:
        bundle["executionReceipts"] = values["execution_receipts"]
    return bundle


def write_evidence_bundle_atomic(root, bundle):
    run_dir = Path(root) / bundle["runId"]
    run_dir.mkdir(parents=True, exist_ok=False)
    destination = run_dir / "evidence.json"
    handle, temporary = tempfile.mkstemp(prefix="evidence-", suffix=".tmp", dir=run_dir)
    try:
        with os.fdopen(handle, "w", encoding="utf-8") as stream:
            json.dump(bundle, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
        os.replace(temporary, destination)
    except BaseException:
        Path(temporary).unlink(missing_ok=True)
        run_dir.rmdir()
        raise
    return destination
