import re
from typing import TypedDict


class DebugOutcome(TypedDict):
    passed: bool
    reason: str


STATUS_LINE = re.compile(
    r"\[DebugCommand\]\[(OK|FAIL)\] command=status reason=([^\r\n]+)"
)
DEBUG_OUTCOME = re.compile(
    r"\[DebugCommand\]\[(OK|FAIL)\] command=([^\s]+)(?: [^\r\n]*?)? " +
    r"reason=([^\r\n]+)"
)
UNIT_TEST_RUN = re.compile(r"\[UnitTests\] Run (?:all|failed):")


def parse_status_line(text: str) -> dict[str, str | bool] | None:
    matches = list(STATUS_LINE.finditer(text))
    if not matches:
        return None
    match = matches[-1]
    fields: dict[str, str] = {}
    for item in match.group(2).split(";"):
        key, separator, value = item.partition("=")
        if separator:
            fields[key] = value
    return {"ready": match.group(1) == "OK", **fields}


def unit_tests_ran_in_current_process(text: str) -> bool:
    return UNIT_TEST_RUN.search(text) is not None


def normalized_debug_command(command: str) -> str:
    for prefix in ("steam-join:", "build:", "replay-load:"):
        if command.startswith(prefix):
            return prefix[:-1]
    return command


def parse_debug_outcome(text: str, command: str) -> DebugOutcome | None:
    expected = normalized_debug_command(command)
    for match in DEBUG_OUTCOME.finditer(text):
        if match.group(2) == expected:
            return {"passed": match.group(1) == "OK", "reason": match.group(3)}
    return None
