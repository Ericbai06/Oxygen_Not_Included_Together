import json
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest import mock

from scripts import oni_integration
from scripts.test_typed_scenario_evidence import envelope, line


class RealRunnerCoverageGateTests(unittest.TestCase):
    def test_full_run_uses_real_producer_and_rejects_unmapped_actual_catalog(self):
        repository = Path(__file__).resolve().parents[1]
        inventory_path = repository / "sync-entry-inventory.json"
        coverage_path = repository / "sync-entry-coverage.json"
        inventory = json.loads(inventory_path.read_text(encoding="utf-8"))
        coverage = json.loads(coverage_path.read_text(encoding="utf-8"))
        self.assertEqual(len(inventory["entries"]), len(coverage["entries"]))
        self.assertGreater(len(inventory["entries"]), 1_000)
        observed_id = inventory["entries"][0]["id"]
        record = envelope("door")
        record["runId"] = "run-real-gate"
        record["entryId"] = observed_id

        with tempfile.TemporaryDirectory() as temporary:
            evidence_root = Path(temporary) / "evidence"
            args = SimpleNamespace(
                scenario="door", selector={"kind": "netId", "netId": 9},
                timeout=1, inventory=inventory_path, coverage=coverage_path,
                evidence_root=evidence_root, mcp_url="http://127.0.0.1:8788/mcp/",
            )
            state = {
                "runId": "run-real-gate", "scenario": "door",
                "selector": args.selector, "offsets": {"host": 0, "client": 0},
            }
            facts = {
                "host_mod_sha256": "sha256:" + "4" * 64,
                "host_pdb_sha256": "sha256:" + "5" * 64,
            }
            patches = (
                mock.patch.object(oni_integration, "preflight",
                                  return_value={"passed": True, "facts": facts}),
                mock.patch.object(oni_integration, "validate_execution_specs",
                                  return_value=[]),
                mock.patch.object(oni_integration, "begin_state", return_value=state),
                mock.patch.object(oni_integration, "run_scenario_execution",
                                  return_value={"passed": True, "failures": [],
                                                "forbidden": []}),
                mock.patch.object(oni_integration, "read_state_logs",
                                  return_value={"host": line(record), "client": ""}),
            )
            with patches[0], patches[1], patches[2], patches[3], patches[4]:
                with self.assertRaisesRegex(
                        ValueError, "execution coverage gate failed"):
                    oni_integration._execute_full_run(
                        args, object(), object(), object(), object())

            manifests = list(evidence_root.glob(".runtime-artifacts/*/manifest.json"))
            self.assertEqual(1, len(manifests),
                             "real ObservedExecutionReceiptProducer did not run")
            self.assertFalse(any(evidence_root.glob("*.json")),
                             "failed gate persisted an acceptance bundle")


if __name__ == "__main__":
    unittest.main()
