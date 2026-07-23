import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

from scripts import oni_integration
from scripts import oni_target
from scripts import oni_traffic_evidence
from scripts import test_typed_scenario_evidence as typed_evidence


class ScenarioEvaluationTests(unittest.TestCase):
    def test_unit_test_log_requires_fresh_game_process(self):
        self.assertTrue(oni_integration.unit_tests_ran_in_current_process(
            "[UnitTests] Run all: total=566, passed=540, failed=0, notRun=26"))
        self.assertFalse(oni_integration.unit_tests_ran_in_current_process(
            "[SyncBacklog] Replay applied for 2"))

    def test_remote_dig_rejects_captured_exception(self):
        logs = {
            "host": "[DiggablePacket] dispatched\n",
            "client": (
                "Registered workable DigPlacer with id: 7 for workable type Diggable\n"
                "Exception in: Worker.StartWork(Diggable)\n"
                "System.NullReferenceException\n"
                "  at Diggable.GetConversationTopic ()\n"
                "  at ONI_Together.Networking.Packets.Animation."
                "StandardWorker_WorkingState_Packet.TryApply (System.Boolean logFailure)\n"
            ),
        }

        verdict = oni_integration.evaluate("remote-dig", logs, expected_cell=7)

        self.assertFalse(verdict["passed"])
        self.assertIn("Diggable.GetConversationTopic", verdict["forbidden"])

    def test_remote_dig_accepts_native_placement_and_remote_work(self):
        logs = complete_typed_logs("remote-dig")

        verdict = oni_integration.evaluate("remote-dig", logs, expected_cell=42)

        self.assertTrue(verdict["passed"])
        self.assertEqual([], verdict["missing"])
        self.assertEqual([], verdict["forbidden"])

    def test_remote_dig_rejects_evidence_from_another_cell(self):
        logs = {
            "host": (
                "Registered workable DigPlacer with id: 7 for workable type Diggable at cell 41\n"
                "[DuplicantPresentationBatch][HOST_SEND] revision=8 netId=5 "
                "action=Digging targetCell=41\n"
            ),
            "client": (
                "[RemoteDuplicantPresenter][CLIENT_APPLY] revision=8 netId=5 "
                "action=Digging targetCell=41\n"
            ),
        }

        verdict = oni_integration.evaluate("remote-dig", logs, expected_cell=42)

        self.assertFalse(verdict["passed"])

    def test_building_lifecycle_rejects_uninitialized_constructable(self):
        logs = {
            "host": "[Host] Sent BuildCompletePacket for Tile at cell 12\n",
            "client": (
                "System.NullReferenceException\n"
                "  at Constructable.OnSpawn ()\n"
                "  at ElementLoader.GetElement(SelectedElementsTags[0])\n"
            ),
        }

        verdict = oni_integration.evaluate("building-lifecycle", logs, expected_cell=12)

        self.assertFalse(verdict["passed"])
        self.assertIn("Constructable.OnSpawn", verdict["forbidden"])

    def test_building_lifecycle_accepts_domain_materialization(self):
        logs = complete_typed_logs("building-lifecycle", 12)

        verdict = oni_integration.evaluate("building-lifecycle", logs, expected_cell=12)

        self.assertTrue(verdict["passed"])
        self.assertEqual([], verdict["missing"])
        self.assertEqual([], verdict["forbidden"])

    def test_success_waits_for_quiet_window_and_catches_late_error(self):
        clean = {
            "host": "[Host] Sent BuildCompletePacket for Tile at cell 12\n",
            "client": (
                "[BuildStatePacket] Applied Queued Tile at cell 12\n"
                "[BuildCompletePacket] Finalized Tile at cell 12\n"
            ),
        }
        late_error = {
            **clean,
            "client": clean["client"] + "Constructable.OnSpawn\n",
        }
        state = {
            "scenario": "building-lifecycle",
            "selector": {"kind": "cell", "cell": 12},
        }

        with mock.patch.object(
                oni_integration, "read_state_logs", side_effect=[clean, late_error]
        ), mock.patch.object(
                oni_integration.time, "monotonic", side_effect=[0.0, 0.0, 0.25]
        ), mock.patch.object(oni_integration.time, "sleep"):
            verdict = oni_integration.wait_for_verdict(state, timeout=10)

        self.assertFalse(verdict["passed"])
        self.assertIn("Constructable.OnSpawn", verdict["forbidden"])


