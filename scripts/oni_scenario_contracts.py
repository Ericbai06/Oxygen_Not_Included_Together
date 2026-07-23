import json
import hashlib
import math
import re
from typing import Final, TypedDict


INTEGRATION_PREFIX: Final = "[IntegrationEvidence] "
HASH_PATTERN = re.compile(r"sha256:[0-9a-f]{64}\Z")
SOAK_COMPARE = re.compile(
    r"\[SoakHash\]\[POST_KEYFRAME_COMPARE\] (?P<fields>[^\r\n]+)"
)
SOAK_COMPLETE = re.compile(
    r"\[SoakHash\]\[COMPLETE\](?P<fields>[^\r\n]*)"
)
SOAK_FAILURE = re.compile(
    r"\[SoakHash\]\[(?:ABORT|COMPLETE_WITH_DIVERGENCE|FAIL(?:URE)?|KEYFRAME_APPLY_FAILED)\]"
)
LIFECYCLE_FIELDS = ("missing", "unexpected", "tombstoned", "unassigned")

SCENARIOS = {
    "remote-dig": {
        "required": {
            "host": [
                ("target DigPlacer registered", r"Registered workable DigPlacer .* at cell {cell}(?:\D|$)"),
                ("presentation state sent", r"\[DuplicantPresentationBatch\]\[HOST_SEND\].*targetCell={cell}(?:\D|$)"),
            ],
            "client": [
                ("presentation state applied", r"\[RemoteDuplicantPresenter\]\[CLIENT_APPLY\].*targetCell={cell}(?:\D|$)"),
            ],
        },
        "forbidden": {
            "client": [
                ("Diggable.GetConversationTopic", r"Diggable\.GetConversationTopic"),
                ("StandardWorker.StartWork", r"(?:StandardWorker|worker)\.StartWork|StartWork\("),
                ("Worker.StartWork(Diggable)", r"Exception in: Worker\.StartWork\(Diggable\)"),
                ("originalDigElement", r"originalDigElement"),
                ("legacy working-state packet", r"StandardWorker_WorkingState_Packet"),
            ]
        },
    },
    "building-lifecycle": {
        "required": {
            "host": [
                ("target build completed on host", r"\[Host\] Sent BuildCompletePacket .* at cell {cell}(?:\D|$)")
            ],
            "client": [
                ("queued build materialized on client", r"\[BuildStatePacket\] Applied Queued(?:Replacement)? .* at cell {cell}(?:\D|$)"),
                ("native build finalized on client", r"\[BuildCompletePacket\] Finalized .* at cell {cell}(?:\D|$)"),
            ],
        },
        "forbidden": {
            "client": [
                ("Constructable.OnSpawn", r"Constructable\.OnSpawn"),
                ("SelectedElementsTags", r"SelectedElementsTags"),
                ("uninitialized building component", r"Error in .*Complete\.(PrimaryElement|Deconstructable|SimTemperatureTransfer)\.OnSpawn"),
                ("completed building NetId collision", r"NetId collision.*Complete vs .*Complete"),
            ]
        },
    },
    **{
        name: {}
        for name in (
            "research", "priority", "schedule", "building-config", "door",
            "uproot", "toggle", "inventory", "storage", "pickup",
            "deconstruct", "effect", "chat", "cursor", "animation",
            "motion", "entity-lifecycle", "dlc-runtime", "rocket",
            "reconnect-world-state",
        )
    },
}

