import hashlib
import os
from pathlib import Path, PureWindowsPath
import re
import subprocess
import tempfile
import time

from scripts.oni_target_contracts import (
    SCENARIO_EXECUTION_SPECS,
    build_evidence_bundle,
    resolve_enabled_mod,
    resolve_loaded_mod_dll,
    validate_execution_specs,
    validate_multiplayer_preflight,
    validate_target_selector,
    write_evidence_bundle_atomic,
)
from .oni_execution_receipts import parse_execution_receipts
from .oni_coverage_gate import canonical_coverage_digest, validate_execution_coverage
from scripts.oni_scenario_execution_specs import ACTION_PROFILES, NATIVE_ACTION_BUILDERS


PERSISTENT_REL = "Library/Application Support/unity.Klei.Oxygen Not Included"
LOG_REL = "Library/Logs/Klei/Oxygen Not Included/Player.log"
COMMAND_REL = f"{PERSISTENT_REL}/oni_together_debug_command.txt"
MCP_DLL_REL = f"{PERSISTENT_REL}/mods/Local/OniMcp/OniMcp.dll"
MODS_REL = f"{PERSISTENT_REL}/mods.json"
SAFE_TARGET = re.compile(r"^[A-Za-z0-9_.@-]+$")
SAFE_SELECTOR_VALUE = re.compile(r"^[A-Za-z0-9_.@-]+$")
INTEGER_SELECTOR_FIELDS = frozenset({
    "cell", "netId", "storageNetId", "itemNetId", "targetCell",
    "buildingNetId", "rocketNetId", "padNetId",
})
POLL_INTERVAL = 0.25


def _canonical_selector(selector):
    parts = []
    for key in sorted(selector):
        value = selector[key]
        if isinstance(value, bool):
            raise ValueError(f"invalid selector value: {key}")
        text = str(value)
        if SAFE_SELECTOR_VALUE.fullmatch(text) is None:
            raise ValueError(f"unsafe selector value: {key}")
        parts.append(f"{key}={text}")
    return ":".join(parts)


def _build_scenario_command(operation, scenario, selector, profile=None):
    failures = validate_target_selector(scenario, selector)
    if failures:
        raise ValueError("; ".join(failures))
    expected_profile = ACTION_PROFILES.get(scenario)
    if profile != expected_profile:
        raise ValueError(f"invalid action profile for scenario: {scenario}")
    match operation:
        case "action":
            if f"{scenario}-action" not in NATIVE_ACTION_BUILDERS:
                raise ValueError(f"unsupported native action scenario: {scenario}")
        case "cleanup":
            if scenario not in SCENARIO_EXECUTION_SPECS:
                raise ValueError(f"unsupported cleanup scenario: {scenario}")
        case _:
            raise ValueError(f"unsupported scenario operation: {operation}")
    command_fields = dict(selector)
    if profile is not None:
        command_fields["profile"] = profile
    return f"scenario-{operation}:{scenario}:{_canonical_selector(command_fields)}"


def build_native_action_command(builder, selector, profile=None):
    if builder not in NATIVE_ACTION_BUILDERS:
        raise ValueError(f"unsupported native action builder: {builder}")
    scenario = builder.removesuffix("-action")
    return _build_scenario_command("action", scenario, selector, profile)


def build_scenario_cleanup_command(scenario, selector, profile=None):
    return _build_scenario_command("cleanup", scenario, selector, profile)


def _parse_selector(parts):
    selector = {}
    for part in parts:
        if part.count("=") != 1:
            return None
        key, value = part.split("=", 1)
        if not key or key in selector or SAFE_SELECTOR_VALUE.fullmatch(value) is None:
            return None
        if key in INTEGER_SELECTOR_FIELDS:
            if not value.isdecimal():
                return None
            selector[key] = int(value)
        else:
            selector[key] = value
    return selector


def _valid_scenario_command(command):
    match = re.fullmatch(
        r"scenario-(action|cleanup):([a-z][a-z0-9-]*):(.+)", command)
    if match is None:
        return False
    operation, scenario, encoded = match.groups()
    selector = _parse_selector(encoded.split(":"))
    if selector is None:
        return False
    profile = selector.pop("profile", None)
    try:
        expected = _build_scenario_command(operation, scenario, selector, profile)
    except ValueError:
        return False
    return command == expected


