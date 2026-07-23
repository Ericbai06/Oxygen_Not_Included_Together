from typing import Final

from .oni_scenario_contracts import SCENARIOS


NATIVE_ACTION_BUILDERS: Final = frozenset(
    f"{scenario}-action"
    for scenario in SCENARIOS
    if scenario not in {"building-lifecycle", "reconnect-world-state"}
)
ACTION_PROFILES: Final = {
    "building-config": "toggle-checkbox",
    "uproot": "mark-and-cancel-uproot",
    "inventory": "add-remove-sand-1000g",
    "pickup": "primary-duplicant-pickup-drop",
    "effect": "toggle-integration-effect",
    "animation": "primary-minion-working-loop",
    "motion": "offset-one-cell-one-tick",
    "entity-lifecycle": "deactivate-reactivate-same-prefab",
    "dlc-runtime": "next-admissible-state-restore",
    "rocket": "next-reachable-boarding-restore",
}
DEBUG_COMMAND_BUILDERS: Final = frozenset(
    {"build:prefab-cell-material", "reconnect-evidence"}
)
REGISTERED_DRIVERS: Final = frozenset({"debug-command", "native-action"})
REGISTERED_CLEANUPS: Final = frozenset({"scenario-cleanup"})
SPECIAL_TRIGGERS: Final = {
    "building-lifecycle": {
        "type": "debug-command",
        "commandBuilder": "build:prefab-cell-material",
        "target": "host",
        "prefab": "Tile",
        "material": "SandStone",
    },
    "reconnect-world-state": {
        "type": "debug-command",
        "commandBuilder": "reconnect-evidence",
        "target": "client",
    },
}
SELECTOR_KINDS: Final = {
    "remote-dig": "cell",
    "animation": "cell",
    "motion": "netId",
    "effect": "netId",
    "building-lifecycle": "cell",
    "priority": "netId",
    "building-config": "netId",
    "door": "netId",
    "uproot": "netId",
    "toggle": "netId",
    "research": "tech",
    "schedule": "schedule",
    "inventory": "inventory",
    "storage": "storage",
    "pickup": "pickup",
    "deconstruct": "deconstruct",
    "chat": "sender",
    "cursor": "player",
    "entity-lifecycle": "netId",
    "dlc-runtime": "dlc",
    "rocket": "rocket",
    "reconnect-world-state": "session",
}


def _trigger(scenario):
    special = SPECIAL_TRIGGERS.get(scenario)
    if special is not None:
        return dict(special)
    trigger = {
        "type": "native-action",
        "commandBuilder": f"{scenario}-action",
        "target": "host",
    }
    profile = ACTION_PROFILES.get(scenario)
    if profile is not None:
        trigger["actionProfile"] = profile
    return trigger


def _spec(scenario):
    barrier = f"typed-final-state:{scenario}"
    return {
        "driver": _trigger(scenario)["type"],
        "trigger": _trigger(scenario),
        "targetSelectorKind": SELECTOR_KINDS[scenario],
        "completionBarrierPredicate": barrier,
        "observationWindowSeconds": 120,
        "cleanup": {"type": "callable", "executor": "scenario-cleanup"},
        "acceptanceSource": "typed-evidence",
        "completionBarrier": barrier,
    }


SCENARIO_EXECUTION_SPECS: Final = {
    scenario: _spec(scenario) for scenario in SCENARIOS
}


def _validate_trigger(scenario, trigger):
    if not isinstance(trigger, dict):
        return [f"{scenario}: trigger must be a typed object"]
    expected = _trigger(scenario)
    trigger_type = trigger.get("type")
    builder = trigger.get("commandBuilder")
    if trigger_type == "debug-command":
        valid = builder in DEBUG_COMMAND_BUILDERS
    elif trigger_type == "native-action":
        valid = builder in NATIVE_ACTION_BUILDERS
    else:
        valid = False
    failures = [] if valid else [f"{scenario}: trigger is not registered"]
    expected_profile = ACTION_PROFILES.get(scenario)
    if trigger.get("actionProfile") != expected_profile:
        failures.append(f"{scenario}: action profile is invalid")
    if expected_profile is None and "actionProfile" in trigger:
        failures.append(f"{scenario}: action profile is not allowed")
    if trigger != expected:
        failures.append(f"{scenario}: trigger does not match its exact scenario action")
    if trigger.get("target") not in {"host", "client"}:
        failures.append(f"{scenario}: trigger target is invalid")
    return failures


def validate_execution_specs(specs):
    failures = []
    if set(specs) != set(SCENARIOS):
        failures.append("execution specs must exactly cover the scenario catalog")
    for scenario, spec in specs.items():
        if not isinstance(spec, dict):
            failures.append(f"{scenario}: execution spec must be an object")
            continue
        if spec.get("driver") not in REGISTERED_DRIVERS:
            failures.append(f"{scenario}: driver is not registered")
        failures.extend(_validate_trigger(scenario, spec.get("trigger")))
        trigger = spec.get("trigger")
        if isinstance(trigger, dict) and spec.get("driver") != trigger.get("type"):
            failures.append(f"{scenario}: driver does not match trigger type")
        if spec.get("targetSelectorKind") != SELECTOR_KINDS.get(scenario):
            failures.append(f"{scenario}: target selector kind is invalid")
        expected_barrier = f"typed-final-state:{scenario}"
        if spec.get("completionBarrierPredicate") != expected_barrier:
            failures.append(f"{scenario}: typed completion barrier is invalid")
        if spec.get("completionBarrier") != expected_barrier:
            failures.append(f"{scenario}: completion barrier alias is invalid")
        if spec.get("acceptanceSource") != "typed-evidence":
            failures.append(f"{scenario}: acceptance source must be typed evidence")
        cleanup = spec.get("cleanup")
        if not isinstance(cleanup, dict) or cleanup != {
            "type": "callable", "executor": "scenario-cleanup"
        }:
            failures.append(f"{scenario}: cleanup is not registered")
        window = spec.get("observationWindowSeconds")
        if isinstance(window, bool) or not isinstance(window, (int, float)) or window <= 0:
            failures.append(f"{scenario}: observation window is invalid")
    return failures
