import copy
import json
import unittest
from collections.abc import Callable
from itertools import combinations
from typing import Final

import scripts.oni_scenario_contracts as contracts
from scripts.test_typed_scenario_evidence import (
    JsonObject,
    TypedEvidenceRecord,
    causal_logs,
    envelope,
    hash_state,
)


ACTION_FIELDS: Final[tuple[str, ...]] = (
    "actionGeneration", "actionCorrelation", "actionSequence",
)
BASE_FIELDS: Final[frozenset[str]] = frozenset({
    "schemaVersion", "runId", "dllHash", "scenario", "entryId", "role",
    "sessionEpoch", "connectionGeneration", "snapshotGeneration", "phase",
    "revisionDomain", "revision", "sequence", "target", "state", "stateHash",
})
ACTION_ENVELOPE_FIELDS: Final[frozenset[str]] = (
    BASE_FIELDS | frozenset(ACTION_FIELDS)
)


def json_line(record: JsonObject | TypedEvidenceRecord) -> str:
    return "[IntegrationEvidence] " + json.dumps(
        record, separators=(",", ":"), sort_keys=True,
    )


def action_record(phase: str, role: str = "client") -> JsonObject:
    record: JsonObject = json.loads(json.dumps(envelope("door", role, phase, 3, 1)))
    record.update({
        "actionGeneration": 7,
        "actionCorrelation": "corr-action-7",
        "actionSequence": 10 if phase == "revision-out-of-order" else 11,
    })
    return record


def action_logs() -> dict[str, str]:
    logs = causal_logs("door")
    result: dict[str, str] = {}
    for role, text in logs.items():
        records = []
        for raw in text.splitlines():
            record = json.loads(raw.removeprefix("[IntegrationEvidence] "))
            record.update({
                "actionGeneration": 7,
                "actionCorrelation": "corr-action-7",
                "actionSequence": 10 if record["phase"] == "revision-out-of-order" else 11,
            })
            records.append("[IntegrationEvidence] " + json.dumps(
                record, separators=(",", ":"), sort_keys=True,
            ))
        result[role] = "\n".join(records)
    return result


def mutate_phase(logs: dict[str, str], expected_role: str, phase: str,
                 mutation: Callable[[JsonObject], None]) -> dict[str, str]:
    result = copy.deepcopy(logs)
    for role, text in result.items():
        records = []
        for raw in text.splitlines():
            record = json.loads(raw.removeprefix("[IntegrationEvidence] "))
            if role == expected_role and record["phase"] == phase:
                mutation(record)
            records.append("[IntegrationEvidence] " + json.dumps(
                record, separators=(",", ":"), sort_keys=True,
            ))
        result[role] = "\n".join(records)
    return result


