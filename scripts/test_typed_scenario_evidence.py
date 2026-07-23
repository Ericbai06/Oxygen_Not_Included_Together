import copy
import hashlib
import json
import unittest
from typing import Final, Literal, TypeAlias, TypedDict, assert_never

import scripts.oni_scenario_contracts as contracts


JsonScalar: TypeAlias = str | int | float | bool | None
JsonValue: TypeAlias = JsonScalar | list["JsonValue"] | dict[str, "JsonValue"]
JsonObject: TypeAlias = dict[str, JsonValue]
IdentityField: TypeAlias = Literal[
    "runId", "dllHash", "sessionEpoch", "connectionGeneration", "snapshotGeneration",
    "revisionDomain", "target", "role",
]


class TypedEvidenceRecord(TypedDict):
    schemaVersion: int; runId: str; dllHash: str
    scenario: str; entryId: str; role: str
    sessionEpoch: int; connectionGeneration: int; snapshotGeneration: int
    phase: str; revisionDomain: str; revision: int; sequence: int
    target: JsonObject; state: JsonObject; stateHash: str

SCENARIOS: Final[tuple[str, ...]] = (
    "remote-dig", "building-lifecycle", "research", "priority", "schedule",
    "building-config", "door", "uproot", "toggle", "inventory", "storage",
    "pickup", "deconstruct", "effect", "chat", "cursor", "animation", "motion",
    "entity-lifecycle", "dlc-runtime", "rocket", "reconnect-world-state",
)

SAMPLES: Final[dict[str, tuple[JsonObject, JsonObject]]] = {
    "remote-dig": ({"minionNetId": 7, "targetNetId": 8, "targetCell": 42},
                   {"action": "Digging", "animation": "dig_loop", "tool": "DigTool", "progress": 0.5}),
    "animation": ({"minionNetId": 7, "targetNetId": 8, "targetCell": 42},
                  {"action": "Digging", "animation": "dig_loop", "tool": "DigTool", "progress": 0.5}),
    "motion": ({"entityNetId": 7},
        {"tick": 120, "startPosition": [1.0, 2.0], "endPosition": [3.0, 4.0],
         "navigationState": "Moving", "motionRevision": 4}),
    "effect": ({"minionNetId": 7}, {"effectHash": "effect:well-fed", "active": True}),
    "building-lifecycle": ({"prefab": "Tile", "cell": 42, "netId": 9},
                           {"lifecycleRevision": 4, "queued": True, "completed": True}),
    "priority": ({"targetNetId": 9},
                 {"lifecycleRevision": 4, "baseRevision": 2, "stateRevision": 3, "priority": 7}),
    "building-config": ({"targetNetId": 9},
                        {"lifecycleRevision": 4, "baseRevision": 2, "stateRevision": 3,
                         "configKind": "Slider", "configValue": 0.75}),
    "door": ({"targetNetId": 9}, {"lifecycleRevision": 4, "stateRevision": 3, "control": "Open"}),
    "uproot": ({"targetNetId": 9}, {"lifecycleRevision": 4, "stateRevision": 3, "uprooted": True}),
    "toggle": ({"targetNetId": 9}, {"lifecycleRevision": 4, "stateRevision": 3, "toggled": True}),
    "research": ({"techId": "AdvancedPower"}, {"revision": 3, "completed": False, "progress": 0.5}),
    "schedule": ({"scheduleId": "schedule:day"},
                 {"revision": 3, "blocks": [{"start": 0, "group": "Work"}, {"start": 6000, "group": "Sleep"}]}),
    "inventory": ({}, {"resources": [{"tag": "Algae", "amount": 10.0}, {"tag": "Water", "amount": 20.0}]}),
    "storage": ({"storageNetId": 10, "itemNetId": 11}, {"membership": True, "amount": 5.0}),
    "pickup": ({"itemNetId": 11, "targetCell": 42}, {"action": "Pickup", "tombstone": False}),
    "deconstruct": ({"buildingNetId": 12, "targetCell": 42}, {"action": "Deconstruct", "tombstone": True}),
    "chat": ({"sender": "player:host"}, {"sequence": 4, "timestamp": 1234567890, "messageHash": "sha256:" + "2" * 64}),
    "cursor": ({"playerId": "player:host"},
               {"connectionGeneration": 2, "worldPosition": [1.0, 2.0], "viewPosition": [3.0, 4.0],
                "dragState": "Idle", "buildState": "None"}),
    "entity-lifecycle": ({"netId": 13, "prefab": "Hatch", "worldId": 1},
                         {"lifecycleRevision": 4, "active": True, "tombstone": False}),
    "dlc-runtime": ({"dlcFamily": "SpacedOut", "prefab": "ScoutRover", "identity": "entity:13"},
                    {"stateMachineState": "idle", "admissionGeneration": 2}),
    "rocket": ({"rocketNetId": 14, "padNetId": 15},
               {"destination": "cluster:3", "craftPhase": "Boarding", "settingsRevision": 4}),
    "reconnect-world-state": (
        {"peerId": "player:client"},
        {"connectionGeneration": 2, "snapshotGeneration": 3,
         "grid": {"count": 100, "hash": "sha256:" + "3" * 64},
         "entity": {"count": 20, "hash": "sha256:" + "4" * 64},
         "world": {"count": 2, "hash": "sha256:" + "5" * 64},
         "storage": {"count": 8, "hash": "sha256:" + "6" * 64},
         "clusterRocket": {"count": 1, "hash": "sha256:" + "7" * 64}},
    ),
}


