import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from typing import Any
from unittest import mock

from scripts import oni_target
from scripts import test_typed_scenario_evidence as typed_evidence


STATIC_ID = "ONI_Together"
DLL_HASH = "sha256:" + "a" * 64


def production_api(name):
    return getattr(oni_target, name)


def mod_entry(distribution_platform=1, mod_id="3768243603", enabled=True):
    return {
        "label": {
            "distribution_platform": distribution_platform,
            "id": mod_id,
            "title": "Oxygen Not Included Together",
            "version": 1784560740,
        },
        "status": 1,
        "enabled": enabled,
        "enabledForDlc": ["", "EXPANSION1_ID"],
        "crash_count": 0,
        "reinstall_path": None,
        "staticID": STATIC_ID,
    }


def mods_json(*entries):
    return {"version": 1, "mods": list(entries), "mod_load_in_progress": False}


def endpoint_facts(role):
    return {
        "build": "U59-740622-S",
        "dllHash": DLL_HASH,
        "protocol": "10",
        "dlcFingerprint": "sha256:" + "b" * 64,
        "steamSession": "109775241095789001",
        "role": role,
    }


def bundle_arguments(run_id="run-20260722-001") -> dict[str, Any]:
    host_record = typed_evidence.envelope("door", "host")
    client_record = typed_evidence.envelope("door", "client")
    host_record["runId"] = run_id
    client_record["runId"] = run_id
    return {
        "run_id": run_id,
        "inventory_digest": "sha256:" + "1" * 64,
        "coverage_digest": "sha256:" + "2" * 64,
        "dll_hash": DLL_HASH,
        "endpoint_logs": {
            "host": {"startOffset": 100, "endOffset": 220, "text": "host log"},
            "client": {"startOffset": 80, "endOffset": 180, "text": "client log"},
        },
        "typed_records": [host_record, client_record],
        "results": [{"scenario": "door", "passed": True}],
        "failure_flow": [],
    }


class ModResolutionTests(unittest.TestCase):
    def test_resolves_exactly_one_enabled_static_id(self):
        expected = mod_entry()
        data = mods_json(
            {**mod_entry(mod_id="old"), "staticID": "Other.Mod"}, expected)

        self.assertEqual(expected, production_api("resolve_enabled_mod")(data, STATIC_ID))

    def test_rejects_missing_disabled_and_duplicate_static_id(self):
        cases = (
            mods_json(),
            mods_json(mod_entry(enabled=False)),
            mods_json(mod_entry(), mod_entry(mod_id="3630759126")),
        )
        for data in cases:
            with self.subTest(data=data), self.assertRaises(ValueError):
                production_api("resolve_enabled_mod")(data, STATIC_ID)

    def test_resolves_workshop_dll_from_config_and_loaded_log(self):
        root = Path("/Users/Test User/Library/Application Support/ONI/mods")
        expected = root / "Steam" / "3768243603" / "ONI_Together.dll"
        log = "Loading mod content DLL [ONI ONLINE:3768243603] (provides DLL)"

        actual = production_api("resolve_loaded_mod_dll")(
            mods_json(mod_entry()), log, root, exists=lambda path: path == expected)

        self.assertEqual(expected, actual)

    def test_resolves_local_dll_without_assuming_static_id_folder(self):
        root = Path("/Users/Test User/Library/Application Support/ONI/mods")
        expected = root / "Local" / "custom-install-id" / "ONI_Together.dll"
        local = mod_entry(0, "custom-install-id")
        log = "Loading mod content DLL [Developer Build:custom-install-id] (provides DLL)"

        actual = production_api("resolve_loaded_mod_dll")(
            mods_json(local), log, root, exists=lambda path: path == expected)

        self.assertEqual(expected, actual)

    def test_rejects_unloaded_or_ambiguous_dll_and_fixed_path_guess(self):
        root = Path("/tmp/ONI With Spaces/mods")
        configured = root / "Steam" / "3768243603" / "ONI_Together.dll"
        guessed = root / "Local" / STATIC_ID / "ONI_Together.dll"
        cases = (
            ("unrelated log", lambda path: path == configured),
            ("Loading mod content DLL [ONI ONLINE:3768243603] (provides DLL)",
             lambda path: path == guessed),
        )
        for log, exists in cases:
            with self.subTest(log=log), self.assertRaises(ValueError):
                production_api("resolve_loaded_mod_dll")(
                    mods_json(mod_entry()), log, root, exists=exists)