class TypedActionAdmissionEnvelopeTests(unittest.TestCase):
    def test_action_parser_requires_exact_admission_fields(self) -> None:
        record = action_record("client-apply")
        try:
            parsed = contracts.parse_typed_evidence_line(
                "[IntegrationEvidence] " + json.dumps(record, separators=(",", ":")),
            )
        except ValueError as error:
            self.fail("complete action admission envelope was rejected: " + str(error))
        self.assertEqual(ACTION_ENVELOPE_FIELDS, frozenset(parsed))
        self.assertEqual(7, parsed["actionGeneration"])
        self.assertEqual("corr-action-7", parsed["actionCorrelation"])
        self.assertEqual(11, parsed["actionSequence"])
        for field in ACTION_FIELDS:
            invalid = action_record("client-apply")
            invalid.pop(field)
            with self.subTest(field=field), self.assertRaises(ValueError):
                contracts.parse_typed_evidence_line(
                    "[IntegrationEvidence] " + json.dumps(invalid, separators=(",", ":")),
                )
        extra = action_record("client-apply")
        extra["unexpected"] = True
        with self.assertRaises(ValueError):
            contracts.parse_typed_evidence_line(json_line(extra))

    def test_all_partial_action_admission_shapes_are_rejected(self) -> None:
        values: JsonObject = {
            "actionGeneration": 7,
            "actionCorrelation": "corr-action-7",
            "actionSequence": 11,
        }
        for size in (1, 2):
            for fields in combinations(ACTION_FIELDS, size):
                record: JsonObject = json.loads(json.dumps(
                    envelope("door", "client", "client-apply", 3, 1),
                ))
                record.update({field: values[field] for field in fields})
                with self.subTest(fields=fields), self.assertRaises(ValueError):
                    contracts.parse_typed_evidence_line(json_line(record))

    def test_ordinary_typed_record_remains_admission_free(self) -> None:
        ordinary = envelope("door", "client", "client-apply", 3, 1)
        self.assertEqual(BASE_FIELDS, frozenset(ordinary))
        self.assertTrue(set(ACTION_FIELDS).isdisjoint(ordinary))
        self.assertEqual(ordinary, contracts.parse_typed_evidence_line(json_line(ordinary)))
        extra: JsonObject = json.loads(json.dumps(ordinary))
        extra["unexpected"] = True
        with self.assertRaises(ValueError):
            contracts.parse_typed_evidence_line(json_line(extra))

    def test_action_admission_rejects_invalid_scalar_values(self) -> None:
        cases = (
            ("actionGeneration", 0),
            ("actionCorrelation", "wrong|correlation"),
            ("actionSequence", 0),
        )
        for field, value in cases:
            record = action_record("client-apply")
            record[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                contracts.parse_typed_evidence_line(
                    "[IntegrationEvidence] " + json.dumps(record, separators=(",", ":")),
                )


class TypedActionAdmissionCausalityTests(unittest.TestCase):
    def test_action_causal_records_share_admission_provenance(self) -> None:
        verdict = contracts.evaluate("door", action_logs(), None)
        self.assertTrue(verdict["passed"], verdict["failures"])

    def test_wrong_generation_correlation_and_sequence_are_rejected(self) -> None:
        mutations = (
            ("client", "client-apply", lambda record: record.update(actionGeneration=6)),
            ("client", "client-apply", lambda record: record.update(actionCorrelation="corr-other")),
            ("host", "host-submit", lambda record: record.update(actionSequence=12)),
            ("client", "revision-accepted", lambda record: record.update(actionSequence=12)),
            ("client", "client-apply", lambda record: record.update(actionSequence=12)),
            ("client", "revision-duplicate", lambda record: record.update(actionSequence=12)),
            ("client", "client-original-blocked", lambda record: record.update(actionSequence=12)),
            ("host", "final-state", lambda record: record.update(actionSequence=12)),
            ("client", "final-state", lambda record: record.update(actionSequence=12)),
            ("client", "revision-out-of-order", lambda record: record.update(actionSequence=11)),
        )
        for role, phase, mutation in mutations:
            changed = mutate_phase(action_logs(), role, phase, mutation)
            verdict = contracts.evaluate("door", changed, None)
            with self.subTest(role=role, phase=phase):
                self.assertFalse(verdict["passed"])

    def test_canonical_envelope_hash_is_order_and_roundtrip_stable(self) -> None:
        hash_envelope: Callable[[JsonObject], str] | None = getattr(
            contracts, "canonical_envelope_hash", None,
        )
        if hash_envelope is None:
            self.fail("canonical envelope hash API is missing")
            return
        record = action_record("client-apply")
        reordered = dict(reversed(tuple(record.items())))
        roundtrip = json.loads(json.dumps(record, sort_keys=True))
        self.assertEqual(hash_envelope(record), hash_envelope(reordered))
        self.assertEqual(hash_envelope(record), hash_envelope(roundtrip))
        self.assertTrue(hash_envelope(record).startswith("sha256:"))
        changed_state = copy.deepcopy(record)
        state = changed_state.get("state")
        self.assertIsInstance(state, dict)
        if not isinstance(state, dict):
            return
        state["control"] = "Closed"
        changed_state["stateHash"] = hash_state(state)
        changed_entry = copy.deepcopy(record)
        changed_entry["entryId"] = "sync:other-entry"
        self.assertNotEqual(record["stateHash"], changed_state["stateHash"])
        self.assertNotEqual(hash_envelope(record), hash_envelope(changed_state))
        self.assertNotEqual(hash_envelope(record), hash_envelope(changed_entry))


if __name__ == "__main__":
    unittest.main()
