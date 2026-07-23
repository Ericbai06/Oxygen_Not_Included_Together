import argparse
import json
import math
from pathlib import Path

from .oni_scenario_contracts import SCENARIOS
from .oni_fault_execution_specs import FAULT_EXECUTION_SPECS


def nonnegative_float(value):
    parsed = float(value)
    if not math.isfinite(parsed) or parsed < 0:
        raise argparse.ArgumentTypeError("value must be finite and non-negative")
    return parsed


def nonnegative_int(value):
    parsed = int(value)
    if parsed < 0:
        raise argparse.ArgumentTypeError("value must be non-negative")
    return parsed


def parse_selector(value):
    parsed = json.loads(value)
    if not isinstance(parsed, dict):
        raise argparse.ArgumentTypeError("selector must be a JSON object")
    return parsed


def _add_state_parsers(sub):
    begin = sub.add_parser("begin")
    begin.add_argument("scenario", choices=SCENARIOS)
    begin.add_argument("--selector", type=parse_selector, required=True)
    begin.add_argument("--state", type=Path, default=Path(".codex_tmp/oni-integration-run.json"))
    verify = sub.add_parser("verify")
    verify.add_argument("--state", type=Path, default=Path(".codex_tmp/oni-integration-run.json"))
    verify.add_argument("--timeout", type=nonnegative_float, default=0)


def _add_traffic_parsers(sub):
    traffic = sub.add_parser("traffic-sample")
    traffic.add_argument("--duration", type=nonnegative_float, default=630)
    traffic.add_argument("--interval", type=nonnegative_float, default=1)
    traffic.add_argument("--output", type=Path, default=Path(".codex_tmp/traffic-evidence.json"))
    compare = sub.add_parser("traffic-compare")
    compare.add_argument("before", type=Path)
    compare.add_argument("after", type=Path)
    compare.add_argument("--minimum-reduction", type=nonnegative_float, default=60)
    health = sub.add_parser("traffic-health")
    health.add_argument("evidence", type=Path)


def _add_replay_parsers(sub):
    replay_measure = sub.add_parser("replay-measure")
    replay_measure.add_argument("target", choices=("host", "client"))
    replay_measure.add_argument("--command", default="replay-load:512:4096")
    replay_measure.add_argument(
        "--output", type=Path, default=Path(".codex_tmp/replay-evidence.json"))
    replay_compare = sub.add_parser("replay-compare")
    replay_compare.add_argument("before", type=Path)
    replay_compare.add_argument("after", type=Path)
    replay_compare.add_argument(
        "--minimum-reduction", type=nonnegative_float, default=50)


def _add_control_parsers(sub):
    debug = sub.add_parser("debug-command")
    debug.add_argument("target", choices=("host", "client"))
    debug.add_argument("command")
    reconnect = sub.add_parser("reconnect-client")
    reconnect.add_argument("lobby")
    reconnect.add_argument("--launch-timeout", type=nonnegative_float, default=120)
    soak = sub.add_parser("soak-verdict")
    soak.add_argument("target", choices=("host", "client"), default="host", nargs="?")
    fault = sub.add_parser("fault-execute")
    fault.add_argument("target", choices=("host", "client"))
    fault.add_argument("case_id", choices=tuple(FAULT_EXECUTION_SPECS))


def _add_run_parser(sub):
    run = sub.add_parser("run")
    run.add_argument("scenario", choices=SCENARIOS)
    run.add_argument("--selector", type=parse_selector, required=True)
    run.add_argument("--timeout", type=nonnegative_float, default=120)
    run.add_argument("--evidence-root", type=Path,
                     default=Path(".codex_tmp/integration-evidence"))
    run.add_argument("--inventory", type=Path, default=Path("sync-entry-inventory.json"))
    run.add_argument("--coverage", type=Path, default=Path("sync-entry-coverage.json"))


def build_parser():
    parser = argparse.ArgumentParser(description="ONI Together real multiplayer integration runner")
    parser.add_argument("--host", default="local")
    parser.add_argument("--client", default="alienware")
    parser.add_argument("--host-platform", choices=("macos", "windows"), default="macos")
    parser.add_argument("--client-platform", choices=("macos", "windows"), default="windows")
    parser.add_argument("--mcp-url", default="http://127.0.0.1:8788/mcp/")
    sub = parser.add_subparsers(dest="action", required=True)
    sub.add_parser("preflight")
    _add_state_parsers(sub)
    _add_traffic_parsers(sub)
    _add_replay_parsers(sub)
    _add_control_parsers(sub)
    _add_run_parser(sub)
    return parser
