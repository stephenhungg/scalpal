#!/usr/bin/env python3
"""Regression checks for PID-scoped unified smoke assessment (no device needed)."""

import unittest

from native_smoke import assess_logs, device_time


def flow(scene="Launch", stamp=109, **overrides):
    values = dict(scene=scene, xr="True", head="True", focus="True")
    values.update(overrides)
    return f"{stamp}.000 1 1 I Unity: SCALPAL_FLOW_STATUS " + " ".join(f"{k}={v}" for k, v in values.items())


def native(stamp=109, **overrides):
    values = dict(xr="True", head="True", floor="True", focus="True", aligned="True")
    values.update(overrides)
    return f"{stamp}.000 1 1 I Unity: SCALPAL_NATIVE_STATUS " + " ".join(f"{k}={v}" for k, v in values.items())


class SmokeAssessmentTests(unittest.TestCase):
    def check(self, logs, code, expected_scene=None, now=110, since=100):
        result, messages = assess_logs(logs, now, since, expected_scene)
        self.assertEqual(code, result, messages)
        return "\n".join(messages)

    def test_ready_full_flow_non_or_scenes_without_native_marker(self):
        for scene in ("Launch", "ScalpalShell", "DiagnosisOffice", "RunEnding"):
            with self.subTest(scene=scene):
                message = self.check(flow(scene), 0, scene)
                self.assertIn("not evidence", message)

    def test_or_keeps_native_floor_alignment_gate(self):
        self.check(flow("NativeSession"), 2)
        self.check(flow("NativeSession") + "\n" + native(), 0)
        for key in ("xr", "head", "floor", "focus", "aligned"):
            with self.subTest(key=key):
                self.check(flow("NativeSession") + "\n" + native(**{key: "False"}), 2)

    def test_legacy_native_workbench_preserved_without_flow_marker(self):
        self.check(native(), 0)
        self.check(native(), 2, "NativeSession")
        self.check(flow("NativeWorkbench") + "\n" + native(), 0)

    def test_freshness_uses_final_sample_and_observation_window(self):
        self.check(flow(stamp=104), 2)
        self.check(flow(stamp=105), 0)
        self.check(flow(stamp=99), 2)
        self.check(flow(stamp=112), 2)
        self.check(flow().replace("109.000", "110.999"), 0)
        self.check(flow().replace("109.000", "111.000"), 2)
        self.check(flow() + "\n" + native(stamp=99), 0)
        self.check(flow("NativeSession") + "\n" + native(stamp=104), 2)

    def test_latest_status_wins_not_earlier_good_state(self):
        self.check(flow(stamp=108) + "\n" + flow(stamp=109, head="False"), 2)
        self.check(flow(stamp=109, head="False") + "\n" + flow(stamp=108), 2)
        self.check(flow("NativeSession") + "\n" + native(stamp=108) + "\n" + native(focus="False"), 2)

    def test_each_flow_readiness_field_and_malformed_boolean_can_fail(self):
        for key in ("xr", "head", "focus"):
            for value in ("False", "Trueish", "true", "missing"):
                with self.subTest(key=key, value=value):
                    self.check(flow(**{key: value}), 2)
        self.check(flow() + " xr=False", 2)
        self.check("109 I Unity: SCALPAL_FLOW_STATUS scene=Launch xr=True focus=True", 2)

    def test_scene_assertion_and_unknown_scene(self):
        self.check(flow("Launch"), 2, "DiagnosisOffice")
        message = self.check(flow("private-patient-name"), 2)
        self.assertNotIn("private-patient-name", message)
        self.check(flow("Other") + "\n" + native(), 2)

    def test_current_exceptions_fail_but_retained_ones_do_not(self):
        for exception in ("NullReferenceException: secret-credential", "FATAL EXCEPTION", "Fatal signal 11", "SCALPAL_NATIVE_BINDING_ERROR"):
            with self.subTest(exception=exception):
                message = self.check(flow() + f"\n108 I Unity: {exception}", 1)
                self.assertNotIn(exception, message)
                self.check(flow() + f"\n99 I Unity: {exception}", 0)

    def test_raw_context_and_non_allowlisted_status_values_are_not_printed(self):
        message = self.check(flow() + " patient=secret-session apiKey=secret-token" + "\n109 I Unity: unrelated endpoint=https://private", 0)
        self.assertNotIn("secret", message)
        self.assertNotIn("endpoint", message)
        message = self.check(flow(focus="secret-token"), 2)
        self.assertNotIn("secret-token", message)

    def test_unavailable_invalid_or_reversed_clock_does_not_pass(self):
        for now, since in ((None, 100), (110, None), (99, 100), (float("nan"), 100), (110, float("inf"))):
            with self.subTest(now=now, since=since):
                self.check(flow(), 2, now=now, since=since)
        for value in ("", "nan", "inf", "-inf", "garbage"):
            self.assertIsNone(device_time(value))
        self.assertEqual(110.0, device_time("110\n"))


if __name__ == "__main__":
    unittest.main()