class McpParsingTests(unittest.TestCase):
    def test_parses_streamable_http_event(self):
        payload = 'event: message\ndata: {"jsonrpc":"2.0","id":1,"result":{"ok":true}}\n\n'

        self.assertEqual({"ok": True}, oni_integration.parse_mcp_response(payload)["result"])

    def test_rejects_non_loopback_mcp_endpoint(self):
        with self.assertRaisesRegex(ValueError, "loopback"):
            oni_integration.McpClient("http://192.0.2.1:8788/mcp/")

    def test_tool_level_error_fails_call(self):
        client = oni_integration.McpClient("http://127.0.0.1:8788/mcp/")
        with mock.patch.object(client, "_call", return_value={"isError": True}):
            with self.assertRaisesRegex(RuntimeError, "MCP tool failed"):
                client.call_tool("world_editor", {"task": "dig"})


class PreflightBoundaryTests(unittest.TestCase):
    def test_mod_enabled_reads_static_id_not_folder_name(self):
        data = {
            "mods": [
                {"staticID": "LIghtJUNction.OniMcp", "enabled": True},
                {"staticID": "ONI_Together", "enabled": False},
            ]
        }

        self.assertTrue(oni_integration.is_mod_enabled(data, "LIghtJUNction.OniMcp"))
        self.assertFalse(oni_integration.is_mod_enabled(data, "ONI_Together"))

    def test_protocol_dlc_and_mcp_role_are_hard_preflight_gates(self):
        facts = {
            "host_status": {"ready": True, "role": "host", "host": "1", "protocol": "10", "dlc": "EXPANSION1_ID"},
            "client_status": {"ready": True, "role": "client", "host": "1", "protocol": "10", "dlc": "EXPANSION1_ID"},
            "host_mcp_enabled": True,
            "client_mcp_enabled": False,
        }

        self.assertEqual([], oni_integration.validate_session_facts(facts))
        facts["client_status"]["protocol"] = "9"
        self.assertIn("protocol v10", " ".join(oni_integration.validate_session_facts(facts)))

    def test_client_mcp_enabled_is_rejected(self):
        facts = {
            "host_status": {"ready": True, "role": "host", "host": "1", "protocol": "10", "dlc": ""},
            "client_status": {"ready": True, "role": "client", "host": "1", "protocol": "10", "dlc": ""},
            "host_mcp_enabled": True,
            "client_mcp_enabled": True,
        }

        self.assertIn("client: OniMcp must be disabled", oni_integration.validate_session_facts(facts))


class TrafficEvidenceTests(unittest.TestCase):
    def test_window_sums_both_endpoints_and_ignores_absolute_baseline(self):
        evidence = {
            "samples": [
                {"host": {"motionCalls": "10", "motionBytes": "100"},
                 "client": {"motionCalls": "20", "motionBytes": "200"}},
                {"host": {"motionCalls": "13", "motionBytes": "160"},
                 "client": {"motionCalls": "25", "motionBytes": "290"}},
            ]
        }

        window = oni_traffic_evidence.window_totals(evidence)

        self.assertEqual(8, window["motionCalls"])
        self.assertEqual(150, window["motionBytes"])

    def test_comparison_requires_sixty_percent_for_every_presentation_metric(self):
        before = {key: 100 for key in oni_traffic_evidence.TRAFFIC_KEYS}
        after = {key: 40 for key in oni_traffic_evidence.TRAFFIC_KEYS}

        accepted = oni_traffic_evidence.compare_totals(before, after)
        after["cursorBytes"] = 41
        rejected = oni_traffic_evidence.compare_totals(before, after)

        self.assertTrue(accepted["passed"])
        self.assertFalse(rejected["passed"])
        self.assertIn("cursorBytes", rejected["failures"])

    def test_health_gate_uses_max_p95_and_native_failures(self):
        samples = []
        for index in range(20):
            samples.append({
                "host": {
                    "txFailures": "0",
                    "steamQueueUsec": str(400_000 if index < 19 else 900_000),
                    "steamUnackedReliableBytes": str(12_000 if index < 19 else 60_000),
                },
                "client": {
                    "txFailures": "0",
                    "steamQueueUsec": "100000",
                    "steamUnackedReliableBytes": "1000",
                },
            })

        result = oni_traffic_evidence.acceptance_health({"samples": samples})

        self.assertTrue(result["passed"])
        self.assertEqual(900.0, result["steamQueueMaxMs"])
        self.assertEqual(400.0, result["steamQueueP95Ms"])

    def test_health_gate_rejects_send_failure(self):
        evidence = {
            "samples": [
                {"host": {"txFailures": "0"}, "client": {"txFailures": "0"}},
                {"host": {"txFailures": "1"}, "client": {"txFailures": "0"}},
            ]
        }

        result = oni_traffic_evidence.acceptance_health(evidence)

        self.assertFalse(result["passed"])
        self.assertIn("txFailures", result["failures"])

    def test_command_window_records_native_send_delta(self):
        before = {"txCalls": "41", "txBytes": "8000", "txFailures": "2"}
        after = {"txCalls": "53", "txBytes": "9200", "txFailures": "2"}

        result = oni_traffic_evidence.command_window(before, after)

        self.assertEqual(12, result["txCalls"])
        self.assertEqual(1200, result["txBytes"])
        self.assertEqual(0, result["txFailures"])

    def test_replay_comparison_requires_fifty_percent_fewer_native_calls(self):
        accepted = oni_traffic_evidence.compare_replay_windows(
            {"txCalls": 100}, {"txCalls": 50})
        rejected = oni_traffic_evidence.compare_replay_windows(
            {"txCalls": 100}, {"txCalls": 51})

        self.assertTrue(accepted["passed"])
        self.assertEqual(50.0, accepted["reductionPercent"])
        self.assertFalse(rejected["passed"])