class PlatformAdapterTests(unittest.TestCase):
    def test_macos_local_handles_spaces_for_read_size_and_hash(self):
        with tempfile.TemporaryDirectory(prefix="ONI Adapter ") as directory:
            path = Path(directory) / "Player Log With Spaces.log"
            path.write_bytes(b"prefix\nloaded\n")
            adapter = production_api("MacTargetAdapter")("local")

            self.assertEqual("loaded\n", adapter.read_text(path, offset=7))
            self.assertEqual(path.stat().st_size, adapter.size(path))
            self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(),
                             adapter.sha256(path))

    def test_macos_ssh_uses_argument_safe_remote_path(self):
        adapter = production_api("MacTargetAdapter")("mm")
        result = mock.Mock(returncode=0, stdout=b"remote log", stderr=b"")
        path = Path("Library/Logs/Klei/Oxygen Not Included/Player.log")

        with mock.patch.object(oni_target.subprocess, "run", return_value=result) as run:
            self.assertEqual("remote log", adapter.read_text(path, offset=3))

        command = run.call_args.args[0]
        self.assertEqual("ssh", command[0])
        self.assertIn("mm", command)
        self.assertIn("Oxygen Not Included", command[-1])
        self.assertNotIn(";", command[-1])

    def test_windows_ssh_uses_encoded_powershell_for_paths_with_spaces(self):
        adapter = production_api("WindowsTargetAdapter")("alienware")
        results = [mock.Mock(returncode=0, stdout=value, stderr=b"")
                   for value in (b"windows log", b"19\r\n")]
        path = Path(r"C:\Users\Alien Ware\AppData\LocalLow\Klei\Oxygen Not Included\Player.log")

        with mock.patch.object(oni_target.subprocess, "run", side_effect=results) as run:
            self.assertEqual("windows log", adapter.read_text(path, offset=5))
            self.assertEqual(19, adapter.size(path))

        command = run.call_args_list[0].args[0]
        self.assertEqual("ssh", command[0])
        self.assertIn("alienware", command)
        self.assertIn("-EncodedCommand", command)
        self.assertNotIn(str(path), " ".join(command))

    def test_windows_hash_rejects_malformed_powershell_output(self):
        adapter = production_api("WindowsTargetAdapter")("alienware")
        path = Path(r"C:\Program Files (x86)\Steam\steamapps\ONI Together.dll")
        valid = mock.Mock(returncode=0, stdout=("A" * 64 + "\r\n").encode(), stderr=b"")
        with mock.patch.object(oni_target.subprocess, "run", return_value=valid):
            self.assertEqual("a" * 64, adapter.sha256(path))
        result = mock.Mock(returncode=0, stdout=b"not-a-sha\r\n", stderr=b"")
        with mock.patch.object(oni_target.subprocess, "run", return_value=result):
            with self.assertRaises(ValueError):
                adapter.sha256(path)


class PreflightTests(unittest.TestCase):
    def test_accepts_matching_u59_hash_protocol_dlc_session_and_roles(self):
        failures = production_api("validate_multiplayer_preflight")(
            endpoint_facts("host"), endpoint_facts("client"))

        self.assertEqual([], failures)

    def test_every_endpoint_identity_fact_is_a_hard_gate(self):
        mutations = {
            "build": "U58-000000-S",
            "dllHash": "sha256:" + "9" * 64,
            "protocol": "9",
            "dlcFingerprint": "sha256:" + "8" * 64,
            "steamSession": "0",
            "role": "host",
        }
        for field, value in mutations.items():
            client = endpoint_facts("client")
            client[field] = value
            with self.subTest(field=field):
                self.assertNotEqual(
                    [], production_api("validate_multiplayer_preflight")(
                        endpoint_facts("host"), client))