SCENARIO_SCHEMAS: Final = {
    "remote-dig": ({"minionNetId": "int", "targetNetId": "int", "targetCell": "int"},
                   {"action": "str", "animation": "str", "tool": "str", "progress": "number"}),
    "building-lifecycle": ({"prefab": "str", "cell": "int", "netId": "int"},
                           {"lifecycleRevision": "int", "queued": "bool", "completed": "bool"}),
    "research": ({"techId": "str"}, {"revision": "int", "completed": "bool", "progress": "number"}),
    "priority": ({"targetNetId": "int"}, {"lifecycleRevision": "int", "baseRevision": "int", "stateRevision": "int", "priority": "int"}),
    "schedule": ({"scheduleId": "str"}, {"revision": "int", "blocks": "blocks"}),
    "building-config": ({"targetNetId": "int"}, {"lifecycleRevision": "int", "baseRevision": "int", "stateRevision": "int", "configKind": "str", "configValue": "number"}),
    "door": ({"targetNetId": "int"}, {"lifecycleRevision": "int", "stateRevision": "int", "control": "str"}),
    "uproot": ({"targetNetId": "int"}, {"lifecycleRevision": "int", "stateRevision": "int", "uprooted": "bool"}),
    "toggle": ({"targetNetId": "int"}, {"lifecycleRevision": "int", "stateRevision": "int", "toggled": "bool"}),
    "inventory": ({}, {"resources": "resources"}),
    "storage": ({"storageNetId": "int", "itemNetId": "int"}, {"membership": "bool", "amount": "number"}),
    "pickup": ({"itemNetId": "int", "targetCell": "int"}, {"action": "str", "tombstone": "bool"}),
    "deconstruct": ({"buildingNetId": "int", "targetCell": "int"}, {"action": "str", "tombstone": "bool"}),
    "effect": ({"minionNetId": "int"}, {"effectHash": "str", "active": "bool"}),
    "chat": ({"sender": "str"}, {"sequence": "int", "timestamp": "int", "messageHash": "hash"}),
    "cursor": ({"playerId": "str"}, {"connectionGeneration": "int", "worldPosition": "position", "viewPosition": "position", "dragState": "str", "buildState": "str"}),
    "animation": ({"minionNetId": "int", "targetNetId": "int", "targetCell": "int"},
                  {"action": "str", "animation": "str", "tool": "str", "progress": "number"}),
    "motion": ({"entityNetId": "int"}, {"tick": "int", "startPosition": "position", "endPosition": "position", "navigationState": "str", "motionRevision": "int"}),
    "entity-lifecycle": ({"netId": "int", "prefab": "str", "worldId": "int"}, {"lifecycleRevision": "int", "active": "bool", "tombstone": "bool"}),
    "dlc-runtime": ({"dlcFamily": "dlc", "prefab": "str", "identity": "str"}, {"stateMachineState": "str", "admissionGeneration": "int"}),
    "rocket": ({"rocketNetId": "int", "padNetId": "int"}, {"destination": "str", "craftPhase": "str", "settingsRevision": "int"}),
    "reconnect-world-state": ({"peerId": "str"}, {"connectionGeneration": "int", "snapshotGeneration": "int", "grid": "summary", "entity": "summary", "world": "summary", "storage": "summary", "clusterRocket": "summary"}),
}

ENVELOPE_FIELDS: Final = {
    "schemaVersion", "runId", "dllHash", "scenario", "entryId", "role",
    "sessionEpoch", "connectionGeneration", "snapshotGeneration", "phase",
    "revisionDomain", "revision", "sequence", "target", "state", "stateHash",
}
ACTION_FIELDS: Final = {"actionGeneration", "actionCorrelation", "actionSequence"}
ACTION_ENVELOPE_FIELDS: Final = ENVELOPE_FIELDS | ACTION_FIELDS
PHASES: Final = {
    "host-submit", "client-apply", "client-original-blocked", "revision-accepted",
    "revision-duplicate", "revision-out-of-order", "final-state", "post-reconnect-state",
}
DLCS: Final = {"Aquatic", "Bionic", "Frosty", "Prehistoric", "SpacedOut", "Common"}


class ContractVerdict(TypedDict):
    passed: bool
    failures: list[str]


def _verdict_failures(verdict: ContractVerdict) -> list[str]:
    return verdict["failures"]