def hash_state(state: JsonObject) -> str:
    canonical = json.dumps(state, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return "sha256:" + hashlib.sha256(canonical.encode()).hexdigest()


def envelope(scenario: str, role: str = "host", phase: str = "final-state",
             revision: int = 3, sequence: int = 1) -> TypedEvidenceRecord:
    target, state = SAMPLES[scenario]
    state_copy = copy.deepcopy(state)
    return {
        "schemaVersion": 1, "runId": "run:typed-evidence", "dllHash": "sha256:" + "1" * 64,
        "scenario": scenario, "entryId": "sync:test:" + scenario, "role": role,
        "sessionEpoch": 8, "connectionGeneration": 2, "snapshotGeneration": 3,
        "phase": phase, "revisionDomain": scenario, "revision": revision, "sequence": sequence,
        "target": copy.deepcopy(target), "state": state_copy, "stateHash": hash_state(state_copy),
    }


def line(record: TypedEvidenceRecord) -> str:
    return "[IntegrationEvidence] " + json.dumps(record, separators=(",", ":"), sort_keys=True)


def causal_logs(scenario: str) -> dict[str, str]:
    host_phases = (
        ("host-submit", 3, "sync:host-send"),
        ("final-state", 3, "sync:host-observer"),
    )
    client_phases = (
        ("revision-accepted", 3, "sync:client-revision-gate"),
        ("client-apply", 3, "sync:client-dispatch"),
        ("revision-duplicate", 3, "sync:client-revision-gate"),
        ("revision-out-of-order", 2, "sync:client-revision-gate"),
        ("client-original-blocked", 3, "sync:client-harmony"),
        ("final-state", 3, "sync:client-observer"),
    )
    host = [phase_line(scenario, "host", spec, index)
            for index, spec in enumerate(host_phases, 1)]
    client = [phase_line(scenario, "client", spec, index)
              for index, spec in enumerate(client_phases, 1)]
    return {"host": "\n".join(host), "client": "\n".join(client)}


def phase_line(scenario: str, role: str, spec: tuple[str, int, str], sequence: int) -> str:
    phase, revision, entry_id = spec
    record = envelope(scenario, role, phase, revision, sequence)
    record["entryId"] = entry_id
    return line(record)


def causal_logs_with_required_native(scenario: str) -> dict[str, str]:
    logs = causal_logs(scenario)
    if scenario == "remote-dig":
        logs["host"] += (
            "\nRegistered workable DigPlacer with id: 7 for workable type Diggable at cell 42\n"
            "[DuplicantPresentationBatch][HOST_SEND] revision=8 netId=7 "
            "action=Digging targetCell=42\n")
        logs["client"] += ("\n[RemoteDuplicantPresenter][CLIENT_APPLY] revision=8 "
                           "netId=7 action=Digging targetCell=42\n")
    if scenario == "building-lifecycle":
        logs["host"] += "\n[Host] Sent BuildCompletePacket for Tile at cell 42\n"
        logs["client"] += ("\n[BuildStatePacket] Applied Queued Tile at cell 42\n"
                           "[BuildCompletePacket] Finalized Tile at cell 42\n")
    return logs


def causal_logs_with_client_mutation(field: IdentityField) -> dict[str, str]:
    phases = (
        ("revision-accepted", 3, "sync:client-revision-gate"),
        ("client-apply", 3, "sync:client-dispatch"),
        ("revision-duplicate", 3, "sync:client-revision-gate"),
        ("revision-out-of-order", 2, "sync:client-revision-gate"),
        ("client-original-blocked", 3, "sync:client-harmony"),
        ("final-state", 3, "sync:client-observer"),
    )
    records = [envelope("door", "client", phase, revision, index)
               for index, (phase, revision, _) in enumerate(phases, 1)]
    for record, (_, _, entry_id) in zip(records, phases):
        record["entryId"] = entry_id
    for record in records:
        match field:
            case "runId": record["runId"] = "run:other"
            case "dllHash": record["dllHash"] = "sha256:" + "9" * 64
            case "sessionEpoch": record["sessionEpoch"] = 9
            case "connectionGeneration": record["connectionGeneration"] = 3
            case "snapshotGeneration": record["snapshotGeneration"] = 4
            case "revisionDomain": record["revisionDomain"] = "other-domain"
            case "target": record["target"] = {"targetNetId": 99}
            case "role": record["role"] = "host"
            case unreachable: assert_never(unreachable)
    logs = causal_logs("door")
    logs["client"] = "\n".join(line(record) for record in records)
    return logs


def legacy_generic_logs() -> dict[str, str]:
    def legacy(phase: str, revision: int, state: str = "scenario-final") -> str:
        applied = "0" if phase in ("revision-duplicate", "revision-out-of-order") else "1"
        return ("[IntegrationEvidence] scenario=door;phase=" + phase
                + ";revision=" + str(revision) + ";applied=" + applied
                + ";state=" + state + ";hash=sha256:legacy")
    return {
        "host": "\n".join((legacy("host-submit", 3), legacy("revision-accepted", 3),
                            legacy("revision-duplicate", 3), legacy("revision-out-of-order", 2),
                            legacy("final-state", 3))),
        "client": "\n".join((legacy("client-apply", 3), legacy("client-original-blocked", 3),
                              legacy("final-state", 3))),
    }


class TypedSchemaTests(unittest.TestCase):
    def test_catalog_is_exact_22_scenarios(self) -> None:
        self.assertEqual(SCENARIOS, tuple(contracts.SCENARIO_SCHEMAS))

    def test_each_scenario_accepts_its_exact_typed_target_and_state(self) -> None:
        validator = contracts.validate_typed_envelope
        for scenario in SCENARIOS:
            with self.subTest(scenario=scenario):
                self.assertEqual([], validator(envelope(scenario)))

    def test_each_scenario_rejects_missing_wrong_type_and_extra_fields(self) -> None:
        validator = contracts.validate_typed_envelope
        for scenario in SCENARIOS:
            for side in ("target", "state"):
                record = envelope(scenario)
                for key in tuple(record[side]):
                    for mutation in ("missing", "wrong-type"):
                        mutated = envelope(scenario)
                        if mutation == "missing":
                            del mutated[side][key]
                        else:
                            mutated[side][key] = None
                        if side == "state":
                            mutated["stateHash"] = hash_state(mutated["state"])
                        with self.subTest(scenario=scenario, side=side, field=key, mutation=mutation):
                            self.assertNotEqual([], validator(mutated))
                record[side]["unexpected"] = True
                if side == "state":
                    record["stateHash"] = hash_state(record["state"])
                with self.subTest(scenario=scenario, side=side, mutation="extra"):
                    self.assertNotEqual([], validator(record))

    def test_inventory_requires_sorted_unique_resource_tags(self) -> None:
        validator = contracts.validate_typed_envelope
        cases: tuple[JsonValue, ...] = (
            [{"tag": "Water", "amount": 20.0}, {"tag": "Algae", "amount": 10.0}],
            [{"tag": "Algae", "amount": 10.0}, {"tag": "Algae", "amount": 20.0}],
        )
        for resources in cases:
            record = envelope("inventory")
            record["state"]["resources"] = resources
            record["stateHash"] = hash_state(record["state"])
            with self.subTest(resources=resources):
                self.assertNotEqual([], validator(record))

    def test_nested_schedule_inventory_and_reconnect_fields_are_exact(self) -> None:
        validator = contracts.validate_typed_envelope
        cases: tuple[tuple[str, str, JsonValue], ...] = (
            ("schedule", "blocks", [{"start": 0}]),
            ("schedule", "blocks", [{"start": "zero", "group": "Work"}]),
            ("schedule", "blocks", [{"start": 0, "group": "Work", "unexpected": True}]),
            ("inventory", "resources", [{"tag": "Algae"}]),
            ("inventory", "resources", [{"tag": "Algae", "amount": "ten"}]),
            ("inventory", "resources", [{"tag": "Algae", "amount": 10.0, "unexpected": True}]),
            ("reconnect-world-state", "grid", {"count": 100}),
            ("reconnect-world-state", "grid", {"count": "many", "hash": "sha256:" + "3" * 64}),
            ("reconnect-world-state", "grid", {"count": 100, "hash": "sha256:" + "3" * 64, "unexpected": True}),
        )
        for scenario, field, invalid in cases:
            record = envelope(scenario)
            record["state"][field] = invalid
            record["stateHash"] = hash_state(record["state"])
            with self.subTest(scenario=scenario, field=field, invalid=invalid):
                self.assertNotEqual([], validator(record))

    def test_dlc_family_is_closed_enum(self) -> None:
        validator = contracts.validate_typed_envelope
        record = envelope("dlc-runtime")
        record["target"]["dlcFamily"] = "UnknownPack"
        self.assertNotEqual([], validator(record))

    def test_numeric_fields_reject_nan_and_infinity(self) -> None:
        for value in (float("nan"), float("inf"), float("-inf")):
            record = envelope("remote-dig")
            record["state"]["progress"] = value
            record["stateHash"] = hash_state(record["state"])
            with self.subTest(value=value):
                self.assertNotEqual([], contracts.validate_typed_envelope(record))


class TypedEnvelopeTests(unittest.TestCase):
    def test_parser_accepts_one_prefixed_json_object(self) -> None:
        record = envelope("door")
        self.assertEqual(record, contracts.parse_typed_evidence_line(line(record)))

    def test_parser_rejects_old_grammar_generic_state_and_multiple_lines(self) -> None:
        parser = contracts.parse_typed_evidence_line
        invalid = (
            "[IntegrationEvidence] scenario=door;phase=final-state;revision=3;state=open",
            line(envelope("door")) + "\n" + line(envelope("door")),
        )
        for value in invalid:
            with self.subTest(value=value[:80]), self.assertRaises(ValueError):
                parser(value)

    def test_parser_rejects_non_finite_json_numbers(self) -> None:
        for value in (float("nan"), float("inf"), float("-inf")):
            record = envelope("remote-dig")
            record["state"]["progress"] = value
            record["stateHash"] = hash_state(record["state"])
            with self.subTest(value=value), self.assertRaises(ValueError):
                contracts.parse_typed_evidence_line(line(record))

    def test_envelope_rejects_hash_identity_phase_and_nonnegative_violations(self) -> None:
        validator = contracts.validate_typed_envelope
        mutations: dict[str, JsonValue] = {
            "dllHash": "not-a-hash", "entryId": "unstable", "role": "server",
            "phase": "anything", "scenario": "unknown", "revision": -1,
            "sequence": -1, "sessionEpoch": -1, "connectionGeneration": -1,
            "snapshotGeneration": -1, "stateHash": "sha256:" + "0" * 64,
        }
        for field, value in mutations.items():
            record = envelope("door")
            record[field] = value
            with self.subTest(field=field):
                self.assertNotEqual([], validator(record))

    def test_envelope_rejects_missing_required_field(self) -> None:
        validator = contracts.validate_typed_envelope
        record = json.loads(json.dumps(envelope("door")))
        record.pop("runId")
        self.assertNotEqual([], validator(record))


class TypedCausalityTests(unittest.TestCase):
    def test_complete_typed_remote_dig_causality_passes(self) -> None:
        logs = causal_logs_with_required_native("remote-dig")
        self.assertTrue(contracts.evaluate("remote-dig", logs, 42)["passed"])

    def test_each_required_native_side_effect_is_mandatory(self) -> None:
        cases = (
            ("remote-dig", "host", "Registered workable DigPlacer", "target DigPlacer registered"),
            ("remote-dig", "host", "[DuplicantPresentationBatch][HOST_SEND]", "presentation state sent"),
            ("remote-dig", "client", "[RemoteDuplicantPresenter][CLIENT_APPLY]", "presentation state applied"),
            ("building-lifecycle", "host", "[Host] Sent BuildCompletePacket", "target build completed on host"),
            ("building-lifecycle", "client", "[BuildStatePacket] Applied Queued", "queued build materialized on client"),
            ("building-lifecycle", "client", "[BuildCompletePacket] Finalized", "native build finalized on client"),
        )
        for scenario, role, fragment, label in cases:
            logs = causal_logs_with_required_native(scenario)
            self.assertTrue(contracts.evaluate(scenario, logs, 42)["passed"])
            logs[role] = "\n".join(value for value in logs[role].splitlines()
                                     if fragment not in value)
            verdict = contracts.evaluate(scenario, logs, 42)
            with self.subTest(scenario=scenario, role=role, label=label):
                self.assertFalse(verdict["passed"])
                self.assertEqual([label], verdict["missing"])

    def test_structured_evidence_cannot_bypass_target_or_forbidden_checks(self) -> None:
        logs = causal_logs("remote-dig")
        logs["host"] = logs["host"].replace('"targetCell":42', '"targetCell":41')
        logs["client"] = (logs["client"].replace('"targetCell":42', '"targetCell":41')
                          + "\nDiggable.GetConversationTopic\n")
        verdict = contracts.evaluate("remote-dig", logs, 42)
        rendered = json.dumps(verdict, sort_keys=True)
        self.assertFalse(verdict["passed"])
        self.assertIn("targetCell", rendered)
        self.assertIn("Diggable.GetConversationTopic", rendered)

    def test_old_generic_value_evidence_never_passes(self) -> None:
        self.assertFalse(contracts.evaluate("door", legacy_generic_logs(), None)["passed"])

    def test_causal_records_must_share_identity_and_generations(self) -> None:
        fields: tuple[IdentityField, ...] = (
            "runId", "dllHash", "sessionEpoch", "connectionGeneration",
            "snapshotGeneration", "revisionDomain", "target", "role",
        )
        for field in fields:
            with self.subTest(field=field):
                verdict = contracts.evaluate("door", causal_logs_with_client_mutation(field), None)
                self.assertFalse(verdict["passed"])


if __name__ == "__main__":
    unittest.main()