def valid_debug_command(command):
    fixed = {
        "tests", "riptide", "host", "join", "steam-host", "status",
        "checkpoint", "hard-sync", "pause", "soak", "reconnect-evidence",
    }
    build = r"build:[A-Za-z0-9_-]{1,128}:[0-9]{1,7}:[A-Za-z0-9_-]{1,128}"
    replay_load = re.fullmatch(r"replay-load:([0-9]{1,3}):([0-9]{3,4})", command)
    if replay_load is not None:
        frames, payload_bytes = map(int, replay_load.groups())
        return 1 <= frames <= 512 and 256 <= payload_bytes <= 4096
    return (
        command in fixed
        or _valid_scenario_command(command)
        or re.fullmatch(r"steam-join:[A-Z0-9]+", command) is not None
        or re.fullmatch(build, command) is not None
    )


def submit_local(path, command):
    claim = Path(str(path) + ".processing")
    if path.exists() or claim.exists():
        raise RuntimeError(f"Debug command slot is busy: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    handle, temp_name = tempfile.mkstemp(
        prefix=".oni-command-", dir=path.parent, text=True)
    try:
        with os.fdopen(handle, "w") as stream:
            stream.write(command + "\n")
        os.replace(temp_name, path)
    except Exception:
        Path(temp_name).unlink(missing_ok=True)
        raise


class Target:
    def __init__(self, name):
        if name != "local" and SAFE_TARGET.fullmatch(name) is None:
            raise ValueError(f"Unsafe SSH target: {name}")
        self.name = name

    def _remote(self, command, input_text=None):
        result = subprocess.run(
            ["ssh", "-o", "BatchMode=yes", self.name, command],
            input=None if input_text is None else input_text.encode("utf-8"),
            capture_output=True,
        )
        stdout = result.stdout.decode("utf-8", errors="replace")
        stderr = result.stderr.decode("utf-8", errors="replace")
        if result.returncode:
            raise RuntimeError(stderr.strip() or stdout.strip() or command)
        return stdout

    @staticmethod
    def _validate_rel(relative):
        if relative.startswith("/") or ".." in Path(relative).parts:
            raise ValueError(f"Path must be home-relative: {relative}")
        return relative.replace('"', '\\"').replace("`", "")

    def _local_path(self, relative):
        return Path.home() / self._validate_rel(relative)

    def size(self, relative):
        if self.name == "local":
            return self._local_path(relative).stat().st_size
        path = self._validate_rel(relative)
        return int(self._remote(f'stat -f %z "$HOME/{path}"').strip())

    def read(self, relative, offset=0):
        if self.name == "local":
            with self._local_path(relative).open("rb") as stream:
                stream.seek(offset)
                return stream.read().decode("utf-8", errors="replace")
        path = self._validate_rel(relative)
        return self._remote(f'tail -c +{offset + 1} "$HOME/{path}"')

    def sha256(self, relative):
        if self.name == "local":
            data = self._local_path(relative).read_bytes()
            return hashlib.sha256(data).hexdigest()
        path = self._validate_rel(relative)
        return self._remote(f'shasum -a 256 "$HOME/{path}"').split()[0]

    def game_running(self):
        if self.name == "local":
            result = subprocess.run(
                ["pgrep", "-f", "[O]xygenNotIncluded"], capture_output=True)
            return result.returncode == 0
        result = subprocess.run([
            "ssh", "-o", "BatchMode=yes", self.name,
            "pgrep -f '[O]xygenNotIncluded'",
        ], capture_output=True)
        return result.returncode == 0

    def _activate_game(self):
        if self.name != "local":
            self._remote("open -a 'Oxygen Not Included'")
            return
        result = subprocess.run(
            ["open", "-a", "Oxygen Not Included"], text=True, capture_output=True)
        if result.returncode:
            raise RuntimeError(result.stderr.strip() or "Failed to activate ONI")

    def restart_game(self, timeout=120):
        if self.name == "local":
            subprocess.run([
                "osascript", "-e", 'tell application "Oxygen Not Included" to quit'
            ], check=False, capture_output=True)
        else:
            self._remote(
                "osascript -e 'tell application \"Oxygen Not Included\" to quit'")
        self._wait_for_game_state(False, timeout)
        if self.name == "local":
            subprocess.run(["open", "steam://run/457140"], check=True)
        else:
            self._remote("open 'steam://run/457140'")
        self._wait_for_game_state(True, timeout)

    def _wait_for_game_state(self, running, timeout):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self.game_running() == running:
                return
            time.sleep(POLL_INTERVAL)
        state = "start" if running else "quit"
        raise RuntimeError(f"{self.name}: ONI did not {state} within {timeout}s")

    def submit(self, command):
        if not valid_debug_command(command):
            raise ValueError(f"Unsupported debug command: {command}")
        if not self.game_running():
            raise RuntimeError(f"{self.name}: ONI is not running")
        if self.name == "local":
            submit_local(self._local_path(COMMAND_REL), command)
            self._activate_game()
            return
        path = self._validate_rel(COMMAND_REL)
        script = (
            f'test ! -e "$HOME/{path}.processing" && test ! -e "$HOME/{path}" '
            f'&& tmp="$HOME/{path}.agent.$$" && umask 077 && cat > "$tmp" '
            f'&& mv "$tmp" "$HOME/{path}"'
        )
        self._remote(script, command + "\n")
        self._activate_game()


