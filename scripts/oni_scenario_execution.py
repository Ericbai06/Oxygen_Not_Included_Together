from __future__ import annotations

from .oni_fault_execution import run_fault_execution
from .oni_fault_execution_specs import FAULT_EXECUTION_SPECS


class ScenarioRuntimeCapabilityError(RuntimeError):
    pass


def _required_runtime_method(runtime, name):
    method = getattr(runtime, name, None)
    if not callable(method):
        raise ScenarioRuntimeCapabilityError(
            f"scenario runtime does not provide {name}")
    return method


def _native_action_driver(spec, runtime):
    trigger = spec["trigger"]
    invoke = _required_runtime_method(runtime, "invoke_native_action")
    profile = trigger.get("actionProfile")
    if profile is None:
        return invoke(trigger["commandBuilder"], trigger["target"])
    return invoke(trigger["commandBuilder"], trigger["target"], profile)


def _debug_command_driver(spec, runtime):
    trigger = spec["trigger"]
    return _required_runtime_method(runtime, "run_scenario_debug_command")(
        trigger["commandBuilder"], trigger)


def _scenario_cleanup(spec, runtime):
    return _required_runtime_method(runtime, "cleanup_scenario")(spec)


DRIVER_REGISTRY = {
    "debug-command": _debug_command_driver,
    "native-action": _native_action_driver,
}
CLEANUP_REGISTRY = {"scenario-cleanup": _scenario_cleanup}


def run_scenario_execution(spec, runtime):
    driver = DRIVER_REGISTRY[spec["driver"]]
    cleanup = CLEANUP_REGISTRY[spec["cleanup"]["executor"]]
    try:
        driver(spec, runtime)
        result = _required_runtime_method(runtime, "wait_for_typed_barrier")(
            spec["completionBarrierPredicate"],
            spec["observationWindowSeconds"],
        )
    except (AttributeError, KeyError, RuntimeError, ValueError) as failure:
        try:
            cleanup(spec, runtime)
        except RuntimeError as cleanup_failure:
            raise failure from cleanup_failure
        raise
    else:
        cleanup(spec, runtime)
        return result
