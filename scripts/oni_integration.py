#!/usr/bin/env python3
import argparse
import hashlib
import json
from pathlib import Path
import re
import sys
import time
from typing import cast
import uuid
if __package__ in {None, ""}:
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from scripts.oni_mcp_client import McpClient, parse_mcp_response
from scripts.oni_integration_cli import (
    build_parser,
    nonnegative_float,
    nonnegative_int,
    parse_selector,
)
from scripts.oni_target import (
    LOG_REL,
    MCP_DLL_REL,
    MODS_REL,
    Target,
    create_target_adapter,
    resolve_adapter_artifacts,
    submit_local,
    valid_debug_command,
)
from scripts import oni_traffic_evidence
from scripts.oni_run_evidence import persist_run_evidence
from scripts.oni_execution_receipts import JsonValue
from scripts.oni_debug_outcome import (
    normalized_debug_command, parse_debug_outcome, parse_status_line,
    unit_tests_ran_in_current_process)
from scripts.oni_fault_adapter import (
    AdapterBackedFaultRuntime,
    TargetFaultRuntime,
    parse_fault_command_receipt,
)
from scripts.oni_receipt_producer import (
    ObservedExecutionReceiptProducer,
    read_execution_digests,
)
from scripts.oni_scenario_execution import (
    CLEANUP_REGISTRY,
    DRIVER_REGISTRY,
    ScenarioRuntimeCapabilityError,
    run_scenario_execution,
)
from scripts.oni_target_contracts import (
    SCENARIO_EXECUTION_SPECS,
    validate_execution_specs,
    validate_multiplayer_preflight,
    validate_target_selector,
)
from scripts.oni_scenario_contracts import (
    SCENARIOS,
    evaluate,
    evaluate_scenario_contract,
    evaluate_soak_log,
    evaluate_soak_post_keyframes,
    parse_fields,
    parse_scenario_evidence,
)
DEBUG_COMMAND_TIMEOUT = 5.0
MUTATION_COMMAND_TIMEOUT = 15.0
UNIT_TEST_COMMAND_TIMEOUT = 30.0
POLL_INTERVAL = 0.25
VERDICT_QUIET_WINDOW = 1.0
def is_mod_enabled(mods_data, static_id):
    return any(
        mod.get("staticID") == static_id and mod.get("enabled") is True
        for mod in mods_data.get("mods", [])
    )


def validate_session_facts(facts):
    failures = []
    host_status = facts.get("host_status", {})
    client_status = facts.get("client_status", {})
    for role, status in (("host", host_status), ("client", client_status)):
        if not status.get("ready") or status.get("role") != role:
            failures.append(f"{role}: multiplayer status is not ready")
        if status.get("protocol") != "10":
            failures.append(f"{role}: protocol v10 is required")
    if host_status.get("host") in {None, "0"} or host_status.get("host") != client_status.get("host"):
        failures.append("host and client are not in the same multiplayer session")
    if host_status.get("dlc") != client_status.get("dlc"):
        failures.append("host and client active DLC sets differ")
    if not facts.get("host_mcp_enabled"):
        failures.append("host: OniMcp must be enabled")
    if facts.get("client_mcp_enabled"):
        failures.append("client: OniMcp must be disabled")
    return failures


def debug_command_timeout(command):
    if command == "tests":
        return UNIT_TEST_COMMAND_TIMEOUT
    if command == "reconnect-evidence" or command.startswith(("build:", "replay-load:")):
        return MUTATION_COMMAND_TIMEOUT
    if command.startswith("steam-join:"):
        return 120.0
    return DEBUG_COMMAND_TIMEOUT


def run_debug_command(target, command):
    path = _log_path(target)
    offset = target.size(path)
    target.submit(command)
    deadline = time.monotonic() + debug_command_timeout(command)
    while time.monotonic() < deadline:
        outcome = parse_debug_outcome(_read_log(target, path, offset), command)
        if outcome is not None:
            return outcome
        time.sleep(POLL_INTERVAL)
    raise RuntimeError(f"{command} debug command produced no outcome")


def get_integration_status(target):
    path = _log_path(target)
    offset = target.size(path)
    target.submit("status")
    deadline = time.monotonic() + DEBUG_COMMAND_TIMEOUT
    while time.monotonic() < deadline:
        status = parse_status_line(_read_log(target, path, offset))
        if status is not None:
            return status
        time.sleep(POLL_INTERVAL)
    raise RuntimeError("status debug command produced no outcome")