class MacTargetAdapter:
    def __init__(self, name):
        if name != "local" and SAFE_TARGET.fullmatch(name) is None:
            raise ValueError(f"Unsafe SSH target: {name}")
        self.name = name

    def _run(self, command):
        result = subprocess.run(command, capture_output=True)
        if result.returncode:
            raise RuntimeError(result.stderr.decode(errors="replace").strip())
        return result.stdout

    @staticmethod
    def _quoted(path):
        return "'" + str(path).replace("'", "'\"'\"'") + "'"

    def read_text(self, path, offset=0):
        if self.name == "local":
            with Path(path).open("rb") as stream:
                stream.seek(offset)
                return stream.read().decode("utf-8", errors="replace")
        remote = f"tail -c +{offset + 1} -- {self._quoted(path)}"
        return self._run(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                          self.name, remote]).decode(
            "utf-8", errors="replace")

    def size(self, path):
        if self.name == "local":
            return Path(path).stat().st_size
        remote = f"stat -f %z -- {self._quoted(path)}"
        return int(self._run(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                              self.name, remote]).strip())

    def sha256(self, path):
        if self.name == "local":
            return hashlib.sha256(Path(path).read_bytes()).hexdigest()
        remote = f"shasum -a 256 -- {self._quoted(path)}"
        digest = self._run(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                            self.name, remote]).split()[0]
        return digest.decode().lower()

    def exists(self, path):
        if self.name == "local":
            return Path(path).is_file()
        remote = f"test -f {self._quoted(path)}"
        return subprocess.run(
            ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
             self.name, remote], capture_output=True
        ).returncode == 0

    def game_data_root(self):
        if self.name == "local":
            return Path.home() / PERSISTENT_REL
        home = self._run([
            "ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
            self.name, "printf %s \"$HOME\"",
        ]).decode()
        return Path(home) / PERSISTENT_REL

    def player_log_path(self):
        if self.name == "local":
            return Path.home() / LOG_REL
        home = self._run([
            "ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
            self.name, "printf %s \"$HOME\"",
        ]).decode()
        return Path(home) / LOG_REL

    def game_running(self):
        if self.name == "local":
            return subprocess.run(
                ["pgrep", "-f", "[O]xygenNotIncluded"], capture_output=True,
            ).returncode == 0
        result = subprocess.run([
            "ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
            self.name, "pgrep -f '[O]xygenNotIncluded'",
        ], capture_output=True)
        return result.returncode == 0

    def _activate_game(self):
        command = ["open", "-a", "Oxygen Not Included"]
        if self.name != "local":
            command = ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                       self.name, "open -a 'Oxygen Not Included'"]
        self._run(command)

    def submit(self, command):
        if not valid_debug_command(command):
            raise ValueError(f"Unsupported debug command: {command}")
        if not self.game_running():
            raise RuntimeError(f"{self.name}: ONI is not running")
        path = self.game_data_root() / "oni_together_debug_command.txt"
        if self.name == "local":
            submit_local(path, command)
        else:
            quoted = self._quoted(path)
            remote = (
                f"test ! -e {quoted}.processing && test ! -e {quoted} "
                f"&& t={quoted}.agent.$$ && umask 077 "
                f"&& printf '%s\\n' '{command}' > \"$t\" && mv \"$t\" {quoted}"
            )
            self._run(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                       self.name, remote])
        self._activate_game()

    def invoke_native_action(self, builder, selector, profile=None):
        self.submit(build_native_action_command(builder, selector, profile))

    def cleanup_scenario(self, scenario, selector, profile=None):
        self.submit(build_scenario_cleanup_command(scenario, selector, profile))

    def restart_game(self, timeout=120):
        Target(self.name).restart_game(timeout)