def _is_kind(value, kind):
    if kind == "int":
        return isinstance(value, int) and not isinstance(value, bool) and value >= 0
    if kind == "number":
        return (isinstance(value, (int, float)) and not isinstance(value, bool)
                and math.isfinite(value))
    if kind == "str":
        return isinstance(value, str) and bool(value)
    if kind == "bool":
        return isinstance(value, bool)
    if kind == "hash":
        return isinstance(value, str) and HASH_PATTERN.fullmatch(value) is not None
    if kind == "dlc":
        return value in DLCS
    if kind == "position":
        return isinstance(value, list) and len(value) == 2 and all(_is_kind(item, "number") for item in value)
    if kind == "summary":
        return _exact_shape(value, {"count": "int", "hash": "hash"})
    if kind == "blocks":
        return isinstance(value, list) and all(_exact_shape(item, {"start": "int", "group": "str"}) for item in value)
    if kind == "resources":
        return _valid_resources(value)
    return False


def _exact_shape(value, schema):
    return (isinstance(value, dict) and set(value) == set(schema)
            and all(_is_kind(value[key], kind) for key, kind in schema.items()))


def _valid_resources(value):
    if not isinstance(value, list) or not all(
            _exact_shape(item, {"tag": "str", "amount": "number"}) for item in value):
        return False
    tags = [item["tag"] for item in value]
    return tags == sorted(set(tags))


