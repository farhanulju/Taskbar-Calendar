import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from taskbar_calendar import config


class ConfigTest(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.dir = Path(tmp.name)
        patches = {
            "CONFIG_DIR": self.dir / "config",
            "CONFIG_PATH": self.dir / "config" / "config.json",
            "LEGACY_CONFIG_PATH": self.dir / "legacy.json",
        }
        for name, value in patches.items():
            patcher = mock.patch.object(config, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def test_defaults_when_nothing_saved(self):
        self.assertTrue(config.is_first_run())
        self.assertEqual(config.load_config(), config.DEFAULT_CONFIG)

    def test_save_and_load_round_trip(self):
        cfg = {**config.DEFAULT_CONFIG, "ics_urls": ["https://example.com/a.ics"], "width": 300}
        config.save_config(cfg)
        self.assertFalse(config.is_first_run())
        self.assertEqual(config.load_config(), cfg)

    def test_legacy_config_is_moved_and_upgraded(self):
        config.LEGACY_CONFIG_PATH.write_text(json.dumps({
            "ics_url": "https://example.com/old.ics",
            "calendar_url": "https://calendar.google.com/calendar/r/week",
            "chrome_profile": "Profile 2",
        }))
        cfg = config.load_config()
        self.assertEqual(cfg["ics_urls"], ["https://example.com/old.ics"])
        self.assertEqual(cfg["calendar_view"], "week")
        self.assertEqual(cfg["chrome_profile"], "Profile 2")
        self.assertFalse(config.LEGACY_CONFIG_PATH.exists())
        self.assertTrue(config.CONFIG_PATH.exists())

    def test_invalid_json_falls_back_to_defaults(self):
        config.CONFIG_DIR.mkdir(parents=True)
        config.CONFIG_PATH.write_text("{not json")
        self.assertEqual(config.load_config(), config.DEFAULT_CONFIG)

    def test_ics_urls_normalised(self):
        cfg = {"ics_urls": [" webcal://example.com/a.ics ", "", "https://example.com/b.ics"]}
        self.assertEqual(config.ics_urls(cfg), ["https://example.com/a.ics", "https://example.com/b.ics"])


if __name__ == "__main__":
    unittest.main()