def _collect_endpoint_artifacts(host, client, adapters, failures, facts):
    for role, target in (("host", host), ("client", client)):
        try:
            facts[f"{role}_game_running"] = target.game_running()
            artifacts = resolve_adapter_artifacts(adapters[role])
            facts[f"{role}_log_bytes"] = adapters[role].size(
                adapters[role].player_log_path())
            facts[f"{role}_mod_sha256"] = artifacts["dllHash"]
            facts[f"{role}_pdb_sha256"] = artifacts["pdbHash"]
            mods_data = artifacts["modsData"]
            facts[f"{role}_mcp_enabled"] = is_mod_enabled(
                mods_data, "LIghtJUNction.OniMcp")
            if not facts[f"{role}_game_running"]:
                failures.append(f"{role}: ONI is not running")
        except Exception as error:
            failures.append(f"{role}: {error}")
    if facts.get("host_mod_sha256") != facts.get("client_mod_sha256"):
        failures.append("ONI Together DLL hashes differ")
    if facts.get("host_pdb_sha256") != facts.get("client_pdb_sha256"):
        failures.append("ONI Together PDB hashes differ")


def _endpoint_identity(role, status, log, dll_hash):
    build_match = re.search(r"Build:\s*(U\d+-\d+-S)", log)
    dlc_value = status.get("dlc", "")
    dlc = dlc_value if isinstance(dlc_value, str) else ""
    return {
        "build": build_match.group(1) if build_match else None,
        "dllHash": dll_hash,
        "protocol": status.get("protocol"),
        "dlcFingerprint": "sha256:" + hashlib.sha256(dlc.encode()).hexdigest(),
        "steamSession": status.get("lobby"),
        "role": status.get("role"),
    }


def _collect_session_facts(host, client, adapters, failures, facts):
    identities = {}
    for role, target in (("host", host), ("client", client)):
        try:
            status = get_integration_status(target)
            facts[f"{role}_status"] = status
            log = adapters[role].read_text(adapters[role].player_log_path())
            if unit_tests_ran_in_current_process(log):
                failures.append(f"{role}: restart ONI after unit tests before integration testing")
            identities[role] = _endpoint_identity(
                role, status, log, facts[f"{role}_mod_sha256"])
        except Exception as error:
            failures.append(f"{role}: {error}")
    failures.extend(validate_session_facts(facts))
    if set(identities) == {"host", "client"}:
        failures.extend(validate_multiplayer_preflight(
            identities["host"], identities["client"]))


def _collect_mcp(host, mcp_url, failures, facts):
    try:
        facts["host_mcp_sha256"] = host.sha256(MCP_DLL_REL)
        mcp = McpClient(mcp_url)
        mcp.initialize()
        facts["mcp_tools"] = sorted(tool["name"] for tool in mcp.list_tools().get("tools", []))
    except Exception as error:
        failures.append(f"host MCP unavailable: {error}")


def preflight(host, client, mcp_url, *, host_adapter=None, client_adapter=None):
    failures = []
    facts = {}
    adapters = {
        "host": host_adapter or create_target_adapter("macos", host.name),
        "client": client_adapter or create_target_adapter("macos", client.name),
    }
    _collect_endpoint_artifacts(host, client, adapters, failures, facts)
    if not failures:
        _collect_session_facts(host, client, adapters, failures, facts)
    _collect_mcp(host, mcp_url, failures, facts)
    return {"passed": not failures, "failures": failures, "facts": facts}


def _log_path(target):
    if hasattr(target, "player_log_path"):
        return target.player_log_path()
    return LOG_REL


def _read_log(target, path, offset=0):
    if hasattr(target, "read_text"):
        return target.read_text(path, offset)
    return target.read(path, offset)


def begin_state(scenario, selector, host, client):
    failures = validate_target_selector(scenario, selector)
    if failures:
        raise ValueError("; ".join(failures))
    return {
        "scenario": scenario,
        "selector": selector,
        "executionSpec": SCENARIO_EXECUTION_SPECS[scenario],
        "runId": "run-" + uuid.uuid4().hex,
        "created_at": time.time(),
        "targets": {"host": host.name, "client": client.name},
        "platforms": {
            "host": "windows" if hasattr(host, "_powershell") else "macos",
            "client": "windows" if hasattr(client, "_powershell") else "macos",
        },
        "offsets": {"host": host.size(_log_path(host)),
                    "client": client.size(_log_path(client))},
    }


def read_state_logs(state):
    logs = {}
    for role in ("host", "client"):
        platform = state["platforms"][role]
        target = (Target(state["targets"][role]) if platform == "macos"
                  else create_target_adapter(platform, state["targets"][role]))
        path = _log_path(target)
        current = target.size(path)
        offset = state["offsets"][role]
        if current < offset:
            raise RuntimeError(f"{role} Player.log was truncated after scenario start")
        logs[role] = _read_log(target, path, offset)
    return logs


