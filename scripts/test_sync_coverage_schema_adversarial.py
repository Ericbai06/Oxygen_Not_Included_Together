import copy
from typing import cast
import unittest

from scripts.oni_execution_receipts import JsonValue
from scripts.test_sync_coverage_adversarial import validate
from scripts.test_sync_coverage_receipts import active_coverage, receipt


def coverage_entry(value: dict[str, JsonValue]) -> dict[str, JsonValue]:
    entries = value.get("entries")
    if not isinstance(entries, list) or not entries or not isinstance(entries[0], dict):
        raise AssertionError("coverage fixture lacks its first entry")
    return entries[0]


class CoverageRowSchemaAdversarialTests(unittest.TestCase):
    def test_required_coverage_row_fields_are_exact_and_typed(self):
        required = (
            "domain", "status", "variants", "testIds",
            "negativeTestIds", "scenarioIds",
        )
        for field in required:
            coverage = cast(dict[str, JsonValue], copy.deepcopy(active_coverage()))
            del coverage_entry(coverage)[field]

            with self.subTest(field=field), self.assertRaises(ValueError):
                validate(coverage, [receipt()])

        for field in ("variants", "testIds", "negativeTestIds", "scenarioIds"):
            coverage = cast(dict[str, JsonValue], copy.deepcopy(active_coverage()))
            coverage_entry(coverage)[field] = "not-an-array"

            with self.subTest(field=f"{field}:type"), self.assertRaises(ValueError):
                validate(coverage, [receipt()])

    def test_invalid_coverage_status_fails_closed(self):
        coverage = cast(dict[str, JsonValue], active_coverage())
        coverage_entry(coverage)["status"] = "BogusStatus"

        with self.assertRaises(ValueError):
            validate(coverage, [receipt()])

    def test_headless_reason_is_missing_or_nonempty_never_null(self):
        missing = cast(dict[str, JsonValue], active_coverage())
        self.assertNotIn("headlessUnsupportedReason", coverage_entry(missing))
        _ = validate(missing, [receipt()])

        nonempty = cast(dict[str, JsonValue], active_coverage())
        coverage_entry(nonempty)["headlessUnsupportedReason"] = "requires ONI runtime"
        _ = validate(nonempty, [receipt()])

        explicit_null = cast(dict[str, JsonValue], active_coverage())
        coverage_entry(explicit_null)["headlessUnsupportedReason"] = None
        with self.assertRaisesRegex(ValueError, "headlessUnsupportedReason"):
            validate(explicit_null, [receipt()])


if __name__ == "__main__":
    unittest.main()
