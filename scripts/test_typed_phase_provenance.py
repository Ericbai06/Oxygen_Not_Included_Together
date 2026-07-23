import unittest

import scripts.oni_scenario_contracts as contracts
from scripts.test_typed_scenario_evidence import envelope, line


def dispatch_provenance_logs() -> dict[str, str]:
    host_specs = (
        ("host-submit", 3, "sync:host-send"),
        ("final-state", 3, "sync:host-observer"),
    )
    client_specs = (
        ("revision-accepted", 3, "sync:client-revision-gate"),
        ("client-apply", 3, "sync:client-dispatch"),
        ("revision-duplicate", 3, "sync:client-revision-gate"),
        ("revision-out-of-order", 2, "sync:client-revision-gate"),
        ("client-original-blocked", 3, "sync:client-harmony"),
        ("final-state", 3, "sync:client-observer"),
    )
    host = [record_line("host", *spec, sequence=index)
            for index, spec in enumerate(host_specs, 1)]
    client = [record_line("client", *spec, sequence=index)
              for index, spec in enumerate(client_specs, 1)]
    return {"host": "\n".join(host), "client": "\n".join(client)}


def record_line(role: str, phase: str, revision: int,
                entry_id: str, sequence: int) -> str:
    record = envelope("door", role, phase, revision, sequence)
    record["entryId"] = entry_id
    return line(record)


def host_synthesized_logs() -> dict[str, str]:
    host_phases = (
        ("host-submit", 3), ("revision-accepted", 3),
        ("revision-duplicate", 3), ("revision-out-of-order", 2),
        ("final-state", 3),
    )
    client_phases = (
        ("client-apply", 3), ("client-original-blocked", 3),
        ("final-state", 3),
    )
    host = [record_line("host", phase, revision, "sync:host-send", index)
            for index, (phase, revision) in enumerate(host_phases, 1)]
    client = [record_line("client", phase, revision, "sync:host-send", index)
              for index, (phase, revision) in enumerate(client_phases, 1)]
    return {"host": "\n".join(host), "client": "\n".join(client)}


def reused_host_entry_logs() -> dict[str, str]:
    logs = dispatch_provenance_logs()
    logs["client"] = logs["client"].replace(
        '"entryId":"sync:client-dispatch"',
        '"entryId":"sync:host-send"',
    )
    return logs


class TypedPhaseProvenanceTests(unittest.TestCase):
    def test_real_client_dispatch_and_gate_provenance_is_accepted(self) -> None:
        verdict = contracts.evaluate("door", dispatch_provenance_logs(), None)
        self.assertTrue(verdict["passed"], verdict["failures"])

    def test_host_synthesized_receiver_phases_and_entry_are_rejected(self) -> None:
        verdict = contracts.evaluate("door", host_synthesized_logs(), None)
        self.assertFalse(
            verdict["passed"],
            "host endpoint and host-send entry satisfied client gate/apply causality",
        )

    def test_client_apply_cannot_reuse_host_submit_entry(self) -> None:
        verdict = contracts.evaluate("door", reused_host_entry_logs(), None)
        self.assertFalse(verdict["passed"], "client apply reused the host submit entry")


if __name__ == "__main__":
    unittest.main()