def wait_for_verdict(state, timeout):
    deadline = time.monotonic() + timeout
    success_since = None
    while True:
        selector = state["selector"]
        expected_cell = selector.get("cell") if selector.get("kind") == "cell" else None
        verdict = evaluate(state["scenario"], read_state_logs(state), expected_cell)
        now = time.monotonic()
        if verdict.get("forbidden"):
            return verdict
        if verdict["passed"]:
            success_since = success_since or now
            if now - success_since >= VERDICT_QUIET_WINDOW:
                return verdict
        else:
            success_since = None
        if now >= deadline and success_since is None:
            return verdict
        time.sleep(POLL_INTERVAL)


class ScenarioRuntime:
    def __init__(self, state, host, client, timeout_override=None):
        self.state = state
        self.targets = {"host": host, "client": client}
        self.timeout_override = timeout_override
    def invoke_native_action(self, action, target_role, profile=None):
        target = self.targets[target_role]
        hook = getattr(target, "invoke_native_action", None)
        if not callable(hook):
            raise ScenarioRuntimeCapabilityError(
                f"{target_role}: in-game native action hook is unavailable for {action}")
        return hook(action, self.state["selector"], *((profile,) if profile is not None else ()))

    def run_scenario_debug_command(self, builder, trigger):
        selector = self.state["selector"]
        commands = {
            "build:prefab-cell-material": (
                f"build:{trigger.get('prefab')}:{selector.get('cell')}:"
                f"{trigger.get('material')}"
            ),
            "reconnect-evidence": "reconnect-evidence",
        }
        if builder not in commands:
            raise ScenarioRuntimeCapabilityError(
                f"unregistered debug command builder: {builder}")
        command = commands[builder]
        return run_debug_command(self.targets[trigger["target"]], command)

    def wait_for_typed_barrier(self, predicate, timeout):
        expected = f"typed-final-state:{self.state['scenario']}"
        if predicate != expected:
            raise ScenarioRuntimeCapabilityError(
                f"unexpected typed barrier predicate: {predicate}")
        window = self.timeout_override if self.timeout_override is not None else timeout
        return wait_for_verdict(self.state, window)

    def cleanup_scenario(self, spec):
        failures = []
        for role, target in self.targets.items():
            hook = getattr(target, "cleanup_scenario", None)
            if not callable(hook):
                failures.append(f"{role}: in-game scenario cleanup hook is unavailable")
                continue
            profile = spec["trigger"].get("actionProfile")
            hook(self.state["scenario"], self.state["selector"], *((profile,) if profile is not None else ()))
        if failures:
            raise ScenarioRuntimeCapabilityError("; ".join(failures))
def parse_arguments(value):
    if value.startswith("@"):
        value = Path(value[1:]).read_text()
    parsed = json.loads(value)
    if not isinstance(parsed, dict):
        raise ValueError("MCP tool arguments must be a JSON object")
    return parsed


def save_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")


def _execute_state_action(args, host, client):
    if args.action == "preflight":
        return None
    if args.action == "begin":
        state = begin_state(args.scenario, args.selector, host, client)
        save_json(args.state, state)
        return {"passed": True, "state": str(args.state), **state}
    if args.action == "verify":
        return wait_for_verdict(json.loads(args.state.read_text()), args.timeout)
    if args.action == "debug-command":
        outcome = run_debug_command(
            {"host": host, "client": client}[args.target], args.command)
        return {"target": args.target, "command": args.command, **outcome}
    return None


def _execute_traffic_action(args, host, client):
    if args.action == "traffic-sample":
        evidence = oni_traffic_evidence.sample_status(
            get_integration_status, host, client, args.duration, args.interval)
        save_json(args.output, evidence)
        return {
            "passed": True,
            "output": str(args.output),
            "sampleCount": len(evidence["samples"]),
            "totals": oni_traffic_evidence.window_totals(evidence),
            "health": oni_traffic_evidence.acceptance_health(evidence),
        }
    if args.action == "traffic-compare":
        before = json.loads(args.before.read_text())
        after = json.loads(args.after.read_text())
        return oni_traffic_evidence.compare_evidence(
            before, after, args.minimum_reduction)
    if args.action == "traffic-health":
        evidence = json.loads(args.evidence.read_text())
        return oni_traffic_evidence.acceptance_health(evidence)
    return None
