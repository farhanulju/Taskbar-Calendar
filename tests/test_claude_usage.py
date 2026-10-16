import json
import os
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest import mock

from taskbar_calendar import claude_usage as cu


class CountdownTest(unittest.TestCase):
    def test_formats(self):
        self.assertEqual(
            [cu.fmt_countdown(s) for s in (-5, 59, 61, 3599, 3660, 86399, 90000, 6 * 86400 + 23 * 3600)],
            ["0m", "0m", "1m", "59m", "1h 01m", "23h 59m", "1d 1h", "6d 23h"],
        )


class PaceTest(unittest.TestCase):
    length = 5 * 3600

    def test_under_pace_never_runs_out(self):
        # 20% used, 2.5 h into a 5 h window.
        elapsed, runs_out = cu.usage_pace(20, 2.5 * 3600, self.length, 0)
        self.assertAlmostEqual(elapsed, 0.5)
        self.assertIsNone(runs_out)

    def test_over_pace_predicts_when_limit_is_hit(self):
        # 60% used after 1 h: 60%/h, so the remaining 40% lasts 40 min.
        elapsed, runs_out = cu.usage_pace(60, 4 * 3600, self.length, 0)
        self.assertAlmostEqual(elapsed, 0.2)
        self.assertAlmostEqual(runs_out, 40 * 60)

    def test_too_early_in_window_to_tell(self):
        self.assertIsNone(cu.usage_pace(30, self.length - 60, self.length, 0)[1])


class ProbeOutputTest(unittest.TestCase):
    STREAM = "\n".join([
        json.dumps({"type": "system", "subtype": "init"}),
        "not json",
        json.dumps({"type": "rate_limit_event", "rate_limit_info": {"unifiedWindows": {
            "five_hour": {"utilization": 0.17, "resetsAt": 1792002000},
            "seven_day": {"utilization": 0.53, "resetsAt": 1792162800},
            "other": {"utilization": 0.9},
        }}}),
        json.dumps({"type": "result", "usage": {"input_tokens": 2, "output_tokens": 4,
                                                "cache_creation_input_tokens": 955, "cache_read_input_tokens": 0}}),
    ])

    def test_parses_both_windows_as_percentages(self):
        self.assertEqual(cu.parse_probe_output(self.STREAM), {
            "five_hour": {"used_percentage": 17.0, "resets_at": 1792002000},
            "seven_day": {"used_percentage": 53.0, "resets_at": 1792162800},
        })

    def test_counts_tokens(self):
        self.assertEqual(cu.probe_tokens(self.STREAM), 961)

    def test_missing_event(self):
        self.assertIsNone(cu.parse_probe_output(json.dumps({"type": "result"})))


class AtomicWriteTest(unittest.TestCase):
    def test_waits_for_a_reader_to_let_go(self):
        with tempfile.TemporaryDirectory() as tmp:
            target = Path(tmp) / "claude_usage.json"
            target.write_text("old")
            reader = open(target)  # Windows: an open handle blocks replacing the file
            threading.Timer(0.3, reader.close).start()
            cu.atomic_write(target, "new")
            self.assertEqual(target.read_text(), "new")
            self.assertEqual([p.name for p in Path(tmp).iterdir()], ["claude_usage.json"])  # no temp files left


