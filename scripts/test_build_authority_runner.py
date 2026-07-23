import json
import unittest

from scripts.oni_build_evidence import PREFIX, evaluate


def evidence(phase, role, **fields):
    value = {
        "schemaVersion": 1,
        "operationId": "8:client:41",
        "prefabId": "Wire",
        "geometry": {"kind": "utility-path", "cells": [10, 11, 12]},
        "materialTags": ["Copper"],
        "facadeId": "DEFAULT_FACADE",
        "priority": {"class": "basic", "value": 5},
        "phase": phase,
        "role": role,
        "stateHash": "sha256:" + "a" * 64,
    }
    value.update(fields)
    return PREFIX + json.dumps(value, sort_keys=True, separators=(",", ":"))


def clean_logs():
    commit = {
        "placements": [
            {"cell": 10, "status": "completed"},
            {"cell": 12, "status": "completed"},
        ],
        "connections": [],
        "publisherCount": 1,
    }
    host = "\n".join((
        evidence("host-request", "host"),
        evidence("host-commit", "host", **commit),
        evidence("save-state", "host"),
        evidence("reload-state", "host"),
    ))
    client = evidence("client-apply", "client")
    return {"host": host, "client": client}


class BuildAuthorityRunnerTests(unittest.TestCase):
    def test_causal_commit_apply_accepts_partial_utility_outcomes(self):
        result = evaluate(clean_logs(), require_save_reload=True)
        self.assertTrue(result["passed"], result["failures"])

    def test_connection_crossing_failed_cell_is_rejected(self):
        logs = clean_logs()
        line = json.loads(logs["host"].splitlines()[1][len(PREFIX):])
        line["connections"] = [{"from": 10, "to": 12, "kind": "wire"}]
        logs["host"] = "\n".join((
            logs["host"].splitlines()[0],
            PREFIX + json.dumps(line, sort_keys=True, separators=(",", ":")),
            *logs["host"].splitlines()[2:],
        ))
        result = evaluate(logs)
        self.assertFalse(result["passed"])
        self.assertIn("failed path cell", " ".join(result["failures"]))

    def test_duplicate_publisher_is_rejected(self):
        logs = clean_logs()
        line = json.loads(logs["host"].splitlines()[1][len(PREFIX):])
        line["publisherCount"] = 2
        logs["host"] = "\n".join((
            logs["host"].splitlines()[0],
            PREFIX + json.dumps(line, sort_keys=True, separators=(",", ":")),
            *logs["host"].splitlines()[2:],
        ))
        result = evaluate(logs)
        self.assertFalse(result["passed"])
        self.assertIn("exactly one lifecycle publisher", " ".join(result["failures"]))

    def test_normal_domain_rejection_does_not_allow_reconnect(self):
        logs = clean_logs()
        logs["host"] += "\n" + evidence(
            "host-rejected", "host", reason="occupied", disconnect=False, reconnect=False
        )
        result = evaluate(logs, require_rejection=True)
        self.assertTrue(result["passed"], result["failures"])
        logs["host"] += "\nreconnect"
        self.assertFalse(evaluate(logs, require_rejection=True)["passed"])

    def test_malformed_or_hash_mismatched_apply_is_rejected(self):
        logs = clean_logs()
        line = json.loads(logs["client"][len(PREFIX):])
        line["stateHash"] = "sha256:" + "b" * 64
        logs["client"] = PREFIX + json.dumps(line, sort_keys=True, separators=(",", ":"))
        result = evaluate(logs)
        self.assertFalse(result["passed"])
        self.assertIn("state hashes differ", " ".join(result["failures"]))


if __name__ == "__main__":
    unittest.main()