class WindowsTargetAdapter:
    def __init__(self, name):
        if SAFE_TARGET.fullmatch(name) is None:
            raise ValueError(f"Unsafe SSH target: {name}")
        self.name = name

    def _powershell(self, script):
        import base64
        encoded = base64.b64encode(script.encode("utf-16-le")).decode("ascii")
        result = subprocess.run([
            "ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
            self.name, "powershell.exe",
            "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded,
        ], capture_output=True)
        if result.returncode:
            raise RuntimeError(result.stderr.decode(errors="replace").strip())
        return result.stdout.decode("utf-8", errors="replace")

    @staticmethod
    def _literal(path):
        return str(path).replace("'", "''")

    def read_text(self, path, offset=0):
        script = (f"$b=[IO.File]::ReadAllBytes('{self._literal(path)}');"
                  f"[Console]::OpenStandardOutput().Write($b,{offset},$b.Length-{offset})")
        return self._powershell(script)

    def size(self, path):
        return int(self._powershell(
            f"(Get-Item -LiteralPath '{self._literal(path)}').Length").strip())

    def sha256(self, path):
        script = f"(Get-FileHash -Algorithm SHA256 -LiteralPath '{self._literal(path)}').Hash"
        digest = self._powershell(script).strip().lower()
        if re.fullmatch(r"[0-9a-f]{64}", digest) is None:
            raise ValueError("PowerShell returned an invalid SHA-256 digest")
        return digest

    def exists(self, path):
        return self._powershell(
            f"if (Test-Path -LiteralPath '{self._literal(path)}' -PathType Leaf) {{'1'}} else {{'0'}}"
        ).strip() == "1"

    def game_data_root(self):
        home = self._powershell("$env:USERPROFILE").strip()
        return PureWindowsPath(home) / "AppData/LocalLow/Klei/Oxygen Not Included"

    def player_log_path(self):
        return self.game_data_root() / "Player.log"

    def game_running(self):
        script = "if (Get-Process OxygenNotIncluded -ErrorAction SilentlyContinue) {'1'} else {'0'}"
        return self._powershell(script).strip() == "1"

    def submit(self, command):
        if not valid_debug_command(command):
            raise ValueError(f"Unsupported debug command: {command}")
        if not self.game_running():
            raise RuntimeError(f"{self.name}: ONI is not running")
        path = self.game_data_root() / "oni_together_debug_command.txt"
        literal = self._literal(path)
        payload = command.replace("'", "''")
        script = (
            f"$p='{literal}';$c=$p+'.processing';"
            "if ((Test-Path -LiteralPath $p) -or (Test-Path -LiteralPath $c)) {exit 17};"
            f"$t=$p+'.agent.'+$PID;[IO.File]::WriteAllText($t,'{payload}`n');"
            "Move-Item -LiteralPath $t -Destination $p"
        )
        self._powershell(script)

    def invoke_native_action(self, builder, selector, profile=None):
        self.submit(build_native_action_command(builder, selector, profile))

    def cleanup_scenario(self, scenario, selector, profile=None):
        self.submit(build_scenario_cleanup_command(scenario, selector, profile))

    def restart_game(self, timeout=120):
        self._powershell(
            "Get-Process OxygenNotIncluded -ErrorAction SilentlyContinue | Stop-Process -Force;"
            "Start-Process 'steam://run/457140'")
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self.game_running():
                return
            time.sleep(POLL_INTERVAL)
        raise RuntimeError(f"{self.name}: ONI did not start within {timeout}s")


def create_target_adapter(platform, name):
    if platform == "macos":
        return MacTargetAdapter(name)
    if platform == "windows":
        return WindowsTargetAdapter(name)
    raise ValueError(f"unsupported target platform: {platform}")


def resolve_adapter_artifacts(adapter):
    root = adapter.game_data_root()
    mods_root = root / "mods"
    mods_data = __import__("json").loads(adapter.read_text(root / "mods.json"))
    player_log = adapter.read_text(adapter.player_log_path())
    dll_path = resolve_loaded_mod_dll(
        mods_data, player_log, mods_root, exists=adapter.exists)
    pdb_path = dll_path.with_suffix(".pdb")
    if not adapter.exists(pdb_path):
        raise FileNotFoundError(
            f"loaded ONI Together portable PDB is missing: {pdb_path}")
    return {
        "dllPath": str(dll_path),
        "dllHash": "sha256:" + adapter.sha256(dll_path),
        "pdbPath": str(pdb_path),
        "pdbHash": "sha256:" + adapter.sha256(pdb_path),
        "modsData": mods_data,
        "playerLogPath": str(adapter.player_log_path()),
    }