class StatusLineScriptTest(unittest.TestCase):
    def run_script(self, payload, localappdata):
        env = {**os.environ, "LOCALAPPDATA": localappdata}
        return subprocess.run([sys.executable, str(cu.STATUSLINE_SCRIPT)], input=json.dumps(payload).encode(),
                              capture_output=True, env=env, check=True).stdout.decode("utf-8").strip()

    def test_saves_usage_and_prints_summary(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = self.run_script({"rate_limits": {"five_hour": {"used_percentage": 23.5, "resets_at": 1},
                                                   "seven_day": {"used_percentage": 41.2, "resets_at": 2}}}, tmp)
            self.assertEqual(out, "5h 24% · wk 41%")
            saved = json.loads((Path(tmp) / "TaskbarCalendar" / "claude_usage.json").read_text())
            self.assertEqual(saved["five_hour"]["used_percentage"], 23.5)

    def test_without_rate_limits_writes_nothing(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.assertEqual(self.run_script({"model": {}}, tmp), "")
            self.assertFalse((Path(tmp) / "TaskbarCalendar").exists())


class StatusLineSettingsTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.original = cu.CLAUDE_SETTINGS
        cu.CLAUDE_SETTINGS = Path(self.tmp.name) / "settings.json"
        self.addCleanup(setattr, cu, "CLAUDE_SETTINGS", self.original)

    def write(self, data):
        cu.CLAUDE_SETTINGS.write_text(json.dumps(data))

    def test_install_keeps_other_settings_and_remove_undoes_it(self):
        self.write({"model": "opus"})
        self.assertEqual(cu.statusline_state(), "none")
        cu.install_statusline()
        self.assertEqual(cu.statusline_state(), "ours")
        cu.remove_statusline()
        self.assertEqual(json.loads(cu.CLAUDE_SETTINGS.read_text()), {"model": "opus"})

    def test_someone_elses_status_line_is_left_alone(self):
        self.write({"statusLine": {"type": "command", "command": "my-prompt.sh"}})
        self.assertEqual(cu.statusline_state(), "other")
        with self.assertRaises(RuntimeError):
            cu.install_statusline()
        cu.remove_statusline()
        self.assertEqual(cu.statusline_state(), "other")


if __name__ == "__main__":
    unittest.main()


class FakeResponse:
    def __init__(self, status, body=None, headers=None):
        self.status_code, self._body, self.headers = status, body or {}, headers or {}

    def json(self):
        return self._body

    def raise_for_status(self):
        if self.status_code >= 400:
            raise RuntimeError(self.status_code)


USAGE_BODY = {
    "five_hour": {"utilization": 27.0, "resets_at": "2026-10-16T14:19:59.649435+00:00"},
    "seven_day": {"utilization": 93.0, "resets_at": "2026-10-16T14:59:59+00:00"},
    "seven_day_opus": None,
}


class FreeUsageServiceTest(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.creds = Path(tmp.name) / ".credentials.json"
        patcher = mock.patch.object(cu, "CREDENTIALS", self.creds)
        patcher.start()
        self.addCleanup(patcher.stop)

    def sign_in(self, token="tok-1", expires_in=3600):
        self.creds.write_text(json.dumps({"claudeAiOauth": {"accessToken": token,
                                                            "expiresAt": int((time.time() + expires_in) * 1000)}}))

    def test_parses_percentages_and_reset_times(self):
        windows = cu.parse_usage_response(USAGE_BODY)
        self.assertEqual(windows["five_hour"]["used_percentage"], 27.0)
        self.assertAlmostEqual(windows["seven_day"]["resets_at"], 1792162799.0)
        self.assertEqual(set(windows), {"five_hour", "seven_day"})

    def test_sends_the_token_only_to_anthropic(self):
        self.sign_in()
        with mock.patch.object(cu.requests, "get", return_value=FakeResponse(200, USAGE_BODY)) as get:
            cu.fetch_usage()
        (url,), kwargs = get.call_args
        self.assertTrue(url.startswith("https://api.anthropic.com/"))
        self.assertEqual(kwargs["headers"]["Authorization"], "Bearer tok-1")

    def test_expired_sign_in_is_renewed_before_the_request(self):
        self.sign_in(expires_in=-60)

        def renew(exe):
            self.sign_in("tok-2")
            return True

        with mock.patch.object(cu, "renew_sign_in", side_effect=renew) as renewed, \
                mock.patch.object(cu.requests, "get", return_value=FakeResponse(200, USAGE_BODY)) as get:
            cu.fetch_usage("claude.exe")
        renewed.assert_called_once()
        self.assertEqual(get.call_args.kwargs["headers"]["Authorization"], "Bearer tok-2")

    def test_rejected_token_is_renewed_once_then_reported(self):
        self.sign_in()
        with mock.patch.object(cu, "renew_sign_in", return_value=False) as renewed, \
                mock.patch.object(cu.requests, "get", return_value=FakeResponse(401)) as get:
            with self.assertRaises(cu.NotSignedIn):
                cu.fetch_usage("claude.exe")
        renewed.assert_called_once()
        self.assertEqual(get.call_count, 2)

    def test_rate_limit_reports_when_to_retry(self):
        self.sign_in()
        with mock.patch.object(cu.requests, "get", return_value=FakeResponse(429, headers={"Retry-After": "120"})):
            with self.assertRaises(cu.RateLimited) as ctx:
                cu.fetch_usage()
        self.assertEqual(ctx.exception.retry_after, 120)

    def test_not_signed_in(self):
        with self.assertRaises(cu.NotSignedIn):
            cu.fetch_usage()