class DebugCommandTests(unittest.TestCase):
    def test_parser_supports_replay_native_call_evidence(self):
        measure = oni_integration.build_parser().parse_args([
            "replay-measure", "host", "--output", "/tmp/replay.json",
        ])
        compare = oni_integration.build_parser().parse_args([
            "replay-compare", "/tmp/before.json", "/tmp/after.json",
        ])

        self.assertEqual("replay-load:512:4096", measure.command)
        self.assertEqual("replay-compare", compare.action)

    def test_full_unit_suite_uses_its_own_bounded_timeout(self):
        self.assertEqual(30.0, oni_integration.debug_command_timeout("tests"))
        self.assertEqual(15.0, oni_integration.debug_command_timeout("build:Tile:95028:SandStone"))
        self.assertEqual(15.0, oni_integration.debug_command_timeout("replay-load:512:4096"))
        self.assertEqual(15.0, oni_integration.debug_command_timeout("reconnect-evidence"))
        self.assertEqual(5.0, oni_integration.debug_command_timeout("status"))
        self.assertEqual(120.0, oni_integration.debug_command_timeout("steam-join:U0W2QTUASK6"))

    def test_accepts_production_checkpoint_command(self):
        self.assertTrue(oni_integration.valid_debug_command("checkpoint"))
        self.assertTrue(oni_integration.valid_debug_command("hard-sync"))
        self.assertTrue(oni_integration.valid_debug_command("reconnect-evidence"))
        self.assertFalse(oni_integration.valid_debug_command("reconnect-evidence:extra"))
        self.assertFalse(oni_integration.valid_debug_command("prefix-reconnect-evidence"))
        self.assertEqual("reconnect-evidence", oni_integration.normalized_debug_command("reconnect-evidence"))

    def test_accepts_only_bounded_network_build_command(self):
        self.assertTrue(oni_integration.valid_debug_command("build:Tile:95028:SandStone"))
        self.assertFalse(oni_integration.valid_debug_command("build:Tile:-1:SandStone"))
        self.assertTrue(oni_integration.valid_debug_command("replay-load:512:4096"))
        self.assertFalse(oni_integration.valid_debug_command("replay-load:513:4096"))
        self.assertFalse(oni_integration.valid_debug_command("replay-load:512:4097"))
        self.assertFalse(oni_integration.valid_debug_command("build:../Tile:95028:SandStone"))

    def test_submits_command_atomically_and_rejects_busy_slot(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "oni_together_debug_command.txt"

            oni_integration.submit_local(path, "soak")

            self.assertEqual("soak\n", path.read_text())
            with self.assertRaisesRegex(RuntimeError, "slot is busy"):
                oni_integration.submit_local(path, "tests")

    def test_local_submit_activates_after_atomic_delivery(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "oni_together_debug_command.txt"
            target = oni_integration.Target("local")

            def activate(arguments, **_):
                self.assertEqual("status\n", path.read_text())
                return mock.Mock(returncode=0, stderr="")

            with mock.patch.object(target, "game_running", return_value=True), \
                    mock.patch.object(target, "_local_path", return_value=path), \
                    mock.patch.object(
                        oni_target.subprocess, "run", side_effect=activate
                    ) as run:
                target.submit("status")

            run.assert_called_once_with(
                ["open", "-a", "Oxygen Not Included"],
                text=True,
                capture_output=True,
            )

    def test_remote_submit_activates_after_atomic_delivery(self):
        target = oni_integration.Target("mm")
        calls = []

        def remote(command, input_text=None):
            calls.append((command, input_text))
            return ""

        with mock.patch.object(target, "game_running", return_value=True), \
                mock.patch.object(target, "_remote", side_effect=remote):
            target.submit("status")

        self.assertEqual("status\n", calls[0][1])
        self.assertIn("oni_together_debug_command.txt", calls[0][0])
        self.assertEqual(("open -a 'Oxygen Not Included'", None), calls[1])

    def test_remote_restart_uses_applescript_and_steam_uri(self):
        target = oni_integration.Target("mm")
        calls = []

        def remote(command, input_text=None):
            calls.append((command, input_text))
            return ""

        with mock.patch.object(target, "_remote", side_effect=remote), \
                mock.patch.object(target, "game_running", side_effect=[False, True]), \
                mock.patch.object(oni_target.time, "sleep"):
            target.restart_game(timeout=5)

        self.assertIn("osascript", calls[0][0])
        self.assertEqual(("open 'steam://run/457140'", None), calls[1])

    def test_remote_log_read_replaces_partial_utf8_prefix(self):
        target = oni_integration.Target("mm")
        result = mock.Mock(returncode=0, stdout=b"\x80ready\n", stderr=b"")

        with mock.patch.object(oni_target.subprocess, "run", return_value=result):
            text = target._remote("tail -c +2 Player.log")

        self.assertEqual("\ufffdready\n", text)

    def test_submit_refuses_to_activate_when_game_is_not_running(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "oni_together_debug_command.txt"
            target = oni_integration.Target("local")
            with mock.patch.object(target, "game_running", return_value=False), \
                    mock.patch.object(target, "_local_path", return_value=path), \
                    mock.patch.object(oni_target.subprocess, "run") as run:
                with self.assertRaisesRegex(RuntimeError, "ONI is not running"):
                    target.submit("status")

            self.assertFalse(path.exists())
            run.assert_not_called()

    def test_parses_ready_multiplayer_status(self):
        line = (
            "[DebugCommand][OK] command=status "
            "reason=role=client;host=1;local=2;session=1;world=1;state=InGame;remotes=1\n"
        )

        status = oni_integration.parse_status_line(line)

        self.assertTrue(status["ready"])
        self.assertEqual("1", status["host"])

    def test_debug_command_requires_matching_game_outcome(self):
        text = (
            "[DebugCommand][OK] command=status reason=role=host\n"
            "[DebugCommand][FAIL] command=tests reason=active-multiplayer-session\n"
        )

        self.assertIsNone(oni_integration.parse_debug_outcome(text, "hard-sync"))
        self.assertEqual(
            {"passed": False, "reason": "active-multiplayer-session"},
            oni_integration.parse_debug_outcome(text, "tests"),
        )

    def test_parameterized_commands_match_their_normalized_game_outcomes(self):
        text = (
            "[DebugCommand][OK] command=steam-join reason=connect-requested\n"
            "[DebugCommand][OK] command=build reason=placed\n"
            "[DebugCommand][OK] command=replay-load reason=completed\n"
        )

        self.assertEqual({"passed": True, "reason": "connect-requested"},
                         oni_integration.parse_debug_outcome(text, "steam-join:U0W2QTUASK6"))
        self.assertEqual({"passed": True, "reason": "placed"},
                         oni_integration.parse_debug_outcome(text, "build:Tile:95028:SandStone"))
        self.assertEqual({"passed": True, "reason": "completed"},
                         oni_integration.parse_debug_outcome(text, "replay-load:512:4096"))

    def test_rejects_non_finite_timeout(self):
        for value in ("nan", "inf", "-1"):
            with self.subTest(value=value):
                with self.assertRaisesRegex(
                    oni_integration.argparse.ArgumentTypeError, "finite and non-negative"
                ):
                    oni_integration.nonnegative_float(value)
REAL_TWO_MACHINE_SCENARIOS = typed_evidence.SCENARIOS


def complete_typed_logs(scenario, cell=42):
    logs = typed_evidence.causal_logs(scenario)
    if scenario == "remote-dig":
        logs["host"] += (
            f"\nRegistered workable DigPlacer with id: 7 for workable type Diggable at cell {cell}\n"
            f"[DuplicantPresentationBatch][HOST_SEND] revision=8 netId=5 action=Digging targetCell={cell}\n")
        logs["client"] += (f"\n[RemoteDuplicantPresenter][CLIENT_APPLY] revision=8 "
                           f"netId=5 action=Digging targetCell={cell}\n")
    if scenario == "building-lifecycle":
        logs["host"] += f"\n[Host] Sent BuildCompletePacket for Tile at cell {cell}\n"
        logs["client"] += (f"\n[BuildStatePacket] Applied Queued Tile at cell {cell}\n"
                           f"[BuildCompletePacket] Finalized Tile at cell {cell}\n")
        logs = {role: text.replace('"cell":42', f'"cell":{cell}')
                for role, text in logs.items()}
    if scenario == "reconnect-world-state":
        for role in ("host", "client"):
            sequence = 3 if role == "host" else 7
            record = typed_evidence.envelope(scenario, role, "post-reconnect-state", 3, sequence)
            record["entryId"] = f"sync:{role}-observer"
            logs[role] += "\n" + typed_evidence.line(record)
    return logs


def mutate_client_state(logs, phase, field, value):
    result = dict(logs)
    records = []
    for raw_line in result["client"].splitlines():
        record = json.loads(raw_line.removeprefix("[IntegrationEvidence] "))
        if record["phase"] == phase:
            record["state"][field] = value
            record["stateHash"] = typed_evidence.hash_state(record["state"])
        records.append(typed_evidence.line(record))
    result["client"] = "\n".join(records)
    return result
class RealTwoMachineContractTests(unittest.TestCase):
    def test_catalog_contains_the_full_real_two_machine_matrix(self):
        self.assertEqual(set(REAL_TWO_MACHINE_SCENARIOS), set(oni_integration.SCENARIOS))

    def test_every_scenario_requires_complete_structured_evidence(self):
        for scenario in REAL_TWO_MACHINE_SCENARIOS:
            with self.subTest(scenario=scenario):
                expected_cell = 42 if scenario in ("remote-dig", "animation") else None
                verdict = oni_integration.evaluate(scenario, complete_typed_logs(scenario), expected_cell)
                self.assertTrue(verdict["passed"])
    def test_missing_causal_phase_and_final_state_mismatch_fail(self):
        for phase in ("host-submit", "client-apply", "client-original-blocked",
                      "revision-accepted", "revision-duplicate", "revision-out-of-order"):
            logs = complete_typed_logs("research")
            role = "client" if phase.startswith("client-") or phase.startswith("revision-") else "host"
            logs[role] = "\n".join(line for line in logs[role].splitlines()
                                     if f'"phase":"{phase}"' not in line)
            with self.subTest(phase=phase):
                self.assertFalse(oni_integration.evaluate("research", logs, None)["passed"])
        logs = mutate_client_state(complete_typed_logs("research"), "final-state", "progress", 0.75)
        self.assertFalse(oni_integration.evaluate("research", logs, None)["passed"])
    def test_typed_evidence_does_not_bypass_forbidden_client_path(self):
        logs = complete_typed_logs("remote-dig")
        logs["client"] += "\nDiggable.GetConversationTopic\n"
        verdict = oni_integration.evaluate("remote-dig", logs, 42)
        self.assertFalse(verdict["passed"])
        self.assertIn("Diggable.GetConversationTopic", json.dumps(verdict))
    def test_reconnect_requires_matching_post_reconnect_state_and_hash(self):
        logs = complete_typed_logs("reconnect-world-state")
        self.assertTrue(oni_integration.evaluate("reconnect-world-state", logs, None)["passed"])
        logs = mutate_client_state(logs, "post-reconnect-state", "snapshotGeneration", 4)
        self.assertFalse(oni_integration.evaluate("reconnect-world-state", logs, None)["passed"])
    def test_soak_verdict_requires_21_equal_zero_lifecycle_records(self):
        record = {domain: True for domain in ("time", "grid", "entity", "world", "storage", "clusterRocket")}
        record["lifecycle"] = dict.fromkeys(("missing", "unexpected", "tombstoned", "unassigned"), 0)
        records = [json.loads(json.dumps(record)) for _ in range(21)]
        self.assertTrue(oni_integration.evaluate_soak_post_keyframes(records)["passed"])
        records[7]["grid"] = False
        self.assertFalse(oni_integration.evaluate_soak_post_keyframes(records)["passed"])
        records[7]["grid"], records[9]["lifecycle"]["tombstoned"] = True, 1
        self.assertFalse(oni_integration.evaluate_soak_post_keyframes(records)["passed"])
        self.assertFalse(oni_integration.evaluate_soak_post_keyframes(records[:20])["passed"])


if __name__ == "__main__":
    unittest.main()