class TypedTargetSelectorTests(unittest.TestCase):
    def test_supports_cell_netid_player_session_and_rocket_identity(self):
        cases = (
            ("remote-dig", {"kind": "cell", "cell": 42}),
            ("door", {"kind": "netId", "netId": 9}),
            ("cursor", {"kind": "player", "playerId": "player:host"}),
            ("reconnect-world-state", {"kind": "session", "sessionId": "steam:1097"}),
            ("rocket", {"kind": "rocket", "rocketNetId": 14, "padNetId": 15}),
        )
        for scenario, selector in cases:
            with self.subTest(scenario=scenario):
                self.assertEqual(
                    [], production_api("validate_target_selector")(scenario, selector))

    def test_rejects_missing_irrelevant_and_extra_selector_fields(self):
        cases = (
            ("remote-dig", {"kind": "cell"}),
            ("door", {"kind": "cell", "cell": 42}),
            ("cursor", {"kind": "player", "playerId": "player:host", "cell": 42}),
            ("rocket", {"kind": "rocket", "rocketNetId": 14}),
        )
        for scenario, selector in cases:
            with self.subTest(scenario=scenario, selector=selector):
                self.assertNotEqual(
                    [], production_api("validate_target_selector")(scenario, selector))


class ScenarioExecutionSpecTests(unittest.TestCase):
    def test_all_22_scenarios_have_fixed_native_execution_contracts(self):
        specs = production_api("SCENARIO_EXECUTION_SPECS")

        self.assertEqual(set(typed_evidence.SCENARIOS), set(specs))
        for scenario, spec in specs.items():
            with self.subTest(scenario=scenario):
                self.assertIsInstance(spec["driver"], str)
                self.assertTrue(spec["driver"])
                self.assertNotIn(spec["driver"], {"mcp-call", "json", "arbitrary-json"})
                self.assertIn(spec["acceptanceSource"], {
                    "debug-command", "native-game-event", "typed-evidence"})
                self.assertTrue(spec["completionBarrier"])
                self.assertGreater(spec["observationWindowSeconds"], 0)
                self.assertTrue(spec["cleanup"])

    def test_validation_rejects_mcp_acceptance_and_incomplete_spec(self):
        specs = json.loads(json.dumps(production_api("SCENARIO_EXECUTION_SPECS")))
        specs["door"]["driver"] = "mcp-call"
        specs["door"]["acceptanceSource"] = "arbitrary-json"
        specs["door"]["cleanup"] = ""

        failures = production_api("validate_execution_specs")(specs)

        self.assertTrue(any("door" in failure for failure in failures))


class EvidenceBundleTests(unittest.TestCase):
    def test_bundle_contains_all_run_isolated_acceptance_artifacts(self):
        bundle = production_api("build_evidence_bundle")(**bundle_arguments())

        self.assertEqual({
            "schemaVersion", "runId", "inventoryDigest", "coverageDigest",
            "dllHash", "endpointLogs", "typedRecords", "results", "failureFlow",
        }, set(bundle))
        self.assertEqual({"host", "client"}, set(bundle["endpointLogs"]))
        self.assertEqual("run-20260722-001", bundle["runId"])

    def test_bundle_rejects_missing_endpoint_window_and_cross_run_record(self):
        missing = bundle_arguments()
        del missing["endpoint_logs"]["client"]
        with self.assertRaises(ValueError):
            production_api("build_evidence_bundle")(**missing)

        cross_run = bundle_arguments()
        cross_run["typed_records"][1]["runId"] = "run-other"
        with self.assertRaises(ValueError):
            production_api("build_evidence_bundle")(**cross_run)

    def test_writer_is_atomic_and_refuses_to_overwrite_a_run(self):
        bundle = production_api("build_evidence_bundle")(**bundle_arguments())
        with tempfile.TemporaryDirectory() as directory:
            output = production_api("write_evidence_bundle_atomic")(Path(directory), bundle)

            self.assertEqual(
                Path(directory) / bundle["runId"] / "evidence.json", output)
            self.assertEqual(bundle, json.loads(output.read_text()))
            self.assertEqual([], list(Path(directory).rglob("*.tmp")))
            with self.assertRaises(FileExistsError):
                production_api("write_evidence_bundle_atomic")(Path(directory), bundle)


if __name__ == "__main__":
    unittest.main()