def _execute_replay_action(args, host, client):
    if args.action == "replay-measure":
        if not args.command.startswith("replay-load:") or not valid_debug_command(args.command):
            raise ValueError("replay measure requires a valid replay-load command")
        target = {"host": host, "client": client}[args.target]
        before = get_integration_status(target)
        outcome = run_debug_command(target, args.command)
        after = get_integration_status(target)
        evidence = {
            "target": args.target,
            "command": args.command,
            "outcome": outcome,
            "before": before,
            "after": after,
            "window": oni_traffic_evidence.command_window(before, after),
        }
        save_json(args.output, evidence)
        return {
            "passed": outcome["passed"] and evidence["window"]["txFailures"] == 0,
            "output": str(args.output),
            **evidence,
        }
    if args.action == "replay-compare":
        before = json.loads(args.before.read_text()).get("window", {})
        after = json.loads(args.after.read_text()).get("window", {})
        return oni_traffic_evidence.compare_replay_windows(
            before, after, args.minimum_reduction)
    return None
def _execute_control_action(args, host, client):
    if args.action == "reconnect-client":
        command = f"steam-join:{args.lobby}"
        if not valid_debug_command(command):
            raise ValueError("invalid Steam lobby code")
        client.restart_game(args.launch_timeout)
        outcome = run_debug_command(client, command)
        return {"target": "client", "command": command, **outcome}
    if args.action == "soak-verdict":
        target = {"host": host, "client": client}[args.target]
        return {"target": args.target,
                **evaluate_soak_log(_read_log(target, _log_path(target)))}
    return None


def _execute_full_run(args, host, client, host_adapter, client_adapter):
    check = preflight(host, client, args.mcp_url,
                      host_adapter=host_adapter, client_adapter=client_adapter)
    if not check["passed"]:
        return check
    spec_failures = validate_execution_specs(SCENARIO_EXECUTION_SPECS)
    if spec_failures:
        return {"passed": False, "failures": spec_failures}
    state = begin_state(args.scenario, args.selector, host, client)
    runtime = ScenarioRuntime(state, host, client, args.timeout)
    verdict = run_scenario_execution(SCENARIO_EXECUTION_SPECS[args.scenario], runtime)
    logs = read_state_logs(state)
    run_id = state.get("runId")
    scenario = state.get("scenario")
    if not isinstance(run_id, str) or not isinstance(scenario, str):
        raise ValueError("scenario state lacks stable run identity")
    evidence = parse_scenario_evidence(scenario, logs)
    raw_records = [] if evidence is None else evidence["records"]
    records = [cast(dict[str, JsonValue], record)
               for _, record in cast(list[tuple[str, object]], raw_records)]
    typed_verdict = cast(dict[str, JsonValue], verdict)
    driver = SCENARIO_EXECUTION_SPECS[args.scenario].get("driver")
    if not isinstance(driver, str):
        raise ValueError("scenario lacks a stable control driver")
    inventory_digest, coverage_digest = read_execution_digests(
        cast(Path, args.inventory), cast(Path, args.coverage))
    facts = check.get("facts")
    if (not isinstance(facts, dict) or not isinstance(
            facts.get("host_mod_sha256"), str) or
            not isinstance(facts.get("host_pdb_sha256"), str)):
        raise ValueError("preflight lacks the host DLL/PDB hash")
    receipt_producer = ObservedExecutionReceiptProducer(
        run_id=run_id, scenario=scenario,
        inventory_digest=inventory_digest, coverage_digest=coverage_digest,
        dll_hash=cast(str, facts["host_mod_sha256"]), pdb_hash=cast(
            str, facts["host_pdb_sha256"]),
        records=records,
        logs=logs, verdict=typed_verdict, evidence_root=args.evidence_root,
        control_driver=driver,
    )
    output = persist_run_evidence({
        "state": state, "logs": logs, "verdict": verdict,
        "dllHash": facts["host_mod_sha256"],
        "inventory": args.inventory, "coverage": args.coverage,
        "outputRoot": args.evidence_root,
        "receiptProducer": receipt_producer,
    })
    return {**typed_verdict, "evidenceBundle": str(output)}


def execute(args):
    host_adapter = create_target_adapter(args.host_platform, args.host)
    client_adapter = create_target_adapter(args.client_platform, args.client)
    host = Target(args.host) if args.host_platform == "macos" else host_adapter
    client = Target(args.client) if args.client_platform == "macos" else client_adapter
    if args.action == "preflight":
        return preflight(host, client, args.mcp_url,
                         host_adapter=host_adapter, client_adapter=client_adapter)
    for handler in (_execute_state_action, _execute_traffic_action,
                    _execute_replay_action, _execute_control_action):
        result = handler(args, host, client)
        if result is not None:
            return result
    return _execute_full_run(args, host, client, host_adapter, client_adapter)
def main():
    try:
        result = execute(build_parser().parse_args())
    except Exception as error:
        result = {"passed": False, "failures": [f"{type(error).__name__}: {error}"]}
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result.get("passed") else 1


if __name__ == "__main__":
    sys.exit(main())