def _state_hash(state):
    canonical = json.dumps(state, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return "sha256:" + hashlib.sha256(canonical.encode()).hexdigest()


def canonical_envelope_hash(record):
    failures = validate_typed_envelope(record)
    if failures:
        raise ValueError("; ".join(failures))
    canonical = json.dumps(record, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return "sha256:" + hashlib.sha256(canonical.encode()).hexdigest()


def _valid_action_admission(record):
    correlation = record["actionCorrelation"]
    return (_is_kind(record["actionGeneration"], "int")
            and record["actionGeneration"] > 0
            and isinstance(correlation, str) and 0 < len(correlation) <= 128
            and all(character.isalnum() or character in "-_." for character in correlation)
            and _is_kind(record["actionSequence"], "int")
            and record["actionSequence"] > 0)


def validate_typed_envelope(record):
    failures = []
    if not isinstance(record, dict):
        return ["envelope must be an object"]
    fields = set(record)
    action = not fields.isdisjoint(ACTION_FIELDS)
    expected_fields = ACTION_ENVELOPE_FIELDS if action else ENVELOPE_FIELDS
    if fields != expected_fields:
        return ["envelope fields are not exact"]
    scenario = record["scenario"]
    if scenario not in SCENARIO_SCHEMAS:
        return ["scenario is not in catalog"]
    target_schema, state_schema = SCENARIO_SCHEMAS[scenario]
    checks = (
        (record["schemaVersion"] == 1, "schemaVersion must be 1"),
        (isinstance(record["runId"], str) and bool(record["runId"]), "runId is invalid"),
        (_is_kind(record["dllHash"], "hash"), "dllHash is invalid"),
        (isinstance(record["entryId"], str) and record["entryId"].startswith("sync:"), "entryId is invalid"),
        (record["role"] in {"host", "client"}, "role is invalid"),
        (record["phase"] in PHASES, "phase is invalid"),
        (record["revisionDomain"] == scenario, "revisionDomain must match scenario"),
    )
    failures.extend(message for valid, message in checks if not valid)
    for field in ("sessionEpoch", "connectionGeneration", "snapshotGeneration", "revision", "sequence"):
        if not _is_kind(record[field], "int"):
            failures.append(f"{field} must be a non-negative integer")
    if not _exact_shape(record["target"], target_schema):
        failures.append("target fields or types are invalid")
    if not _exact_shape(record["state"], state_schema):
        failures.append("state fields or types are invalid")
    if not _is_kind(record["stateHash"], "hash") or record["stateHash"] != _state_hash(record["state"]):
        failures.append("stateHash does not match canonical state")
    if action and not _valid_action_admission(record):
        failures.append("action admission fields are invalid")
    return failures


def parse_typed_evidence_line(line):
    if "\n" in line or "\r" in line or not line.startswith(INTEGRATION_PREFIX):
        raise ValueError("typed evidence must be one prefixed JSON line")
    try:
        record = json.loads(
            line[len(INTEGRATION_PREFIX):],
            parse_constant=_reject_non_finite_constant,
        )
    except json.JSONDecodeError as error:
        raise ValueError("typed evidence JSON is invalid") from error
    failures = validate_typed_envelope(record)
    if failures:
        raise ValueError("; ".join(failures))
    return record


def _reject_non_finite_constant(value):
    raise ValueError(f"non-standard JSON numeric constant is forbidden: {value}")


def _typed_lines(log):
    return [line for line in log.splitlines() if line.startswith("[IntegrationEvidence]")]


def parse_scenario_evidence(scenario_name, logs):
    records = []
    failures = []
    for log_role in ("host", "client"):
        for line in _typed_lines(logs.get(log_role, "")):
            try:
                record = parse_typed_evidence_line(line)
            except ValueError as error:
                failures.append(f"{log_role}: {error}")
                continue
            if record["scenario"] == scenario_name:
                records.append((log_role, record))
    if not records and not failures:
        return None
    return {"scenario": scenario_name, "records": records, "failures": failures}


def _phase_map(records, failures):
    phases = {}
    sequences = {"host": [], "client": []}
    host_only = {"host-submit"}
    client_only = {
        "revision-accepted", "revision-duplicate", "revision-out-of-order",
        "client-apply", "client-original-blocked",
    }
    for log_role, record in records:
        if record["role"] != log_role:
            failures.append(f"{log_role}: envelope role differs from log role")
        if record["phase"] in host_only and log_role != "host":
            failures.append(f"{record['phase']} must be emitted by host")
        if record["phase"] in client_only and log_role != "client":
            failures.append(f"{record['phase']} must be emitted by client")
        if record["phase"] == "post-reconnect-state" and record["scenario"] != "reconnect-world-state":
            failures.append("post-reconnect-state is only valid for reconnect-world-state")
        sequences[log_role].append(record["sequence"])
        key = (log_role, record["phase"])
        if key in phases:
            failures.append(f"duplicate phase {log_role}:{record['phase']}")
        phases[key] = record
    for role, values in sequences.items():
        if values != sorted(values) or len(values) != len(set(values)):
            failures.append(f"{role}: sequence is not strictly increasing")
    return phases


def _causal_failures(records, phases):
    failures = []
    required = (("host", "host-submit"), ("client", "revision-accepted"),
                ("client", "revision-duplicate"), ("client", "revision-out-of-order"),
                ("host", "final-state"), ("client", "client-apply"),
                ("client", "client-original-blocked"), ("client", "final-state"))
    failures.extend(f"missing phase {role}:{phase}" for role, phase in required if (role, phase) not in phases)
    if failures:
        return failures
    accepted = phases[("client", "revision-accepted")]["revision"]
    revisions = (phases[("host", "host-submit")]["revision"],
                 phases[("client", "revision-duplicate")]["revision"],
                 phases[("client", "client-apply")]["revision"])
    if any(revision != accepted for revision in revisions):
        failures.append("submitted, accepted, duplicate and applied revisions differ")
    if phases[("client", "revision-out-of-order")]["revision"] >= accepted:
        failures.append("out-of-order revision was not older")
    failures.extend(_provenance_failures(phases))
    failures.extend(_action_causal_failures(records, phases))
    identity = ("runId", "dllHash", "sessionEpoch", "connectionGeneration",
                "snapshotGeneration", "revisionDomain", "target")
    baseline = records[0][1]
    for _, record in records[1:]:
        failures.extend(f"{field} differs across causal records" for field in identity
                        if record[field] != baseline[field])
    return failures


def _action_causal_failures(records, phases):
    action_records = [record for _, record in records if "actionGeneration" in record]
    if not action_records:
        return []
    if len(action_records) != len(records):
        return ["action admission is missing from part of the causal chain"]
    failures = []
    if len({record["actionGeneration"] for record in action_records}) != 1:
        failures.append("actionGeneration differs across causal records")
    if len({record["actionCorrelation"] for record in action_records}) != 1:
        failures.append("actionCorrelation differs across causal records")
    accepted = phases[("client", "revision-accepted")]["actionSequence"]
    for role, phase in (("host", "host-submit"), ("client", "revision-accepted"),
                        ("client", "client-apply"), ("client", "revision-duplicate"),
                        ("client", "client-original-blocked"),
                        ("host", "final-state"), ("client", "final-state")):
        if phases[(role, phase)]["actionSequence"] != accepted:
            failures.append(f"{phase} actionSequence differs from accepted")
    if phases[("client", "revision-out-of-order")]["actionSequence"] != accepted - 1:
        failures.append("out-of-order actionSequence is not accepted minus one")
    return failures


def _provenance_failures(phases):
    failures = []
    host_entry = phases[("host", "host-submit")]["entryId"]
    receiver_phases = (
        "revision-accepted", "revision-duplicate", "revision-out-of-order",
        "client-apply", "client-original-blocked", "final-state",
    )
    for phase in receiver_phases:
        if phases[("client", phase)]["entryId"] == host_entry:
            failures.append(f"client {phase} reused host submit entry")
    gate_entries = {
        phases[("client", phase)]["entryId"]
        for phase in ("revision-accepted", "revision-duplicate", "revision-out-of-order")
    }
    if len(gate_entries) != 1:
        failures.append("client revision outcomes do not share one gate entry")
    return failures


def _final_state_failures(phases, phase="final-state"):
    host = phases.get(("host", phase))
    client = phases.get(("client", phase))
    if host is None or client is None:
        return [f"{phase} host/client records are missing"]
    failures = []
    if host["state"] != client["state"]:
        failures.append(f"{phase} host/client states differ")
    if host["stateHash"] != client["stateHash"]:
        failures.append(f"{phase} host/client hashes differ")
    return failures


def evaluate_scenario_contract(scenario_name, evidence) -> ContractVerdict:
    failures = list(evidence.get("failures", []))
    if scenario_name not in SCENARIO_SCHEMAS or evidence.get("scenario") != scenario_name:
        failures.append("scenario does not match catalog entry")
    records = evidence.get("records", [])
    if not records:
        failures.append("typed IntegrationEvidence records are missing")
        return {"passed": False, "failures": failures}
    phases = _phase_map(records, failures)
    failures.extend(_causal_failures(records, phases))
    failures.extend(_final_state_failures(phases))
    if scenario_name == "reconnect-world-state":
        failures.extend(_final_state_failures(phases, "post-reconnect-state"))
        final = phases.get(("host", "final-state"))
        post = phases.get(("host", "post-reconnect-state"))
        if final is not None and post is not None and final["state"] != post["state"]:
            failures.append("post-reconnect state differs from final state")
    return {"passed": not failures, "failures": failures}


def _safety_failures(scenario_name, logs, expected_cell):
    missing = []
    forbidden = []
    scenario = SCENARIOS[scenario_name]
    evidence = parse_scenario_evidence(scenario_name, logs)
    records = [] if evidence is None else evidence.get("records", [])
    target_field = "cell" if scenario_name == "building-lifecycle" else "targetCell"
    resolved_cell = expected_cell
    if resolved_cell is None and records and scenario.get("required"):
        resolved_cell = records[0][1]["target"].get(target_field)
    for role, checks in scenario.get("required", {}).items():
        for label, pattern in checks:
            required_pattern = pattern.format(cell=re.escape(str(resolved_cell)))
            if re.search(required_pattern, logs.get(role, ""), re.MULTILINE) is None:
                missing.append(label)
    if expected_cell is not None:
        if not records or any(record["target"].get(target_field) != expected_cell for _, record in records):
            missing.append(f"expected {target_field}={expected_cell}")
    for role, checks in scenario.get("forbidden", {}).items():
        for label, pattern in checks:
            if re.search(pattern, logs.get(role, ""), re.MULTILINE):
                forbidden.append(label)
    return missing, forbidden


def evaluate(scenario_name, logs, expected_cell):
    evidence = parse_scenario_evidence(scenario_name, logs)
    contract: ContractVerdict
    if evidence is None:
        contract = {"passed": False, "failures": ["typed IntegrationEvidence records are missing"]}
    else:
        contract = evaluate_scenario_contract(scenario_name, evidence)
    missing, forbidden = _safety_failures(scenario_name, logs, expected_cell)
    failures = _verdict_failures(contract) + missing
    return {"passed": contract["passed"] and not missing and not forbidden,
            "failures": failures, "missing": missing, "forbidden": forbidden}


def parse_fields(text):
    fields = {}
    for item in text.split(";"):
        key, separator, value = item.strip().partition("=")
        if separator:
            fields[key] = value
    return fields


def evaluate_soak_post_keyframes(records, completed=True, failed=False):
    failures = []
    if len(records) != 21:
        failures.append(f"expected 21 post-keyframe records, got {len(records)}")
    if not completed:
        failures.append("soak completion marker is missing")
    if failed:
        failures.append("soak failure marker was observed")
    for index, record in enumerate(records, start=1):
        for domain in ("time", "grid", "entity", "world", "storage", "clusterRocket"):
            if record.get(domain) is not True:
                failures.append(f"record {index}: {domain} differs")
        lifecycle = record.get("lifecycle", {})
        for field in LIFECYCLE_FIELDS:
            if lifecycle.get(field) != 0:
                failures.append(f"record {index}: lifecycle {field} is not zero")
    return {"passed": not failures, "recordCount": len(records), "failures": failures}


def evaluate_soak_log(text):
    starts = [match.start() for match in re.finditer(r"\[SoakHash\]\[START\]", text)]
    run_text = text[starts[-1]:] if starts else text
    records = []
    for match in SOAK_COMPARE.finditer(run_text):
        fields = parse_fields(match.group("fields").replace(" ", ";"))
        raw_lifecycle = re.search(r"rawLifecycle=\(([^)]*)\)", match.group("fields"))
        lifecycle_fields = parse_fields(raw_lifecycle.group(1).replace(" ", ";")) if raw_lifecycle else {}
        records.append({
            "time": fields.get("timeEqual", "").lower() == "true",
            "grid": fields.get("gridEqual", "").lower() == "true",
            "entity": fields.get("entityEqual", "").lower() == "true",
            "world": fields.get("worldEqual", "").lower() == "true",
            "storage": fields.get("storageEqual", "").lower() == "true",
            "clusterRocket": fields.get("clusterRocketEqual", "").lower() == "true",
            "lifecycle": {
                field: int(lifecycle_fields[field]) if lifecycle_fields.get(field, "").isdigit() else None
                for field in LIFECYCLE_FIELDS
            },
        })
    complete_matches = list(SOAK_COMPLETE.finditer(run_text))
    complete_fields = (
        parse_fields(complete_matches[-1].group("fields").replace(" ", ";"))
        if complete_matches else {}
    )
    terminal_failed = bool(complete_matches) and any(
        complete_fields.get(key, "").lower() != expected
        for key, expected in {
            "elapsedSimTicks": "37800", "rawSamples": "21", "postSamples": "21",
            "rawMismatchSeen": "false", "postMismatchSeen": "false",
            "keyframeApplyFailureSeen": "false", "rawPreKeyframeEqual": "true",
            "postKeyframeEqual": "true",
        }.items()
    )
    return evaluate_soak_post_keyframes(
        records,
        completed=bool(complete_matches),
        failed=SOAK_FAILURE.search(run_text) is not None or terminal_failed,
    )
